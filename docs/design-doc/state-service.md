# State Service 设计文档

> **服务定位**：断线重连数据供给与对战回放存储微服务  
> **服务类型**：无状态 gRPC 服务（底层存储有状态）  
> **对外协议**：**gRPC over TCP**（不使用 KCP/UDP）  
> **数据存储**：对象存储（MinIO/S3，帧数据）+ PostgreSQL（元数据）  
> **核心依赖**：`FOBackend.Protocol`（共享消息库）

---

## 一、职责边界

### 1.1 核心功能

| 功能模块 | 说明 |
|----------|------|
| **帧数据持久化** | 接收 Battle Service 上报的帧同步包，压缩后写入对象存储 |
| **断线重连数据拉取** | 根据 `player_id` + `room_id` + `last_frame` 查询缺失帧 |
| **回放生成** | 将整局对局的帧数据打包为可下载的回放文件 |
| **回放查询** | 按玩家、时间、对局 ID 查询历史回放 |
| **对局元数据管理** | 存储对局结果、时长、参与玩家等结构化数据 |

### 1.2 不包含的职责

- ❌ 实时帧同步 → Battle Service
- ❌ 房间管理 → Matchmaking Service
- ❌ 玩家认证 → Auth Service
- ❌ 战绩统计计算 → Auth Service

---

## 二、架构设计

> **协议选择**：State Service **仅使用基于 TCP 的 gRPC**，不暴露任何 UDP/KCP 端口。
> 原因：帧数据持久化和回放查询属于大流量、高可靠性的后台操作，gRPC 的流式传输（client/server streaming）配合 TCP 的可靠性，可简化数据完整性保障。KCP 的不可靠特性不适合此类数据存储场景。

```
┌─────────────────────────────────────────────────────────────────┐
│                        State Service                            │
│                                                                 │
│  ┌─────────────────┐    ┌─────────────────────────────────────┐ │
│  │   gRPC API      │    │         Application Layer            │ │
│  │                 │    │  ┌──────────┐ ┌──────────────────┐  │ │
│  │  SaveFrames     │───►│  │ 帧数据写入 │ │  断线重连查询     │  │ │
│  │  (Client Stream)│    │  └──────────┘ └──────────────────┘  │ │
│  │                 │    │  ┌──────────┐ ┌──────────────────┐  │ │
│  │  GetFramesFor   │    │  │ 回放生成  │ │  对局元数据管理   │  │ │
│  │   Reconnect     │    │  └──────────┘ └──────────────────┘  │ │
│  │                 │    └─────────────────────────────────────┘ │
│  │  GetReplay      │                     │                      │
│  │  ListMatches    │           ┌─────────┴──────────┐           │
│  └─────────────────┘           ▼                    ▼           │
│                      ┌─────────────────┐  ┌─────────────────┐  │
│                      │  FrameStorage   │  │  MetadataStore  │  │
│                      │  (对象存储抽象层)  │  │   (PostgreSQL)  │  │
│                      └────────┬────────┘  └────────┬────────┘  │
│                               │                    │            │
└───────────────────────────────┼────────────────────┼────────────┘
                                │                    │
                    ┌───────────▼────────┐  ┌────────▼────────┐
                    │   MinIO / S3       │  │   PostgreSQL    │
                    │  (frames/*.zst)    │  │  (match_history) │
                    └────────────────────┘  └─────────────────┘
```

---

## 三、接口定义

### 3.1 gRPC 服务定义 (`.proto`)

