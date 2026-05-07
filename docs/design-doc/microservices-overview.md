# FOBackend 微服务架构总览

> **文档定位**：微服务架构的入口文档，描述 4 个核心服务之间的职责边界、依赖关系与数据流转。  
> **配套文档**：各服务的详细设计请阅读同目录下的 `*-service.md`。

---

## 一、架构全景

```
                               ┌─────────────┐
                               │   Client    │
                               └──────┬──────┘
                                      │
         ┌────────────────────────────┼────────────────────────────┐
         │                            │                            │
         ▼                            ▼                            ▼
  ┌──────────────┐          ┌──────────────┐            ┌──────────────────┐
  │ Auth Service │          │ Matchmaking  │            │   Battle Node    │
  │  (TCP)       │          │ Service      │            │   (UDP+TCP)      │
  │  登录/鉴权    │          │  (TCP)       │            │   帧同步核心      │
  └──────┬───────┘          │  房间/匹配    │            └───────┬──────────┘
         │                  └──────┬───────┘                    │
         │                         │                            │
    gRPC │                    gRPC │                       gRPC │
         │                         │                            │
         │    ┌────────────────────┼────────────────────┐       │
         │    │                    │                    │       │
         └───►│◄───────────────────┘                    │◄──────┘
              │                                         │
              ▼                                         ▼
     ┌─────────────────┐                       ┌─────────────────┐
     │  PostgreSQL     │                       │  State Service  │
     │  (玩家/账号)     │                       │  (TCP)          │
     └─────────────────┘                       │  帧存储/回放     │
                                               └───────┬─────────┘
                                                       │
                                          ┌────────────┴────────────┐
                                          ▼                         ▼
                                   ┌─────────────┐         ┌─────────────┐
                                   │ MinIO / S3  │         │ PostgreSQL  │
                                   │ (帧数据压缩)  │         │ (对局元数据)  │
                                   └─────────────┘         └─────────────┘
```

### 一句话速览

| 服务 | 英文 | 核心一句话 | 协议 | 存储 |
|------|------|-----------|------|------|
| **鉴权服务** | Auth Service | 谁是你？签发和验证 JWT，管理玩家档案 | HTTP/gRPC **(TCP)** | PostgreSQL |
| **匹配服务** | Matchmaking Service | 去哪里玩？创建/加入房间，调度 Battle Node | HTTP/gRPC **(TCP)** | Redis + PostgreSQL |
| **对战服务** | Battle Service | 实时打！60 FPS 帧同步，KCP 直连客户端 | KCP/UDP **(客户端)** + gRPC **(内部 TCP)** | 纯内存 |
| **状态服务** | State Service | 存下来！历史帧压缩存储，支持断线重连与回放 | gRPC **(TCP)** | MinIO/S3 + PostgreSQL |

> **协议原则**：只有 Battle Service 对外暴露 UDP/KCP（实时帧同步必须低延迟），其余所有服务只使用基于 **TCP** 的 HTTP/gRPC，利用 TCP 的可靠性和成熟运维生态。

---

## 二、服务间依赖关系

### 2.1 依赖拓扑图

```
                        ┌─────────────┐
                        │   Client    │
                        └──────┬──────┘
                               │
          ┌────────────────────┼────────────────────┐
          │                    │                    │
          ▼                    ▼                    ▼
   ┌──────────────┐   ┌──────────────┐    ┌──────────────────┐
   │ Auth Service │   │ Matchmaking  │    │   Battle Node    │
   │              │   │   Service    │    │                  │
   │  ◄───────────┼───┼──────────────┤◄───┼──────────┐       │
   │  ValidateToken│   │  ValidateToken│   │          │       │
   └──────┬───────┘   └──────┬───────┘    │    ┌─────┘       │
          │                  │             │    │             │
          │ ValidateToken    │ StartRoom   │    │ SaveFrames  │
          │ GetProfile       │ ReportRoom  │    │ GetFrames   │
          │                  │   Closed    │    │             │
          │                  │             │    ▼             │
          │         ┌────────┴────────┐    ┌──────────────────┐
          └────────►│  State Service  │◄───┘                  │
                    │                 │                       │
                    └─────────────────┘                       │
```

### 2.2 调用矩阵

| 调用方 ↓ \ 被调用方 → | Auth Service | Matchmaking Service | Battle Service | State Service |
|----------------------|:------------:|:-------------------:|:--------------:|:-------------:|
| **Auth Service** | — | — | — | `UpdateStats` (可选) |
| **Matchmaking Service** | `ValidateToken` | — | `StartRoom` | — |
| **Battle Service** | `ValidateToken` | `ReportRoomClosed` | — | `SaveFrames` (流式) |
| **State Service** | `ValidateToken` | — | — | — |
| **Client** | `Login/Register` | `CreateRoom/JoinRoom` | KCP 帧同步 | `GetReplay/ListMatches` |

