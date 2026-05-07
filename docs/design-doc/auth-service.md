# Auth Service 设计文档

> **服务定位**：玩家身份认证与账号管理微服务  
> **服务类型**：无状态 HTTP/gRPC 服务  
> **对外协议**：**HTTP/gRPC over TCP**（不使用 KCP/UDP）  
> **数据存储**：PostgreSQL（生产）/ SQLite（开发）  
> **核心依赖**：`FOBackend.Protocol`（共享消息库）

---

## 一、职责边界

### 1.1 核心功能

| 功能模块 | 说明 |
|----------|------|
| **玩家注册** | 支持游客模式（自动注册）和账号密码注册 |
| **登录鉴权** | 验证身份后签发 JWT Access Token + Refresh Token |
| **Token 管理** | Token 刷新、吊销、过期检查 |
| **玩家档案** | 玩家基本信息、战绩统计、等级分 (ELO) 的 CRUD |
| **安全审计** | 登录历史记录、异常登录检测 |

### 1.2 不包含的职责（由其他服务处理）

- ❌ 房间管理 → Matchmaking Service
- ❌ 对局帧同步 → Battle Service
- ❌ 历史帧/回放存储 → State Service
- ❌ 玩家在线状态 → Matchmaking Service (Redis)

---

## 二、架构设计

> **协议选择**：Auth Service **仅使用基于 TCP 的 HTTP/gRPC**，不暴露任何 UDP/KCP 端口。
> 原因：认证服务对可靠性要求高于延迟，TCP 的内建重传、流量控制和成熟的 TLS/负载均衡生态更适合登录、注册等请求-响应型操作。

```
┌─────────────────────────────────────────────┐
│                Auth Service                  │
│                                              │
│  ┌─────────────┐    ┌─────────────────────┐ │
│  │  gRPC API   │    │   HTTP API (可选)    │ │
│  │  (内部服务)  │    │   (外部/管理后台)    │ │
│  └──────┬──────┘    └──────────┬──────────┘ │
│         │                      │            │
│  ┌──────▼──────────────────────▼──────────┐ │
│  │         Auth Application Layer          │ │
│  │  ┌──────────┐ ┌──────────┐ ┌─────────┐ │ │
│  │  │ 登录/注册 │ │ Token管理 │ │ 档案查询 │ │ │
│  │  └──────────┘ └──────────┘ └─────────┘ │ │
│  └──────┬─────────────────────────────────┘ │
│         │                                    │
│  ┌──────▼──────────┐    ┌─────────────────┐ │
│  │ IPlayerRepository │    │ ITokenRepository │ │
│  └──────┬──────────┘    └────────┬────────┘ │
│         │                        │            │
│  ┌──────▼──────────┐    ┌────────▼────────┐ │
│  │   PostgreSQL     │    │     Redis       │ │
│  │  (players 表)    │    │ (token 黑名单)   │ │
│  └──────────────────┘    └─────────────────┘ │
└─────────────────────────────────────────────┘
```

---

## 三、接口定义

### 3.1 gRPC 服务定义 (`.proto`)

```protobuf
syntax = "proto3";
package fobackend.auth;

service AuthService {
  // 注册/登录（游客模式：player_name 必填，password 留空）
  rpc Authenticate (AuthenticateRequest) returns (AuthenticateResponse);
  
  // 验证 Token 有效性（供其他服务调用）
  rpc ValidateToken (ValidateTokenRequest) returns (ValidateTokenResponse);
  
  // 刷新 Access Token
  rpc RefreshToken (RefreshTokenRequest) returns (RefreshTokenResponse);
  
  // 吊销 Token（登出）
  rpc RevokeToken (RevokeTokenRequest) returns (RevokeTokenResponse);
  
  // 获取玩家档案
  rpc GetProfile (GetProfileRequest) returns (GetProfileResponse);
  
  // 更新玩家档案
  rpc UpdateProfile (UpdateProfileRequest) returns (UpdateProfileResponse);
}

message AuthenticateRequest {
  string player_name = 1;      // 玩家显示名称
  string password = 2;         // 密码（可选，空则为游客）
  string client_version = 3;   // 客户端版本
  string device_id = 4;        // 设备标识
}

message AuthenticateResponse {
  string player_id = 1;
  string player_name = 2;
  string access_token = 3;     // JWT，有效期 2 小时
  string refresh_token = 4;    // 刷新令牌，有效期 7 天
  int64 expires_at = 5;        // Unix 时间戳
  bool is_new_player = 6;      // 是否新注册
}

message ValidateTokenRequest {
  string access_token = 1;
}

message ValidateTokenResponse {
  bool valid = 1;
  string player_id = 2;
  string player_name = 3;
  int64 expires_at = 4;
}

message RefreshTokenRequest {
  string refresh_token = 1;
}

message RefreshTokenResponse {
  string access_token = 1;
  string refresh_token = 2;
  int64 expires_at = 3;
}

message RevokeTokenRequest {
  string access_token = 1;
}

message RevokeTokenResponse {
  bool success = 1;
}

message GetProfileRequest {
  string player_id = 1;
}

message GetProfileResponse {
  string player_id = 1;
  string player_name = 2;
  int32 total_games = 3;
  int32 total_wins = 4;
  int32 rating = 5;
  int64 created_at = 6;
  int64 last_login_at = 7;
}

message UpdateProfileRequest {
  string player_id = 1;
  string player_name = 2;  // 可选，空则不更新
}

message UpdateProfileResponse {
  bool success = 1;
}
```

