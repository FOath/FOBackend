# FOBackend 帧同步服务器架构设计 v2.0

> **核心定位**: 通用帧同步中间件 - 与客户端逻辑无关、与游戏引擎无关  
> **适用场景**: 平面射击、动作类等实时对战游戏（1v1）

---

## 一、设计原则与约束

### ✅ 核心设计目标

| 原则 | 说明 |
|------|------|
| **游戏无关性** | 后台不包含任何游戏逻辑代码，只负责帧同步调度 |
| **引擎无关性** | 不依赖 Unity/Unreal/Godot 等，纯 .NET 后端 |
| **输入透明** | 服务端不解析输入内容，仅做字节流转发 |
| **确定性保障** | 保证所有客户端在同一帧号收到相同输入 |
| **低延迟优先** | UDP+KCP 协议，优化至 60 FPS |

### 🎮 适用游戏类型
- **平面射击** (2D Shooter) - 如《魂斗罗》式、弹幕游戏
- **动作类** (Action/Fighting) - 如《街霸》《拳皇》式格斗

### 📊 关键参数

| 参数 | 值 | 说明 |
|------|-----|------|
| **帧率 (FPS)** | 60 | 每秒60帧，帧间隔 ~16.67ms |
| **房间规模** | 2 人 | 1v1 对战 |
| **传输协议** | UDP + KCP | 可靠UDP，低延迟 |
| **序列化协议** | protobuf-net | 高效二进制 |
| **最大延迟容忍** | 200ms | 超过此值体验下降明显 |

---

## 二、整体架构概览

```
┌─────────────────────────────────────────────────────────────────────┐
│                                                                     │
│                        客户端层 (Client Layer)                       │
│   ┌──────────────────────┐         ┌──────────────────────┐        │
│   │     Client A          │         │     Client B          │        │
│   │  (任意游戏引擎)        │◄──────►│  (任意游戏引擎)        │        │
│   │  Unity/Godot/自研...  │  网络    │  Unity/Godot/自研...  │        │
│   └──────────┬───────────┘         └──────────┬───────────┘        │
│              │                                  │                    │
└──────────────┼──────────────────────────────────┼────────────────────┘
               │         UDP + KCP                │
               │      protobuf-net 序列化           │
               └──────────────────────────────────┘
                              │
┌─────────────────────────────▼──────────────────────────────────────┐
│                                                                     │
│                      服务器端 (Server)                               │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                   Transport Layer (传输层)                    │   │
│  │                                                              │   │
│  │   ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐   │   │
│  │   │  KCP Server  │  │  Connection │  │  Heartbeat       │   │   │
│  │   │  (UDP+KCP)   │  │  Manager    │  │  & Timeout      │   │   │
│  │   └─────────────┘  └─────────────┘  └──────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                              │                                      │
│  ┌────────────────────────────▼────────────────────────────────┐   │
│  │                  Protocol Layer (协议层)                       │   │
│  │                                                               │   │
│  │   ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐   │   │
│  │   │  Serializer  │  │  Message    │  │  Packet          │   │   │
│  │   │ (protobuf)   │  │  Router     │  │  Validator       │   │   │
│  │   └─────────────┘  └─────────────┘  └──────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                              │                                      │
│  ┌────────────────────────────▼────────────────────────────────┐   │
│  │              Application Core (应用核心) ⭐                     │   │
│  │                                                               │   │
│  │   ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐   │   │
│  │   │   Session    │  │   Frame     │  │   Player         │   │   │
│  │   │   Manager    │  │   Sync      │  │   Manager        │   │   │
│  │   │   (1v1房間)   │  │   Engine    │  │   (认证/信息)     │   │   │
│  │   └─────────────┘  └─────┬───────┘  └──────────────────┘   │   │
│  │                            │                                  │   │
│  │                   ┌────────▼────────┐                         │   │
│  │                   │  Input Collector│  ← 输入收集器(透明转发) │   │
│  │                   └─────────────────┘                         │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                              │                                      │
│  ┌────────────────────────────▼────────────────────────────────┐   │
│  │              Persistence Layer (持久化层)                      │   │
│  │                                                               │   │
│  │   ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐   │   │
│  │   │   Player     │  │   Match     │  │   Replay         │   │   │
│  │   │   Repository │  │   History   │  │   Recorder       │   │   │
│  │   └─────────────┘  └─────────────┘  └──────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 三、核心模块详解

### 3.1 Transport Layer（传输层）- UDP + KCP

#### **为什么选 KCP？**

| 特性 | TCP | UDP | KCP (我们的选择) |
|------|-----|-----|------------------|
| **可靠性** | ✅ 可靠 | ❌ 不可靠 | ✅ 可靠（ARQ） |
| **延迟** | ❌ 高（拥塞控制） | ✅ 极低 | ✅ 低（可配置） |
| **顺序保证** | ✅ | ❌ | ✅ |
| **丢包恢复** | ✅ 重传 | ❌ 无 | ✅ 快速重传 |
| **适合场景** | 文件/网页 | 视频/语音 | **实时游戏** ✅ |

#### **KCP 配置策略（针对 60 FPS 游戏）**

```csharp
/// <summary>
/// KCP 配置 - 针对 60 FPS 实时对战优化
/// </summary>
public class KcpConfig
{
    /// <summary>
    /// 发送窗口大小（默认32）
    /// 60 FPS 下建议增大以减少阻塞
    /// </summary>
    public int SendWindowSize { get; set; } = 128;
    
    /// <summary>
    /// 接收窗口大小（默认128）
    /// </summary>
    public int ReceiveWindowSize { get; set; } = 128;
    
    /// <summary>
    /// 更新间隔（毫秒），KCP内部时钟频率
    /// 60 FPS 对应 ~16ms，建议设为 10ms 以提高精度
    /// </summary>
    public int UpdateIntervalMs { get; set; } = 10;
    
    /// <summary>
    /// 是否启用快速模式（禁用标准拥塞控制）
    /// 游戏场景推荐开启以降低延迟
    /// </summary>
    public bool NoCongestionWindow { get; set; } = true;
    
    /// <summary>
    /// 最小 RTO（超时重传时间，毫秒）
    /// 默认 200ms，可降低到 30-50ms 加快恢复
    /// </summary>
    public int MinRtoMs { get; set; } = 30;
    
    /// <summary>
    /// 最大重传次数
    /// </summary>
    public int MaxResend { get; set; } = 4;
    
    /// <summary>
    /// 是否启用流控（关闭可提升速度）
    /// </summary>
    public bool DisableFlowControl { get; set; } = true;
}
```

#### **网络库选择：自研纯 C# KCP 实现**

> 项目采用**自研纯 C# KCP 协议栈**，完全兼容 [skywind3000/kcp](https://github.com/skywind3000/kcp) 协议格式
> - 纯 C# 实现，零外部依赖，无需 P/Invoke 或 native 库
> - 完全可控，可针对 60FPS 游戏场景深度优化
> - 内存安全，跨平台（Windows/Linux/容器）
> - 协议格式与 skywind3000/kcp 一致，可对接任何标准 KCP 客户端
> - 代码量约 800 行，可维护性极高

##### **核心组件**

| 文件 | 职责 |
|------|------|
| `KcpCore.cs` | KCP 协议核心（RTO 计算、拥塞窗口、快速重传、ACK 管理） |
| `KcpSession.cs` | UDP + KCP 桥接（单连接管理、输入/输出回调） |
| `KcpServerService.cs` | 服务器监听、握手流程、多会话管理、Update 驱动循环 |
| `KcpConnectionAdapter.cs` | `IGameConnection` 适配器（封装 KCP 为统一连接接口） |

```csharp
// 示例：KCP 服务端启动
public sealed class KcpServerService : IKcpServerService
{
    private readonly KcpConfig _config;
    private readonly ILogger<KcpServerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;

    private UdpClient? _udpClient;
    private long _convCounter;
    private readonly ConcurrentDictionary<uint, KcpSession> _sessions = new();

    public event Func<IGameConnection, Task>? OnNewConnection;

    public KcpServerService(
        IOptions<KcpConfig> config,
        ILogger<KcpServerService> logger,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _config = config.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        var endPoint = new IPEndPoint(
            IPAddress.Parse(_config.ListenAddress),
            _config.Port);

        _udpClient = new UdpClient(endPoint);

        // 启动 UDP 数据接收循环
        _ = ListenLoopAsync(ct);
        // 启动 KCP Update 驱动循环（每 10ms）
        _ = UpdateLoopAsync(ct);

        _logger.LogInformation(
            "🎮 FOBackend KCP Server started on {Address}:{Port}",
            _config.ListenAddress, _config.Port);
    }

    // 握手流程：ClientHello → ServerHello（分配 conv）
    // 后续数据通过 conv 路由到对应 KcpSession
}
```

#### **连接管理接口**

```csharp
/// <summary>
/// 连接抽象接口 - 屏蔽底层传输实现
/// </summary>
public interface IGameConnection : IDisposable
{
    string ConnectionId { get; }
    EndPoint RemoteEndPoint { get; }
    DateTime ConnectedTime { get; }
    ConnectionState State { get; }
    
    // 发送消息
    Task SendAsync<TMessage>(TMessage message, DeliveryMode mode = DeliveryMode.Reliable) 
        where TMessage : class;
    Task SendRawAsync(byte[] data, DeliveryMode mode = DeliveryMode.Reliable);
    Task SendPacketAsync(MessageId messageId, byte[] payload, DeliveryMode mode = DeliveryMode.Reliable);
    
    // 接收事件（属性形式，支持闭包绑定当前连接上下文）
    Func<MessageId, byte[], Task>? OnDataReceived { get; set; }
    Action<string>? OnDisconnected { get; set; }
}

public enum DeliveryMode
{
    Reliable,      // 可靠有序（用于控制命令）
    Unreliable     // 不可靠（用于频繁更新的位置等，可选）
}

public enum ConnectionState
{
    Connecting,    // 握手中
    Connected,     // 已连接
    Authenticated, // 已认证
    InGame,        // 游戏中
    Disconnected   // 已断开
}
```

#### **心跳机制（适配 KCP 特性）**

```csharp
public class HeartbeatManager
{
    /// <summary>
    /// 心跳间隔（毫秒）
    /// KCP 本身有 keepalive，这里做业务层面检测
    /// 建议：5000ms（比 TCP 场景更宽松，因为 KCP 更敏感）
    /// </summary>
    public int HeartbeatIntervalMs { get; set; } = 5000;
    
    /// <summary>
    /// 超时阈值（毫秒）
    /// 建议：15000ms（3次心跳未响应则断开）
    /// </summary>
    public int TimeoutMs { get; set; } = 15000;
    
    // 心跳包结构（极简，减少带宽）
    [ProtoContract]
    public class HeartbeatRequest { }
    
    [ProtoContract]
    public class HeartbeatResponse 
    { 
        [ProtoMember(1)]
        public long ServerTime { get; set; }
        
        [ProtoMember(2)]
        public int CurrentFrameHint { get; set; } // 可选：告知当前帧号
    }
}
```

---

### 3.2 Protocol Layer（协议层）- protobuf-net 定义

#### **协议设计原则**
- ✅ **版本兼容**：字段编号不可变更
- ✅ **前向兼容**：新增字段不影响旧版
- ✅ **最小化开销**：紧凑的二进制格式
- ✅ **游戏无关**：输入数据为 `bytes` 类型，不解析内容

#### **完整 Protobuf 定义**

```protobuf
// ============================================================
// FOBackend Protocol Definition v1.0
// 适用于：平面射击 / 动作类 1v1 帧同步对战
// ============================================================

syntax = "proto3";
package fobackend;

import "google/protobuf/timestamp.proto";

// ==================== 基础类型 ====================

// 请求基础（所有请求都带此头）
message RequestHeader {
    int64 request_id = 1;           // 请求唯一ID（用于去重和追踪）
    int32 version = 2;              // 协议版本号
    google.protobuf.Timestamp timestamp = 3;  // 客户端时间戳
}

