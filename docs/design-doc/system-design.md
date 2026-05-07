# FOBackend 微服务系统设计（System Design）

> **文档定位**：在架构设计（Architecture Design）之下的详细系统设计层，回答"系统如何工作"。  
> 涵盖核心流程的时序图、领域模型、状态机、数据一致性策略、并发模型与容错设计。  
> **前置阅读**：[`microservices-overview.md`](./microservices-overview.md)、各 `*-service.md`。

---

## 一、系统上下文（System Context）

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              外部系统 / 用户                                  │
│                                                                             │
│   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐              │
│   │  Player  │   │  Player  │   │  Player  │   │  Admin   │              │
│   │ (Client) │   │ (Client) │   │ (Client) │   │ (Web)    │              │
│   └────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘              │
└───────┼──────────────┼──────────────┼──────────────┼─────────────────────┘
        │              │              │              │
        │  HTTP/gRPC   │  HTTP/gRPC   │  UDP/KCP     │  HTTP
        ▼              ▼              ▼              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           FOBackend 微服务系统                                │
│                                                                             │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────────┐ ┌──────────────┐   │
│  │ Auth Service │ │ Matchmaking  │ │   Battle Node    │ │ State Service│   │
│  │              │ │   Service    │ │                  │ │              │   │
│  │ • 注册/登录   │ │ • 房间管理   │ │ • KCP 帧同步     │ │ • 帧持久化   │   │
│  │ • JWT 签发   │ │ • 匹配队列   │ │ • 60 FPS 广播   │ │ • 断线补帧   │   │
│  │ • 档案管理   │ │ • 节点调度   │ │ • 房间生命周期  │ │ • 回放生成   │   │
│  └──────┬───────┘ └──────┬───────┘ └────────┬─────────┘ └──────┬───────┘   │
│         │                │                  │                  │          │
│         └────────────────┴──────────────────┴──────────────────┘          │
│                                    │                                       │
│                                    ▼                                       │
│                    ┌───────────────────────────────┐                       │
│                    │  PostgreSQL  │  Redis  │ S3/MinIO                     │
│                    └───────────────────────────────┘                       │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 1.1 外部实体清单

| 实体 | 角色 | 接入方式 | 说明 |
|------|------|----------|------|
| **Game Client** | 玩家客户端 | HTTP/gRPC + UDP/KCP | 登录/房间走 HTTP，对战走 KCP |
| **Admin Web** | 运营后台 | HTTP REST | 查询玩家、对局数据、强制踢人 |
| **PostgreSQL** | 关系数据库 | TCP | Auth/State/Matchmaking 持久化 |
| **Redis** | 缓存/消息总线 | TCP | 房间状态、Token 黑名单、Pub/Sub |
| **Object Storage** | 对象存储 | HTTP(S) | State Service 存储压缩帧数据 |

---

## 二、核心领域模型（Domain Model）

### 2.1 Auth Service 领域模型

```
┌──────────────────┐       ┌──────────────────┐       ┌──────────────────┐
│     Player       │1     N│   LoginHistory   │       │  RefreshToken    │
├──────────────────┤◄──────┼──────────────────┤       ├──────────────────┤
│ player_id (PK)   │       │ id (PK)          │       │ token_hash (PK)  │
│ player_name (UQ) │       │ player_id (FK)   │       │ player_id (FK)   │
│ password_hash    │       │ ip_address       │       │ device_id        │
│ email            │       │ client_version   │       │ expires_at       │
│ rating           │       │ login_at         │       │ created_at       │
│ total_games      │       │ success          │       └──────────────────┘
│ current_jti      │       └──────────────────┘
└──────────────────┘
```

**聚合根**：`Player`  
**不变式**：
- `player_name` 全局唯一
- `password_hash` 为 NULL 时表示游客账号
- `current_jti` 变更时，旧 Token 自动加入 Redis 黑名单（TTL 与过期时间一致）

### 2.2 Matchmaking Service 领域模型

