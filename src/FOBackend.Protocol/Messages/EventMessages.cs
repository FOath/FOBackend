using ProtoBuf;

namespace FOBackend.Protocol.Messages;

/// <summary>
/// 房间状态变化通知（服务器 -> 客户端）
/// </summary>
[ProtoContract]
public class RoomStatusChangedNotification
{
    [ProtoMember(1)]
    public string RoomId { get; set; } = string.Empty;
    
    [ProtoMember(2)]
    public RoomStatus NewStatus { get; set; }
    
    [ProtoMember(3)]
    public string? TriggerPlayerId { get; set; }
}

/// <summary>
/// 玩家加入通知（服务器 -> 房间内其他客户端）
/// </summary>
[ProtoContract]
public class PlayerJoinedNotification
{
    [ProtoMember(1)]
    public string RoomId { get; set; } = string.Empty;
    
    [ProtoMember(2)]
    public PlayerInfo? Player { get; set; }
}

/// <summary>
/// 玩家离开通知（服务器 -> 房间内其他客户端）
/// </summary>
[ProtoContract]
public class PlayerLeftNotification
{
    [ProtoMember(1)]
    public string RoomId { get; set; } = string.Empty;
    
    [ProtoMember(2)]
    public string PlayerId { get; set; } = string.Empty;
    
    [ProtoMember(3)]
    public LeaveReason Reason { get; set; }
}
