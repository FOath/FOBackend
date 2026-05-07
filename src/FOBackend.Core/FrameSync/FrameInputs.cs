using System.Collections.Concurrent;
using FOBackend.Infrastructure;

namespace FOBackend.Core.FrameSync;

/// <summary>
/// 单个玩家的输入条目
/// 记录原始输入数据及其校验信息
/// </summary>
internal record PlayerInputEntry(byte[] Data, int Checksum, DateTime ReceivedTime);

/// <summary>
/// 单帧输入集合
/// 用于收集某一帧号下所有玩家的输入
/// 针对 1v1 场景优化（固定2人）
/// </summary>
internal class FrameInputs
{
    private readonly Dictionary<string, PlayerInputEntry> _inputs = new(2);
    private readonly object _lockObj = new();
    private volatile int _receivedCount = 0;
    
    /// <summary>
    /// 预期玩家数量（1v1 固定为2）
    /// </summary>
    public int ExpectedCount { get; } = 2;

    /// <summary>
    /// 设置某个玩家的输入
    /// 线程安全：支持从网络IO线程并发调用
    /// </summary>
    public void SetInput(string playerId, byte[] data, int checksum)
    {
        ArgumentNullException.ThrowIfNull(playerId);
        ArgumentNullException.ThrowIfNull(data);
        
        lock (_lockObj)
        {
            // 防重复设置同一玩家的输入
            if (_inputs.ContainsKey(playerId))
                return;
            
            _inputs[playerId] = new PlayerInputEntry(data, checksum, DateTime.UtcNow);
            Interlocked.Increment(ref _receivedCount);
        }
    }

    /// <summary>
    /// 获取指定玩家的输入
    /// </summary>
    public PlayerInputEntry? GetInput(string playerId)
    {
        lock (_lockObj)
        {
            return _inputs.GetValueOrDefault(playerId);
        }
    }

    /// <summary>
    /// 获取所有已收到的输入
    /// 返回副本以避免并发修改问题
    /// </summary>
    public IReadOnlyDictionary<string, PlayerInputEntry> GetAllInputs()
    {
        lock (_lockObj)
        {
            return new Dictionary<string, PlayerInputEntry>(_inputs);
        }
    }

    /// <summary>
    /// 检查是否所有预期玩家的输入都已收到
    /// </summary>
    public bool IsComplete() => Volatile.Read(ref _receivedCount) >= ExpectedCount;

    /// <summary>
    /// 已收到的输入数量
    /// </summary>
    public int ReceivedCount => Volatile.Read(ref _receivedCount);

    /// <summary>
    /// 清理（准备复用对象池时调用）
    /// </summary>
    public void Clear()
    {
        lock (_lockObj)
        {
            _inputs.Clear();
            _receivedCount = 0;
        }
    }

    /// <summary>
    /// 检查是否包含指定玩家的输入
    /// </summary>
    public bool HasInput(string playerId)
    {
        lock (_lockObj)
        {
            return _inputs.ContainsKey(playerId);
        }
    }
}
