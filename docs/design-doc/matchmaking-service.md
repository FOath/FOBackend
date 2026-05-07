# Matchmaking Service 设计文档

> **服务定位**：房间管理与玩家匹配微服务  
> **服务类型**：有状态（Redis）+ 无状态 HTTP/gRPC 服务  
> **对外协议**：**HTTP/gRPC over TCP**（不使用 KCP/UDP）  
> **数据存储**：Redis（房间运行时状态）+ PostgreSQL（房间持久化归档）  
> **核心依赖**：`FOBackend.Protocol`（共享消息库）

---

## 一、职责边界

### 1.1 核心功能

| 功能模块 | 说明 |
|----------|------|
| **房间生命周期** | 创建、加入、离开、销毁房间 |
| **邀请码系统** | 生成 6 位字母数字邀请码，支持通过邀请码加入 |
| **准备状态管理** | 玩家 Ready/Unready，全员 Ready 后触发对局启动 |
| **Battle 节点调度** | 为房间分配合适的 Battle Node（负载均衡） |
| **匹配队列** | 基于 ELO 等级的 1v1 自动匹配（可选） |
| **房间状态查询** | 房间列表、房间详情、玩家当前房间查询 |

### 1.2 不包含的职责

- ❌ 玩家认证 → Auth Service
- ❌ 帧同步逻辑 → Battle Service
- ❌ 历史帧存储 → State Service

---

## 二、架构设计

> **协议选择**：Matchmaking Service **仅使用基于 TCP 的 HTTP/gRPC**，不暴露任何 UDP/KCP 端口。
> 原因：房间管理属于控制面操作，请求频率低、可靠性要求高，TCP + gRPC 的连接管理、流控和成熟的服务网格生态更适合。仅向客户端返回 Battle Node 的 KCP 地址，自身不处理 KCP 流量。

```
┌─────────────────────────────────────────────────────────────┐
│                  Matchmaking Service                        │
│                                                             │
│  ┌─────────────────┐    ┌─────────────────────────────────┐ │
│  │   gRPC API      │    │        Application Layer         │ │
│  │                 │    │  ┌──────────┐ ┌──────────────┐  │ │
│  │  CreateRoom     │───►│  │ RoomMgmt │ │ Battle调度   │  │ │
│  │  JoinRoom       │    │  └──────────┘ └──────────────┘  │ │
│  │  LeaveRoom      │    │  ┌──────────┐ ┌──────────────┐  │ │
│  │  SetReady       │    │  │ Matchmaking│ │ InviteCode  │  │ │
│  │  GetRoom        │    │  └──────────┘ └──────────────┘  │ │
│  └─────────────────┘    └─────────────────────────────────┘ │
│           │                              │                  │
│           │                    ┌─────────▼──────────┐       │
│           │                    │  IBattleNodeRegistry │       │
│           │                    │  (节点发现+负载均衡)   │       │
│           │                    └─────────┬──────────┘       │
│           │                              │                  │
│  ┌────────▼──────────┐    ┌─────────────▼──────────────┐   │
│  │   Redis           │    │        Consul / etcd        │   │
│  │  (房间运行时状态)   │    │    (Battle Node 服务发现)    │   │
│  └───────────────────┘    └─────────────────────────────┘   │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

---

## 三、接口定义

### 3.1 gRPC 服务定义 (`.proto`)

```protobuf
syntax = "proto3";
package fobackend.matchmaking;

service MatchmakingService {
  // 创建房间
  rpc CreateRoom (CreateRoomRequest) returns (CreateRoomResponse);
  
  // 加入房间（通过 room_id 或 invite_code）
  rpc JoinRoom (JoinRoomRequest) returns (JoinRoomResponse);
  
  // 离开房间
  rpc LeaveRoom (LeaveRoomRequest) returns (LeaveRoomResponse);
  
  // 设置准备状态
  rpc SetReady (SetReadyRequest) returns (SetReadyResponse);
  
  // 查询房间信息
  rpc GetRoom (GetRoomRequest) returns (GetRoomResponse);
  
  // 查询玩家当前房间
  rpc GetPlayerRoom (GetPlayerRoomRequest) returns (GetPlayerRoomResponse);
  
  // 加入匹配队列
  rpc JoinMatchmaking (JoinMatchmakingRequest) returns (JoinMatchmakingResponse);
  
  // 取消匹配
  rpc CancelMatchmaking (CancelMatchmakingRequest) returns (CancelMatchmakingResponse);
  
  // 通知：Battle Node 报告房间已关闭（供 Battle 回调）
  rpc ReportRoomClosed (ReportRoomClosedRequest) returns (ReportRoomClosedResponse);
}