```protobuf
syntax = "proto3";
package fobackend.state;

service StateService {
  // Battle Node 调用：流式上报帧数据（推荐每 1 秒调用一次，批量 60 帧）
  rpc SaveFrames (stream SaveFramesRequest) returns (SaveFramesResponse);
  
  // Battle Node / Client 调用：获取断线重连所需的历史帧
  rpc GetFramesForReconnect (GetFramesRequest) returns (stream GetFramesResponse);
  
  // 生成并获取回放信息
  rpc GetReplay (GetReplayRequest) returns (GetReplayResponse);
  
  // 查询玩家历史对局
  rpc ListMatches (ListMatchesRequest) returns (ListMatchesResponse);
  
  // 保存对局元数据（Battle Node 在对局结束时调用）
  rpc SaveMatchMetadata (SaveMatchMetadataRequest) returns (SaveMatchMetadataResponse);
}

// ==================== SaveFrames ====================

message SaveFramesRequest {
  string match_id = 1;           // 对局唯一 ID（与 room_id 相同）
  int32 start_frame = 2;         // 本批次起始帧号
  repeated FrameSyncPackage frames = 3;
  bool is_final_batch = 4;       // 是否为最后一批（对局结束）
}

message FrameSyncPackage {
  int32 frame_number = 1;
  int64 server_time = 2;
  repeated FramePlayerInput inputs = 3;
  bytes latency_info = 4;        // 序列化后的 LatencyInfo
  bytes sync_flags = 5;          // 序列化后的 SyncFlags
}

message FramePlayerInput {
  string player_id = 1;
  bytes input_data = 2;
  int32 input_checksum = 3;
}

message SaveFramesResponse {
  bool success = 1;
  int32 saved_count = 2;
  string storage_path = 3;       // 对象存储路径
}

// ==================== GetFramesForReconnect ====================

message GetFramesRequest {
  string match_id = 1;
  string player_id = 2;
  int32 from_frame = 3;          // 客户端已收到的最后一帧 + 1
  int32 to_frame = 4;            // 可选，0 表示到最新帧
}

message GetFramesResponse {
  repeated FrameSyncPackage frames = 1;
  bool has_more = 2;             // 是否还有后续数据（分页）
  int32 next_frame = 3;          // 下一批起始帧号
}

// ==================== Replay ====================

message GetReplayRequest {
  string match_id = 1;
  string requester_player_id = 2; // 鉴权：只允许参与玩家下载
}

message GetReplayResponse {
  bool success = 1;
  string download_url = 2;       // 预签名 URL，有效期 1 小时
  int64 expires_at = 3;
  int32 total_frames = 4;
  int64 file_size_bytes = 5;
}

// ==================== ListMatches ====================

message ListMatchesRequest {
  string player_id = 1;
  int32 page = 2;
  int32 page_size = 3;           // 默认 20，最大 100
  int32 game_mode = 4;           // 0 = 全部
}

message ListMatchesResponse {
  repeated MatchSummary matches = 1;
  int32 total_count = 2;
}

message MatchSummary {
  string match_id = 1;
  int32 game_mode = 2;
  string player1_id = 3;
  string player2_id = 4;
  string winner_id = 5;
  int32 total_frames = 6;
  int32 duration_sec = 7;
  int64 played_at = 8;
  bool has_replay = 9;
}

// ==================== SaveMatchMetadata ====================

message SaveMatchMetadataRequest {
  string match_id = 1;
  string room_id = 2;
  int32 game_mode = 3;
  string player1_id = 4;
  string player2_id = 5;
  string winner_id = 6;          // 空字符串表示平局/中断
  int32 total_frames = 7;
  int32 duration_sec = 8;
  int32 end_reason = 9;          // 0=Normal, 1=Disconnect, 2=ServerShutdown
}

message SaveMatchMetadataResponse {
  bool success = 1;
}
```

---

## 四、数据模型

### 4.1 对象存储结构

```
Bucket: fobackend-state

Prefix 结构：
├── matches/
│   └── {match_id}/
│       ├── frames.bin.zst          # 压缩后的帧数据（自定义二进制格式）
│       ├── metadata.json           # 对局元数据副本
│       └── replay/
│           └── replay.bin          # 回放专用格式（可选，预生成）

frames.bin.zst 自定义格式：
[Header: 16 bytes]
  - magic: "FOFS" (4 bytes)
  - version: 1 (4 bytes)
  - frame_count (4 bytes)
  - reserved (4 bytes)
[Frame Records...]
  - frame_number (4 bytes)
  - timestamp (8 bytes)
  - input_count (1 byte)
  - [Inputs...]
    - player_id_length (1 byte)
    - player_id (N bytes)
    - input_data_length (2 bytes)
    - input_data (N bytes)
    - checksum (4 bytes)
```

