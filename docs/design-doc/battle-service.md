# Battle Service 设计文档

> **服务定位**：帧同步对战核心微服务  
> **服务类型**：有状态服务（每个房间绑定一个 FrameSyncEngine + KcpSession）  
> **对外协议**：**KCP/UDP（客户端直连）+ gRPC over TCP（内部服务间）**  
> **数据存储**：纯内存（环形缓冲区，~5秒历史帧），不持久化  
> **核心依赖**：`FOBackend.Protocol`（共享消息库）

---

## 一、职责边界

### 1.1 核心功能

| 功能模块 | 说明 |
|----------|------|
| **KCP 连接管理** | UDP 监听、KCP 握手、会话生命周期管理 |
| **帧同步引擎** | 60 FPS Lockstep 输入收集、帧广播、延迟追踪 |
| **输入处理** | 接收 `PlayerInputReport`，校验帧号与 Checksum |
| **丢包重传** | 响应 `ResendFrameRequest`，从历史缓存补发 |
| **房间启动/停止** | 接收 Matchmaking 指令，启动或停止 FrameSyncEngine |
| **断线检测** | KCP 层超时 + 应用层心跳双重检测 |
| **对局结果上报** | 对局结束后，向 Matchmaking 和 State Service 上报结果 |

### 1.2 不包含的职责

- ❌ 玩家认证 → Auth Service（Battle Service 仅做 Token 验签，不管理账号）
- ❌ 房间创建/加入逻辑 → Matchmaking Service
- ❌ 历史帧长期存储 → State Service
- ❌ 战绩统计更新 → Auth Service（异步通知）

---

## 二、架构设计

> **协议选择**：Battle Service 采用 **双协议栈** 设计：
> - **对外（客户端）**：KCP/UDP —— 专为 60 FPS 实时帧同步优化，比 TCP 降低 20~50% 延迟
> - **对内（服务间）**：gRPC over TCP —— 与 Auth/Matchmaking/State 服务通信时，使用 TCP 保证可靠性，利用 gRPC 流式接口高效传输批量帧数据

```
┌─────────────────────────────────────────────────────────────────┐
│                      Battle Node (单实例)                         │
│                                                                 │
│  ┌─────────────────────┐    ┌─────────────────────────────────┐ │
│  │   KCP/UDP Layer     │    │      gRPC Internal API (TCP)     │ │
│  │                     │    │                                  │ │
│  │  UdpClient (7777)   │    │  StartRoom(RoomStartRequest)     │ │
│  │  KcpServerService   │    │  StopRoom(RoomStopRequest)       │ │
│  │  KcpSession (conv)  │    │  GetRoomStats(NodeStatsRequest)  │ │
│  │  KcpConnectionAdapter│   │  GetNodeHealth(HealthRequest)    │ │
│  └──────────┬──────────┘    └─────────────────────────────────┘ │
│             │                                                   │
│             ▼                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              Connection / Room Router                    │   │
│  │  ┌─────────────────────────────────────────────────┐    │   │
│  │  │  ConcurrentDictionary<uint, KcpSession>         │    │   │
│  │  │  ConcurrentDictionary<string, FrameSyncEngine>  │    │   │
│  │  │  (key = room_id)                                │    │   │
│  │  └─────────────────────────────────────────────────┘    │   │
│  └────────────────────────────┬────────────────────────────┘   │
│                               │                                 │
│                               ▼                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              FrameSyncEngine (per room)                  │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────────┐  │   │
│  │  │ InputBuffer │  │ HistoryCache│  │ LatencyTracker  │  │   │
│  │  │ (Concurrent │  │ (Circular   │  │ (per player)    │  │   │
│  │  │  Dictionary)│  │  Buffer)    │  │                 │  │   │
│  │  └─────────────┘  └─────────────┘  └─────────────────┘  │   │
│  │  ┌─────────────┐  ┌─────────────┐                        │   │
│  │  │ FrameTimer  │  │ Broadcast   │                        │   │
│  │  │ (Periodic)  │  │ (并行发送)   │                        │   │
│  │  └─────────────┘  └─────────────┘                        │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │           Async Reporters (后台上报线程)                  │   │
│  │  ──gRPC──► StateService.SaveFrames (批量)               │   │
│  │  ──gRPC──► Matchmaking.ReportRoomClosed                 │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

## 三、接口定义

### 3.1 对外：KCP/UDP 协议（客户端直连）

客户端与 Battle Node 通过 KCP 直连，消息格式与单体版本保持一致：

| MessageId | 方向 | 说明 |
|-----------|------|------|
| `AuthRequest` | C→S | 连接后首次发送，携带 JWT Token |
| `HeartbeatRequest` | C→S | 业务层心跳 |
| `PlayerInputReport` | C→S | ⭐ 帧输入上报 |
| `ResendFrameRequest` | C→S | 请求补帧 |
| `ReadyRequest` | C→S | 准备就绪（可选，也可走 HTTP） |
| `FrameSyncStart` | S→C | 通知帧同步开始 |
| `FrameSyncPackage` | S→C | ⭐ 帧同步广播包 |
| `ResendFrameResponse` | S→C | 补帧响应 |
| `FrameSyncEnd` | S→C | 对局结束通知 |

**认证流程（KCP 连接后）**：
```
Client ──KCP──► AuthRequest(player_id, jwt_token)
Battle Node ──gRPC──► AuthService.ValidateToken(jwt_token)
若有效：返回 AuthResponse(success)
若无效：断开 KCP 连接
```

### 3.2 对内：gRPC 服务定义

```protobuf
syntax = "proto3";
package fobackend.battle;

