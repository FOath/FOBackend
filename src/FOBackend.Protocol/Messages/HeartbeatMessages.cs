using ProtoBuf;

namespace FOBackend.Protocol.Messages;

/// <summary>
/// 心跳请求（客户端 -> 服务器）
/// 极简设计，减少带宽消耗
/// </summary>
[ProtoContract]
public class HeartbeatRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
}

/// <summary>
/// 心跳响应（服务器 -> 客户端）
/// </summary>
[ProtoContract]
public class HeartbeatResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
    
    /// <summary>
    /// 服务器当前时间戳（用于时钟同步和RTT计算）
    /// </summary>
    [ProtoMember(2)]
    public long ServerTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    /// <summary>
    /// 可选：告知当前帧号（如果正在游戏中）
    /// </summary>
    [ProtoMember(3)]
    public int CurrentFrameHint { get; set; }
}