### 4.2 PostgreSQL 元数据表

```sql
-- 对局元数据表
CREATE TABLE match_history (
    match_id        VARCHAR(36) PRIMARY KEY,
    room_id         VARCHAR(36) NOT NULL,
    game_mode       INT NOT NULL,
    player1_id      VARCHAR(36) NOT NULL,
    player2_id      VARCHAR(36) NOT NULL,
    winner_id       VARCHAR(36),           -- NULL 表示平局/中断
    total_frames    INT NOT NULL,
    duration_sec    INT,
    end_reason      INT NOT NULL,          -- 0=Normal, 1=Disconnect, 2=ServerShutdown
    storage_path    VARCHAR(512) NOT NULL, -- 对象存储路径
    has_replay      BOOLEAN DEFAULT FALSE,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- 索引
CREATE INDEX idx_match_history_p1 ON match_history(player1_id, created_at DESC);
CREATE INDEX idx_match_history_p2 ON match_history(player2_id, created_at DESC);
CREATE INDEX idx_match_history_created ON match_history(created_at DESC);

-- 玩家战绩汇总表（物化视图或异步更新）
CREATE TABLE player_stats (
    player_id       VARCHAR(36) PRIMARY KEY REFERENCES players(player_id),
    total_games     INT DEFAULT 0,
    total_wins      INT DEFAULT 0,
    total_losses    INT DEFAULT 0,
    rating          INT DEFAULT 1000,
    updated_at      TIMESTAMPTZ DEFAULT NOW()
);
```

---

## 五、存储策略

### 5.1 压缩策略

| 数据类型 | 压缩算法 | 压缩比 | 说明 |
|----------|----------|--------|------|
| 帧数据 | Zstd | ~5:1 | 快速压缩/解压，适合实时重连场景 |
| 回放文件 | Zstd | ~5:1 | 预生成，下载前无需解压 |

### 5.2 生命周期策略

| 数据 | 热存储（SSD） | 温存储（HDD） | 冷存储（归档） | 删除 |
|------|-------------|-------------|--------------|------|
| 原始帧数据 | 7 天 | 30 天 | 90 天 | 90 天后 |
| 回放文件 | 永久 | - | - | - |
| 元数据 | 永久 | - | - | - |

**实现方式**：对象存储生命周期规则（S3 Lifecycle / MinIO ILM）自动转换存储类型。

### 5.3 写入优化

```csharp
/// <summary>
/// 批量写入器：聚合多房间帧数据，减少对象存储 API 调用
/// </summary>
public class FrameBatchUploader
{
    // 每 5 秒或每 1000 帧触发一次批量上传
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(5);
    private readonly int _flushThreshold = 1000;
    
    public async Task AppendAsync(string matchId, FrameSyncPackage[] frames)
    {
        // 1. 追加到内存缓冲区
        _buffer.AddRange(frames);
        
        // 2. 检查触发条件
        if (_buffer.Count >= _flushThreshold || _timer.Elapsed > _flushInterval)
        {
            await FlushAsync();
        }
    }
    
    private async Task FlushAsync()
    {
        // 3. 序列化为自定义二进制格式
        var bytes = SerializeFrames(_buffer);
        
        // 4. 压缩
        var compressed = ZstdCompress(bytes);
        
        // 5. 上传到对象存储（追加写或覆写）
        await _objectStorage.AppendAsync(
            key: $"matches/{matchId}/frames.bin.zst",
            data: compressed);
        
        _buffer.Clear();
    }
}
```

---

## 六、断线重连数据流

### 6.1 查询流程