service BattleService {
  // Matchmaking 调用：启动房间帧同步
  rpc StartRoom (StartRoomRequest) returns (StartRoomResponse);
  
  // Matchmaking 调用：强制停止房间（如房主解散）
  rpc StopRoom (StopRoomRequest) returns (StopRoomResponse);
  
  // 上报节点状态（供 Matchmaking 调度使用）
  rpc GetNodeStats (NodeStatsRequest) returns (NodeStatsResponse);
  
  // 健康检查
  rpc HealthCheck (HealthCheckRequest) returns (HealthCheckResponse);
  
  // 查询房间状态（管理用途）
  rpc GetRoomState (GetRoomStateRequest) returns (GetRoomStateResponse);
}

message StartRoomRequest {
  string room_id = 1;
  repeated PlayerConnection players = 2;
  int32 game_mode = 3;
  int32 random_seed = 4;       // Matchmaking 生成或 Battle 本地生成
}

message PlayerConnection {
  string player_id = 1;
  string player_name = 2;
  string remote_endpoint = 3;  // "ip:port"，用于断线重连匹配
}

message StartRoomResponse {
  bool success = 1;
  string error_message = 2;
}

message StopRoomRequest {
  string room_id = 1;
  int32 end_reason = 2;        // 0=Normal, 1=Disconnect, 2=AdminStop
}

message StopRoomResponse {
  bool success = 1;
  int32 final_frame = 2;
}

message NodeStatsRequest {}

message NodeStatsResponse {
  string node_id = 1;
  int32 active_rooms = 2;
  int32 active_connections = 3;
  float cpu_percent = 4;
  float memory_mb = 5;
}

message HealthCheckRequest {}

message HealthCheckResponse {
  bool healthy = 1;
}

message GetRoomStateRequest {
  string room_id = 1;
}

message GetRoomStateResponse {
  string room_id = 1;
  int32 status = 2;            // 0=Stopped, 1=Running, 2=Paused
  int32 current_frame = 3;
  repeated string player_ids = 4;
}
```

---

## 四、数据模型

### 4.1 内存数据结构

```csharp
/// <summary>
/// Battle Node 运行时状态（纯内存，重启丢失）
/// </summary>
public class BattleNodeState
{
    // KCP 会话：key = conv
    public ConcurrentDictionary<uint, KcpSession> Sessions { get; } = new();
    
    // 房间引擎：key = room_id
    public ConcurrentDictionary<string, FrameSyncEngine> Rooms { get; } = new();
    
    // 玩家到房间的映射：key = player_id
    public ConcurrentDictionary<string, string> PlayerRoomMap { get; } = new();
    
    // 待认证连接：conv → (player_id, token, deadline)
    public ConcurrentDictionary<uint, PendingAuth> PendingAuths { get; } = new();
}