```
┌──────────────────┐       ┌──────────────────┐
│      Room        │1     2│   PlayerSlot     │
├──────────────────┤◄──────┼──────────────────┤
│ room_id (PK)     │       │ player_id (PK)   │
│ game_mode        │       │ player_name      │
│ status           │       │ slot_number      │
│ host_player_id   │       │ is_ready         │
│ battle_node_id   │       │ join_time        │
│ invite_code (UQ) │       └──────────────────┘
│ created_at       │
└──────────────────┘
```

**聚合根**：`Room`（运行时存储于 Redis）  
**状态枚举**：`Waiting(0) → Ready(1) → Playing(2) → Finished(3)`  
**不变式**：
- 1v1 房间最多 2 个 `PlayerSlot`
- `status = Playing` 时不可加入新玩家
- 房主离开且房间未开始 → 房间解散

### 2.3 Battle Service 领域模型

```
┌──────────────────┐       ┌──────────────────┐       ┌──────────────────┐
│   RoomContext    │1     N│   KcpSession     │       │ FrameSyncEngine  │
├──────────────────┤◄──────┼──────────────────┤       ├──────────────────┤
│ room_id (PK)     │       │ conv (PK)        │       │ room_id          │
│ start_time       │       │ player_id        │       │ current_frame    │
│ game_mode        │       │ remote_endpoint  │       │ random_seed      │
│ random_seed      │       │ last_active      │       │ input_buffer     │
│ engine_ref       │       │ auth_state       │       │ history_buffer   │
└──────────────────┘       └──────────────────┘       └──────────────────┘
```

**聚合根**：`RoomContext`（纯内存，无持久化）  
**不变式**：
- 一个 `RoomContext` 绑定恰好一个 `FrameSyncEngine`
- 一个 `KcpSession`（conv）同时只能属于一个 `RoomContext`
- 房间启动后 `FrameSyncEngine` 以固定 16.67ms 间隔驱动，不可暂停（除非停止）

### 2.4 State Service 领域模型

```
┌──────────────────┐       ┌──────────────────┐
│  MatchHistory    │1     N│   FrameIndex     │
├──────────────────┤◄──────┼──────────────────┤
│ match_id (PK)    │       │ match_id (PK)    │
│ room_id          │       │ frame_number(PK) │
│ game_mode        │       │ file_offset      │
│ player1_id       │       │ compressed_size  │
│ player2_id       │       └──────────────────┘
│ winner_id        │
│ total_frames     │       ┌──────────────────┐
│ duration_sec     │       │  PlayerStats     │
│ end_reason       │       ├──────────────────┤
│ storage_path     │       │ player_id (PK)   │
│ has_replay       │       │ total_games      │
│ created_at       │       │ total_wins       │
└──────────────────┘       │ rating           │
                           │ updated_at       │
                           └──────────────────┘
```

**聚合根**：`MatchHistory`  
**不变式**：
- `match_id` 全局唯一，与 `room_id` 一一对应
- `storage_path` 指向的对象存储文件必须存在
- `FrameIndex` 是物化视图/缓存，可从 `frames.bin.zst` 重建

---

## 三、核心流程时序图

### 3.1 玩家登录 → 进入房间 → 开始对局

```
Client          AuthSvc         Matchmaking      BattleNode       StateSvc
  │               │                │                │                │
  │ ──① POST /login──────────────►│                │                │
  │               │                │                │                │
  │ ◄────────────② {jwt, pid}─────│                │                │
  │               │                │                │                │
  │ ──③ POST /rooms (JWT)────────►│                │                │
  │               │                │                │                │
  │               │                │──④ gRPC ValidateToken──────────►│
  │               │                │                │                │
  │               │                │◄───────────────⑤ valid──────────│
  │               │                │                │                │
  │               │                │──⑥ 创建 Redis 房间─────────────►│
  │               │                │                │                │
  │               │                │──⑦ gRPC StartRoom──────────────►│
  │               │                │                │                │
  │               │                │◄───────────────⑧ OK─────────────│
  │               │                │                │                │
  │ ◄─────────────⑨ {room_id,      │                │                │
  │                 battle_host:port}│                │                │
  │               │                │                │                │
  │ ──⑩ UDP KCP Handshake─────────►│                │                │
  │               │                │                │                │
  │ ──⑪ KCP AuthRequest───────────►│                │                │
  │               │                │                │                │
  │               │                │                │──⑫ gRPC ValidateToken
  │               │                │                │                │
  │               │                │                │◄──────────────⑬ valid
  │               │                │                │                │
  │ ◄─────────────⑭ KCP FrameSyncStart─────────────│                │
  │               │                │                │                │
  ═══════════════════════════════════════════════════════════════════════
  │               │                │                │                │
  │ ◄═════════════⑮ KCP FrameSyncPackage (60 FPS) ══════════════════════
  │ ─────────────►⑯ KCP PlayerInputReport          │                │
  │               │                │                │                │
  │               │                │                │──⑰ 每1秒 gRPC SaveFrames
  │               │                │                │                │
  │               │                │                │                │──⑱ 压缩写入S3
```

