using System;

namespace FOBackend.Protocol;

/// <summary>
/// 消息 ID 枚举 - 对应所有 Protobuf 消息类型
/// 用于路由分发和协议版本管理
/// </summary>
public enum MessageId : ushort
{
    // ======== 系统保留 (0x0000) ========
    Unknown = 0x0000,
    
    // ======== 认证 (0x0001-0x001F) ========
    AuthRequest = 0x0001,
    AuthResponse = 0x0002,
    
    // ======== 心跳 (0x0020-0x002F) ========
    HeartbeatRequest = 0x0020,
    HeartbeatResponse = 0x0021,
    
    // ======== 房间管理 (0x0100-0x01FF) ========
    CreateRoomRequest = 0x0100,
    CreateRoomResponse = 0x0101,
    JoinRoomRequest = 0x0102,
    JoinRoomResponse = 0x0103,
    LeaveRoomRequest = 0x0104,
    LeaveRoomResponse = 0x0105,
    ReadyRequest = 0x0106,
    ReadyResponse = 0x0107,
    
    // ======== 帧同步核心 (0x0200-0x02FF) ⭐ ========
    FrameSyncStart = 0x0200,       // 服务器->客户端：开始帧同步
    PlayerInputReport = 0x0201,    // 客户端->服务器：上报输入
    FrameSyncPackage = 0x0202,     // 服务器->客户端：广播同步包
    ResendFrameRequest = 0x0203,   // 客户端->服务器：请求重传
    ResendFrameResponse = 0x0204,  // 服务器->客户端：重传响应
    FrameSyncEnd = 0x0205,         // 服务器->客户端：结束帧同步
    
    // ======== 通知事件 (0x0300-0x03FF) ========
    RoomStatusChanged = 0x0300,
    PlayerJoined = 0x0301,
    PlayerLeft = 0x0302,
}

/// <summary>
/// 消息方向辅助判断
/// </summary>
public static class MessageDirection
{
    /// <summary>
    /// 判断是否为请求/上行消息（客户端→服务器）
    /// </summary>
    public static bool IsClientToServer(MessageId id)
    {
        return id switch
        {
            MessageId.AuthRequest or
            MessageId.HeartbeatRequest or
            MessageId.CreateRoomRequest or
            MessageId.JoinRoomRequest or
            MessageId.LeaveRoomRequest or
            MessageId.ReadyRequest or
            MessageId.PlayerInputReport or
            MessageId.ResendFrameRequest => true,
            _ => false
        };
    }
    
    /// <summary>
    /// 判断是否为响应/下行消息（服务器→客户端）
    /// </summary>
    public static bool IsServerToClient(MessageId id)
    {
        return id switch
        {
            MessageId.AuthResponse or
            MessageId.HeartbeatResponse or
            MessageId.CreateRoomResponse or
            MessageId.JoinRoomResponse or
            MessageId.LeaveRoomResponse or
            MessageId.ReadyResponse or
            MessageId.FrameSyncStart or
            MessageId.FrameSyncPackage or
            MessageId.ResendFrameResponse or
            MessageId.FrameSyncEnd or
            MessageId.RoomStatusChanged or
            MessageId.PlayerJoined or
            MessageId.PlayerLeft => true,
            _ => false
        };
    }
}