// 响应基础
message ResponseHeader {
    int64 request_id = 1;           // 对应的请求ID
    ErrorCode error_code = 2;       // 错误码
    string error_message = 3;       // 错误描述
}

enum ErrorCode {
    SUCCESS = 0;
    UNKNOWN_ERROR = 1000;
    INVALID_REQUEST = 1001;
    NOT_AUTHENTICATED = 1002;
    SESSION_NOT_FOUND = 2000;
    SESSION_FULL = 2001;
    SESSION_ALREADY_STARTED = 2002;
    INVALID_FRAME_NUMBER = 3000;
    INPUT_TIMEOUT = 3001;
    PLAYER_NOT_IN_SESSION = 3002;
    RATE_LIMITED = 4000;
}

// ==================== 认证相关 ====================

// 玩家注册/登录请求
message AuthenticateRequest {
    RequestHeader header = 1;
    string player_name = 2;         // 玩家显示名称
    string client_version = 3;      // 客户端版本号
}

message AuthenticateResponse {
    ResponseHeader header = 1;
    string player_id = 2;           // 服务端分配的唯一ID
    string session_token = 3;       // 会话令牌（后续请求需携带）
}

// ==================== 房间管理 ====================

// 创建房间（1v1）
message CreateRoomRequest {
    RequestHeader header = 1;
    GameMode game_mode = 2;         // 游戏模式
    map<string, string> room_options = 3;  // 自定义选项（如地图ID等）
}

message CreateRoomResponse {
    ResponseHeader header = 1;
    string room_id = 2;             // 房间唯一ID
    string invite_code = 3;         // 邀请码（方便分享）
}

// 加入房间
message JoinRoomRequest {
    RequestHeader header = 1;
    oneof join_method {
        string room_id = 2;         // 通过房间ID加入
        string invite_code = 3;     // 通过邀请码加入
    }
}

message JoinRoomResponse {
    ResponseHeader header = 1;
    RoomInfo room_info = 2;         // 房间详细信息
    repeated PlayerInfo players = 3; // 当前已加入的玩家列表
}

// 离开房间
message LeaveRoomRequest {
    RequestHeader header = 1;
    string room_id = 2;
}

message LeaveRoomResponse {
    ResponseHeader header = 1;
}

// 准备就绪
message ReadyRequest {
    RequestHeader header = 1;
    string room_id = 2;
    bool is_ready = 3;              // true=准备就绪, false=取消准备
}

message ReadyResponse {
    ResponseHeader header = 1;
}

// ==================== 帧同步核心 ⭐ ====================

// 帧同步开始通知（服务器 -> 客户端）
message FrameSyncStartNotification {
    string room_id = 1;
    int64 start_time = 2;           // 开始时的服务器时间戳
    int32 random_seed = 3;          // 随机种子（确保PRNG一致性）
    repeated string player_ids = 4;  // 玩家顺序（决定执行顺序）
    int32 fps = 5;                  // 服务器帧率
    int32 frame_interval_ms = 6;    // 帧间隔（毫秒）
}

// 玩家输入上报（客户端 -> 服务器）⭐ 核心消息
message PlayerInputReport {
    RequestHeader header = 1;
    string room_id = 2;
    int32 frame_number = 3;         // 当前帧号
    bytes input_data = 4;           // ⚠️ 输入数据（二进制，服务端不解析！）
                                    // 格式由客户端自行约定：
                                    // 例如：[按键掩码][摇杆X][摇杆Y][自定义...]
    int32 input_checksum = 5;       // 输入校验和（CRC16/32，防篡改）
}

// 帧同步包广播（服务器 -> 客户端）⭐ 核心消息
message FrameSyncPackage {
    int32 frame_number = 1;         // 当前帧号
    int64 server_time = 2;          // 服务器时间戳
    repeated FramePlayerInput inputs = 3;  // 所有玩家的输入
    
    // 延迟补偿信息（帮助客户端调整预测）
    LatencyInfo latency_info = 4;
    
    // 同步状态标记
    SyncFlags sync_flags = 5;
}

// 单个玩家的输入
message FramePlayerInput {
    string player_id = 1;           // 玩家ID
    int32 frame_number = 2;         // 帧号
    bytes input_data = 3;           // 输入数据（原样转发，不修改）
    int32 input_checksum = 4;       // 校验和（原样转发）
}

// 延迟补偿辅助信息
message LatencyInfo {
    int32 max_rtt_ms = 1;           // 当前房间内最高RTT
    int32 recommended_buffer_frames = 2;  // 建议客户端缓冲帧数
    bool is_lagging = 3;            // 是否有玩家卡顿
    string lagging_player_id = 4;   // 卡顿的玩家ID（如果有）
}

// 同步标志位
message SyncFlags {
    bool is_key_frame = 1;          // 是否为关键帧（如每秒第1帧）
    bool force_resync = 2;          // 是否强制重新同步（异常恢复）
    int32 resync_target_frame = 3;  // 重同步目标帧号
}

// 请求丢失帧重传
message ResendFrameRequest {
    RequestHeader header = 1;
    string room_id = 2;
    repeated int32 missing_frame_numbers = 3;  // 丢失的帧号列表
}

message ResendFrameResponse {
    ResponseHeader header = 1;
    repeated FrameSyncPackage frames = 2;  // 重发的帧数据
}

// 帧同步结束通知
message FrameSyncEndNotification {
    string room_id = 1;
    int32 final_frame_number = 2;  // 最终帧号
    EndReason end_reason = 3;      // 结束原因
}

enum EndReason {
    NORMAL_FINISH = 0;             // 正常结束
    PLAYER_DISCONNECT = 1;         // 玩家断线
    SERVER_SHUTDOWN = 2;           // 服务器关闭
}

// ==================== 数据模型 ====================

// 游戏模式枚举
enum GameMode {
    UNKNOWN = 0;
    SHOOTER_1V1 = 1;               // 平面射击 1v1
    FIGHTING_1V1 = 2;              // 格斗/动作 1v1
    CUSTOM = 99;                   // 自定义模式
}

// 房间信息
message RoomInfo {
    string room_id = 1;
    GameMode game_mode = 2;
    RoomStatus status = 3;
    int32 max_players = 4;         // 固定为2
    int32 current_player_count = 5;
    string host_player_id = 6;
    google.protobuf.Timestamp create_time = 7;
    map<string, string> options = 8;  // 创建时的自定义选项
}

enum RoomStatus {
    WAITING = 0;       // 等待玩家
    READY = 1;         // 所有玩家已准备
    PLAYING = 2;       // 游戏中
    FINISHED = 3;      // 已结束
}

// 玩家信息
message PlayerInfo {
    string player_id = 1;
    string player_name = 2;
    bool is_ready = 3;
    int32 ping_ms = 4;              // 当前延迟（动态更新）
    DateTime join_time = 5;
}

// ==================== 事件推送 ====================

// 房间状态变化通知
message RoomStatusChangedNotification {
    string room_id = 1;
    RoomStatus new_status = 2;
    string trigger_player_id = 3;   // 触发者（离开/准备等）
}

// 玩家加入/离开通知
message PlayerJoinedNotification {
    string room_id = 1;
    PlayerInfo player = 2;
}

message PlayerLeftNotification {
    string room_id = 1;
    string player_id = 2;
    LeaveReason reason = 3;
}

enum LeaveReason {
    NORMAL_LEAVE = 0;
    DISCONNECTED = 1;
    KICKED = 2;
    TIMEOUT = 3;
}
```

#### **消息路由系统**

```csharp
/// <summary>
/// 消息 ID 枚举（对应 Protobuf 消息类型）
/// </summary>
public enum MessageId : ushort
{
    // ======== 认证 (0x0001-0x001F) ========
    AuthRequest             = 0x0001,
    AuthResponse            = 0x0002,
    
    // ======== 心跳 (0x0020-0x002F) ========
    HeartbeatRequest        = 0x0020,
    HeartbeatResponse       = 0x0021,
    
    // ======== 房间管理 (0x0100-0x01FF) ========
    CreateRoomRequest       = 0x0100,
    CreateRoomResponse      = 0x0101,
    JoinRoomRequest         = 0x0102,
    JoinRoomResponse        = 0x0103,
    LeaveRoomRequest        = 0x0104,
    LeaveRoomResponse       = 0x0105,
    ReadyRequest            = 0x0106,
    ReadyResponse           = 0x0107,
    
    // ======== 帧同步核心 (0x0200-0x02FF) ⭐ ========
    FrameSyncStart          = 0x0200,   // 服务器->客户端：开始帧同步
    PlayerInputReport       = 0x0201,   // 客户端->服务器：上报输入
    FrameSyncPackage        = 0x0202,   // 服务器->客户端：广播同步包
    ResendFrameRequest      = 0x0203,   // 客户端->服务器：请求重传
    ResendFrameResponse     = 0x0204,   // 服务器->客户端：重传响应
    FrameSyncEnd            = 0x0205,   // 服务器->客户端：结束帧同步
    
    // ======== 通知事件 (0x0300-0x03FF) ========
    RoomStatusChanged       = 0x0300,
    PlayerJoined            = 0x0301,
    PlayerLeft              = 0x0302,
}

/// <summary>
/// 消息处理器接口
/// </summary>
public interface IMessageHandler<in TMessage> where TMessage : IMessage
{
    Task HandleAsync(TMessage message, IGameConnection connection, CancellationToken ct);
}

/// <summary>
/// 消息路由器
/// </summary>
public interface IMessageRouter
{
    void RegisterHandler<TMessage>(IMessageHandler<TMessage> handler) where TMessage : IMessage;
    Task RouteAsync(MessageId messageId, byte[] payload, IGameConnection connection);
}
```

---

### 3.3 Application Core（应用核心）⭐ 最重要

#### **3.3.1 SessionManager - 房间管理（1v1专用）**

```csharp
/// <summary>
/// 房间状态机（针对1v1优化）
/// </summary>
public enum SessionState
{
    Waiting,    // 等待第2名玩家加入
    Ready,      // 双方都已准备
    Playing,    // 帧同步进行中
    Finished    // 对局结束
}

/// <summary>
/// 游戏会话（房间）- 1v1 对战单元
/// </summary>
public class GameSession : IDisposable
{
    public string SessionId { get; init; }
    public GameMode Mode { get; init; }
    public SessionState State { get; private set; } = SessionState.Waiting;
    
    // 玩家（固定2个位置）
    public PlayerSlot Player1 { get; private set; }  // 房主
    public PlayerSlot Player2 { get; private set; }  // 加入者
    