### 3.2 HTTP REST API (可选，供管理后台/Web 使用)

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/api/v1/auth/register` | 账号密码注册 |
| POST | `/api/v1/auth/login` | 登录 |
| POST | `/api/v1/auth/refresh` | 刷新 Token |
| POST | `/api/v1/auth/revoke` | 登出/吊销 |
| GET  | `/api/v1/players/{playerId}` | 查询档案 |
| PUT  | `/api/v1/players/{playerId}` | 更新档案 |

---

## 四、数据模型

### 4.1 数据库 Schema

```sql
-- 玩家主表
CREATE TABLE players (
    player_id       VARCHAR(36) PRIMARY KEY,
    player_name     VARCHAR(64) NOT NULL,
    password_hash   VARCHAR(256),           -- bcrypt hash，游客为 NULL
    email           VARCHAR(128),
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    last_login_at   TIMESTAMPTZ,
    total_games     INT DEFAULT 0,
    total_wins      INT DEFAULT 0,
    rating          INT DEFAULT 1000,       -- ELO 等级分
    current_jti     VARCHAR(36),             -- 当前有效 JWT ID
    UNIQUE(player_name)
);

-- 登录历史（安全审计）
CREATE TABLE login_history (
    id              BIGSERIAL PRIMARY KEY,
    player_id       VARCHAR(36) NOT NULL REFERENCES players(player_id),
    ip_address      INET,
    client_version  VARCHAR(32),
    device_id       VARCHAR(64),
    login_at        TIMESTAMPTZ DEFAULT NOW(),
    success         BOOLEAN NOT NULL
);

-- 刷新令牌表（支持多设备登录）
CREATE TABLE refresh_tokens (
    token_hash      VARCHAR(64) PRIMARY KEY,  -- SHA-256 of token
    player_id       VARCHAR(36) NOT NULL REFERENCES players(player_id),
    device_id       VARCHAR(64),
    expires_at      TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- 索引
CREATE INDEX idx_players_name ON players(player_name);
CREATE INDEX idx_login_history_player ON login_history(player_id, login_at DESC);
CREATE INDEX idx_refresh_tokens_player ON refresh_tokens(player_id);
```

### 4.2 JWT 载荷结构

```json
{
  "sub": "player_id_here",
  "name": "PlayerName",
  "jti": "unique-token-id",
  "iat": 1714411200,
  "exp": 1714418400,
  "iss": "fobackend-auth",
  "aud": "fobackend-services"
}
```

---

## 五、安全设计

### 5.1 密码策略
- 使用 **bcrypt** 哈希（cost factor ≥ 12）
- 密码最小长度 8 位，需包含字母+数字
- 游客模式不存储密码（`password_hash IS NULL`）

### 5.2 Token 策略
- Access Token：JWT，有效期 **2 小时**
- Refresh Token：随机字符串，有效期 **7 天**
- Token 吊销列表存储于 Redis（TTL 与 Token 过期时间一致）
- 支持多设备登录（每个设备独立 Refresh Token）

### 5.3 传输安全
- 生产环境强制 **TLS 1.3**
- gRPC 服务间通信使用 **mTLS**（双向证书验证）

---

## 六、与其他服务交互

### 6.1 被调用方（其他服务调用 Auth Service）

```
Matchmaking Service ──gRPC──► ValidateToken ──► 验证客户端请求合法性
Battle Service ──────gRPC──► ValidateToken ──► 验证 KCP 连接绑定请求
State Service ───────gRPC──► GetProfile ─────► 生成对战记录时补全玩家信息
```

### 6.2 调用其他服务

```
Auth Service ──gRPC──► State Service: UpdateStats
  玩家注册/登录成功后，上报活跃玩家统计（可选）
```

---

## 七、部署规格

| 项目 | 建议配置 |
|------|----------|
| **运行时** | .NET 8, ASP.NET Core gRPC |
| **容器资源** | 1 CPU / 512MB 内存 |
| **副本数** | ≥ 2（无状态，可任意扩缩容） |
| **网络** | **TCP only**（HTTP/gRPC，不暴露 UDP） |
| **数据库** | PostgreSQL 14+（主从复制） |
| **缓存** | Redis 7+（Token 黑名单） |
| **健康检查** | gRPC health probe (`/grpc.health.v1.Health/Check`) |

---

## 八、错误码定义

| 错误码 | 说明 |
|--------|------|
| `AUTH_INVALID_CREDENTIALS` | 用户名或密码错误 |
| `AUTH_TOKEN_EXPIRED` | Access Token 已过期 |
| `AUTH_TOKEN_REVOKED` | Token 已被吊销 |
| `AUTH_INVALID_REFRESH_TOKEN` | Refresh Token 无效或过期 |
| `AUTH_PLAYER_NAME_TAKEN` | 玩家名已被占用 |
| `AUTH_PLAYER_NOT_FOUND` | 玩家不存在 |
| `AUTH_RATE_LIMITED` | 登录频率限制（防暴力破解） |

---

## 九、关键实现要点

1. **游客模式兼容**：当前单体版本的 `PlayerManagerImpl.AuthenticateAsync(string playerName, string clientVersion)` 可直接迁移，增加可选的 `password` 参数即可。
2. **Token 验证缓存**：高频调用的 `ValidateToken` 应在 Battle Service / Matchmaking Service 本地做 **JWT 签名缓存**（避免重复验签）。
3. **战绩更新异步化**：`total_games` / `total_wins` / `rating` 的更新由 Battle Service 通过消息队列异步通知，避免阻塞对局结束流程。
