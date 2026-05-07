using FOBackend.Protocol.Messages;
using FOBackend.Transport;

namespace FOBackend.Core.Sessions;

/// <summary>
/// 房间/会话状态枚举（针对1v1优化）
/// </summary>
public enum SessionState
{
    /// <summary>等待第2名玩家加入</summary>
    Waiting,
    
    /// <summary>双方都已准备就绪</summary>
    Ready,
    
    /// <summary>帧同步进行中</summary>
    Playing,
    
    /// <summary>对局结束</summary>
    Finished,
    
    /// <summary>已关闭</summary>
    Closed
}

/// <summary>
/// 玩家槽位信息（包含连接引用和状态）
/// </summary>
public class PlayerSlot
{
    public string PlayerId { get; }
    public string PlayerName { get; set; }
    public int SlotNumber { get; }  // 1 (房主) or 2 (加入者)
    
    /// <summary>
    /// 是否已准备好开始游戏
    /// </summary>
    public bool IsReady { get; set; }
    
    /// <summary>
    /// 关联的网络连接
    /// </summary>
    public IGameConnection? Connection { get; set; }

    /// <summary>
    /// RTT 追踪器（滑动窗口平均）
    /// </summary>
    public FOBackend.Infrastructure.RollingAverage PingTracker { get; } = new(windowSize: 20);

    public PlayerSlot(string playerId, string playerName, int slotNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        if (slotNumber != 1 && slotNumber != 2)
            throw new ArgumentOutOfRangeException(nameof(slotNumber), "Slot must be 1 or 2");
        
        PlayerId = playerId;
        PlayerName = playerName;
        SlotNumber = slotNumber;
    }
}