**关键设计点**：
- 步骤 ①~⑨ 全部走 TCP（HTTP/gRPC），可靠性由协议保证
- 步骤 ⑩ 起切换为 UDP/KCP，Battle Node 直接面向客户端
- 步骤 ⑫~⑬ 的 Token 验证可缓存（Battle Node 本地 JWT 签名缓存），避免每次连接都 RPC
- 步骤 ⑰~⑱ 为后台异步流，不影响帧同步主循环

### 3.2 断线重连（同节点）

```
Client                          BattleNode
  │                                 │
  │  [网络断开，KCP 超时]             │
  │                                 │
  │  [恢复网络]                      │
  │                                 │
  │ ──① UDP KCP Handshake─────────►│
  │ ◄────────────② ServerHello─────│
  │                                 │
  │ ──③ KCP ReconnectRequest──────►│
  │     {player_id, last_frame=420} │
  │                                 │
  │                                 │  ④ 查找 RoomContext
  │                                 │     验证 player_id
  │                                 │     新 conv 绑定到原 Room
  │                                 │     旧 conv 标记废弃
  │                                 │
  │ ◄────────────⑤ KCP ResendFrameResponse (frame 421~430)
  │                                 │
  │  ⑥ 客户端本地快进 10 帧恢复同步   │
  │                                 │
  ═══════════════════════════════════════════════════
  │ ◄════════════ 恢复 FrameSyncPackage 广播 ════════
```

**关键设计点**：
- 旧 `conv` 不立即删除，保留 5 秒（防网络抖动导致的重复包）
- 补发帧范围：`last_frame + 1` 到 `current_frame`
- 客户端收到补发帧后，在本地以 10 倍速快进模拟，追赶当前帧

### 3.3 断线重连（跨节点 — 原节点宕机）

```
Client          Matchmaking      NewBattleNode      StateSvc
  │               │                │                │
  │ [网络恢复]     │                │                │
  │               │                │                │
  │ ──① GET /reconnect?room_id──►│                │
  │               │                │                │
  │               │──② 查询原节点心跳──►│                │
  │               │◄───────────────③ 超时（宕机）    │
  │               │                │                │
  │               │──④ 分配新节点────►│                │
  │               │                │                │
  │               │                │──⑤ gRPC GetFramesForReconnect
  │               │                │   {match_id, from=420}
  │               │                │                │
  │               │                │◄───────────────⑥ 流式返回 frames
  │               │                │                │
  │◄──────────────⑦ {new_host:port}│                │
  │               │                │                │
  │ ──⑧ KCP Handshake ───────────►│                │
  │               │                │                │
  │               │                │──⑨ 重建 Engine
  │               │                │   注入历史帧 420~N
  │               │                │   启动广播    │
  │               │                │                │
  │◄═════════════⑩ FrameSyncPackage (从 N+1 继续) ════
```

**关键设计点**：
- Matchmaking 维护 `room_id → battle_node_id` 的绑定记录（Redis），用于快速定位原节点
- 新节点从 State Service 拉取完整历史帧（最坏情况：丢失最近 1 秒 = 60 帧）
- 对局期间的其他玩家不受影响，继续在当前帧同步