> **通信方式**：服务间全部为 **gRPC over TCP**。Battle → State 的 `SaveFrames` 使用 **gRPC Client Stream**，以高效传输大批量帧数据。

### 2.3 异步事件（Redis Pub/Sub）

除同步 gRPC 调用外，以下场景使用 Redis 解耦：

| Channel | 发布者 | 订阅者 | 触发时机 |
|---------|--------|--------|----------|
| `room:assigned` | Matchmaking | Battle Node | 房间创建完成，通知目标节点接管 |
| `room:closed` | Battle Node | Matchmaking | 对局结束，Matchmaking 归档房间 |
| `player:disconnected` | Battle Node | Matchmaking, State | 玩家断线，触发超时踢出或数据保存 |

---

## 三、数据流总览

### 3.1 正常对局生命周期

```
Client ──TCP──► Auth Service
  │              1. POST /login → 返回 JWT
  │
  └──TCP──► Matchmaking Service
  │         2. POST /rooms (Header: JWT)
  │         3. 返回 { room_id, battle_node_host, battle_node_port }
  │
  └──UDP/KCP──► Battle Node
  │             4. KCP Handshake → ServerHello(conv)
  │             5. KCP AuthRequest (携带 JWT)
  │             6. Battle Node ──gRPC──► Auth.ValidateToken
  │             7. KCP FrameSyncStart → 帧循环开始
  │             8. [每 60 帧] Battle Node ──gRPC(stream)──► State.SaveFrames
  │             9. 对局结束
  │             10. Battle Node ──gRPC──► Matchmaking.ReportRoomClosed
  │             11. Battle Node ──gRPC──► State.SaveMatchMetadata
  │
  └──TCP──► State Service
            12. GET /replay/{match_id} (可选，战后查回放)
```

### 3.2 断线重连数据流

```
Client 断线
  │
  ├── 本地缓存最后收到的帧号 N
  │
  └── 重新 KCP Handshake → Battle Node (原节点或新节点)
       │
       ├─ 节点存活 ──► 从内存环形缓冲区补发 N 之后的帧
       │
       └─ 节点已宕 ──► Matchmaking 分配新 Battle Node
                     新节点 ──gRPC(stream)──► State.GetFramesForReconnect
                     从对象存储拉取缺失帧，恢复同步
```

### 3.3 各服务数据归属

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Auth Service                                                           │
│    └── PostgreSQL: players, login_history                               │
├─────────────────────────────────────────────────────────────────────────┤
│  Matchmaking Service                                                    │
│    ├── Redis: room:{id}, invite:{code}, matchmaking:queue:{mode}        │
│    └── PostgreSQL: room_archive (历史归档，非关键路径)                   │
├─────────────────────────────────────────────────────────────────────────┤
│  Battle Service                                                         │
│    └── 内存: FrameSyncEngine, KcpSession, CircularBuffer<Frame>         │
│        (房间关闭即销毁，零持久化)                                         │
├─────────────────────────────────────────────────────────────────────────┤
│  State Service                                                          │
│    ├── MinIO/S3: s3://replays/{match_id}/frames.bin.zst                 │
│    └── PostgreSQL: match_history (元数据索引)                            │
└─────────────────────────────────────────────────────────────────────────┘
```

> **设计原则**：Battle Service 不做任何持久化，确保单节点崩溃时可通过 State Service 重建房间状态，实现快速故障转移。

---

## 四、客户端接入总览

```
┌─────────┐
│  Client │
└────┬────┘
     │
     │  ① TCP  POST /auth/login
     │      ─────────────────────────►  Auth Service
     │  ◄────────────────────────────   { jwt_token, player_id }
     │
     │  ② TCP  POST /matchmaking/rooms (Authorization: Bearer JWT)
     │      ─────────────────────────►  Matchmaking Service
     │  ◄────────────────────────────   { room_id, battle_node: "1.2.3.4:7777" }
     │
     │  ③ UDP  KCP Handshake ───────►  Battle Node
     │  ◄────────────────────────────   ServerHello(conv)
     │
     │  ④ UDP  KCP FrameSyncStart ◄──  帧循环 (60 FPS)
     │
     ══════════════════════════════════════ 实时对局 ════════════════════════
     │
     │  ⑤ 断线后
     │     UDP  KCP ReconnectRequest ─► Battle Node
     │     ◄── ResendFrameResponse ────  补发缺失帧，恢复同步