/// <summary>
/// 帧同步引擎包装器（扩展原 FrameSyncEngine）
/// </summary>
public class RoomContext
{
    public string RoomId { get; init; } = "";
    public FrameSyncEngine Engine { get; init; } = null!;
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public List<IGameConnection> Connections { get; } = new();
    
    // 上报队列：批量异步发送给 State Service
    public Channel<FrameSyncPackage> FrameUploadQueue { get; } = 
        Channel.CreateUnbounded<FrameSyncPackage>();
}
```

### 4.2 与原单体代码的对应

| 单体组件 | 微服务位置 | 变化说明 |
|----------|-----------|----------|
| `KcpServerService` | BattleService 核心 | 基本不变，增加 `StartRoom` / `StopRoom` 控制接口 |
| `FrameSyncEngine` | BattleService 核心 | 将 `IGameConnection[]` 注入改为从 `RoomContext` 获取 |
| `ConnectionManager` | BattleService 内存字典 | 功能简化，仅管理 KCP 会话 |
| `HeartbeatManager` | BattleService 内部 | 保留，增加 Token 过期检查 |
| `SessionManagerImpl` | Matchmaking Service | 完全迁出，Battle 不再管理房间元数据 |

---

## 五、房间生命周期

### 5.1 启动流程

```
Matchmaking Service          Battle Node
       │                          │
       │  1. gRPC StartRoom       │
       │ ───────────────────────► │
       │  { room_id, players[],   │
       │    game_mode, seed }     │
       │                          │
       │                          │  2. 创建 FrameSyncEngine
       │                          │     初始化 HistoryBuffer
       │                          │
       │  3. 等待客户端 KCP 连接   │
       │                          │
       │◄─────────────────────────│  4. 返回 StartRoomResponse
       │                          │
       │                          │  5. 客户端 KCP Handshake
       │                          │     客户端发送 AuthRequest
       │                          │
       │                          │  6. 验证 JWT（本地缓存或调用 AuthService）
       │                          │
       │                          │  7. 绑定 player_id → conv → RoomContext
       │                          │
       │                          │  8. 双方连接就绪后，Engine.StartAsync()
       │                          │     广播 FrameSyncStart
```

### 5.2 停止流程

```
Battle Node / Client           Matchmaking / State
       │                              │
       │  1. 对局结束 / 玩家掉线       │
       │                              │
       │  2. Engine.StopAsync()       │
       │     广播 FrameSyncEnd        │
       │                              │
       │  3. 异步批量上报历史帧        │
       │ ──────gRPC──────────────►    │ StateService.SaveFrames
       │                              │
       │  4. 上报房间关闭              │
       │ ──────gRPC──────────────►    │ Matchmaking.ReportRoomClosed
       │                              │
       │  5. 清理内存                  │
       │     Sessions.Remove(conv)
       │     Rooms.Remove(room_id)
```

---

## 六、断线重连设计

### 6.1 快速重连（原 Battle Node 存活）

```
Client (断线后)              Battle Node
       │                          │
       │  1. 重新 KCP Handshake   │
       │ ───────────────────────► │
       │  2. 发送 ReconnectRequest│
       │     { player_id, jwt,    │
       │       last_frame }       │
       │                          │
       │                          │  3. 查找 RoomContext
       │                          │     验证 player_id + token
       │                          │
       │                          │  4. 新 conv 绑定到原 Room
       │                          │     旧 conv 标记过期
       │                          │
       │◄─────────────────────────│  5. 从 HistoryBuffer 拉取
       │     ResendFrameResponse   │     last_frame 之后的帧
       │     (批量补帧)            │
       │                          │
       │  6. 客户端快进恢复          │