---

## 四、状态机详图

### 4.1 房间生命周期状态机（Matchmaking Service）

```
                          ┌───────────┐
                          │  Waiting  │◄────────────────────────────┐
                          │ (等待玩家) │                             │
                          └─────┬─────┘                             │
                                │ 玩家2加入                           │
                                ▼                                   │
                          ┌───────────┐                             │
                    ┌────►│   Ready   │                             │
                    │     │ (等待准备) │                             │
                    │     └─────┬─────┘                             │
         房主离开/超时 │           │ 双方 Ready                      │
                    │     ┌─────┘                                   │
                    │     ▼                                         │
                    │     ┌───────────┐     对局结束                 │
                    │     │  Playing  │─────────────────────────────┘
                    │     │ (对局中)   │
                    │     └─────┬─────┘
                    │           │ 玩家掉线/异常
                    └───────────┘
                          │
                          ▼
                    ┌───────────┐
                    │ Finished  │
                    │ (已结束)   │
                    └───────────┘
```

| 转换 | 触发条件 | 副作用 |
|------|----------|--------|
| `Waiting → Ready` | 第二名玩家 `JoinRoom` 成功 | — |
| `Ready → Playing` | 双方 `SetReady(true)` | Matchmaking → BattleNode `StartRoom` |
| `Playing → Finished` | BattleNode 上报 `ReportRoomClosed` | 房间数据归档 PostgreSQL，Redis TTL 延迟清理 |
| `Ready → Waiting` | 玩家2离开，仅剩房主 | — |
| `任意 → Finished` | 房主离开 / 超时(>1小时) | 若已在 Playing，通知 BattleNode `StopRoom` |

### 4.2 KCP 会话状态机（Battle Service）

```
                    ┌─────────────┐
                    │   Init      │
                    │ (conv 已分配)│
                    └──────┬──────┘
                           │ 收到 AuthRequest
                           ▼
                    ┌─────────────┐
                    │  Authenticating │
                    │ (验证 JWT...)  │
                    └──────┬──────┘
                           │ 验证通过
                           ▼
                    ┌─────────────┐
              ┌────►│  Connected  │
              │     │ (帧同步中)   │
              │     └──────┬──────┘
              │            │ 收到 ReconnectRequest
              │            ▼
              │     ┌─────────────┐
              └─────┤ Reconnecting│
                    │ (换 conv)    │
                    └─────────────┘
                           │ 超时 / 验证失败
                           ▼
                    ┌─────────────┐
                    │  Disposed   │
                    │ (资源释放)   │
                    └─────────────┘
```

| 转换 | 触发条件 | 副作用 |
|------|----------|--------|
| `Init → Authenticating` | 收到首个 `AuthRequest` | 启动 JWT 验证（本地缓存或 RPC Auth） |
| `Authenticating → Connected` | `ValidateToken` 返回 valid | 绑定 `player_id → conv → RoomContext` |
| `Authenticating → Disposed` | Token 无效或超时(5s) | 发送 `AuthFailed`，释放 conv |
| `Connected → Reconnecting` | 收到 `ReconnectRequest` | 生成新 conv，旧 conv 进入 5s 宽限期 |
| `Reconnecting → Connected` | 补发帧完成 | 恢复正常帧同步广播 |
| `任意 → Disposed` | KCP 超时(30s) 无心跳 | 通知 Engine 玩家掉线，上报 Matchmaking |

---

## 五、数据一致性设计

### 5.1 跨服务事务：房间创建与 Battle Node 启动

**问题**：`CreateRoom` 需要同时完成 Redis 房间创建和 Battle Node `StartRoom`，如何保证一致性？

**方案：Saga 模式（编排式）**

```
Matchmaking Service (Saga Orchestrator)
  │
  ├── Step 1: Redis 创建房间
  │     失败 → 直接返回错误，无副作用
  │
  ├── Step 2: gRPC 调用 BattleNode.StartRoom
  │     成功 → 房间状态 = Ready
  │     失败 → 执行补偿：Redis 删除房间，返回 "节点不可用"
  │
  └── Step 3: 等待双方 Ready
        成功 → 房间状态 = Playing
        失败 → 执行补偿：gRPC BattleNode.StopRoom + Redis 删除房间
```