```

### 端口暴露策略

| 服务 | 对外端口 | 协议 | 说明 |
|------|---------|------|------|
| Auth Service | `8081` | TCP | 公网开放，HTTPS 终结 |
| Matchmaking Service | `8082` | TCP | 公网开放，HTTPS 终结 |
| Battle Node | `7777` | **UDP** | 公网开放，KCP 直连客户端 |
| Battle Node | `9081` | TCP | 仅内网/管理面，gRPC |
| State Service | `8084` | TCP | 公网开放（回放查询），或仅内网 |

---

## 五、关键技术决策

### 5.1 为什么只有 Battle Service 用 UDP/KCP？

| 维度 | Auth / Matchmaking / State | Battle Service |
|------|---------------------------|----------------|
| **延迟要求** | 百毫秒级可接受 | 必须 < 16.67ms (60 FPS) |
| **可靠性要求** | 登录失败可重试，房间创建不能丢包 | 丢帧可预测/补发，但延迟不可忍 |
| **连接模型** | 请求-响应，短连接 | 长连接，持续双向流 |
| **运维生态** | TCP + TLS + 负载均衡成熟 | KCP 需直连，穿透/防火墙更复杂 |

**结论**：控制面（登录、房间管理、数据存储）走 TCP 保可靠；数据面（实时帧同步）走 KCP 保低延迟。

### 5.2 Battle Node 宕机如何容灾？

1. **Matchmaking** 通过心跳检测到节点失联
2. **Matchmaking** 将该房间绑定到新的 Battle Node
3. **新 Battle Node** 从 **State Service** 拉取完整历史帧（`GetFramesForReconnect`）
4. **Client** 重新 KCP Handshake 到新节点地址
5. **新 Battle Node** 下发缺失帧，客户端本地快进恢复

> 核心保障：**State Service** 每 1 秒接收一次 Battle Node 的帧快照，最坏情况下丢失 1 秒数据（约 60 帧）。

### 5.3 服务发现与负载均衡

```
┌─────────────────┐
│   Consul / etcd │
│   ───────────── │
│   Auth Service  │  健康检查 + KV 存储
│   Matchmaking   │  无状态，任意扩缩容
│   Battle Node   │  注册节点 ID + 负载指标
│   State Service │  无状态，任意扩缩容
└─────────────────┘
```

- **Auth / Matchmaking / State**：无状态，通过标准 HTTP/gRPC 负载均衡器（Nginx/YARP/Envoy）横向扩展
- **Battle Node**：有状态（房间绑定节点），由 Matchmaking 根据节点负载（CPU/内存/房间数）主动调度，客户端直连已分配节点

---

## 六、部署拓扑示例

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         公网 (Internet)                                  │
│                                                                         │
│   ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────────┐  │
│   │  Auth Svc    │  │ Matchmaking  │  │      Battle Node Cluster     │  │
│   │  :8081 (TCP) │  │ Svc :8082    │  │  ┌─────┐ ┌─────┐ ┌─────┐   │  │
│   │  x2 replicas │  │ (TCP) x2     │  │  │:7777│ │:7777│ │:7777│   │  │
│   └──────────────┘  └──────────────┘  │  └─────┘ └─────┘ └─────┘   │  │
│                                       └──────────────────────────────┘  │
│                                                                         │
│   ┌──────────────┐                                                      │
│   │  State Svc   │                                                      │
│   │  :8084 (TCP) │                                                      │
│   │  x2 replicas │                                                      │
│   └──────────────┘                                                      │
└─────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                         内网 (VPC / Overlay)                             │
│                                                                         │
│   ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────────┐  │
│   │ PostgreSQL   │  │    Redis     │  │        MinIO Cluster         │  │
│   │ (主从复制)    │  │  (Sentinel)  │  │      (对象存储)               │  │
│   └──────────────┘  └──────────────┘  └──────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 七、阅读指引

| 你想了解 | 阅读文档 |
|---------|---------|
| JWT 策略、玩家注册/登录流程、Token 刷新 | [`auth-service.md`](./auth-service.md) |
| 房间状态机、邀请码、ELO 匹配、Battle 节点调度 | [`matchmaking-service.md`](./matchmaking-service.md) |
| KCP 连接管理、帧同步引擎、房间生命周期、断线重连 | [`battle-service.md`](./battle-service.md) |
| 帧数据压缩存储、回放生成、断线补帧查询 | [`state-service.md`](./state-service.md) |
| 单体到微服务的迁移路径、Docker Compose 编排 | [`../ARCHITECTURE.md`](../ARCHITECTURE.md) 第 11 章 |

---

**文档版本**: v1.0  
**最后更新**: 2026-04-30  
**作者**: AI Assistant
