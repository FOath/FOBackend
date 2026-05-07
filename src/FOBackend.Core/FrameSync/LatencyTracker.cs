using FOBackend.Infrastructure;
using FOBackend.Protocol.Messages;

namespace FOBackend.Core.FrameSync;

/// <summary>
/// 延迟追踪器
/// 监控每个玩家的网络延迟情况
/// 用于动态调整缓冲策略和发出卡顿警告
/// </summary>
public class LatencyTracker : IDisposable
{
    private readonly Dictionary<string, RollingAverage> _playerRttTrackers = new();
    private readonly Dictionary<string, RollingAverage> _jitterTrackers = new();
    private readonly object _lockObj = new();
    private readonly int _windowSize;
    private bool _disposed;

    /// <summary>
    /// 创建延迟追踪器
    /// </summary>
    /// <param name="windowSize">滑动窗口大小（样本数），默认30</param>
    public LatencyTracker(int windowSize = 30)
    {
        _windowSize = windowSize;
    }

    /// <summary>
    /// 注册要追踪的玩家
    /// </summary>
    public void RegisterPlayer(string playerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        
        lock (_lockObj)
        {
            if (!_playerRttTrackers.ContainsKey(playerId))
            {
                _playerRttTrackers[playerId] = new RollingAverage(_windowSize);
                _jitterTrackers[playerId] = new RollingAverage(_windowSize);
            }
        }
    }

    /// <summary>
    /// 移除玩家
    /// </summary>
    public void UnregisterPlayer(string playerId)
    {
        lock (_lockObj)
        {
            _playerRttTrackers.Remove(playerId);
            _jitterTrackers.Remove(playerId);
        }
    }

    /// <summary>
    /// 更新玩家的 RTT 估计值
    /// 应在每次收到该玩家输入时调用
    /// </summary>
    /// <param name="playerId">玩家ID</param>
    /// <param name="estimatedRttMs">估计的往返时延（毫秒）</param>
    public void UpdateRtt(string playerId, long estimatedRttMs)
    {
        lock (_lockObj)
        {
            if (_playerRttTrackers.TryGetValue(playerId, out var tracker))
            {
                tracker.Update(estimatedRttMs);
                
                // 同时计算抖动（RTT的变化率）
                if (_jitterTrackers.TryGetValue(playerId, out var jitter))
                {
                    var avg = tracker.Average;
                    var deviation = Math.Abs(estimatedRttMs - avg);
                    jitter.Update(deviation);
                }
            }
        }
    }

    /// <summary>
    /// 获取指定玩家的平均 RTT
    /// </summary>
    public long? GetAverageRtt(string playerId)
    {
        lock (_lockObj)
        {
            if (_playerRttTrackers.TryGetValue(playerId, out var tracker))
                return tracker.Average;
            return null;
        }
    }

    /// <summary>
    /// 获取所有玩家的最大 RTT
    /// 用于确定房间级别的缓冲策略
    /// </summary>
    public long GetMaxRtt()
    {
        long maxRtt = 0;
        
        lock (_lockObj)
        {
            foreach (var tracker in _playerRttTrackers.Values)
            {
                var avg = tracker.Average;
                if (avg > maxRtt)
                    maxRtt = avg;
            }
        }
        
        return maxRtt;
    }

    /// <summary>
    /// 获取所有玩家的最小 RTT
    /// </summary>
    public long GetMinRtt()
    {
        long minRtt = long.MaxValue;
        bool hasData = false;
        
        lock (_lockObj)
        {
            foreach (var tracker in _playerRttTrackers.Values)
            {
                hasData = true;
                var avg = tracker.Average;
                if (avg < minRtt)
                    minRtt = avg;
            }
        }
        
        return hasData ? minRtt : 0;
    }

    /// <summary>
    /// 计算 RTT 差异（最大值 - 最小值）
    /// 差异过大说明网络条件不公平
    /// </summary>
    public long GetRttDifference()
    {
        return GetMaxRtt() - GetMinRtt();
    }

    /// <summary>
    /// 判断是否存在明显的不公平延迟
    /// </summary>
    /// <param name="thresholdMs">阈值（毫秒）</param>
    public bool IsUnfairLatency(int thresholdMs = 80)
    {
        return GetRttDifference() > thresholdMs;
    }

    /// <summary>
    /// 获取所有注册玩家的ID列表
    /// </summary>
    public IReadOnlyList<string> GetRegisteredPlayers()
    {
        lock (_lockObj)
        {
            return _playerRttTrackers.Keys.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 构建延迟信息消息体（用于广播给客户端）
    /// </summary>
    public LatencyInfo BuildLatencyInfo(FrameSyncConfig config)
    {
        var maxRtt = (int)GetMaxRtt();
        var rttDiff = GetRttDifference();
        
        return new LatencyInfo
        {
            MaxRttMs = maxRtt,
            RecommendedBufferFrames = CalculateRecommendedBuffer(maxRtt, config),
            IsLagging = rttDiff > config.MaxRttDiffMs,
            LaggingPlayerId = IdentifyLaggingPlayer(rttDiff > config.MaxRttDiffMs ? config.MaxRttDiffMs : 0)
        };
    }

    /// <summary>
    /// 根据最大RTT计算建议的客户端缓冲帧数
    /// </summary>
    private static int CalculateRecommendedBuffer(int maxRttMs, FrameSyncConfig config)
    {
        if (maxRttMs <= 0) return 2;  // 默认值
        
        // 公式: bufferFrames = ceil(maxRtt / frameInterval) + safetyMargin
        var framesFromRtt = Math.Ceiling(maxRttMs / config.FrameIntervalMs);
        var safetyMargin = 2;  // 额外2帧安全边际
        
        return Math.Max(2, (int)(framesFromRtt + safetyMargin));
    }

    /// <summary>
    /// 找出延迟最高的玩家（如果有）
    /// </summary>
    private string? IdentifyLaggingPlayer(long diffThreshold)
    {
        string? laggingPlayer = null;
        long maxRtt = 0;
        
        lock (_lockObj)
        {
            foreach (var kv in _playerRttTrackers)
            {
                var avg = kv.Value.Average;
                if (avg > maxRtt)
                {
                    maxRtt = avg;
                    laggingPlayer = kv.Key;
                }
            }
        }
        
        return laggingPlayer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        lock (_lockObj)
        {
            _playerRttTrackers.Clear();
            _jitterTrackers.Clear();
        }
        
        _disposed = true;
    }
}