**补偿机制**：
- 所有补偿操作必须幂等（`StopRoom` 对不存在的 room_id 返回成功）
- 补偿失败时写入死信队列，人工介入

### 5.2 最终一致性：战绩更新

**问题**：对局结束后，`total_games` / `total_wins` / `rating` 需要更新，但不应阻塞 Battle Node 的房间关闭流程。

**方案：事件驱动 + 异步消费**

```
Battle Node ──gRPC──► Matchmaking.ReportRoomClosed
                           │
                           ▼
                    Redis Pub/Sub `match:finished:{match_id}`
                           │
              ┌────────────┼────────────┐
              ▼            ▼            ▼
        Auth Service   State Service   (其他消费者)
        ────────────   ─────────────
        更新 Player    保存 MatchMetadata
        的 rating      到 PostgreSQL
```

**保证**：
- 消息至少投递一次（Redis Pub/Sub 不重传，但可改为 Redis Stream 保证）
- 消费者幂等：`UPDATE player_stats SET total_games = total_games + 1 WHERE player_id = ?` 天然幂等
- 延迟可接受：战绩更新延迟 1~5 秒不影响游戏体验

### 5.3 State Service 帧数据一致性

**问题**：Battle Node 每 1 秒上报 60 帧，如果上报过程中 Battle Node 崩溃，数据是否完整？

**方案**：
- `SaveFrames` 的 `is_final_batch = true` 时，State Service 做一次最终 `Flush` 并校验 `frame_count`
- 若 Battle Node 崩溃导致最后一批未上报，State Service 从已有数据中感知缺失（`max(frame_number)` < `total_frames`），标记为 `end_reason = ServerShutdown`
- 断线重连时，若请求帧超出存储范围，返回 `STATE_FRAMES_NOT_AVAILABLE`，客户端回退到初始状态重新同步

---

## 六、并发与线程模型

### 6.1 Battle Service：单线程帧循环 + IO 线程池

```
┌─────────────────────────────────────────────────────────────┐
│                     Battle Node 进程                         │
│                                                             │
│  ┌─────────────────┐     ┌─────────────────────────────┐   │
│  │   UDP Receiver  │     │     Frame Sync Thread       │   │
│  │   (Thread Pool) │     │     (单线程，每 16.67ms)      │   │
│  │                 │     │                             │   │
│  │ 接收 KCP 原始包   │────►│ 1. 收集所有玩家 Input        │   │
│  │ 解包 → KcpInput  │     │ 2. Engine.Tick()            │   │
│  └─────────────────┘     │ 3. 生成 FrameSyncPackage     │   │
│                          │ 4. 广播到所有 KcpSession      │   │
│  ┌─────────────────┐     └─────────────────────────────┘   │
│  │  gRPC Server    │                    │                   │
│  │  (Thread Pool)  │                    │                   │
│  │                 │                    ▼                   │
│  │ StartRoom/Stop  │     ┌─────────────────────────────┐   │
│  │  Room 控制      │     │   Background Upload Task    │   │
│  └─────────────────┘     │   (Channel + Task)          │   │
│                          │                             │   │
│                          │ 从 Channel 消费帧数据         │   │
│                          │ 批量压缩 → gRPC SaveFrames    │   │
│                          └─────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

**关键约束**：
- `Frame Sync Thread` 必须是单线程，避免锁竞争导致的帧时间抖动
- UDP Receiver 和 gRPC Server 使用 .NET Thread Pool，与帧循环通过无锁队列（`Channel<T>`）通信
- `Broadcast` 操作将 `FrameSyncPackage` 加入每个 `KcpSession` 的发送队列，由 KCP 内部定时器驱动实际 UDP 发送

### 6.2 Matchmaking Service：无状态 + Redis 乐观锁

```
┌─────────────────────────────────────────────────────────────┐
│                  Matchmaking Service                        │
│                                                             │
│  gRPC Request ──► Handler ──► Redis Lua Script ──► Response │
│                                                             │
│  Lua 脚本保证原子性：                                         │
│  1. 检查 player 是否已在其他房间                               │
│  2. 检查 room 是否已满/已开始                                  │
│  3. 写入 room:{id}:players                                    │
│  4. 写入 player_room:{pid}                                    │
│  5. 返回结果                                                  │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