    public DateTime CreateTime { get; init; }
    public DateTime? StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }
    
    // 帧同步引擎引用
    public FrameSyncEngine? FrameSyncEngine { get; private set; }
    
    // 事件
    public event Action<GameSession, SessionState, SessionState>? OnStateChanged;
    public event Action<GameSession, PlayerSlot>? OnPlayerJoined;
    public event Action<GameSession, string>? OnPlayerLeft;
    
    /// <summary>
    /// 加入玩家（返回槽位号：1或2）
    /// </summary>
    public (int slot, JoinResult result) AddPlayer(PlayerInfo player)
    {
        if (State == SessionState.Playing)
            return (0, JoinResult.AlreadyStarted);
        
        if (Player1?.PlayerId == player.PlayerId || Player2?.PlayerId == player.PlayerId)
            return (0, JoinResult.AlreadyInRoom);
        
        if (Player1 == null)
        {
            Player1 = new PlayerSlot(player, 1);
            OnPlayerJoined?.Invoke(this, Player1);
            return (1, JoinResult.Success);
        }
        
        if (Player2 == null)
        {
            Player2 = new PlayerSlot(player, 2);
            OnPlayerJoined?.Invoke(this, Player2);
            
            if (State == SessionState.Waiting)
            {
                TransitionTo(SessionState.Ready);  // 自动转为Ready
            }
            return (2, JoinResult.Success);
        }
        
        return (0, JoinResult.RoomFull);
    }
    
    /// <summary>
    /// 移除玩家
    /// </summary>
    public void RemovePlayer(string playerId)
    {
        var slot = GetPlayerSlot(playerId);
        if (slot == null) return;
        
        if (slot.SlotNumber == 1) Player1 = null;
        else Player2 = null;
        
        OnPlayerLeft?.Invoke(this, playerId);
        
        if (State == SessionState.Playing)
        {
            // 游戏中断处理
            FrameSyncEngine?.Stop(EndReason.PlayerDisconnect);
            TransitionTo(SessionState.Finished);
        }
        else if (Player1 == null && Player2 == null)
        {
            TransitionTo(SessionState.Waiting);  // 空房间回归Waiting
        }
        else
        {
            TransitionTo(SessionState.Waiting);  // 有人离开，等待新人
        }
    }
    
    /// <summary>
    /// 设置玩家准备状态（双方都Ready后可开始）
    /// </summary>
    public SetReadyResult SetReady(string playerId, bool ready)
    {
        var slot = GetPlayerSlot(playerId);
        if (slot == null) return SetReadyResult.PlayerNotFound;
        
        slot.IsReady = ready;
        
        if (Player1?.IsReady == true && Player2?.IsReady == true && State == SessionState.Ready)
        {
            // 双方都准备好了！可以开始帧同步
            StartFrameSync();
        }
        
        return SetReadyResult.Success;
    }
    
    private void StartFrameSync()
    {
        TransitionTo(SessionState.Playing);
        StartTime = DateTime.UtcNow;
        
        // 初始化帧同步引擎
        FrameSyncEngine = new FrameSyncEngine(
            sessionId: SessionId,
            playerIds: new[] { Player1!.PlayerId, Player2!.PlayerId },
            connections: new[] { Player1.Connection!, Player2.Connection! },
            config: new FrameSyncConfig());
        
        FrameSyncEngine.Start();
    }
    
    // ... 其他方法省略 ...
}

public enum JoinResult { Success, AlreadyInRoom, RoomFull, AlreadyStarted }
public enum SetReadyResult { Success, PlayerNotFound }

/// <summary>
/// 玩家槽位（包含连接引用）
/// </summary>
public class PlayerSlot
{
    public string PlayerId { get; }
    public string PlayerName { get; }
    public int SlotNumber { get; }  // 1 or 2
    public bool IsReady { get; set; }
    public IGameConnection? Connection { get; set; }  // 网络连接引用
    public RollingAverage PingTracker { get; } = new(windowSize: 20);
    
    public PlayerSlot(PlayerInfo info, int slotNumber)
    {
        PlayerId = info.PlayerId;
        PlayerName = info.PlayerName;
        SlotNumber = slotNumber;
    }
}
```

#### **3.3.2 FrameSyncEngine - 帧同步引擎（60 FPS 核心）⭐⭐⭐**

这是整个系统的**最核心组件**。它实现了 Lockstep 算法。

```csharp
/// <summary>
/// 帧同步引擎 - 实现 Lockstep 确定性帧同步
/// 核心职责：
/// 1. 按 60 FPS 固定节拍运行
/// 2. 收集两个玩家的输入
/// 3. 广播同步包（确保两客户端同时执行）
/// 4. 处理延迟补偿和异常恢复
/// </summary>
public class FrameSyncEngine : IDisposable
{
    #region 配置参数（60 FPS 优化）
    
    private readonly FrameSyncConfig _config;
    public class FrameSyncConfig
    {
        /// <summary>
        /// 目标帧率 - 60 FPS
        /// </summary>
        public int TargetFPS { get; set; } = 60;
        
        /// <summary>
        /// 帧间隔（毫秒）≈ 16.666ms
        /// </summary>
        public double FrameIntervalMs => 1000.0 / TargetFPS;
        
        /// <summary>
        /// 单帧最大收集输入的超时时间
        /// 如果某玩家在此时限内没发送输入，跳过其输入或暂停该玩家
        /// 推荐：100-200ms（约6-12帧）
        /// </summary>
        public int InputCollectTimeoutMs { get; set; } = 150;
        
        /// <summary>
        /// 历史输入缓存帧数（用于丢包重传）
        /// </summary>
        public int HistoryBufferSize { get; set; } = 300;  // 约5秒
        
        /// <summary>
        /// 关键帧间隔（每隔多少帧发送一次完整快照提示）
        /// 用于异常恢复点
        /// </summary>
        public int KeyFrameInterval { get; set; } = 60;  // 每1秒一个关键帧
        
        /// <summary>
        /// 最大允许的 RTT 差异（超过则触发警告/降速）
        /// </summary>
        public int MaxRttDiffMs { get; set; } = 100;
        
        /// <summary>
        /// 是否启用自适应帧率（网络差时自动降帧）
        /// </summary>
        public bool EnableAdaptiveFps { get; set; } = false;
    }
    
    #endregion

    #region 状态
    
    public string SessionId { get; }
    public string[] PlayerIds { get; }
    public IGameConnection[] Connections { get; }
    
    private int _currentFrame = 0;
    public int CurrentFrame => _currentFrame;
    
    public EngineState State { get; private set; } = EngineState.Stopped;
    
    // 输入缓冲区（按帧号索引）
    private readonly ConcurrentDictionary<int, FrameInputs> _inputBuffer = new();
    
    // 历史缓存（用于重传）
    private readonly CircularBuffer<FrameSyncPackage> _historyCache;
    
    // RTT 追踪
    private readonly Dictionary<string, RollingAverage> _playerRtt;
    
    // 定时器
    private PeriodicTimer? _frameTimer;
    private CancellationTokenSource? _cts;
    
    #endregion

    #region 公共方法
    
    public FrameSyncEngine(
        string sessionId,
        string[] playerIds,
        IGameConnection[] connections,
        FrameSyncConfig config)
    {
        Debug.Assert(playerIds.Length == 2, "Only support 1v1");
        Debug.Assert(connections.Length == 2, "Need exactly 2 connections");
        
        SessionId = sessionId;
        PlayerIds = playerIds;
        Connections = connections;
        _config = config;
        _historyCache = new CircularBuffer<FrameSyncPackage>(config.HistoryBufferSize);
        _playerRtt = playerIds.ToDictionary(
            id => id, 
            _ => new RollingAverage(windowSize: 30));
    }
    
