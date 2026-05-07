using ProtoBuf;
using System;

namespace FOBackend.Protocol.Messages;

/// <summary>
/// 错误码枚举
/// </summary>
public enum ErrorCode : int
{
    [ProtoEnum]
    Success = 0,
    
    // ====== 通用错误 (1000-1999) ======
    [ProtoEnum]
    UnknownError = 1000,
    [ProtoEnum]
    InvalidRequest = 1001,
    [ProtoEnum]
    NotAuthenticated = 1002,
    
    // ====== 房间错误 (2000-2999) ======
    [ProtoEnum]
    SessionNotFound = 2000,
    [ProtoEnum]
    SessionFull = 2001,
    [ProtoEnum]
    SessionAlreadyStarted = 2002,
    
    // ====== 帧同步错误 (3000-3999) ======
    [ProtoEnum]
    InvalidFrameNumber = 3000,
    [ProtoEnum]
    InputTimeout = 3001,
    [ProtoEnum]
    PlayerNotInSession = 3002,
    
    // ====== 安全限制 (4000-4999) ======
    [ProtoEnum]
    RateLimited = 4000,
}

/// <summary>
/// 游戏模式枚举
/// </summary>
[ProtoContract]
public enum GameMode : int
{
    [ProtoEnum]
    Unknown = 0,
    [ProtoEnum]
    Shooter1V1 = 1,      // 平面射击 1v1
    [ProtoEnum]
    Fighting1V1 = 2,     // 格斗/动作 1v1
    [ProtoEnum]
    Custom = 99,         // 自定义模式
}

/// <summary>
/// 房间状态枚举
/// </summary>
[ProtoContract]
public enum RoomStatus : int
{
    [ProtoEnum]
    Waiting = 0,         // 等待玩家加入
    [ProtoEnum]
    Ready = 1,           // 所有玩家已准备
    [ProtoEnum]
    Playing = 2,         // 帧同步进行中
    [ProtoEnum]
    Finished = 3,        // 已结束
}

/// <summary>
/// 对局结束原因
/// </summary>
[ProtoContract]
public enum EndReason : int
{
    [ProtoEnum]
    NormalFinish = 0,           // 正常结束
    [ProtoEnum]
    PlayerDisconnect = 1,       // 玩家断线
    [ProtoEnum]
    ServerShutdown = 2,         // 服务器关闭
}

/// <summary>
/// 玩家离开原因
/// </summary>
[ProtoContract]
public enum LeaveReason : int
{
    [ProtoEnum]
    NormalLeave = 0,           // 正常离开
    [ProtoEnum]
    Disconnected = 1,          // 断线
    [ProtoEnum]
    Kicked = 2,                // 被踢出
    [ProtoEnum]
    Timeout = 3,               // 超时
}

/// <summary>
/// 请求基础头（所有请求消息应包含或继承此结构）
/// </summary>
[ProtoContract]
public class RequestHeader
{
    [ProtoMember(1)]
    public long RequestId { get; set; }
    
    [ProtoMember(2)]
    public int Version { get; set; } = ProtocolVersion.Current;
    
    [ProtoMember(3)]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    /// <summary>
    /// 会话令牌（认证后必须携带）
    /// </summary>
    [ProtoMember(4)]
    public string? SessionToken { get; set; }
}

/// <summary>
/// 响应基础头（所有响应消息应包含或继承此结构）
/// </summary>
[ProtoContract]
public class ResponseHeader
{
    [ProtoMember(1)]
    public long RequestId { get; set; }
    
    [ProtoMember(2)]
    public ErrorCode ErrorCode { get; set; } = ErrorCode.Success;
    
    [ProtoMember(3)]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 协议版本信息
/// </summary>
public static class ProtocolVersion
{
    public const int Current = 1;
    public const int MinCompatible = 1;
}

/// <summary>
/// 玩家基本信息（轻量级）
/// </summary>
[ProtoContract]
public class PlayerInfo
{
    [ProtoMember(1)]
    public string PlayerId { get; set; } = string.Empty;
    
    [ProtoMember(2)]
    public string PlayerName { get; set; } = string.Empty;
    
    [ProtoMember(3)]
    public bool IsReady { get; set; }
    
    [ProtoMember(4)]
    public int PingMs { get; set; }
    
    [ProtoMember(5)]
    public long JoinTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