**关键约束**：
- 所有 Redis 写操作通过 Lua 脚本或 `WATCH/MULTI/EXEC` 事务执行，防止并发竞态
- 无本地状态，任意实例均可处理任意请求，Nginx/YARP 轮询负载均衡即可

### 6.3 State Service：流式处理 + 背压控制

```
┌─────────────────────────────────────────────────────────────┐
│                    State Service                             │
│                                                             │
│  gRPC SaveFrames (stream) ──► FrameBuffer ──► Compressor   │
│                                    │             │          │
│                                    │             ▼          │
│                                    │      Object Storage    │
│                                    │             │          │
│                                    │             ▼          │
│                                    │      PostgreSQL        │
│                                    │      (元数据)           │
│                                    │                        │
│  gRPC GetFrames (stream) ◄─────────┘                        │
│       从 Object Storage 读取                                  │
│       解压 → 过滤 → 流式返回                                   │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

**关键约束**：
- `FrameBuffer` 使用有界通道（Bounded Channel），防止 Battle Node 上报过快导致 OOM
- 背压触发时，gRPC 流自动阻塞（`Channel.Writer.TryWrite` 返回 false），Battle Node 侧 `await WriteAsync` 挂起
- 解压/过滤操作在后台线程执行，不阻塞 gRPC 接收流

---

## 七、容错设计

### 7.1 服务级容错

| 故障场景 | 检测方式 | 恢复策略 | 影响 |
|----------|----------|----------|------|
| **Auth Service 宕机** | gRPC 健康检查失败 | 客户端无法登录/注册；已登录用户凭缓存 JWT 继续游戏 | 高（阻断新用户） |
| **Matchmaking 宕机** | gRPC 健康检查失败 | 无法创建/加入房间；进行中对局不受影响 | 中（阻断新对局） |
| **Battle Node 宕机** | Redis 心跳超时(15s) | Matchmaking 分配新节点，State Service 补帧 | 低（房间级，秒级恢复） |
| **State Service 宕机** | gRPC 健康检查失败 | Battle Node 帧数据本地缓存，恢复后批量补报 | 低（断线重连延迟，不丢数据） |
| **Redis 宕机** | Sentinel 故障转移 | Matchmaking 降级为只读模式，禁止创建房间 | 中（房间功能中断） |
| **PostgreSQL 宕机** | 连接超时 | Auth/State 只读缓存模式，禁止注册/新对局归档 | 高（持久化中断） |

### 7.2 客户端容错

```
Client 本地状态机：

[Idle] ──登录成功──► [Lobby] ──创建/加入房间──► [Room]
                                            │
                                            │ 双方 Ready
                                            ▼
                                       [Battle]
                                            │
                          ┌─────────────────┼─────────────────┐
                          │                 │                 │
                          ▼                 ▼                 ▼
                    [Reconnecting]    [Reconnecting]    [Finished]
                    (同节点)            (跨节点)
                          │                 │
                          └─────────────────┘
                                            │
                                            ▼
                                       [Battle] 恢复同步