    /// <summary>
    /// 启动帧同步引擎
    /// </summary>
    public async Task StartAsync(CancellationToken externalCt = default)
    {
        if (State != EngineState.Stopped)
            throw new InvalidOperationException("Engine already running");
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        State = EngineState.Running;
        
        // 生成随机种子（双方客户端必须相同）
        var randomSeed = Random.Shared.Next();
        
        // 1. 向两个客户端发送 FrameSyncStartNotification
        var startNotification = new FrameSyncStartNotification
        {
            RoomId = SessionId,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RandomSeed = randomSeed,
            PlayerIds = { PlayerIds },
            Fps = _config.TargetFPS,
            FrameIntervalMs = (int)_config.FrameIntervalMs
        };
        
        await Task.WhenAll(
            Connections[0].SendAsync(startNotification),
            Connections[1].SendAsync(startNotification));
        
        // 2. 启动帧循环定时器
        _frameTimer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_config.FrameIntervalMs));
        
        _ = RunFrameLoopAsync(_cts.Token);  // Fire and forget（实际应监控）
        
        Logger.LogInformation("FrameSync started for session {Session}, FPS={FPS}", 
            SessionId, _config.TargetFPS);
    }
    
    /// <summary>
    /// 接收玩家输入（由 Protocol Layer 调用）
    /// </summary>
    public async Task ReceivePlayerInputAsync(PlayerInputReport inputReport, IGameConnection fromConn)
    {
        var playerId = /* 从认证信息获取 */;
        var frameNumber = inputReport.FrameNumber;
        var inputData = inputReport.InputData;
        
        // 1. 验证帧号合法性
        if (!IsValidFrameNumber(frameNumber))
        {
            Logger.LogWarning("Invalid frame number {Frame} from {Player}", frameNumber, playerId);
            return;
        }
        
        // 2. 校验输入完整性
        if (!VerifyChecksum(inputData, inputReport.InputChecksum))
        {
            Logger.LogWarning("Input checksum mismatch for player {Player} at frame {Frame}",
                playerId, frameNumber);
            // 可选：踢出作弊玩家
            return;
        }
        
        // 3. 存入输入缓冲区
        var frameInputs = _inputBuffer.GetOrAdd(frameNumber, _ => new FrameInputs(_config));
        frameInputs.SetInput(playerId, inputData, inputReport.InputChecksum);
        
        // 4. 更新 RTT（基于帧号的延迟估算）
        UpdateRttEstimate(playerId, frameNumber);
        
        // 5. 检查是否本帧所有输入已到齐
        if (frameInputs.IsComplete())
        {
            // 输入齐了，立即广播（不等下一帧tick）
            await BroadcastFramePackageAsync(frameNumber);
        }
    }
    
    /// <summary>
    /// 处理重传请求
    /// </summary>
    public async Task<ResendFrameResponse> HandleResendRequestAsync(ResendFrameRequest request)
    {
        var response = new ResendFrameResponse();
        foreach (var frameNum in request.MissingFrameNumbers)
        {
            if (_historyCache.TryGet(frameNum, out var package))
            {
                response.Frames.Add(package);
            }
        }
        return response;
    }
    
    /// <summary>
    /// 停止引擎
    /// </summary>
    public async Task StopAsync(EndReason reason = EndReason.NormalFinish)
    {
        if (State != EngineState.Running) return;
        
        State = EngineState.Stopping;
        _cts?.Cancel();
        
        // 通知客户端结束
        var endNotification = new FrameSyncEndNotification
        {
            RoomId = SessionId,
            FinalFrameNumber = _currentFrame,
            EndReason = reason
        };
        
        try
        {
            await Task.WhenAll(
                Connections[0].SendAsync(endNotification),
                Connections[1].SendAsync(endNotification));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error sending end notification");
        }
        
        State = EngineState.Stopped;
        Dispose();
    }
    
    #endregion

    #region 私有核心方法
    
    /// <summary>
    /// 主帧循环 - 60 FPS 心跳
    /// </summary>
    private async Task RunFrameLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var frameCount = 0;
        
        while (await _frameTimer!.WaitForNextTickAsync(ct))
        {
            var frameStart = sw.ElapsedTicks;
            
            try
            {
                await ProcessFrame(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing frame {Frame}", _currentFrame);
            }
            
            // 性能监控
            frameCount++;
            var frameCostMs = (sw.ElapsedTicks - frameStart) / (double)Stopwatch.Frequency * 1000;
            
            if (frameCount % 60 == 0)  // 每秒输出一次统计
            {
                Logger.LogDebug("Frame {_CurrentFrame}: cost={Cost:F2}ms", 
                    _currentFrame, frameCostMs);
                
                if (frameCostMs > _config.FrameIntervalMs)
                {
                    Logger.LogWarning("Frame overrun! Cost={Cost:F2}ms > Budget={Budget:F2}ms",
                        frameCostMs, _config.FrameIntervalMs);
                }
            }
        }
    }
    
    /// <summary>
    /// 处理单帧逻辑
    /// </summary>
    private async Task ProcessFrame(CancellationToken ct)
    {
        Interlocked.Increment(ref _currentFrame);
        var frameNum = _currentFrame;
        
        // 1. 检查本帧输入是否已收集完成
        // （可能在 ReceivePlayerInputAsync 中已经提前广播过了）
        if (_inputBuffer.TryGetValue(frameNum, out var frameInputs) && frameInputs.IsComplete())
        {
            // 输入已在 Receive 时广播过，跳过
            _inputBuffer.TryRemove(frameNum, out _);
            return;
        }
        
        // 2. 检查超时（某玩家未按时发送输入）
        if (!_inputBuffer.ContainsKey(frameNum))
        {
            _inputBuffer[frameNum] = new FrameInputs(_config);
        }
        
        // 等待一小段时间（最多 InputCollectTimeoutMs）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_config.InputCollectTimeoutMs));
        
        try
        {
            // 使用 SpinWait + Token 轮询，避免阻塞线程
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                if (_inputBuffer.TryGetValue(frameNum, out var inputs) && inputs.IsComplete())
                {
                    break;
                }
                await Task.Delay(1, timeoutCts.Token);  // 1ms 轮询间隔
            }
        }
        catch (OperationCanceledException)
        {
            // 超时，部分玩家可能掉线或高延迟
            Logger.LogWarning("Frame {Frame} input collection timed out", frameNum);
        }
        
        // 3. 广播帧同步包（无论是否全员到齐）
        await BroadcastFramePackageAsync(frameNum);
    }
    
    /// <summary>
    /// 广播帧同步包给所有玩家 ⭐ 核心操作
    /// </summary>
    private async Task BroadcastFramePackageAsync(int frameNumber)
    {
        if (!_inputBuffer.TryRemove(frameNumber, out var frameInputs))
        {
            frameInputs = new FrameInputs(_config);
        }
        
        // 构建同步包
        var syncPackage = new FrameSyncPackage
        {
            FrameNumber = frameNumber,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SyncFlags = new SyncFlags
            {
                IsKeyFrame = (frameNumber % _config.KeyFrameInterval == 0)
            },
            LatencyInfo = BuildLatencyInfo()
        };
        
        // 收集所有玩家的输入
        foreach (var playerId in PlayerIds)
        {
            var input = frameInputs.GetInput(playerId);
            if (input != null)
            {
                syncPackage.Inputs.Add(new FramePlayerInput
                {
                    PlayerId = playerId,
                    FrameNumber = frameNumber,
                    InputData = input.Data,
                    InputChecksum = input.Checksum
                });
            }
            else
            {
                // 该玩家本帧无输入（可能是掉线或高延迟）
                // 可以发送空输入或上一帧的输入（取决于游戏容忍度）
                Logger.LogDebug("Missing input from player {Player} at frame {Frame}",
                    playerId, frameNumber);
                
                syncPackage.LatencyInfo.IsLagging = true;
                syncPackage.LatencyInfo.LaggingPlayerId = playerId;
            }
        }
        
        // 缓存到历史（用于重传）
        _historyCache.Add(frameNumber, syncPackage);
        
        // 并行发送给两个客户端
        try
        {
            await Task.WhenAll(
                Connections[0].SendAsync(syncPackage),
                Connections[1].SendAsync(syncPackage));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error broadcasting frame {Frame}", frameNumber);
            // 处理发送失败（可能是某个客户端断开）
        }
    }
    
    /// <summary>
    /// 构建延迟信息
    /// </summary>
    private LatencyInfo BuildLatencyInfo()
    {
        var rtts = _playerRtt.Values.Select(r => r.Average).ToList();
        return new LatencyInfo
        {
            MaxRttMs = (int)(rtts.DefaultIfEmpty(0).Max()),
            RecommendedBufferFrames = CalculateRecommendedBuffer(rtts.Max()),
            IsLagging = rtts.Max() - rtts.Min() > _config.MaxRttDiffMs
        };
    }
    
    private int CalculateRecommendedBuffer(double maxRtt)
    {
        // 根据RTT计算建议的客户端缓冲帧数
        // 例：100ms RTT / 16.67ms per frame ≈ 6 帧
        return Math.Max(2, (int)Math.Ceiling(maxRtt / _config.FrameIntervalMs));
    }
    
    private void UpdateRttEstimate(string playerId, int frameNumber)
    {
        // 基于帧号差异和接收时间估算RTT
        // 具体算法可根据实际情况调整
        if (_playerRtt.TryGetValue(playerId, out var tracker))
        {
            // 简化的RTT估算（实际应结合心跳或ACK机制）
            var estimatedRtt = Math.Abs(DateTime.UtcNow.Ticks % 10000);  // 占位
            tracker.Update(estimatedRtt);
        }
    }
    
    private bool IsValidFrameNumber(int frameNumber)
    {
        // 帧号必须在合理范围内（防止作弊或重放攻击）
        // 例如：不允许超过当前帧号太多（±5帧容差）
        var diff = frameNumber - _currentFrame;
        return diff >= -2 && diff <= 10;  // 允许稍微超前或滞后几帧
    }
    
    private static bool VerifyChecksum(byte[] data, int expectedChecksum)
    {
        // CRC16/32 校验
        var computed = Crc32.Compute(data);
        return computed == expectedChecksum;
    }
    
    #endregion

    public void Dispose()
    {
        _frameTimer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public enum EngineState
{
    Stopped,
    Running,
    Stopping,
    Error
}

/// <summary>
/// 单帧输入集合
/// </summary>
internal class FrameInputs
{
    private readonly Dictionary<string, PlayerInputEntry> _inputs = new(2);
    private readonly int _expectedCount;
    private volatile int _receivedCount = 0;
    
    public FrameInputs(FrameSyncConfig config)
    {
        _expectedCount = 2;  // 1v1 固定2人
    }
    
    public void SetInput(string playerId, byte[] data, int checksum)
    {
        lock (_inputs)
        {
            if (_inputs.ContainsKey(playerId)) return;  // 防重复
            
            _inputs[playerId] = new PlayerInputEntry(data, checksum);
            Interlocked.Increment(ref _receivedCount);
        }
    }
    
    public PlayerInputEntry? GetInput(string playerId)
    {
        lock (_inputs)
        {
            return _inputs.GetValueOrDefault(playerId);
        }
    }
    
    public bool IsComplete() => Volatile.Read(ref _receivedCount) >= _expectedCount;
}

internal record PlayerInputEntry(byte[] Data, int Checksum);

/// <summary>
/// 环形缓冲区（用于历史帧缓存）
/// </summary>
internal class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private int _head;
    
    public CircularBuffer(int sizePowerOf2)
    {
        // 大小必须是2的幂，便于位运算取模
        var size = 1;
        while (size < sizePowerOf2) size <<= 1;
        _buffer = new T[size];
        _mask = size - 1;
    }
    
    public void Add(int index, T item)
    {
        _buffer[index & _mask] = item;
    }
    
    public bool TryGet(int index, [NotNullWhen(true)] out T? item)
    {
        item = _buffer[index & _mask];
        return item != null;
    }
}
```

#### **3.3.3 PlayerManager - 玩家管理**

```csharp
/// <summary>
/// 玩家管理 - 极简实现（仅需姓名）
/// </summary>
public class PlayerManager
{
    private readonly ConcurrentDictionary<string, PlayerProfile> _players = new();
    private readonly IPlayerRepository _repository;  // 持久化（SQLite/PostgreSQL）
    
    public async Task<PlayerProfile> AuthenticateAsync(string playerName, string clientVersion)
    {
        // 1. 生成或获取玩家ID
        var playerId = GeneratePlayerId(playerName);
        
        // 2. 从数据库加载或创建档案
        var profile = await _repository.GetOrCreateAsync(playerId, playerName);
        
        // 3. 更新最后登录时间
        profile.LastLoginTime = DateTime.UtcNow;
        profile.LastClientVersion = clientVersion;
        
        // 4. 生成会话令牌
        profile.SessionToken = GenerateToken();
        
        _players[playerId] = profile;
        return profile;
    }
    
    public PlayerProfile? GetPlayer(string playerId)
    {
        return _players.GetValueOrDefault(playerId);
    }
    
    public bool ValidateToken(string playerId, string token)
    {
        return _players.TryGetValue(playerId, out var p) && p.SessionToken == token;
    }
}

