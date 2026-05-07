using System.Collections.Concurrent;
using System.Diagnostics;
using FOBackend.Infrastructure;
using FOBackend.Protocol;
using FOBackend.Protocol.Messages;
using FOBackend.Transport;
using Microsoft.Extensions.Logging;

namespace FOBackend.Core.FrameSync;

/// <summary>
/// 帧同步引擎 - 实现 Lockstep 确定性帧同步算法 ⭐⭐⭐
/// 
/// 核心职责：
/// 1. 按 60 FPS 固定节拍运行主循环
/// 2. 收集两个玩家的输入（1v1）
/// 3. 构建并广播帧同步包（确保两客户端在同一帧号收到相同数据）
/// 4. 处理延迟补偿、丢包重传、异常恢复
/// 
/// 设计原则：
/// - 输入透明：服务端不解析输入内容（bytes），仅做字节流转发
/// - 确定性保证：所有客户端在相同帧号必须收到相同的输入集合
/// - 低延迟优先：针对60FPS优化，单帧处理时间 < 1ms
/// 
/// 使用方式：
/// var engine = new FrameSyncEngine(sessionId, playerIds, connections, config);
/// await engine.StartAsync();
/// // ... 引擎自动运行 ...
/// await engine.StopAsync();
/// </summary>
public class FrameSyncEngine : IDisposable
{
    #region 状态字段
    
    public string SessionId { get; }
    public string[] PlayerIds { get; }
    public IGameConnection[] Connections { get; }
    
    private int _currentFrame = 0;
    /// <summary>当前帧号（线程安全读取）</summary>
    public int CurrentFrame => Volatile.Read(ref _currentFrame);
    
    public EngineState State { get; private set; } = EngineState.Stopped;
    
    #endregion

    #region 内部组件
    
    private readonly FrameSyncConfig _config;
    private readonly ILogger _logger;
    
    // 输入缓冲区：按帧号索引
    private readonly ConcurrentDictionary<int, FrameInputs> _inputBuffer = new();
    
    // 历史缓存：用于响应重传请求
    private readonly CircularBuffer<FrameSyncPackage> _historyCache;
    
    // RTT 追踪器
    private readonly LatencyTracker _latencyTracker;
    
    // 定时器：驱动60FPS节拍
    private PeriodicTimer? _frameTimer;
    
    // 取消令牌
    private CancellationTokenSource? _cts;
    
    // 性能统计
    private long _totalFramesProcessed = 0;
    private double _maxFrameProcessingTimeMs = 0;
    private int _missedInputCount = 0;
    
    #endregion

    #region 事件
    
    /// <summary>
    /// 帧处理完成事件（用于监控和日志）
    /// </summary>
    public event Action<int, double>? OnFrameProcessed;
    
    /// <summary>
    /// 输入缺失警告事件
    /// </summary>
    public event Action<int, string>? OnInputMissing;
    
    /// <summary>
    /// 引擎错误事件
    /// </summary>
    public event Action<Exception>? OnError;
    
    /// <summary>
    /// 引擎停止事件
    /// </summary>
    public event Action<EndReason>? OnStopped;
    
    #endregion

    #region 构造函数

    /// <summary>
    /// 创建帧同步引擎实例
    /// </summary>
    /// <param name="sessionId">房间/会话ID</param>
    /// <param name="playerIds">玩家ID数组（必须是2个元素）</param>
    /// <param name="connections">对应玩家的连接对象数组</param>
    /// <param name="config">配置参数</param>
    /// <param name="logger">日志记录器</param>
    public FrameSyncEngine(
        string sessionId,
        string[] playerIds,
        IGameConnection[] connections,
        FrameSyncConfig config,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        
        if (playerIds == null || playerIds.Length != 2)
            throw new ArgumentException("Exactly 2 players required for 1v1", nameof(playerIds));
            
        if (connections == null || connections.Length != 2)
            throw new ArgumentException("Exactly 2 connections required for 1v1", nameof(connections));

        SessionId = sessionId;
        PlayerIds = playerIds;
        Connections = connections;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // 初始化组件
        _historyCache = new CircularBuffer<FrameSyncPackage>(config.HistoryBufferSize);
        _latencyTracker = new LatencyTracker();
        
        // 注册所有玩家到延迟追踪器
        foreach (var pid in playerIds)
        {
            _latencyTracker.RegisterPlayer(pid);
        }
        
        _logger.LogInformation(
            "🎯 FrameSyncEngine created for session {Session}, FPS={FPS}, Interval={Interval:F3}ms",
            sessionId, config.TargetFPS, config.FrameIntervalMs);
    }