message CreateRoomRequest {
  string player_id = 1;
  string player_name = 2;
  int32 game_mode = 3;
  map<string, string> options = 4;
}

message CreateRoomResponse {
  string room_id = 1;
  string invite_code = 2;
  string battle_node_host = 3;
  int32 battle_node_port = 4;
  string battle_node_id = 5;
}

message JoinRoomRequest {
  string player_id = 1;
  string player_name = 2;
  oneof join_method {
    string room_id = 3;
    string invite_code = 4;
  }
}

message JoinRoomResponse {
  bool success = 1;
  string room_id = 2;
  RoomInfo room = 3;
  string battle_node_host = 4;
  int32 battle_node_port = 5;
  string error_message = 6;
}

message LeaveRoomRequest {
  string player_id = 1;
  string room_id = 2;
}

message LeaveRoomResponse {
  bool success = 1;
}

message SetReadyRequest {
  string player_id = 1;
  string room_id = 2;
  bool is_ready = 3;
}

message SetReadyResponse {
  bool success = 1;
  bool all_ready = 2;  // 是否双方都已准备
}

message GetRoomRequest {
  string room_id = 1;
}

message GetRoomResponse {
  RoomInfo room = 1;
}

message GetPlayerRoomRequest {
  string player_id = 1;
}

message GetPlayerRoomResponse {
  string room_id = 1;
  RoomInfo room = 2;
}

message JoinMatchmakingRequest {
  string player_id = 1;
  int32 game_mode = 2;
  int32 rating = 3;  // ELO 等级分
}

message JoinMatchmakingResponse {
  bool success = 1;
  string queue_position = 2;
}

message CancelMatchmakingRequest {
  string player_id = 1;
}

message CancelMatchmakingResponse {
  bool success = 1;
}

message ReportRoomClosedRequest {
  string room_id = 1;
  string battle_node_id = 2;
  int32 end_reason = 3;
  int32 total_frames = 4;
}

message ReportRoomClosedResponse {
  bool success = 1;
}

message RoomInfo {
  string room_id = 1;
  int32 game_mode = 2;
  int32 status = 3;           // 0=Waiting, 1=Ready, 2=Playing, 3=Finished
  string host_player_id = 4;
  repeated PlayerSlotInfo players = 5;
  string battle_node_id = 6;
  int64 created_at = 7;
}

message PlayerSlotInfo {
  string player_id = 1;
  string player_name = 2;
  int32 slot_number = 3;      // 1 or 2
  bool is_ready = 4;
  int64 join_time = 5;
}
```

---

## 四、数据模型

### 4.1 Redis 数据结构

```
# 房间基本信息 (Hash)
HSET room:{room_id}
  host_player_id -> "player_123"
  game_mode -> "1"
  status -> "0"              # 0=Waiting, 1=Ready, 2=Playing, 3=Finished
  battle_node_id -> "battle-1"
  battle_node_host -> "10.0.1.5"
  battle_node_port -> "7777"
  created_at -> "1714411200000"
  invite_code -> "ABC123"
  TTL -> 3600                # 1小时无操作自动销毁

# 房间玩家槽位 (Hash)
HSET room:{room_id}:players
  player1_id -> "player_123"
  player1_name -> "Alice"
  player1_ready -> "false"
  player1_join_time -> "1714411200000"
  player2_id -> ""           # 空表示未加入
  player2_name -> ""
  player2_ready -> "false"
  player2_join_time -> "0"

# 邀请码映射 (String)
SET invite:ABC123 -> "room_uuid_here"
EXPIRE invite:ABC123 3600

# 玩家当前房间 (String)
SET player_room:{player_id} -> "room_uuid_here"
EXPIRE player_room:{player_id} 3600

# 匹配队列 (Sorted Set，按 rating 排序)
ZADD matchmaking:queue:1 "1200" "player_456"
ZADD matchmaking:queue:1 "1350" "player_789"
```

### 4.2 房间状态机

```
[Waiting] --玩家2加入--> [Ready]
   |                        |
   | 玩家离开                | 双方 Ready
   ▼                        ▼
[Closed]               [Playing] --对局结束--> [Finished]
                            |
                            | 玩家中途离开
                            ▼
                         [Finished]
