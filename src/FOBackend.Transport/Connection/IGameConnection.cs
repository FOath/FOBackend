using FOBackend.Protocol;
using FOBackend.Protocol.Messages;

namespace FOBackend.Transport;

/// <summary>
/// 连接状态枚举
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// 握手中（KCP Syn Cookie 阶段）
    /// </summary>
    Connecting,
    
    /// <summary>
    /// 已连接（网络层已建立）
    /// </summary>
    Connected,
    
    /// <summary>
    /// 已认证（通过身份验证）
    /// </summary>
    Authenticated,
    
    /// <summary>
    /// 游戏中（已加入房间并参与对局）
    /// </summary>
    InGame,
    
    /// <summary>
    /// 已断开（连接关闭）
    /// </summary>
    Disconnected,
    
    /// <summary>
    /// 错误状态（异常情况）
    /// </summary>
    Error
}

/// <summary>
/// 消息投递模式
/// </summary>
public enum DeliveryMode
{
    /// <summary>
    /// 可靠有序（默认模式，用于所有控制命令）
    /// KCP 保证可靠性和顺序
    /// </summary>
    Reliable,
    
    /// <summary>
    /// 不可靠（可选，用于频繁更新的位置同步等场景）
    /// 注意：当前帧同步架构主要使用 Reliable 模式
    /// </summary>
    Unreliable
}

/// <summary>
/// 游戏连接抽象接口
/// 屏蔽底层传输实现细节（UDP/TCP/KCP/WebSocket等）
/// 上层业务代码仅依赖此接口
/// </summary>
public interface IGameConnection : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 连接唯一标识符
    /// </summary>
    string ConnectionId { get; }
    
    /// <summary>
    /// 远程端点地址（IP:Port）
    /// </summary>
    System.Net.EndPoint? RemoteEndPoint { get; }
    
    /// <summary>
    /// 建立连接的时间戳（UTC）
    /// </summary>
    DateTime ConnectedTime { get; }
    
    /// <summary>
    /// 最后一次活动时间（UTC）
    /// 用于心跳检测和超时判断
    /// </summary>
    DateTime LastActivityTime { get; set; }
    
    /// <summary>
    /// 当前连接状态
    /// </summary>
    ConnectionState State { get; set; }
    
    // ======== 认证相关 ========
    
    /// <summary>
    /// 关联的玩家ID（认证后填充）
    /// </summary>
    string? PlayerId { get; set; }
    
    /// <summary>
    /// 会话令牌（认证后填充）
    /// </summary>
    string? SessionToken { get; set; }
    
    // ======== 发送操作 ========
    
    /// <summary>
    /// 发送 Protobuf 消息（自动序列化+组包）
    /// </summary>
    Task SendAsync<TMessage>(TMessage message, DeliveryMode mode = DeliveryMode.Reliable) 
        where TMessage : class;
    
    /// <summary>
    /// 发送原始字节数据（已组包的数据包）
    /// </summary>
    Task SendRawAsync(byte[] data, DeliveryMode mode = DeliveryMode.Reliable);
    
    /// <summary>
    /// 按 MessageId + Payload 格式发送（内部使用）
    /// </summary>
    Task SendPacketAsync(MessageId messageId, byte[] payload, DeliveryMode mode = DeliveryMode.Reliable);
    
    // ======== 事件 ========
    
    /// <summary>
    /// 接收到原始数据时触发（已解包后的载荷）
    /// </summary>
    Func<MessageId, byte[], Task>? OnDataReceived { get; set; }
    
    /// <summary>
    /// 连接断开时触发
    /// reason: 断开原因描述
    /// </summary>
    Action<string>? OnDisconnected { get; set; }
}
