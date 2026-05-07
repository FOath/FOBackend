using ProtoBuf;

namespace FOBackend.Protocol.Messages;

/// <summary>
/// 认证请求 - 客户端登录时发送
/// </summary>
[ProtoContract]
public class AuthenticateRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
    
    /// <summary>
    /// 玩家显示名称
    /// </summary>
    [ProtoMember(2)]
    public string PlayerName { get; set; } = string.Empty;
    
    /// <summary>
    /// 客户端版本号
    /// </summary>
    [ProtoMember(3)]
    public string ClientVersion { get; set; } = "1.0.0";
}

/// <summary>
/// 认证响应 - 服务端返回玩家身份和会话令牌
/// </summary>
[ProtoContract]
public class AuthenticateResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
    
    /// <summary>
    /// 服务端分配的唯一玩家ID
    /// </summary>
    [ProtoMember(2)]
    public string PlayerId { get; set; } = string.Empty;
    
    /// <summary>
    /// 会话令牌（后续请求需携带用于鉴权）
    /// </summary>
    [ProtoMember(3)]
    public string SessionToken { get; set; } = string.Empty;
    
    /// <summary>
    /// 服务器当前时间戳（用于时间同步）
    /// </summary>
    [ProtoMember(4)]
    public long ServerTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