```

**客户端行为**：
- 断线后，先尝试原 Battle Node 地址（KCP Handshake）
- 5 秒内无响应 → 向 Matchmaking 请求新的 `battle_node_host:port`
- 收到新地址后重新 Handshake，发送 `ReconnectRequest` 并声明 `last_frame`
- 最多重试 3 次，全部失败则回到 Lobby 并提示 "连接失败"

### 7.3 幂等性保证

| 操作 | 幂等键 | 实现方式 |
|------|--------|----------|
| `Authenticate` | `device_id` + 时间窗口 | 同一设备 5 秒内重复请求返回相同 Token |
| `CreateRoom` | `player_id` + 时间窗口 | Redis `SET room_creating:{pid} NX EX 5` |
| `StartRoom` | `room_id` | Battle Node 本地字典已存在则返回成功 |
| `SaveFrames` | `match_id` + `start_frame` | 对象存储文件名包含帧范围，重复上传覆盖 |
| `SaveMatchMetadata` | `match_id` | PostgreSQL `INSERT ... ON CONFLICT DO NOTHING` |

---

## 八、性能模型

### 8.1 请求延迟预算（P99）

| 路径 | 延迟预算 | 实际估算 | 说明 |
|------|----------|----------|------|
| Client → Auth (登录) | < 200ms | ~50ms | 1 次 DB 查询 + bcrypt |
| Client → Matchmaking (创建房间) | < 100ms | ~20ms | Redis 操作 + 1 次 gRPC |
| Matchmaking → Battle (StartRoom) | < 50ms | ~10ms | 内网 gRPC |
| Client → Battle (KCP Handshake) | < 100ms | ~30ms | UDP RTT + 内存操作 |
| Battle → Client (帧广播) | < 16.67ms | ~5ms | 目标：60 FPS 无感知 |
| Battle → State (SaveFrames) | < 500ms | ~100ms | 后台异步，不阻塞主循环 |
| State → Client (断线补帧) | < 1s | ~300ms | 对象存储读取 + 解压 |

### 8.2 容量估算

**假设**：日活 10,000 人，峰值同时在线 2,000 人，每局 2 人，平均对局时长 5 分钟。

| 指标 | 计算 | 结果 |
|------|------|------|
| 峰值并发房间数 | 2,000 / 2 | ~1,000 房间 |
| Battle Node 数量 | 1,000 / 20 (单节点上限) | ~50 节点 |
| 峰值帧广播 QPS | 1,000 房间 × 60 FPS × 2 玩家 | 120,000 包/秒 |
| 日对局数 | 10,000 × 10 局/人 | 100,000 局 |
| 日存储增长 | 100,000 局 × 5 分钟 × 60 帧 × 200 字节 × 压缩比 5:1 | ~12 GB |
| State Service 峰值带宽 | 1,000 房间 × 60 帧/秒 × 200 字节 | ~12 MB/s 入站 |

---

## 九、安全设计

### 9.1 认证链

```
Client ──JWT──► Matchmaking API Gateway
  │               └── 验证签名（本地缓存公钥）
  │
  └──JWT──► Battle Node KCP
            └── 首次验证：gRPC → AuthService.ValidateToken
            └── 后续：本地缓存（player_id → expiry）
            └── 每 5 分钟异步刷新
```

### 9.2 防作弊要点

| 风险 | 防护措施 |
|------|----------|
| 伪造帧输入 | Battle Node 校验 `input_checksum`，与历史模式比对异常 |
| 快进/慢放 | 服务器帧号为权威来源，客户端帧号必须跟随服务器 |
| 重放攻击 | `FrameSyncPackage` 包含单调递增的 `server_time`，旧包被丢弃 |
| Token 窃取 | JWT 有效期仅 2 小时，Refresh Token 绑定设备 ID |
| 脚本刷匹配 | Matchmaking API 限流：单 IP 10 次/秒，单玩家 1 次/秒 |

---

## 十、演进路线图

| 阶段 | 目标 | 关键动作 |
|------|------|----------|
| **Phase 1** | 单体拆分 | Auth/Matchmaking 先拆，Battle 保持单体但接口化 |
| **Phase 2** | 帧同步服务化 | Battle 拆出独立节点，支持多实例 |
| **Phase 3** | 状态持久化 | State Service 上线，支持断线重连与回放 |
| **Phase 4** | 自动化运维 | 引入 K8s + HPA，Battle Node 按 CPU/房间数自动扩缩容 |
| **Phase 5** | 全球部署 | Battle Node 区域化部署，Matchmaking 按延迟调度最近节点 |

---

**文档版本**: v1.0  
**最后更新**: 2026-04-30  
**作者**: AI Assistant
