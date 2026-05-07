using System.Collections.Concurrent;
using FOBackend.Protocol.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace FOBackend.Transport.Security;

/// <summary>
/// 心跳管理器
/// 负责检测客户端存活状态和处理超时断开
/// </summary>
public class HeartbeatManager : IDisposable
{
    private readonly HeartbeatConfig _config;
    private readonly ILogger<HeartbeatManager> _logger;
    private readonly Timer? _checkTimer;
    
    /// <summary>
    /// 获取连接的心跳回调（由外部注入，因为不直接依赖IGameConnection）
    /// </summary>
    private Func<string, DateTime?>? _getLastActivityTime;
    private Action<string>? _onTimeout;
    
    public HeartbeatConfig Config => _config;

    public HeartbeatManager(IOptions<HeartbeatConfig> config, ILogger<HeartbeatManager> logger)
    {
        _config = config.Value;
        _logger = logger;
        
        // 启动定时检查任务（间隔为心跳间隔的一半）
        _checkTimer = new Timer(
            callback: CheckTimeouts,
            state: null,
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromMilliseconds(_config.CheckIntervalMs));
    }

    /// <summary>
    /// 初始化回调（在DI注册后调用）
    /// </summary>
    public void Initialize(
        Func<string, DateTime?> getLastActivityTime,
        Action<string> onTimeout)
    {
        _getLastActivityTime = getLastActivityTime;
        _onTimeout = onTimeout;
    }

    /// <summary>
    /// 处理收到的心跳请求
    /// 返回心跳响应消息
    /// </summary>
    public HeartbeatResponse HandleHeartbeatRequest(HeartbeatRequest request, int currentFrameHint = 0)
    {
        return new HeartbeatResponse
        {
            Header = new ResponseHeader
            {
                RequestId = request.Header?.RequestId ?? 0,
                ErrorCode = ErrorCode.Success
            },
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CurrentFrameHint = currentFrameHint
        };
    }

    /// <summary>
    /// 定期检查超时的连接
    /// </summary>
    private void CheckTimeouts(object? state)
    {
        if (_getLastActivityTime == null || _onTimeout == null)
            return;

        var now = DateTime.UtcNow;
        var timeoutThreshold = TimeSpan.FromMilliseconds(_config.TimeoutMs);

        // 注意：这里只是示意逻辑
        // 实际应该遍历所有活跃连接并检查LastActivityTime
        // 具体实现在 ConnectionManager.CleanupStaleConnections 中
        
        _logger.LogTrace("Heartbeat check at {Time}", now);
    }

    /// <summary>
    /// 判断是否超时
    /// </summary>
    public bool IsTimedOut(DateTime lastActivityTime)
    {
        return DateTime.UtcNow - lastActivityTime > TimeSpan.FromMilliseconds(_config.TimeoutMs);
    }

    public void Dispose()
    {
        _checkTimer?.Dispose();
    }
}

/// <summary>
/// 心跳配置
/// </summary>
public class HeartbeatConfig
{
    /// <summary>
    /// 心跳间隔（毫秒）
    /// 客户端应按此频率发送心跳
    /// 推荐：5000ms（5秒）
    /// </summary>
    public int IntervalMs { get; set; } = 5000;
    
    /// <summary>
    /// 超时阈值（毫秒）
    /// 超过此时间未活动则判定为掉线
    /// 推荐：15000ms（3次心跳未响应）
    /// </summary>
    public int TimeoutMs { get; set; } = 15000;
    
    /// <summary>
    /// 服务端检查间隔（毫秒）
    /// 服务端扫描超时连接的频率
    /// 推荐：2500ms（心跳间隔的一半）
    /// </summary>
    public int CheckIntervalMs { get; set; } = 2500;
}

/// <summary>
/// 流量限制器
/// 使用令牌桶算法防止恶意请求和DDoS攻击
/// </summary>
public class RateLimiter
{
    private readonly RateLimitConfig _config;
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();

    public RateLimiter(RateLimitConfig? config = null)
    {
        _config = config ?? new RateLimitConfig();
    }

    /// <summary>
    /// 尝试获取许可（单连接级别）
    /// </summary>
    public bool TryAcquire(string connectionId)
    {
        var bucket = _buckets.GetOrAdd(connectionId, _ => 
            new TokenBucket(_config.MaxRequestsPerSecond, _config.BurstSize));
        
        return bucket.TryConsume();
    }

    /// <summary>
    /// 尝试获取许可（全局级别）
    /// </summary>
    public bool TryAcquireGlobal()
    {
        // 简化：全局限制可由独立计数器实现
        // 此处返回true，实际生产环境应添加全局限流
        return true;
    }

    /// <summary>
    /// 重置指定连接的限制器（如连接断开时清理）
    /// </summary>
    public void Reset(string connectionId)
    {
        _buckets.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// 清理所有限制器
    /// </summary>
    public void Clear()
    {
        _buckets.Clear();
    }
}

/// <summary>
/// 令牌桶实现
/// </summary>
internal class TokenBucket
{
    private long _tokens;
    private readonly long _maxTokens;
    private readonly double _refillRate;  // tokens per millisecond
    private long _lastRefillTick;

    public TokenBucket(double requestsPerSecond, int burstSize)
    {
        _maxTokens = burstSize;
        _tokens = burstSize;  // 初始满桶
        _refillRate = requestsPerSecond / 1000.0;
        _lastRefillTick = Environment.TickCount64;
    }

    public bool TryConsume()
    {
        Refill();
        
        if (Interlocked.Read(ref _tokens) > 0)
        {
            Interlocked.Decrement(ref _tokens);
            return true;
        }
        
        return false;  // 令牌耗尽，拒绝请求
    }

    private void Refill()
    {
        var now = Environment.TickCount64;
        var elapsed = now - Interlocked.Read(ref _lastRefillTick);
        
        if (elapsed <= 0) return;
        
        var tokensToAdd = (long)(elapsed * _refillRate);
        
        if (tokensToAdd > 0)
        {
            var newValue = Math.Min(
                _maxTokens,
                Interlocked.Add(ref _tokens, tokensToAdd));
            
            Interlocked.Exchange(ref _tokens, newValue);
            Interlocked.Exchange(ref _lastRefillTick, now);
        }
    }
}

/// <summary>
/// 流量限制配置
/// </summary>
public class RateLimitConfig
{
    /// <summary>
    /// 单连接每秒最大请求数
    /// 对于60FPS游戏，正常情况下每帧1个输入+偶尔控制命令
    /// 建议：60-120（留有余量）
    /// </summary>
    public int MaxRequestsPerSecond { get; set; } = 120;
    
    /// <summary>
    /// 突发容量（允许短时间内的突发请求）
    /// 建议：100-200
    /// </summary>
    public int BurstSize { get; set; } = 200;
    
    /// <summary>
    /// 全局每秒最大请求数（所有连接合计）
    /// 根据服务器性能设定
    /// 建议：10000+
    /// </summary>
    public int GlobalMaxRequestsPerSecond { get; set; } = 10000;
}