    #endregion

    #region 公共方法：启动与停止

    /// <summary>
    /// 启动帧同步引擎
    /// 会向所有客户端发送 FrameSyncStartNotification，然后开始帧循环
    /// </summary>
    public async Task StartAsync(CancellationToken externalCt = default)
    {
        if (State != EngineState.Stopped)
            throw new InvalidOperationException($"Cannot start engine in state: {State}");

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            State = EngineState.Running;
            
            // ===== 步骤 1: 生成共享的随机种子 =====
            var randomSeed = Random.Shared.Next(0, int.MaxValue);
            _logger.LogInformation("Session {Session} using random seed: {Seed}", SessionId, randomSeed);

            // ===== 步骤 2: 向两个客户端发送开始通知 =====
            var startNotification = BuildStartNotification(randomSeed);
            
            _logger.LogDebug("Sending FrameSyncStart to both clients...");
            try
            {
                await Task.WhenAll(
                    Connections[0].SendAsync(startNotification),
                    Connections[1].SendAsync(startNotification)
                ).ConfigureAwait(false);
                
                _logger.LogInformation("✅ Both clients received FrameSyncStart notification");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send start notification");
                throw new InvalidOperationException("Failed to notify clients of sync start", ex);
            }

            // ===== 步骤 3: 启动帧循环定时器（60FPS） =====
            _frameTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.FrameIntervalMs));
            
            _ = RunFrameLoopAsync(_cts.Token);  // Fire-and-forget（后台运行）
            
            _logger.LogInformation(
                "🚀 FrameSyncEngine started! Session={Session}, Seed={Seed}, FPS={FPS}",
                SessionId, randomSeed, _config.TargetFPS);
        }
        catch (Exception ex)
        {
            State = EngineState.Error;
            _logger.LogError(ex, "Failed to start FrameSyncEngine for session {Session}", SessionId);
            OnError?.Invoke(ex);
            throw;
        }
    }

    /// <summary>
    /// 停止帧同步引擎
    /// 向所有客户端发送结束通知并清理资源
    /// </summary>
    public async Task StopAsync(EndReason reason = EndReason.NormalFinish)
    {
        if (State != EngineState.Running && State != EngineState.Paused)
            return;  // 已经不在运行状态

        _logger.LogInformation(
            "⏹️ Stopping FrameSyncEngine for session {Session}, Reason={Reason}, FinalFrame={FinalFrame}",
            SessionId, reason, _currentFrame);

        State = EngineState.Stopping;
        _cts?.Cancel();

        try
        {
            // 发送结束通知
            var endNotification = new FrameSyncEndNotification
            {
                RoomId = SessionId,
                FinalFrameNumber = _currentFrame,
                EndReason = reason
            };

            await Task.WhenAll(
                Connections[0].SendAsync(endNotification),
                Connections[1].SendAsync(endNotification)
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending end notification (may be due to disconnected client)");
        }
        finally
        {
            State = EngineState.Stopped;
            DisposeInternal();
            
            LogPerformanceSummary();
            OnStopped?.Invoke(reason);
        }
    }

    #endregion

    #region 公共方法：输入处理（由上层调用）

    /// <summary>
    /// 接收玩家输入上报
    /// 由 Protocol Layer 的 InputHandler 调用
    /// 这是外部注入输入的唯一入口！
    /// </summary>
    /// <param name="inputReport">客户端发送的输入数据</param>
    /// <param name="fromConn">来源连接</param>
    public async Task ReceiveInputAsync(PlayerInputReport inputReport, IGameConnection fromConn)
    {
        if (State != EngineState.Running)
        {
            _logger.LogDebug("Ignoring input from {Player} at frame {Frame}: engine not running",
                fromConn.PlayerId, inputReport.FrameNumber);
            return;
        }

        var playerId = fromConn.PlayerId ?? "unknown";
        var frameNumber = inputReport.FrameNumber;
        var inputData = inputReport.InputData;

        // 1. 验证帧号合法性（防作弊/防重放）
        if (!IsValidFrameNumber(frameNumber))
        {
            _logger.LogWarning(
                "❌ Invalid frame number {Frame} from player {Player} (current: {Current})",
                frameNumber, playerId, _currentFrame);
            return;
        }

        // 2. 校验输入完整性（CRC校验，防篡改）
        if (!VerifyInputChecksum(inputData, inputReport.InputChecksum))
        {
            _logger.LogWarning(
                "⚠️ Input checksum mismatch! Player={Player}, Frame={Frame}, Expected={Expected}",
                playerId, frameNumber, Crc32.ComputeCrc16(inputData));
            
            // 可选：踢出作弊玩家或忽略该输入
            // 此处选择忽略（不中断对局）
            return;
        }

        // 3. 存入输入缓冲区（按帧号索引）
        var frameInputs = _inputBuffer.GetOrAdd(frameNumber, _ => new FrameInputs());
        frameInputs.SetInput(playerId, inputData, inputReport.InputChecksum);

        _logger.LogTrace(
            "📥 Input received: Player={Player}, Frame={Frame}, DataSize={Size} bytes",
            playerId, frameNumber, inputData.Length);

        // 4. 更新延迟估计（基于帧号差值估算RTT）
        UpdateRttEstimate(playerId, frameNumber);

        // 5. 检查本帧是否所有输入已收集完毕
        if (frameInputs.IsComplete())
        {
            // 所有玩家输入已到齐 → 立即广播（不等定时器tick！）
            _logger.LogTrace(
                "⚡ All inputs ready for frame {Frame}, broadcasting immediately!",
                frameNumber);
            
            await BroadcastFramePackageAsync(frameNumber);
        }
        // 否则：等待帧循环的定时器超时机制来处理
    }

    /// <summary>
    /// 处理丢包重传请求
    /// 当客户端检测到丢失某些帧的同步包时会调用此方法
    /// </summary>
    public async Task<ResendFrameResponse> HandleResendRequestAsync(ResendFrameRequest request)
    {
        var response = new ResendFrameResponse
        {
            Header = new ResponseHeader
            {
                RequestId = request.Header?.RequestId ?? 0,
                ErrorCode = ErrorCode.Success
            },
            Frames = new List<FrameSyncPackage>()
        };

        foreach (var missingFrameNum in request.MissingFrameNumbers)
        {
            if (_historyCache.TryGet(missingFrameNum, out var cachedPackage))
            {
                response.Frames.Add(cachedPackage!);
                _logger.LogDebug("Resending frame {Frame} to client", missingFrameNum);
            }
            else
            {
                _logger.LogWarning(
                    "Requested frame {Frame} not found in history buffer (may have expired)",
                    missingFrameNum);
            }
        }

        _logger.LogInformation(
            "Resend response: Requested={Count}, Found={Found}",
            request.MissingFrameNumbers.Count, response.Frames.Count);

        return response;
    }

    #endregion

    #region 私有核心逻辑：主帧循环

    /// <summary>
    /// 主帧循环 - 60 FPS 心跳驱动
    /// 这是最关键的方法！每16.67ms被调用一次
    /// </summary>
    private async Task RunFrameLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var statsLogCounter = 0;
        int StatsLogInterval = _config.TargetFPS;  // 每秒输出一次统计

        _logger.LogDebug("🔄 Frame loop started for session {Session}", SessionId);

        while (!ct.IsCancellationRequested)
        {
            // 等待下一帧的定时器触发
            var waitForNextTickSuccess = await _frameTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false);
            
            if (!waitForNextTickSuccess)
                break;  // 定时器已被dispose或取消

            var frameStartTime = sw.ElapsedTicks;

            try
            {
                await ProcessSingleFrame(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;  // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Critical error processing frame {Frame}!", _currentFrame);
                OnError?.Invoke(ex);
                
                // 可选：继续尝试下一帧，或直接停止引擎
                // 此处选择继续运行（除非连续多次失败）
            }

            // 性能监控与日志
            Interlocked.Increment(ref _totalFramesProcessed);
            var frameCostMs = (sw.ElapsedTicks - frameStartTime) / (double)Stopwatch.Frequency * 1000;
            
            // 更新最大耗时记录
            UpdateMaxProcessingTime(frameCostMs);

            statsLogCounter++;
            if (statsLogCounter >= StatsLogInterval)
            {
                statsLogCounter = 0;
                _logger.LogDebug(
                    "📊 [Stats] Session={Session}, Frame={Frame:P0}, Cost={Cost:F3}ms, MaxCost={MaxCost:F3}ms, MissedInputs={Missed}",
                    SessionId, _currentFrame, frameCostMs, _maxFrameProcessingTimeMs, _missedInputCount);
            }

            // 触发帧完成事件
            OnFrameProcessed?.Invoke(_currentFrame, frameCostMs);
        }

        _logger.LogInformation("🔚 Frame loop ended for session {Session}", SessionId);
    }

    /// <summary>
    /// 处理单帧的逻辑
    /// 由主循环以60FPS频率调用
    /// </summary>
    private async Task ProcessSingleFrame(CancellationToken ct)
    {
        // 1. 推进帧号
        var frameNum = Interlocked.Increment(ref _currentFrame);

        // 2. 快速路径检查：是否已在 ReceiveInputAsync 中提前广播过？
        if (_inputBuffer.TryGetValue(frameNum, out var existingInputs) && existingInputs.IsComplete())
        {
            // 已提前广播，清理缓冲区后返回
            _inputBuffer.TryRemove(frameNum, out _);
            return;
        }

        // 3. 正常路径：检查输入是否已收集（可能还在等待中）
        EnsureInputBufferExists(frameNum);
        
        // 4. 等待一小段时间收集剩余输入（带超时保护）
        await WaitForRemainingInputsAsync(frameNum, ct);

        // 5. 广播帧同步包（无论是否全员到齐都发送）
        await BroadcastFramePackageAsync(frameNum);
    }

    /// <summary>
    /// 广播帧同步包给所有玩家 ⭐ 最关键操作之一
    /// </summary>
    private async Task BroadcastFramePackageAsync(int frameNumber)
    {
        // 从缓冲区获取（或创建空集合并移除）
        if (!_inputBuffer.TryRemove(frameNumber, out var frameInputs) || frameInputs == null)
        {
            frameInputs = new FrameInputs();
        }

        // ===== 构建帧同步包 =====
        var syncPackage = new FrameSyncPackage
        {
            FrameNumber = frameNumber,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SyncFlags = new SyncFlags
            {
                IsKeyFrame = (frameNumber % _config.KeyFrameInterval == 0),
                ForceResync = false,
                ResyncTargetFrame = 0
            },
            LatencyInfo = _latencyTracker.BuildLatencyInfo(_config)
        };

        // ===== 收集所有玩家的输入 =====
        var hasMissingInput = false;
        string? missingPlayerId = null;

        foreach (var playerId in PlayerIds)
        {
            var inputEntry = frameInputs.GetInput(playerId);
            
            if (inputEntry != null)
            {
                // 有输入 → 添加到同步包
                syncPackage.Inputs.Add(new FramePlayerInput
                {
                    PlayerId = playerId,
                    FrameNumber = frameNumber,
                    InputData = inputEntry.Data,
                    InputChecksum = inputEntry.Checksum
                });
            }
            else
            {
                // 缺失输入！标记为卡顿
                hasMissingInput = true;
                missingPlayerId = playerId;
                Interlocked.Increment(ref _missedInputCount);
                
                _logger.LogWarning(
                    "⚠️ Missing input from player {Player} at frame {Frame}",
                    playerId, frameNumber);

                // 触发输入缺失事件
                OnInputMissing?.Invoke(frameNumber, playerId);

                // 更新延迟信息标志
                syncPackage.LatencyInfo!.IsLagging = true;
                syncPackage.LatencyInfo.LaggingPlayerId = playerId;
                
                // 注意：此处可以选择：
                // a) 发送空输入给对方客户端（让客户端知道对方掉线）
                // b) 不发送该玩家的输入条目（客户端自行判断）
                // c) 使用上一帧的输入作为预测（需保存历史）
                // 选择方案 b（简单且安全）
            }
        }

        // ===== 缓存到历史（用于重传）=====
        _historyCache.Add(frameNumber, syncPackage);

        // ===== 并行广播给两个客户端 =====
        try
        {
            var sendTasks = Connections.Select(conn => conn.SendAsync(syncPackage)).ToArray();
            await Task.WhenAll(sendTasks).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "📤 Broadcast Frame={Frame} to {Count} players, Inputs={InputCount}, Size≈{Size} bytes",
                    frameNumber, 
                    Connections.Length, 
                    syncPackage.Inputs.Count,
                    EstimatePackageSize(syncPackage));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast frame {Frame}", frameNumber);
            
            // 检查是否有连接断开
            CheckConnectionErrors(ex);
        }
    }

    #endregion

    #region 辅助方法

    /// <summary>
    /// 验证帧号的合法性范围
    /// 防止作弊（如发送未来帧号的输入）和重放攻击（旧帧号）
    /// </summary>
    private bool IsValidFrameNumber(int frameNumber)
    {
        var current = Volatile.Read(ref _currentFrame);
        var diff = frameNumber - current;
        
        // 允许范围：当前帧 ~ 未来10帧（考虑网络延迟和预发送）
        // 以及过去2帧（容忍轻微乱序）
        return diff >= -2 && diff <= 15;
    }

    /// <summary>
    /// 校验输入数据的完整性
    /// </summary>
    private static bool VerifyInputChecksum(byte[] data, int expectedChecksum)
    {
        if (data.Length == 0 && expectedChecksum == 0)
            return true;  // 空输入允许checksum=0
        
        var computed = Crc32.ComputeCrc16(data);
        return computed == (ushort)(expectedChecksum & 0xFFFF);
    }

    /// <summary>
    /// 确保指定帧号的输入缓冲区存在
    /// </summary>
    private void EnsureInputBufferExists(int frameNumber)
    {
        _inputBuffer.GetOrAdd(frameNumber, _ => new FrameInputs());
    }

    /// <summary>
    /// 等待剩余输入到达（带超时）
    /// </summary>
    private async Task WaitForRemainingInputsAsync(int frameNumber, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_config.InputCollectTimeoutMs));

        try
        {
            // 轮询等待（避免阻塞线程）
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                if (_inputBuffer.TryGetValue(frameNumber, out var inputs) && inputs.IsComplete())
                {
                    break;  // 输入齐了
                }

                // 短暂休眠再检查（1ms轮询间隔）
                await Task.Delay(1, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 超时：部分玩家未按时发送输入
            _logger.LogDebug(
                "⏰ Frame {Frame} input collection timed out after {Timeout}ms",
                frameNumber, _config.InputCollectTimeoutMs);
        }
    }

    /// <summary>
    /// 更新 RTT 估计值（简化版本）
    /// 实际生产环境应使用更精确的算法（如基于心跳ACK时间戳）
    /// </summary>
    private void UpdateRttEstimate(string playerId, int frameNumber)
    {
        // 简化实现：基于帧号差异和当前时间估算
        // 注意：这不是精确RTT计算，仅用于演示框架
        var currentFrame = Volatile.Read(ref _currentFrame);
        var frameDiff = Math.Abs(frameNumber - currentFrame);
        
        // 将帧数差异转换为大致的时间估计（毫秒）
        // 假设正常情况下输入应在当前帧附近到达
        var estimatedRtt = (long)(frameDiff * _config.FrameIntervalMs * 2);  // 粗略估算
        
        // 限制在合理范围内（0-500ms）
        estimatedRtt = Math.Clamp(estimatedRtt, 0, 500);
        
        _latencyTracker.UpdateRtt(playerId, estimatedRtt);
    }

    /// <summary>
    /// 构建 FrameSyncStart 通知消息
    /// </summary>
    private FrameSyncStartNotification BuildStartNotification(int randomSeed)
    {
        return new FrameSyncStartNotification
        {
            RoomId = SessionId,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RandomSeed = randomSeed,
            PlayerIds = new List<string>(PlayerIds),  // 玩家顺序很重要！
            Fps = _config.TargetFPS,
            FrameIntervalMs = (int)Math.Ceiling(_config.FrameIntervalMs)
        };
    }

    /// <summary>
    /// 估算同步包的大小（用于日志输出）
    /// </summary>
    private static int EstimatePackageSize(FrameSyncPackage pkg)
    {
        // 粗略估算：固定开销 + 输入数据大小
        const int fixedOverhead = 50;  // 基础结构开销（帧号、时间戳等）
        int inputsSize = pkg.Inputs.Sum(i => 30 + i.InputData.Length);  // 每个输入的开销
        return fixedOverhead + inputsSize;
    }

    /// <summary>
    /// 检查并发送错误是否由连接断开引起
    /// </summary>
    private void CheckConnectionErrors(Exception ex)
    {
        // 检查异常类型判断是否为连接断开
        // 实际实现应根据具体KCP库的异常类型来判断
        if (ex is ObjectDisposedException || 
            (ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Connection error detected, may need to handle disconnect");
            // 可以在此处触发房间中断逻辑
        }
    }

    /// <summary>
    /// 更新最大帧处理时间
    /// </summary>
    private void UpdateMaxProcessingTime(double costMs)
    {
        double currentMax;
        do
        {
            currentMax = _maxFrameProcessingTimeMs;
            if (costMs <= currentMax) break;
        } while (
            Interlocked.CompareExchange(ref _maxFrameProcessingTimeMs, costMs, currentMax) 
            != currentMax);
    }

    /// <summary>
    /// 输出性能总结日志
    /// </summary>
    private void LogPerformanceSummary()
    {
        var totalFrames = Interlocked.Read(ref _totalFramesProcessed);
        _logger.LogInformation(
            "📈 Performance Summary for Session {Session}:\n" +
            "  Total Frames: {Total}\n" +
            "  Max Processing Time: {MaxCost:F3}ms\n" +
            "  Missed Inputs: {Missed}\n" +
            "  Avg Frame Budget: {Budget:F3}ms\n" +
            "  Target FPS: {FPS}",
            SessionId,
            totalFrames,
            _maxFrameProcessingTimeMs,
            _missedInputCount,
            _config.FrameIntervalMs,
            _config.TargetFPS);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// 清理内部资源
    /// </summary>
    private void DisposeInternal()
    {
        _frameTimer?.Dispose();
        _cts?.Dispose();
        _inputBuffer.Clear();
        _historyCache.Clear();
        _latencyTracker.Dispose();
    }

    public void Dispose()
    {
        if (State == EngineState.Running)
        {
            StopAsync().GetAwaiter().GetResult();  // 同步阻塞式停止
        }
        
        DisposeInternal();
        GC.SuppressFinalize(this);
    }

    #endregion
}