```
Client/Battle Node           State Service              Object Storage
       │                          │                          │
       │  GetFramesForReconnect   │                          │
       │  { match_id, from=1500 } │                          │
       │ ───────────────────────► │                          │
       │                          │                          │
       │                          │  1. 查询 metadata        │
       │                          │     确认 match 存在       │
       │                          │                          │
       │                          │  2. 从对象存储读取帧数据   │
       │                          │ ───────────────────────► │
       │                          │ ◄─────────────────────── │
       │                          │     frames.bin.zst       │
       │                          │                          │
       │                          │  3. 解压并过滤 >=1500    │
       │                          │     的帧                 │
       │                          │                          │
       │◄─────────────────────────│  4. 流式返回（分页）      │
       │   stream FrameSyncPackage│                          │
```

### 6.2 性能优化

- **帧号索引**：在对象存储侧维护 `frame_number → byte_offset` 的索引（存储于 PostgreSQL），实现 O(1) 定位，无需全量扫描。
- **热点数据缓存**：最近 7 天的对局帧数据缓存于 State Service 本地内存（LRU），减少对象存储读取。

```sql
-- 帧偏移索引表
CREATE TABLE frame_index (
    match_id        VARCHAR(36) NOT NULL,
    frame_number    INT NOT NULL,
    file_offset     BIGINT NOT NULL,    -- 在 frames.bin.zst 中的偏移
    compressed_size INT NOT NULL,
    PRIMARY KEY (match_id, frame_number)
);
```

---

## 七、与其他服务交互

### 7.1 被调用方

```
Battle Node ──gRPC(stream)──► StateService.SaveFrames
  实时上报帧数据

Battle Node ──gRPC──► StateService.SaveMatchMetadata
  对局结束时上报元数据

Battle Node ──gRPC(stream)──► StateService.GetFramesForReconnect
  为断线玩家拉取历史帧

Client ──gRPC──► StateService.GetReplay / ListMatches
  查询回放和历史对局
```

### 7.2 调用其他服务

```
State Service ──gRPC──► AuthService.ValidateToken
  下载回放时验证请求者身份
```

---

## 八、部署规格

| 项目 | 建议配置 |
|------|----------|
| **运行时** | .NET 8, ASP.NET Core gRPC |
| **容器资源** | 2 CPU / 4GB 内存（含文件缓存） |
| **副本数** | ≥ 2（无状态，可任意扩缩容） |
| **对象存储** | MinIO 集群 或 AWS S3 / 腾讯云 COS |
| **数据库** | PostgreSQL 14+ |
| **网络** | **TCP only**，与 Battle Node 同 VPC，内网 gRPC |

---

## 九、错误码定义

| 错误码 | 说明 |
|--------|------|
| `STATE_MATCH_NOT_FOUND` | 对局不存在 |
| `STATE_UNAUTHORIZED` | 非参与玩家请求回放 |
| `STATE_FRAMES_NOT_AVAILABLE` | 请求帧范围超出存储范围 |
| `STATE_REPLAY_NOT_READY` | 回放文件尚未生成 |
| `STATE_STORAGE_ERROR` | 对象存储读写失败 |
| `STATE_INVALID_FRAME_RANGE` | from_frame > to_frame |

---

## 十、关键实现要点

1. **流式接口设计**：`SaveFrames` 和 `GetFramesForReconnect` 均使用 gRPC 双向/服务端流式，避免大消息包导致的内存峰值。
2. **对象存储抽象**：封装 `IObjectStorage` 接口，支持 MinIO（开发）和 S3/COS（生产）无缝切换。
3. **与单体代码的对应关系**：
   - `HistoryBuffer` / `CircularBuffer<FrameSyncPackage>` → 扩展到对象存储持久化
   - `IMatchHistoryRepository` → `StateService.SaveMatchMetadata` + `ListMatches`
   - `HandleResendRequestAsync` → `StateService.GetFramesForReconnect`
4. **回放格式**：建议与客户端协商统一的回放二进制格式，便于客户端直接解析播放，无需额外转换。
5. **成本优化**：原始帧数据 90 天后自动删除，仅保留回放文件和元数据，大幅降低存储成本。