public class PlayerProfile
{
    public string PlayerId { get; init; }
    public string PlayerName { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime LastLoginTime { get; set; }
    public string? LastClientVersion { get; set; }
    public string SessionToken { get; set; } = "";
    public int TotalGamesPlayed { get; set; }
    public int TotalWins { get; set; }
}
```

---

### 3.4 Persistence Layer（持久化层）

#### **数据库 Schema（轻量级）**

由于只需要存储玩家基本信息，推荐使用 **SQLite**（开发阶段）或 **PostgreSQL**（生产环境）。

```sql
-- 玩家表（极简设计）
CREATE TABLE IF NOT EXISTS players (
    player_id VARCHAR(36) PRIMARY KEY,      -- UUID
    player_name VARCHAR(64) NOT NULL,        -- 显示名称
    created_at TIMESTAMPTZ DEFAULT NOW(),
    last_login_at TIMESTAMPTZ,
    last_client_version VARCHAR(32),
    total_games_played INT DEFAULT 0,
    total_wins INT DEFAULT 0
);

-- 对局记录表（用于回放和统计）
CREATE TABLE IF NOT EXISTS match_history (
    match_id VARCHAR(36) PRIMARY KEY,
    room_id VARCHAR(36) NOT NULL,
    game_mode INT NOT NULL,                 -- GameMode 枚举值
    player1_id VARCHAR(36) REFERENCES players(player_id),
    player2_id VARCHAR(36) REFERENCES players(player_id),
    winner_id VARCHAR(36) REFERENCES players(player_id),  -- NULL表示平局/中断
    total_frames INT,                       -- 总帧数
    duration_seconds FLOAT,
    end_reason INT,                         -- EndReason 枚举值
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 输入回放数据（可选：存储每帧输入用于复盘）
CREATE TABLE IF NOT EXISTS replay_data (
    replay_id VARCHAR(36) PRIMARY KEY REFERENCES match_history(match_id),
    frame_number INT NOT NULL,
    player_id VARCHAR(36) NOT NULL,
    input_data BYTEA NOT NULL,              -- 原始输入二进制
    PRIMARY KEY (replay_id, frame_number, player_id)
);

-- 索引
CREATE INDEX idx_players_name ON players(player_name);
CREATE INDEX idx_match_history_player ON match_history(player1_id, player2_id);
CREATE INDEX idx_match_history_created ON match_history(created_at DESC);
```

#### **Repository 实现**

```csharp
public interface IPlayerRepository
{
    Task<PlayerProfile?> GetByIdAsync(string playerId);
    Task<PlayerProfile> GetOrCreateAsync(string playerId, string playerName);
    Task UpdateAsync(PlayerProfile profile);
}

public interface IMatchHistoryRepository
{
    Task SaveMatchResultAsync(MatchRecord record);
    Task<IReadOnlyList<MatchRecord>> GetPlayerHistoryAsync(string playerId, int limit = 20);
    Task<ReplayData?> GetReplayDataAsync(string matchId);
}

// Dapper 实现（高性能 ORM 替代）
public class SqlitePlayerRepository : IPlayerRepository
{
    private readonly IDbConnection _db;
    
    public SqlitePlayerRepository(string connectionString)
    {
        _db = new SqliteConnection(connectionString);
        _db.Open();
        InitializeSchema();
    }
    
    public async Task<PlayerProfile> GetOrCreateAsync(string playerId, string playerName)
    {
        var existing = await _db.QueryFirstOrDefaultAsync<PlayerProfile>(
            "SELECT * FROM players WHERE player_id = @PlayerId", new { PlayerId = playerId });
        
        if (existing != null)
        {
            existing.PlayerName = playerName;  // 允许改名？
            await _db.ExecuteAsync(
                "UPDATE players SET player_name = @Name, last_login_at = @Now WHERE player_id = @Id",
                new { Name = playerName, Now = DateTime.UtcNow, Id = playerId });
            return existing;
        }
        
        var profile = new PlayerProfile
        {
            PlayerId = playerId,
            PlayerName = playerName,
            CreateTime = DateTime.UtcNow,
            LastLoginTime = DateTime.UtcNow
        };
        
        await _db.ExecuteAsync(@"
            INSERT INTO players (player_id, player_name, created_at, last_login_at) 
            VALUES (@PlayerId, @PlayerName, @Created, @LastLogin)", profile);
        
        return profile;
    }
    
    // ... 其他方法实现 ...
}
```

---

## 四、技术栈最终选型

| 层级 | 技术 | 版本 | 选型理由 |
|------|------|------|----------|
| **运行时** | .NET 8 LTS | 8.0.x | 长期支持、高性能、跨平台 |
| **网络传输** | UDP + KCP | 自研纯C#实现 | 零依赖、兼容skywind3000/kcp、低延迟 |
| **序列化** | protobuf-net | 3.x | 极致性能、体积小、schema演进友好 |
| **日志** | Serilog | 3.x | 结构化日志、多目标输出 |
| **数据库** | SQLite → PostgreSQL | - | 开发简单、生产可切换 |
| **ORM** | Dapper | 2.x | 微ORM、极致性能 |
| **IOC容器** | Microsoft.Extensions.DependencyInjection | 内置 | 官方、零依赖 |
| **配置** | Microsoft.Extensions.Options | 内置 | 类型安全配置绑定 |
| **监控** | OpenTelemetry | 1.x | 标准化可观测性 |
| **单元测试** | xUnit + Moq | 最新 | 行业标准 |
| **容器化** | Docker | 最新 | 统一部署环境 |

### NuGet 包清单

```xml
<!-- 项目文件核心依赖 -->
<ItemGroup>
    <!-- 核心框架 -->
    <PackageReference Include="Microsoft.NET.Sdk" Version="8.0.*" />
    
    <!-- 网络层：自研纯C# KCP实现，无外部NuGet依赖 -->
    
    <!-- 序列化 -->
    <PackageReference Include="protobuf-net" Version="3.*" />
    
    <!-- 日志 -->
    <PackageReference Include="Serilog" Version="3.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="5.*" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.*" />
    
    <!-- 数据库 -->
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
    <PackageReference Include="Dapper" Version="2.*" />
    <PackageReference Include="Npgsql" Version="8.*" />  <!-- PostgreSQL（生产） -->
    
    <!-- 监控 -->
    <PackageReference Include="OpenTelemetry" Version="1.*" />
    <PackageReference Include="OpenTelemetry.Exporter.Prometheus" Version="1.*" />
    
    <!-- 工具库 -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="8.*" />
</ItemGroup>

<!-- 测试 -->
<ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="Moq" Version="4.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.*" />
</ItemGroup>
```

---

## 五、项目目录结构（最终版）

```
FOBackend/
├── src/
│   ├── FOBackend.sln                          # 解决方案文件
│   │
│   ├── FOBackend.Protocol/                     # 🔷 协议定义层（独立项目，可共享给客户端）
│   │   ├── Protos/
│   │   │   ├── base.proto                     # 基础类型、错误码
│   │   │   ├── auth.proto                     # 认证相关
│   │   │   ├── room.proto                     # 房间管理
│   │   │   ├── frame_sync.proto               # ⭐ 帧同步核心
│   │   │   └── events.proto                  # 事件通知
│   │   ├── Messages/                          # 生成的C#消息类（或直接用Attribute方式）
│   │   │   ├── BaseMessages.cs
│   │   │   ├── AuthMessages.cs
│   │   │   ├── RoomMessages.cs
│   │   │   ├── FrameSyncMessages.cs           # ⭐
│   │   │   └── EventMessages.cs
│   │   ├── MessageId.cs                       # 消息ID枚举
│   │   ├── Extensions/
│   │   │   ├── ProtoSerializer.cs             # protobuf-net 序列化扩展
│   │   │   └── PacketBuilder.cs               # 组包/拆包工具
│   │   └── FOBackend.Protocol.csproj
│   │
│   ├── FOBackend.Transport/                   # 🔶 传输层（KCP封装）
│   │   ├── Kcp/
│   │   │   ├── KcpCore.cs                     # ⭐ KCP 协议核心（纯C#实现）
│   │   │   ├── KcpSession.cs                  # UDP + KCP 会话桥接
│   │   │   ├── KcpServerService.cs            # KCP 服务端（监听+握手+Update驱动）
│   │   │   ├── KcpConnectionAdapter.cs        # 连接适配器（实现IGameConnection）
│   │   │   └── KcpTypes.cs                    # KCP 配置与接口定义
│   │   ├── Connection/
│   │   │   ├── IGameConnection.cs             # 连接抽象接口
│   │   │   ├── ConnectionManager.cs           # 全局连接管理
│   │   │   ├── ConnectionState.cs             # 连接状态枚举
│   │   │   └── ConnectionPool.cs              # 对象池
│   │   ├── Security/
│   │   │   ├── HeartbeatManager.cs            # 心跳管理
│   │   │   ├── RateLimiter.cs                 # 流量限制
│   │   │   └── IpFilter.cs                    # IP过滤（可选）
│   │   └── FOBackend.Transport.csproj
│   │
│   ├── FOBackend.Core/                        # 🔴 应用核心层 ⭐⭐⭐
│   │   ├── Sessions/
│   │   │   ├── ISessionManager.cs             # 房间管理接口
│   │   │   ├── SessionManagerImpl.cs          # 实现（内存）
│   │   │   ├── GameSession.cs                 # 游戏会话实体
│   │   │   ├── SessionState.cs                # 状态机
│   │   │   └── PlayerSlot.cs                  # 玩家槽位
│   │   ├── FrameSync/                         # ⭐ 帧同步引擎
│   │   │   ├── FrameSyncEngine.cs             # 核心引擎
│   │   │   ├── FrameSyncConfig.cs             # 配置（60 FPS）
│   │   │   ├── IInputCollector.cs             # 输入收集接口
│   │   │   ├── InputCollector.cs              # 输入收集器实现
│   │   │   ├── FrameInputs.cs                 # 帧输入结构
│   │   │   ├── LatencyTracker.cs              # 延迟追踪
│   │   │   └── HistoryBuffer.cs               # 历史帧缓存
│   │   ├── Players/
│   │   │   ├── IPlayerManager.cs              # 玩家管理接口
│   │   │   ├── PlayerManagerImpl.cs           # 实现
│   │   │   └── PlayerProfile.cs               # 玩家档案
│   │   ├── Handlers/                          # 消息处理器
│   │   │   ├── AuthHandler.cs                 # 认证处理
│   │   │   ├── RoomHandler.cs                 # 房间操作处理
│   │   │   ├── InputHandler.cs                # ⭐ 输入上报处理
│   │   │   ├── ResendHandler.cs               # 重传请求处理
│   │   │   └── HandlerBase.cs                 # 处理器基类
│   │   └── FOBackend.Core.csproj
│   │
│   ├── FOBackend.Persistence/                 # 🔵 持久化层
│   │   ├── Repositories/
│   │   │   ├── IPlayerRepository.cs
│   │   │   ├── SqlitePlayerRepository.cs      # SQLite 实现（开发）
│   │   │   ├── PostgresPlayerRepository.cs    # PostgreSQL 实现（生产）
│   │   │   ├── IMatchHistoryRepository.cs
│   │   │   ├── SqliteMatchHistoryRepository.cs
│   │   │   └── ReplayRepository.cs            # 回放数据存取
│   │   ├── Migrations/
│   │   │   ├── Schema.sql                     # DDL脚本
│   │   │   └── SeedData.sql                   # 种子数据（如有）
│   │   └── FOBackend.Persistence.csproj
│   │
│   ├── FOBackend.Infrastructure/              # ⚪ 基础设施
│   │   ├── Logging/
│   │   │   ├── SerilogSetup.cs                # 日志初始化
│   │   │   └── LoggingConfiguration.cs
│   │   ├── Config/
│   │   │   ├── AppSettings.cs                 # 配置POCO
│   │   │   └── OptionsValidator.cs            # 配置验证
│   │   ├── Monitoring/
│   │   │   ├── MetricsCollector.cs            # Prometheus 指标
│   │   │   └── HealthCheckService.cs          # 健康检查
│   │   ├── Utilities/
│   │   │   ├── Crc32.cs                       # CRC校验
│   │   │   ├── RollingAverage.cs              # 滑动平均
│   │   │   ├── IdGenerator.cs                 # ID生成（UUID/雪花）
│   │   │   └── TimeUtils.cs                   # 时间工具
│   │   └── FOBackend.Infrastructure.csproj
│   │
│   └── FOBackend.Server/                      # 🟢 主程序入口（Host）
│       ├── Program.cs                         # 入口 & DI 注册
│       ├── StartupTasks.cs                    # 启动任务（初始化DB等）
│       ├── appsettings.json                   # 配置文件
│       ├── appsettings.Development.json        # 开发配置
│       ├── FOBackend.Server.csproj
│       └── Dockerfile                         # 容器化
│
├── tests/                                     # 测试项目
│   ├── FOBackend.Core.Tests/
│   │   ├── FrameSync/
│   │   │   ├── FrameSyncEngineTests.cs        # ⭐ 引擎单元测试
│   │   │   ├── InputCollectorTests.cs
│   │   │   └── DeterminismTests.cs            # 确定性测试
│   │   ├── Session/
│   │   │   ├── GameSessionTests.cs            # 房间生命周期测试
│   │   │   └── PlayerJoinLeaveTests.cs
│   │   └── Helpers/
│   │       └── MockObjects.cs                 # Mock对象工厂
│   │
│   ├── FOBackend.Protocol.Tests/
│   │   ├── SerializationTests.cs             # 序列化正确性测试
│   │   ├── CompatibilityTests.cs             # 协议兼容性测试
│   │   └── PerformanceTests.cs               # 序列化性能基准测试
│   │
│   └── FOBackend.IntegrationTests/
│       ├── EndToEndTests.cs                  # 端到端测试（启动真实服务）
│       ├── LoadTests.cs                      # 压力测试（模拟多连接）
│       └── TestFixtures/
│           ├── TestServerFactory.cs
│           └── TestClients.cs
│
├── docs/                                      # 文档
│   ├── ARCHITECTURE.md                        # 本文档
│   ├── PROTOCOL.md                            # 协议详细说明
│   ├── API.md                                 # API参考手册
│   ├── DEPLOYMENT.md                          # 部署指南
│   └── CLIENT_INTEGRATION_GUIDE.md            # ⭐ 客户端接入指南（重要）
│
├── docker-compose.yml                         # Docker 编排（开发环境）
├── docker-compose.prod.yml                    # Docker 编排（生产环境）
├── Dockerfile
├── .dockerignore
├── README.md
└── .gitignore
```

---

## 六、测试部署方案（本地主机 + 腾讯云）

### 6.1 物理拓扑

```
┌─────────────────────────────────────────────────────────────────┐
│                        你的局域网                                │
│                                                                  │
│   ┌──────────────────────┐        ┌──────────────────────┐      │
│   │  你的开发机 (主机A)    │        │  客户端实例 1         │      │
│   │  - 运行 FOBackend     │        │  - Unity/Godot 编辑器  │      │
│   │  - 运行 PostgreSQL    │        │  - 或打包的游戏客户端   │      │
│   │  - Port: 7777 (UDP)   │        │                      │      │
│   └──────────┬───────────┘        └──────────┬───────────┘      │
│              │                                │                  │
│              │  局域网                         │  局域网           │
│              │                                │                  │
└──────────────┼────────────────────────────────┼──────────────────┘
               │                                │
         ┌─────▼────────┐               ┌──────▼────────┐
         │  路由器/NAT   │               │  客户端实例 2   │
         │  (公网IP)     │               │  - 另一台设备    │
         └───────┬───────┘               │  - 手机/平板    │
                 │                       │  - 远程调试     │
                 │ Internet              └──────┬─────────┘
                 │                                │
┌────────────────▼────────────────────────────────▼───────────────┐
│                        腾讯云 VPC                                 │
│                                                                  │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │   腾讯云 CVM 实例 (主机B)                                 │   │
│   │                                                          │   │
│   │   ┌─────────────────────────────────────────────────┐   │   │
│   │   │  FOBackend Server (Docker Container)             │   │   │
│   │   │  - 监听: 0.0.0.0:7777 (UDP/KCP)                 │   │   │
│   │   │  - 日志: /var/log/fobackend/                    │   │   │
│   │   │  - 数据: /var/data/fobackend.db (SQLite)         │   │   │
│   │   └─────────────────────────────────────────────────┘   │   │
│   │                                                          │   │
│   │   公网 IP: xxx.xxx.xxx.xxx                             │   │
│   │   安全组: 开放 UDP 7777                                 │   │
│   └──────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────┘
```

### 6.2 部署步骤

#### **Step 1: 本地主机开发环境搭建**

```powershell
# 1. 克隆项目
git clone https://github.com/YOUR_REPO/FOBackend.git
cd FOBackend

# 2. 还原依赖
dotnet restore

# 3. 编译
dotnet build --configuration Release

# 4. 运行（本地模式，监听 127.0.0.1:7777）
dotnet run --project src/FOBackend.Server -- \
    --urls "udp://127.0.0.1:7777"
```

#### **Step 2: 腾讯云服务器部署**

```bash
# 1. SSH 登录腾讯云 CVM
ssh root@你的腾讯云公网IP

# 2. 安装 Docker（如果没有）
curl -fsSL https://get.docker.com | sh
systemctl enable docker && systemctl start docker

# 3. 上传项目代码（git clone 或 scp）
git clone https://github.com/YOUR_REPO/FOBackend.git
cd FOBackend

# 4. 构建并运行容器
docker compose up -d

# 5. 验证端口监听
ss -ulnp | grep 7777
# 应输出：UNCONN 0 0  *:7777  users:(("dotnet",pid=...,fd=...))

# 6. 测试连通性（从本地）
# 安装 ncat 或 netcat
ncat -u 腾讯云公网IP 7777
```

#### **Step 3: 安全组配置（腾讯云控制台操作）**

```
入站规则：
┌────────────┬──────────┬─────────────────────┬──────┐
│   协议     │   端口    │      来源            │ 用途  │
├────────────┼──────────┼─────────────────────┼──────┤
│   UDP      │   7777   │   0.0.0.0/0         │ KCP  │
│   TCP      │   22     │   你的IP/32         │ SSH  │
│   ICMP     │    -     │   你的IP/32         │ Ping │
└────────────┴──────────┴─────────────────────┴──────┘
```

### 6.3 Docker 配置

```yaml
# docker-compose.yml（开发/测试环境）
version: '3.8'

services:
  fobackend:
    build:
      context: .
      dockerfile: Dockerfile
    container_name: fobackend-server
    restart: unless-stopped
    ports:
      - "7777:7777/udp"    # KCP/UDP 端口
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Connection__ListenAddress=0.0.0.0
      - Connection__Port=7777
      - Database__ConnectionString=Data Source=/var/data/fobackend.db
      - FrameSync__TargetFPS=60
      - Logging__MinimumLevel=Information
    volumes:
      - ./data:/var/data           # SQLite 数据持久化
      - ./logs:/var/log/fobackend  # 日志输出
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost:7777/health || exit 1"]
      interval: 30s
      timeout: 5s
      retries: 3
    deploy:
      resources:
        limits:
          cpus: '2.0'
          memory: 512M
        reservations:
          cpus: '0.5'
          memory: 256M
```

```dockerfile
# Dockerfile（多阶段构建）
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/*.sln .
COPY src/FOBackend.Protocol/*.csproj ./FOBackend.Protocol/
COPY src/FOBackend.Transport/*.csproj ./FOBackend.Transport/
COPY src/FOBackend.Core/*.csproj ./FOBackend.Core/
COPY src/FOBackend.Persistence/*.csproj ./FOBackend.Persistence/
COPY src/FOBackend.Infrastructure/*.csproj ./FOBackend.Infrastructure/
COPY src/FOBackend.Server/*.csproj ./FOBackend.Server/
RUN dotnet restore FOBackend.sln
COPY src/ .
RUN dotnet publish FOBackend.sln -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "FOBackend.Server.dll"]
```

---

## 七、客户端接入指南（概念性）

> 由于后台是**游戏无关**的，客户端需要自行对接协议

### 7.1 客户端对接流程图

```
┌──────────┐  1. UDP Send Handshake  ┌──────────┐
│  Client  │ ──────────────────────► │  Server  │
│          │ ◄────────────────────── │          │
└────┬─────┘  2. ServerHello(conv)   └────┬─────┘
     │                                   │
     ▼  3. Auth(player_name)             │
     │ ───────────────────────────────►  │
     │ ◄─────────────────────────────── │  返回 player_id + token
     │                                   │
     ▼  4. CreateRoom / JoinRoom        │
     │ ───────────────────────────────►  │
     │ ◄─────────────────────────────── │  返回 room_id
     │                                   │
     ▼  5. Ready(true)                  │
     │ ───────────────────────────────►  │
     │                                   │  等待另一玩家 Ready
     │ ◄═══════════════════════════════ │  6. FrameSyncStart
     │   (seed, fps, player_order)       │
     │                                   │
     ▼ ═════════════════════════════════╧═══════════════▶ 帧循环开始
                                                                
     ┌─────────────────────────────────────────────────────┐
     │                                                     │
     │  FOR EACH FRAME N:                                   │
     │  ├─ 7. Collect local user input                      │
     │  │   (键盘/手柄/触摸 → bytes[])                     │
     │  │                                                  │
     │  ├─ 8. Send PlayerInputReport(frame=N, input=bytes) │
     │  │   ──────────────────────────────────────────────► │
     │  │                                                  │
     │  ├─ 9. Receive FrameSyncPackage (frame=N)            │
     │  │   ◄───────────────────────────────────────────── │
     │  │                                                  │
     │  ├─ 10. Execute game logic with both inputs         │
     │  │   my_input = package.inputs[my_player_id]        │
     │  │   opp_input = package.inputs[opponent_id]        │
     │  │   game_logic.Update(my_input, opp_input)          │
     │  │                                                  │
     │  └─ 11. Render & wait for next frame                │
     │                                                     │
     └─────────────────────────────────────────────────────┘
```

### 7.2 客户端伪代码示例

```csharp
// ===== 客户端伪代码（非实际实现，展示对接思路）=====

class FoBackendClient
{
    private KcpClient _kcp;
    private string _myPlayerId;
    private string _opponentPlayerId;
    private int _currentFrame = 0;
    private IGameLogic _gameLogic;  // 你的游戏逻辑实现
    private DeterministicRandom _rng;
    
    // 步骤 1-3: KCP 握手与认证
    async Task ConnectAndAuthAsync(string serverIp, int port, string playerName)
    {
        // 1. KCP 握手：发送 ClientHello，接收 ServerHello（获取 conv）
        var udpClient = new UdpClient();
        var serverEp = new IPEndPoint(IPAddress.Parse(serverIp), port);
        
        // 握手包：第1字节为命令(0x01)，后4字节为 conv(0，请求分配)
        byte[] handshake = new byte[5] { 0x01, 0x00, 0x00, 0x00, 0x00 };
        await udpClient.SendAsync(handshake, handshake.Length, serverEp);
        
        // 接收 ServerHello
        var result = await udpClient.ReceiveAsync();
        uint conv = BitConverter.ToUInt32(result.Buffer, 1);  // 小端序
        
        // 2. 使用 conv 初始化 KCP 对象（ikcp_create）
        _kcp = new Kcp(conv, (data) => udpClient.Send(data, data.Length, serverEp));
        _kcp.NoDelay(1, 10, 2, 1);
        _kcp.WndSize(128, 128);
        
        // 3. 认证
        var authResp = await _kcp.SendAndReceiveAsync<AuthenticateResponse>(
            new AuthenticateRequest { PlayerName = playerName });
        
        _myPlayerId = authResp.PlayerId;
        // 保存 token 用于后续请求鉴权...
    }
    
    // 步骤 4-6: 创建/加入房间并准备
    async Task JoinMatchAsync()
    {
        var roomResp = await _kcp.SendAndReceiveAsync<CreateRoomResponse>(
            new CreateRoomRequest { GameMode = GameMode.Shooter1V1 });
        
        await _kcp.SendAsync(new ReadyRequest { RoomId = roomResp.RoomId, IsReady = true });
        
        // 等待 FrameStart 通知
        var startNote = await _kcp.WaitForNotificationAsync<FrameSyncStartNotification>();
        
        // 初始化游戏逻辑（传入共享的随机种子！）
        _rng = new DeterministicRandom(startNote.RandomSeed);
        _gameLogic.Initialize(rng: _rng, myId: _myPlayerId, opponentId: _opponentPlayerId);
    }
    
    // 步骤 7-11: 主帧循环
    async Task MainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 7. 收集本地输入
            var myInputBytes = CollectInputFromHardware();  // 你自定义的输入编码
            
            // 8. 发送输入给服务器
            await _kcp.SendAsync(new PlayerInputReport
            {
                FrameNumber = _currentFrame,
                InputData = myInputBytes,
                InputChecksum = Crc16.Compute(myInputBytes)
            });
            
            // 9. 等待服务器同步包（带超时）
            var syncPkg = await WaitForFramePackageAsync(timeoutMs: 200);
            
            if (syncPkg == null)
            {
                // 超时处理：请求重传或使用预测
                continue;
            }
            
            // 10. 执行游戏逻辑
            var myFrameInput = syncPkg.Inputs.FirstOrDefault(i => i.PlayerId == _myPlayerId);
            var oppFrameInput = syncPkg.Inputs.FirstOrDefault(i => i.PlayerId == _opponentPlayerId);
            
            _gameLogic.ExecuteFrame(
                frameNumber: syncPkg.FrameNumber,
                myInput: myFrameInput?.InputData ?? Array.Empty<byte>(),
                oppInput: oppFrameInput?.InputData ?? Array.Empty<byte>());
            
            // 11. 渲染
            Render(_gameLogic.GetCurrentState());
            
            _currentFrame++;
            
            // 帧率控制（可选，如果游戏有自己的主循环）
            await FrameDelay(targetFps: 60);
        }
    }
    
    // 自定义：将硬件输入转换为字节数组
    byte[] CollectInputFromHardware()
    {
        // === 完全由你自定义！服务端不关心格式 ===
        // 示例（平面射击）：
        // Byte 0: 方向键掩码 (bit0=左 bit1=右 bit2=上 bit3=下)
        // Byte 1: 动作键掩码 (bit0=跳跃 bit1=攻击 bit2=技能1 bit3=技能2)
        // Byte 2-3: 摇杆X (short, -32768~32767)
        // Byte 4-5: 摇杆Y (short, -32768~32767)
        // ... 更多自定义字段
        
        using var ms = new MemoryStream(6);
        ms.Write((byte)(Input.Left ? 0x01 : 0 | Input.Right ? 0x02 : 0 | ...));
        ms.Write((byte)(Input.Jump ? 0x01 : 0 | Input.Attack ? 0x02 : 0 | ...));
        ms.Write(BitConverter.GetBytes((short)(Input.AnalogX * 32767)));
        ms.Write(BitConverter.GetBytes((short)(Input.AnalogY * 32767)));
        return ms.ToArray();
    }
}
```

---

## 八、性能指标与调优目标

### 8.1 关键性能指标 (KPI)

| 指标 | 目标值 | 测量方法 | 说明 |
|------|--------|----------|------|
| **单帧耗时** | < 1ms (P99) | Stopwatch | 服务端处理耗时 |
| **端到端延迟** | < 100ms (P95) | 客户端打点 | 输入→显示 |
| **输入同步延迟** | < 50ms (P95) | 服务端记录 | 收集→广播 |
| **抖动 (Jitter)** | < ±2ms | 标准差 | 帧间隔稳定性 |
| **CPU占用** | < 30% (单核) | 系统监控 | 空载（1个房间） |
| **内存占用** | < 100MB | 系统监控 | 运行时 |
| **丢包率** | < 0.1% | KCP 统计 | 内网/公网 |

### 8.2 60 FPS 下的时间预算

```
总帧间隔: 16.666ms (100%)
├── 网络IO (收发):     ~0.5ms   (3%)    ← KCP异步，大部分时间重叠
├── 输入收集等待:      ~2.0ms   (12%)   ← 可配置超时
├── 序列化/反序列化:   ~0.1ms   (0.6%)  ← protobuf-net 极快
├── 业务逻辑处理:      ~0.3ms   (1.8%)  ← 房间状态检查
├── 广播发送:          ~0.5ms   (3%)    ← 并行发送给2个客户端
├── 缓冲区管理:        ~0.05ms  (0.3%)  ← O(1) 操作
├── 日志/监控:         ~0.1ms   (0.6%)  ← 异步写入
└───────────────────────────────
总计:                   ~3.55ms (21%)
剩余预算:              ~13.11ms (79%)  ✓ 充裕
```

> **结论**: 60 FPS 在现代硬件上完全可行，CPU 时间充裕。

### 8.3 压力测试计划

```bash
# 使用 FOBackend.IntegrationTests 中的 LoadTests
dotnet test tests/FOBackend.IntegrationTests \
    --filter "LoadTests" \
    --logger "console;verbosity=detailed"

# 预期结果：
# - 同时支持 100+ 个 1v1 房间（200并发连接）
# - CPU < 80%, Memory < 2GB
# - P99 帧耗时 < 5ms
```

---

## 九、开发路线图（修订版）

### Phase 1: 基础框架 ⏱️ 1周
- [ ] 项目脚手架（解决方案、项目结构）
- [ ] Protobuf 协议定义与代码生成
- [ ] KCP 传输层封装（连接管理、心跳）
- [ ] DI 容器搭建与配置系统

### Phase 2: 核心功能 ⏱️ 2周
- [ ] **帧同步引擎 v1**（60 FPS 输入收集→广播）
- [ ] **房间管理系统**（创建/加入/离开/Ready）
- [ ] **认证系统**（简易，基于玩家名）
- [ ] **数据库持久化**（SQLite + Dapper）
- [ ] 单元测试（引擎、协议、房间）

### Phase 3: 增强完善 ⏱️ 1周
- [ ] **延迟补偿**（动态等待、RTT追踪）
- [ ] **丢包重传机制**
- [ ] **断线检测与恢复**
- [ ] **对局记录保存**
- [ ] 日志与监控集成（Serilog + Prometheus）

### Phase 4: 部署验证 ⏱️ 3天
- [ ] **Docker 容器化**
- [ ] **腾讯云部署**（单机测试）
- [ ] **双机联调**（本地客户端 ↔ 云服务器）
- [ ] **压力测试与调优**
- [ ] 文档编写（API文档、客户端接入指南）

### Phase 5: 生产就绪（可选）⏱️ 1周
- [ ] **安全性加固**（速率限制、IP黑名单）
- [ ] **反作弊基础**（输入校验、状态哈希）
- [ ] **自动扩容**（Docker Compose / K8s）
- [ ] **CI/CD 流水线**

---

## 十、风险分析与应对

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|----------|
| **60 FPS 下公网延迟不稳定** | 中 | 高 | 自适应帧率降级；客户端输入预测 |
| **KCP 协议兼容性** | 低 | 低 | 自研C#实现，完全可控；协议格式与skywind3000/kcp一致 |
| **1v1 中一方掉线** | 中 | 中 | 断线判负机制；短时间重连窗口 |
| **输入作弊（篡改字节流）** | 中 | 中 | CRC 校验 + 服务端规则验证 |
| **确定性不一致（浮点数）** | 低 | 高 | 文档强调客户端使用定点数/统一舍入 |
| **UDP 被 NAT/防火墙拦截** | 低 | 高 | 提供 STUN/TURN 备选方案；NAT穿透文档 |

---

## 附录 A: KCP vs 其他协议对比

| 特性 | 自研KCP | ENet | eNetLite | RakNet | UDP* (*裸UDP) |
|------|---------|------|----------|--------|---------------|
| 语言 | C#(纯，自研) | C | C | C++ | - |
| 可靠性 | ✅ | ✅ | ✅ | ✅ | ❌ |
| 有序 | ✅ | ✅ | ✅ | ✅ | ❌ |
| 延迟（典型） | 20-50ms | 30-80ms | 25-60ms | 40-100ms | <10ms |
| 维护状态 | 🟢完全可控 | 🟡缓慢 | 🟢活跃 | 🟡商业 | N/A |
| .NET生态 | 🟢零依赖 | 🟡需P/Invoke | 🟡需P/Invoke | 🟡需封装 | 🟢内置 |
| 许可证 | MIT（自研） | BSD-3 | zlib | 商业/BSD | - |
| **推荐度** | **⭐⭐⭐⭐⭐** | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐ |

---

---

## 十一、微服务架构演进 (Microservices Architecture)

> **本章为架构演进规划，描述如何将当前单体架构拆分为支持水平扩展的微服务体系。**
> 当前分层架构（Protocol/Transport/Core/Persistence）已为微服务化奠定良好基础。

### 11.1 演进可行性分析

当前架构具备以下微服务化优势：

| 优势 | 说明 |
|------|------|
| **分层清晰** | Protocol/Transport/Core/Persistence 四层边界明确，可独立拆解 |
| **接口抽象完善** | `IPlayerManager`、`ISessionManager`、`IGameConnection` 等接口天然适合服务化 |
| **DI容器就绪** | Microsoft.Extensions.DependencyInjection 支持服务替换为远程代理 |
| **协议共享** | `FOBackend.Protocol` 可打包为 NuGet 共享库，供所有微服务引用 |
| **传输层自研** | KCP 纯 C# 实现，可无缝嵌入 Battle Service，无需外部依赖 |

**需要引入的新组件**：
- 服务间同步通信：**gRPC**（高性能二进制 RPC）
- 服务间异步通信：**Redis Pub/Sub** 或 **RabbitMQ**
- 服务发现：**Consul** / **etcd** 或简化版 DNS 轮询
- API 网关：**YARP** 或 **Envoy**（可选，用于 HTTP 服务统一入口）

---

### 11.2 服务拆分方案

将单体拆分为 **4 个核心微服务 + 1 个共享库**：

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              客户端层 (Client)                                │
│                                                                             │
│   ┌──────────────┐    ┌──────────────┐    ┌──────────────────────────────┐ │
│   │   登录/注册    │    │   房间大厅     │    │         对局画面              │ │
│   │  (HTTP/gRPC)  │    │  (HTTP/gRPC)  │    │      (KCP/UDP 直连)          │ │
│   └──────┬───────┘    └──────┬───────┘    └──────────────┬───────────────┘ │
└──────────┼───────────────────┼───────────────────────────┼─────────────────┘
           │                   │                           │
┌──────────▼───────────────────▼───────────────────────────▼─────────────────┐
│                         API Gateway (可选)                                   │
│              JWT 鉴权 / 限流 / 路由 / 负载均衡                                │
└──────────┬───────────────────┬───────────────────────────┬─────────────────┘
           │                   │                           │
           ▼                   ▼                           ▼
┌────────────────────┐ ┌────────────────────┐ ┌────────────────────────────┐
│   Auth Service     │ │ Matchmaking Service│ │      Battle Nodes          │
│   登录/鉴权/账号      │ │   匹配/房间信息       │ │     对局帧同步服务            │
│   ─────────────    │ │   ─────────────    │ │     ─────────────          │
│   PostgreSQL       │ │   Redis            │ │     内存帧历史 (5s)          │
│   JWT Token        │ │   房间状态机         │ │     KCP Server (UDP)       │
│   密码加密/刷新       │ │   邀请码/匹配队列     │ │     FrameSyncEngine        │
└──────────┬─────────┘ └──────────┬─────────┘ └──────────────┬─────────────┘
           │                      │                          │
           │                      │                          │
           └──────────────────────┼──────────────────────────┘
                                  ▼
                    ┌────────────────────────────┐
                    │      State Service         │
                    │    断线重连/回放数据存储      │
                    │    ─────────────────       │
                    │    对象存储 / 压缩存档        │
                    │    历史帧查询 / Replay生成   │
                    └────────────────────────────┘
```

#### 服务职责一览

| 服务 | 英文命名 | 核心职责 | 对外协议 | 数据存储 |
|------|----------|----------|----------|----------|
| **鉴权服务** | `Auth Service` | 玩家注册/登录、JWT 签发/验证、玩家档案 CRUD | **HTTP/gRPC (TCP)** | **PostgreSQL** (生产) / SQLite (开发) |
| **匹配服务** | `Matchmaking Service` | 房间创建/加入/销毁、匹配队列、准备状态、邀请码 | **HTTP/gRPC (TCP)** | **Redis** (房间状态) + PostgreSQL (持久化) |
| **对战服务** | `Battle Service` | KCP 接入、帧同步引擎、输入收集与广播、丢包重传 | **KCP/UDP (客户端) + gRPC (TCP，内部)** | **内存** (环形缓冲区，~5s) |
| **状态服务** | `State Service` | 历史帧持久化、断线重连数据拉取、回放生成与查询 | **gRPC (TCP)** | **对象存储** (MinIO/S3) + PostgreSQL (元数据) |
| **共享协议库** | `FOBackend.Protocol` | Protobuf 消息定义、MessageId、序列化扩展 | - | 无状态 |

> **协议选择原则**：仅 **Battle Service** 对外暴露 UDP/KCP 端口，因其承担 60 FPS 实时帧同步，对延迟极度敏感。其余服务（Auth/Matchmaking/State）均采用基于 **TCP** 的 HTTP/gRPC，利用 TCP 的可靠性、流量控制和成熟负载均衡生态，降低运维复杂度。

---

### 11.3 服务间通信设计

#### 同步通信：gRPC

用于低延迟、强一致性的服务间调用：

```
┌─────────────────┐     gRPC     ┌─────────────────┐
│ Matchmaking Svc │ ───────────► │   Auth Svc      │
│                 │ ValidateToken│                 │
└─────────────────┘              └─────────────────┘

┌─────────────────┐     gRPC     ┌─────────────────┐
│   Battle Node   │ ───────────► │ Matchmaking Svc │
│                 │  GetRoomInfo │                 │
└─────────────────┘              └─────────────────┘

┌─────────────────┐     gRPC     ┌─────────────────┐
│   Battle Node   │ ───────────► │  State Service  │
│                 │  SaveFrames  │                 │
└─────────────────┘              └─────────────────┘
```

#### 异步通信：Redis Pub/Sub

用于事件广播和解耦：

| Channel | 发布者 | 订阅者 | 用途 |
|---------|--------|--------|------|
| `room:assigned` | Matchmaking | Battle Node | 通知 Battle 节点接管房间 |
| `room:closed` | Battle Node | Matchmaking | 通知匹配服务房间已关闭 |
| `player:disconnected` | Battle Node | Matchmaking, State | 广播玩家断线事件 |

---

### 11.4 各服务数据存储拆分

#### Auth Service
```sql
-- players 表（扩展版）
CREATE TABLE players (
    player_id       VARCHAR(36) PRIMARY KEY,
    player_name     VARCHAR(64) NOT NULL UNIQUE,
    password_hash   VARCHAR(256),        -- 支持账号密码登录
    email           VARCHAR(128),
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    last_login_at   TIMESTAMPTZ,
    session_token   VARCHAR(256),        -- 当前有效 JWT ID
    token_expires_at TIMESTAMPTZ,
    total_games     INT DEFAULT 0,
    total_wins      INT DEFAULT 0,
    rating          INT DEFAULT 1000     -- ELO 等级分
);

-- login_history 表（安全审计）
CREATE TABLE login_history (
    id              BIGSERIAL PRIMARY KEY,
    player_id       VARCHAR(36) REFERENCES players(player_id),
    ip_address      INET,
    client_version  VARCHAR(32),
    login_at        TIMESTAMPTZ DEFAULT NOW(),
    success         BOOLEAN
);
```

#### Matchmaking Service
```
Redis Key 设计：
- Hash  `room:{room_id}`      → 房间基本信息 (host, mode, status, battle_node)
- Hash  `room:{room_id}:players` → 玩家槽位信息 (player1_id, player2_id, ready_state)
- String `invite:{code}`      → 映射到 room_id
- ZSet  `matchmaking:queue:{mode}` → 匹配队列 (score=rating, member=player_id)
- Set   `player_rooms:{pid}`  → 玩家当前所在房间（防重复加入）
```

#### Battle Service
```
纯内存数据结构（无持久化）：
- ConcurrentDictionary<string, FrameSyncEngine> _engines
- ConcurrentDictionary<uint, KcpSession> _sessions
- CircularBuffer<FrameSyncPackage> _historyCache (per room, 300 frames)
```

#### State Service
```sql
-- match_history 表（对局元数据）
CREATE TABLE match_history (
    match_id        VARCHAR(36) PRIMARY KEY,
    room_id         VARCHAR(36) NOT NULL,
    game_mode       INT NOT NULL,
    player1_id      VARCHAR(36),
    player2_id      VARCHAR(36),
    winner_id       VARCHAR(36),
    total_frames    INT,
    duration_sec    FLOAT,
    end_reason      INT,
    storage_path    VARCHAR(512),        -- 对象存储路径
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- 对象存储 Bucket 结构：
-- s3://fobackend-replays/{match_id}/frames.bin.zst
-- s3://fobackend-replays/{match_id}/metadata.json
```

---

### 11.5 客户端接入流程（微服务版）

```
┌─────────┐  1. POST /auth/login      ┌─────────────┐
│ Client  │ ─────────────────────────►│ Auth Service│
│         │ ◄─────────────────────────│             │
└────┬────┘   JWT Token + player_id   └─────────────┘
     │
     │  2. POST /matchmaking/rooms     ┌──────────────────┐
     │  (Header: Authorization: JWT)   │ Matchmaking Svc  │
     │ ───────────────────────────────►│                  │
     │ ◄───────────────────────────────│                  │
     │      { room_id, invite_code,     │                  │
     │        battle_node_host,         │                  │
     │        battle_node_port }        │                  │
     │                                 └──────────────────┘
     │
     │  3. KCP Handshake → Battle Node  ┌──────────────────┐
     │  ───────────────────────────────►│   Battle Node    │
     │  ◄───────────────────────────────│   (UDP + KCP)    │
     │       ServerHello(conv)          │                  │
     │                                 └──────────────────┘
     │
     │  4. KCP Send: ReadyRequest       ┌──────────────────┐
     │  ───────────────────────────────►│   Battle Node    │
     │                                  │   FrameSyncStart │
     │  ◄═══════════════════════════════│   (seed, fps)    │
     │         FrameSyncStart           └──────────────────┘
     │
     ▼  ════════════════════════════════════════════════════▶ 帧循环

     5. 断线重连：
        a) 重新 KCP Handshake → Battle Node (相同 host:port)
        b) 发送 ReconnectRequest (player_id, last_received_frame)
        c) Battle Node 向 State Service 查询缺失帧
        d) 下发 ResendFrameResponse，恢复同步
```

---

### 11.6 关键设计决策

#### Q1: 为什么 Battle Service 需要直连客户端（KCP），而不是通过 Gateway 转发？

**决策**：客户端直接连接 Battle Node 的 KCP 端口。

**理由**：
- 帧同步对延迟极度敏感（预算 < 16.67ms）
- Gateway 转发会引入额外 1-3ms 延迟 + 单点瓶颈
- Battle Node 是有状态服务（持有 FrameSyncEngine + KcpSession），天然需要维护长连接
- 房间分配后，客户端已知目标 Battle Node 地址，无需中心化代理

**替代方案**（若必须收敛端口）：
- 使用 DSR (Direct Server Return) 模式的负载均衡器
- 或使用基于 conv 的 Gateway 路由（conv 高比特位编码节点 ID）

#### Q2: Battle Service 如何做到水平扩展？

**策略**：
1. **房间级分片**：一个房间的对局固定绑定到一个 Battle Node 实例
2. **无共享架构 (Share-Nothing)**：Battle Node 之间不共享内存状态
3. **Matchmaking 负责调度**：创建房间时，根据节点负载（CPU/内存/房间数）选择最优 Battle Node
4. **Sticky Session**：客户端断线重连时，通过 `room_id` 哈希到原节点（或从 Redis 读取节点分配记录）

```
Matchmaking Service 选择 Battle Node 的算法：
1. 查询 Consul/Redis 获取所有健康 Battle Nodes
2. 过滤掉负载 > 80% 或房间数 > 上限的节点
3. 优先选择已缓存该房间历史数据的节点（断线重连场景）
4. 返回节点地址给客户端
```

#### Q3: 断线重连时，如果原 Battle Node 已宕机怎么办？

**容灾流程**：
1. Matchmaking Service 检测到原节点失联（心跳超时）
2. 将房间重新分配到新 Battle Node
3. 新 Battle Node 从 State Service 拉取完整历史帧数据
4. 客户端重连到新节点地址，恢复同步
5. 客户端本地缓冲 + 服务端补帧，实现无缝恢复

---

### 11.7 代码迁移路径

当前代码到微服务的渐进式迁移：

#### Phase 1: 提取共享协议库（1周）
- [ ] 将 `FOBackend.Protocol` 打包为内部 NuGet 包
- [ ] 所有服务引用同一 Protocol 包，确保消息格式一致
- [ ] 提取通用的 gRPC 服务定义（`.proto` 文件）

#### Phase 2: 拆分 Auth Service（1周）
- [ ] 新建 `src/Services/AuthService` 项目
- [ ] 从 `FOBackend.Core/Players` 迁移 `IPlayerManager` 逻辑
- [ ] 从 `FOBackend.Persistence` 迁移 `IPlayerRepository` 及数据库 Schema
- [ ] 实现 gRPC 接口：`Authenticate`, `ValidateToken`, `GetProfile`
- [ ] 引入 JWT 库（`System.IdentityModel.Tokens.Jwt`）
- [ ] 部署为独立 Docker 容器

#### Phase 3: 拆分 Matchmaking Service（1周）
- [ ] 新建 `src/Services/MatchmakingService` 项目
- [ ] 从 `FOBackend.Core/Sessions` 迁移 `ISessionManager` 逻辑
- [ ] 房间状态从内存字典迁移到 Redis
- [ ] 实现 gRPC 接口：`CreateRoom`, `JoinRoom`, `LeaveRoom`, `SetReady`, `GetRoom`
- [ ] 实现 Battle Node 调度逻辑（负载均衡 + 房间分配）

#### Phase 4: 拆分 Battle Service（2周）
- [ ] 新建 `src/Services/BattleService` 项目
- [ ] 从 `FOBackend.Transport` 迁移 KCP 相关代码
- [ ] 从 `FOBackend.Core/FrameSync` 迁移 `FrameSyncEngine`
- [ ] 修改 `FrameSyncEngine`：移除对 `IGameConnection` 的直接依赖，改为输出到消息总线
- [ ] 实现 gRPC 对内接口：`StartRoom`, `StopRoom`, `GetRoomStats`
- [ ] 集成 State Service gRPC 客户端，异步上报帧数据

#### Phase 5: 拆分 State Service（1周）
- [ ] 新建 `src/Services/StateService` 项目
- [ ] 从 `FOBackend.Persistence` 迁移 `IMatchHistoryRepository`
- [ ] 实现帧数据压缩（Zstd）和对象存储上传
- [ ] 实现 gRPC 接口：`SaveFrames`, `GetFramesForReconnect`, `GetReplay`

#### Phase 6: 接入 API Gateway 与统一配置（1周）
- [ ] 部署 YARP 或 Envoy 作为 HTTP 服务网关
- [ ] JWT 鉴权中间件下沉到 Gateway（Battle Service 的 KCP 仍需自行验签）
- [ ] 引入 Consul 做服务发现与健康检查
- [ ] 更新 `docker-compose.yml` 为多服务编排

---

### 11.8 微服务版 Docker Compose 示例

```yaml
# docker-compose.microservices.yml
version: '3.8'

services:
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: fobackend
      POSTGRES_USER: fobackend
      POSTGRES_PASSWORD: secret
    volumes:
      - pgdata:/var/lib/postgresql/data

  auth-service:
    build:
      context: .
      dockerfile: src/Services/AuthService/Dockerfile
    ports: ["8081:8080"]
    environment:
      - ConnectionStrings__PostgreSQL=Host=postgres;...
      - Jwt__Secret=your-256-bit-secret

  matchmaking-service:
    build:
      context: .
      dockerfile: src/Services/MatchmakingService/Dockerfile
    ports: ["8082:8080"]
    environment:
      - Redis__ConnectionString=redis:6379
      - AuthService__Url=http://auth-service:8080

  battle-node-1:
    build:
      context: .
      dockerfile: src/Services/BattleService/Dockerfile
    ports:
      - "7777:7777/udp"
      - "9081:8080"
    environment:
      - KCP__Port=7777
      - NodeId=battle-1
      - MatchmakingService__Url=http://matchmaking-service:8080
      - StateService__Url=http://state-service:8080
    deploy:
      resources:
        limits:
          cpus: '2.0'
          memory: 1G

  state-service:
    build:
      context: .
      dockerfile: src/Services/StateService/Dockerfile
    ports: ["8084:8080"]
    environment:
      - ConnectionStrings__PostgreSQL=Host=postgres;...
      - ObjectStorage__Endpoint=minio:9000

volumes:
  pgdata:
```

---

### 11.9 风险与应对（微服务新增）

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|----------|
| **Battle Node 单点故障** | 中 | 高 | 房间级快速迁移（<5s）；State Service 持久化保障数据不丢 |
| **服务间网络延迟** | 中 | 中 | gRPC + 连接池；服务部署在同 VPC/可用区；Redis 就近部署 |
| **Redis 单点故障** | 低 | 高 | Redis Sentinel 或 Cluster 模式；房间状态可 Battle Node 本地重建 |
| **数据一致性（房间状态）** | 中 | 中 | Saga 模式处理跨服务事务；最终一致性 + 补偿机制 |
| **KCP 连接跨节点迁移** | 中 | 高 | conv 重新分配 + 服务端补帧；客户端支持帧号跳跃恢复 |

---

**文档版本**: v3.0  
**最后更新**: 2026-04-29  
**作者**: AI Assistant  
**变更说明**: 
- v1.0 → v2.0: 全面重构
  - WebSocket/TCP → **UDP + KCP**
  - 20 FPS → **60 FPS**
  - N人房间 → **固定 1v1 双人**
  - 耦合游戏逻辑 → **通用帧同步中间件**
  - Redis + PG → **SQLite 轻量化**
  - 新增客户端对接伪代码示例
  - 新增双机部署方案
- v2.0 → v2.1: KCP 实现替换
  - Cysharp/KcpTransport NuGet包 → **自研纯C# KCP协议栈**
  - 完全兼容 skywind3000/kcp 协议格式
  - 零外部依赖，单文件可维护
  - 自定义 ClientHello/ServerHello 握手流程
  - IGameConnection 事件从 `event` 改为属性形式
- v2.1 → v3.0: 微服务架构演进
  - 新增微服务拆分方案（Auth/Matchmaking/Battle/State）
  - 新增服务间通信设计（gRPC + Redis Pub/Sub）
  - 新增客户端微服务接入流程
  - 新增数据存储拆分策略
  - 新增 6 阶段代码迁移路径
  - 新增微服务版 Docker Compose 示例