```

### 6.2 跨节点重连（原 Battle Node 宕机）

```
Client (断线后)           New Battle Node          Matchmaking         State
       │                       │                      │                 │
       │  1. 请求重连地址        │                      │                 │
       │ ──HTTP/gRPC────────►   │                      │                 │
       │                       │                      │                 │
       │                       │  2. Matchmaking 查询  │                 │
       │                       │     原 room 分配记录   │                 │
       │                       │ ──gRPC────────────►  │                 │
       │                       │                      │                 │
       │                       │  3. 发现原节点失联     │                 │
       │                       │◄─────────────────────│ 分配新节点       │
       │                       │                      │                 │
       │                       │  4. 从 State Service  │                 │
       │                       │     拉取完整历史帧     │                 │
       │                       │ ──gRPC────────────────────────────────►│
       │                       │                      │                 │
       │◄──────────────────────│  5. 返回新节点地址     │                 │
       │   { new_host, port }  │                      │                 │
       │                       │                      │                 │
       │  6. KCP 连接新节点      │                      │                 │
       │ ───────────────────►  │                      │                 │
       │                       │  7. 重建 Engine +      │                 │
       │                       │     注入历史帧         │                 │
       │                       │                      │                 │
       │◄──────────────────────│  8. 恢复帧同步广播     │                 │
```

---

## 七、与其他服务交互

### 7.1 调用其他服务

```
Battle Node ──gRPC──► AuthService.ValidateToken
  客户端 KCP 连接后的首次认证

Battle Node ──gRPC──► StateService.SaveFrames (流式)
  每 60 帧（约 1 秒）批量上报一次历史帧
  使用 gRPC 客户端流式调用减少开销

Battle Node ──gRPC──► Matchmaking.ReportRoomClosed
  房间关闭时上报最终状态
```

### 7.2 被其他服务调用

```
Matchmaking ──gRPC──► BattleService.StartRoom / StopRoom
  房间生命周期控制

Matchmaking ──gRPC──► BattleService.GetNodeStats
  调度决策依据
```

---

## 八、性能与资源规格

### 8.1 单节点承载能力估算

| 指标 | 值 | 说明 |
|------|-----|------|
| **单房间 CPU** | ~5% / 核 | 60 FPS 帧循环 + KCP Update |
| **单房间内存** | ~10 MB | FrameSyncEngine + HistoryBuffer (300帧) |
| **单节点上限** | ~20 房间 | 预留 50% CPU 给突发和网络 IO |
| **网络带宽** | ~50 KB/s 每房间 | 双向：输入上报 + 同步广播 |

### 8.2 推荐部署配置

| 项目 | 建议配置 |
|------|----------|
| **运行时** | .NET 8, 自托管 KCP Server + ASP.NET Core gRPC |
| **容器资源** | 4 CPU / 4GB 内存 |
| **副本策略** | 按房间数水平扩展（非无状态，需 Sticky） |
| **网络** | **UDP 7777**（客户端 KCP 直连公网）+ **TCP**（内部 gRPC） |
| **磁盘** | 无持久化需求（日志除外） |

---

## 九、错误码定义

| 错误码 | 说明 |
|--------|------|
| `BATTLE_AUTH_FAILED` | KCP 连接后 Token 验证失败 |
| `BATTLE_ROOM_NOT_FOUND` | 请求的房间不存在于当前节点 |
| `BATTLE_ROOM_ALREADY_RUNNING` | 房间已启动 |
| `BATTLE_PLAYER_NOT_IN_ROOM` | 玩家不在该房间中 |
| `BATTLE_INVALID_FRAME` | 帧号非法（作弊/重放） |
| `BATTLE_INPUT_CHECKSUM_MISMATCH` | 输入校验失败 |
| `BATTLE_NODE_FULL` | 节点房间数已达上限 |

---

## 十、关键实现要点

1. **Token 验签缓存**：Battle Node 本地维护 `ConcurrentDictionary<string, (claims, expiry)>`，避免每帧都调用 Auth Service。JWT 过期前 5 分钟异步刷新。
2. **批量上报优化**：向 State Service 上报历史帧时，采用 **gRPC 流式 + 压缩**（Zstd），每 1 秒批量上报 60 帧，降低 RPC 开销。
3. **内存泄漏防护**：房间关闭后必须显式调用 `FrameSyncEngine.Dispose()` + `KcpSession.Dispose()`，并清空所有事件订阅。
4. **与原代码兼容性**：`FrameSyncEngine` 核心逻辑完全复用，仅需修改构造函数：将 `IGameConnection[] connections` 改为从 `RoomContext` 动态解析。
5. **UDP 端口暴露**：每个 Battle Node 需要独立的公网 UDP 端口（或端口范围），安全组需开放对应端口。
