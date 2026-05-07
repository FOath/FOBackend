using ProtoBuf;

namespace FOBackend.Protocol.Messages;

/// <summary>
/// 帧同步开始通知（服务器 -> 客户端）
/// 标志着帧循环即将启动
/// </summary>
[ProtoContract]
public class FrameSyncStartNotification
{
    [ProtoMember(1)]
    public string RoomId { get; set; } = string.Empty;
    
    /// <summary>
    /// 开始时的服务器时间戳
    /// </summary>
    [ProtoMember(2)]
    public long StartTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    /// <summary>
    /// 随机种子（确保双方PRNG一致性！）
    /// </summary>
    [ProtoMember(3)]
    public int RandomSeed { get; set; }
    
    /// <summary>
    /// 玩家顺序（决定输入处理和状态更新的固定顺序）
    /// </summary>
    [ProtoMember(4)]
    public List<string> PlayerIds { get; set; } = new();
    
    /// <summary>
    /// 服务器目标帧率
    /// </summary>
    [ProtoMember(5)]
    public int Fps { get; set; } = 60;
    
    /// <summary>
    /// 帧间隔（毫秒）≈ 16.67ms for 60 FPS
    /// </summary>
    [ProtoMember(6)]
    public int FrameIntervalMs { get; set; } = 16;
}

/// <summary>
/// 玩家输入上报（客户端 -> 服务器）⭐ 最频繁的消息
/// 每帧由每个客户端发送一次
/// </summary>
[ProtoContract]
public class PlayerInputReport
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }

    /// <summary>
    /// 玩家ID
    /// </summary>
    [ProtoMember(2)]
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>
    /// 所属房间ID
    /// </summary>
    [ProtoMember(3)]
    public string RoomId { get; set; } = string.Empty;
    
    /// <summary>
    /// 当前帧号
    /// </summary>
    [ProtoMember(4)]
    public int FrameNumber { get; set; }
    
    /// <summary>
    /// 输入数据（二进制黑盒）⚠️ 服务端不解析内容，仅做字节流转发
    /// 格式完全由客户端自行约定，例如：
    /// - 平面射击: [方向键掩码][动作键掩码][摇杆X][摇杆Y]...
    /// - 动作格斗: [移动指令][攻击类型][防御状态]...
    /// 长度建议: 1-64 bytes
    /// </summary>
    [ProtoMember(5)]
    public byte[] InputData { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// 输入校验和（CRC16/32，防止传输篡改）
    /// 服务端验证完整性后原样转发
    /// </summary>
    [ProtoMember(6)]
    public int InputChecksum { get; set; }
}

/// <summary>
/// 单个玩家的输入数据（包含在同步包中）
/// </summary>
[ProtoContract]
public class FramePlayerInput
{
    [ProtoMember(1)]
    public string PlayerId { get; set; } = string.Empty;
    
    [ProtoMember(2)]
    public int FrameNumber { get; set; }
    
    /// <summary>
    /// 输入数据（原样来自客户端，服务端未修改）
    /// </summary>
    [ProtoMember(3)]
    public byte[] InputData { get; set; } = Array.Empty<byte>();
    
    [ProtoMember(4)]
    public int InputChecksum { get; set; }
}

/// <summary>
/// 延迟补偿辅助信息
/// 帮助客户端调整预测和缓冲策略
/// </summary>
[ProtoContract]
public class LatencyInfo
{
    /// <summary>
    /// 当前房间内最高RTT（毫秒）
    /// </summary>
    [ProtoMember(1)]
    public int MaxRttMs { get; set; }
    
    /// <summary>
    /// 建议客户端缓冲帧数
    /// （基于 RTT / FrameIntervalMs 计算）
    /// </summary>
    [ProtoMember(2)]
    public int RecommendedBufferFrames { get; set; } = 2;
    
    /// <summary>
    /// 是否有玩家出现明显卡顿
    /// </summary>
    [ProtoMember(3)]
    public bool IsLagging { get; set; }
    
    /// <summary>
    /// 卡顿的玩家ID（如果有）
    /// </summary>
    [ProtoMember(4)]
    public string? LaggingPlayerId { get; set; }
}

/// <summary>
/// 同步标志位
/// </summary>
[ProtoContract]
public class SyncFlags
{
    /// <summary>
    /// 是否为关键帧（如每秒第1帧）
    /// 关键帧可用于保存检查点、强制状态校验等
    /// </summary>
    [ProtoMember(1)]
    public bool IsKeyFrame { get; set; }
    
    /// <summary>
    /// 是否强制重新同步（异常恢复时使用）
    /// </summary>
    [ProtoMember(2)]
    public bool ForceResync { get; set; }
    
    /// <ProtoMember(3)]
    public int ResyncTargetFrame { get; set; }
}

/// <summary>
/// 帧同步包广播（服务器 -> 所有客户端）⭐ 核心消息
/// 每帧由服务器向所有客户端广播一次
/// 包含该帧所有玩家的输入数据
/// </summary>
[ProtoContract]
public class FrameSyncPackage
{
    /// <summary>
    /// 当前帧号
    /// </summary>
    [ProtoMember(1)]
    public int FrameNumber { get; set; }
    
    /// <summary>
    /// 服务器发送时的UTC时间戳
    /// </summary>
    [ProtoMember(2)]
    public long ServerTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    /// <summary>
    /// 本帧所有玩家的输入集合
    /// 对于1v1场景，通常包含2个元素
    /// </summary>
    [ProtoMember(3)]
    public List<FramePlayerInput> Inputs { get; set; } = new();
    
    /// <summary>
    /// 延迟补偿信息
    /// </summary>
    [ProtoMember(4)]
    public LatencyInfo? LatencyInfo { get; set; }
    
    /// <summary>
    /// 同步标志位
    /// </summary>
    [ProtoMember(5)]
    public SyncFlags? SyncFlags { get; set; }
}

/// <summary>
/// 丢失帧重传请求（客户端 -> 服务器）
/// 当客户端检测到丢包时可主动请求重发
/// </summary>
[ProtoContract]
public class ResendFrameRequest
{
    [ProtoMember(1)]
    public RequestHeader? Header { get; set; }
    
    /// <summary>
    /// 房间ID
    /// </summary>
    [ProtoMember(2)]
    public string RoomId { get; set; } = string.Empty;
    
    /// <summary>
    /// 需要重传的帧号列表
    /// </summary>
    [ProtoMember(3)]
    public List<int> MissingFrameNumbers { get; set; } = new();
}

/// <summary>
/// 重传响应（服务器 -> 客户端）
/// 包含请求的帧同步包数据
/// </summary>
[ProtoContract]
public class ResendFrameResponse
{
    [ProtoMember(1)]
    public ResponseHeader? Header { get; set; }
    
    /// <summary>
    /// 重发的帧同步包列表
    /// </summary>
    [ProtoMember(2)]
    public List<FrameSyncPackage> Frames { get; set; } = new();
}

/// <summary>
/// 帧同步结束通知（服务器 -> 客户端）
/// 对局结束或中断时发送
/// </summary>
[ProtoContract]
public class FrameSyncEndNotification
{
    [ProtoMember(1)]
    public string RoomId { get; set; } = string.Empty;
    
    /// <summary>
    /// 最终帧号
    /// </summary>
    [ProtoMember(2)]
    public int FinalFrameNumber { get; set; }
    
    /// <summary>
    /// 结束原因
    /// </summary>
    [ProtoMember(3)]
    public EndReason EndReason { get; set; }
}