```

状态转换触发：
- `Waiting → Ready`：第二名玩家加入
- `Ready → Playing`：双方均调用 `SetReady(true)`，Matchmaking 通知 Battle Node 启动房间
- `Playing → Finished`：Battle Node 上报房间关闭
- 任意状态 → `Closed`：房主离开或超时

---

## 五、Battle 节点调度策略

### 5.1 调度算法

```csharp
public class BattleNodeScheduler
{
    /// <summary>
    /// 为房间选择最优 Battle Node
    /// </summary>
    public BattleNodeInfo SelectNode(string roomId, GameMode mode)
    {
        var healthyNodes = _discovery.GetHealthyNodes();
        
        // 1. 过滤不健康或超载节点
        var candidates = healthyNodes
            .Where(n => n.CpuPercent < 80)
            .Where(n => n.RoomCount < n.MaxRooms)
            .ToList();
        
        // 2. 断线重连优先：检查 room_id 是否已绑定过节点
        var previouslyAssigned = _redis.Get($"room_node:{roomId}");
        if (previouslyAssigned != null)
        {
            var node = candidates.FirstOrDefault(n => n.NodeId == previouslyAssigned);
            if (node != null) return node;  // 原节点仍健康
        }
        
        // 3. 负载均衡：选择房间数最少的节点
        return candidates.OrderBy(n => n.RoomCount).First();
    }
}
```

### 5.2 Battle Node 心跳

每个 Battle Node 每 5 秒向 Redis 写入心跳：

```
HSET battle_nodes:{node_id}
  host -> "10.0.1.5"
  port -> "7777"
  cpu_percent -> "45.2"
  memory_mb -> "512"
  room_count -> "12"
  max_rooms -> "50"
  last_heartbeat -> "1714411500"
EXPIRE battle_nodes:{node_id} 15  # 15秒无心跳视为失联
```

---

## 六、与其他服务交互

### 6.1 调用其他服务

```
Matchmaking --gRPC--> AuthService.ValidateToken
  验证客户端 JWT 有效性

Matchmaking --gRPC--> BattleService.StartRoom
  双方 Ready 后，通知 Battle Node 初始化帧同步引擎
```

### 6.2 被其他服务调用

```
BattleService --gRPC--> Matchmaking.ReportRoomClosed
  对局结束后，Battle Node 上报结果，Matchmaking 归档房间

Client --gRPC/HTTP--> Matchmaking.*
  客户端直接调用房间管理接口
```

### 6.3 异步事件（Redis Pub/Sub）

| Channel | 方向 | 内容 |
|---------|------|------|
| `matchmaking:room:created` | Pub | 新房间创建通知（可用于推送大厅更新） |
| `matchmaking:room:closed` | Pub | 房间关闭通知 |
| `battle:room:assigned` | Sub ← Battle | Battle Node 确认接管房间 |

---

## 七、匹配算法（可选）

### 7.1 ELO 匹配

```
1. 玩家 A 加入队列（rating=1200）
2. 后台定时任务（每 3 秒）扫描队列：
   a. 取出等待时间最长的玩家
   b. 在 rating +/- 50 范围内寻找对手
   c. 若找到：创建房间，移除双方
   d. 若未找到：扩大搜索范围（+/-100, +/-200... 上限 +/-500）
3. 超时机制：单个玩家等待超过 60 秒，放宽匹配条件
```

---

## 八、部署规格

| 项目 | 建议配置 |
|------|----------|
| **运行时** | .NET 8, ASP.NET Core gRPC |
| **容器资源** | 1 CPU / 512MB 内存 |
| **副本数** | >= 2（无状态，Redis 集中存储状态） |
| **网络** | **TCP only**（HTTP/gRPC，不暴露 UDP） |
| **Redis** | Redis 7+（建议 Cluster 模式） |
| **数据库** | PostgreSQL（仅用于房间历史归档，非关键路径） |

---

## 九、错误码定义

| 错误码 | 说明 |
|--------|------|
| `ROOM_NOT_FOUND` | 房间不存在 |
| `ROOM_FULL` | 房间已满（1v1 已有2人） |
| `ROOM_ALREADY_STARTED` | 房间已开始对局 |
| `PLAYER_ALREADY_IN_ROOM` | 玩家已在其他房间 |
| `PLAYER_NOT_IN_ROOM` | 玩家不在该房间中 |
| `INVITE_CODE_INVALID` | 邀请码无效或已过期 |
| `BATTLE_NODE_UNAVAILABLE` | 无可用 Battle Node |
| `MATCHMAKING_ALREADY_QUEUED` | 玩家已在匹配队列中 |

---

## 十、关键实现要点

1. **Redis 事务保证**：创建房间时，需原子性地写入 `room:{id}`、`invite:{code}`、`player_room:{pid}`，使用 Redis 事务或 Lua 脚本保证一致性。
2. **房间超时清理**：Redis TTL 自动过期 + 后台定时任务兜底，防止孤儿房间。
3. **与单体代码的对应关系**：
   - `SessionManagerImpl` → `MatchmakingService` 的核心业务逻辑
   - `GameSession` → Redis Hash `room:{id}` + `room:{id}:players`
   - 创建房间时的 `FrameSyncEngine` 初始化 → 延迟到 Battle Service 的 `StartRoom`
