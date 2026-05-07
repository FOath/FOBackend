using ProtoBuf;

namespace FOBackend.Protocol.Messages;

/// <summary>
/// 创建房间请求（1v1对局）
/// </summary>
[ProtoContract]
public class CreateRoomRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
    
    /// <summary>
    /// 游戏模式
    /// </summary>
    [ProtoMember(2)]
    public GameMode GameMode { get; set; } = GameMode.Shooter1V1;
    
    /// <summary>
    /// 自定义房间选项（如地图ID、规则等）
    /// </summary>
    [ProtoMember(3)]
    public Dictionary<string, string> RoomOptions { get; set; } = new();
}

/// <summary>
/// 创建房间响应
/// </summary>
[ProtoContract]
public class CreateRoomResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
    
    /// <summary>
    /// 房间唯一ID
    /// </summary>
    [ProtoMember(2)]
    public string RoomId { get; set; } = string.Empty;
    
    /// <summary>
    /// 邀请码（便于分享给好友）
    /// </summary>
    [ProtoMember(3)]
    public string InviteCode { get; set; } = string.Empty;
    
    /// <summary>
    /// 房间详细信息
    /// </summary>
    [ProtoMember(4)]
    public RoomInfo? RoomInfo { get; set; }
}

/// <summary>
/// 加入房间请求
/// </summary>
[ProtoContract]
public class JoinRoomRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
    
    /// <summary>
    /// 通过房间ID加入
    /// </summary>
    [ProtoMember(2)]
    public string? RoomId { get; set; }
    
    /// <summary>
    /// 通过邀请码加入（与 RoomId 二选一）
    /// </summary>
    [ProtoMember(3)]
    public string? InviteCode { get; set; }
}

/// <summary>
/// 加入房间响应
/// </summary>
[ProtoContract]
public class JoinRoomResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
    
    /// <summary>
    /// 房间详细信息
    /// </summary>
    [ProtoMember(2)]
    public RoomInfo? RoomInfo { get; set; }
    
    /// <summary>
    /// 当前已加入的玩家列表
    /// </summary>
    [ProtoMember(3)]
    public List<PlayerInfo> Players { get; set; } = new();
}

/// <summary>
/// 离开房间请求
/// </summary>
[ProtoContract]
public class LeaveRoomRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
    
    /// <summary>
    /// 房间ID
    /// </summary>
    [ProtoMember(2)]
    public string RoomId { get; set; } = string.Empty;
}

/// <summary>
/// 离开房间响应
/// </summary>
[ProtoContract]
public class LeaveRoomResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
}

/// <summary>
/// 准备就绪请求
/// </summary>
[ProtoContract]
public class ReadyRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
    
    /// <summary>
    /// 房间ID
    /// </summary>
    [ProtoMember(2)]
    public string RoomId { get; set; } = string.Empty;
    
    /// <summary>
    /// 是否准备就绪
    /// </summary>
    [ProtoMember(3)]
    public bool IsReady { get; set; }
}

/// <summary>
/// 准备就绪响应
/// </summary>
[ProtoContract]
public class ReadyResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
}

/// <summary>
/// 房间详细信息
/// </summary>
[ProtoContract]
public class RoomInfo
{
    [ProtoMember(1)]
    public string RoomId { get; set; } = string.Empty;
    
    [ProtoMember(2)]
    public GameMode GameMode { get; set; }
    
    [ProtoMember(3)]
    public RoomStatus Status { get; set; }
    
    /// <summary>
    /// 最大玩家数（固定为2，1v1）
    /// </summary>
    [ProtoMember(4)]
    public int MaxPlayers { get; set; } = 2;
    
    /// <summary>
    /// 当前玩家数量
    /// </summary>
    [ProtoMember(5)]
    public int CurrentPlayerCount { get; set; }
    
    /// <summary>
    /// 房主玩家ID
    /// </summary>
    [ProtoMember(6)]
    public string HostPlayerId { get; set; } = string.Empty;
    
    /// <summary>
    /// 创建时间
    /// </summary>
    [ProtoMember(7)]
    public long CreateTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    /// <summary>
    /// 自定义选项
    /// </summary>
    [ProtoMember(8)]
    public Dictionary<string, string> Options { get; set; } = new();
}
