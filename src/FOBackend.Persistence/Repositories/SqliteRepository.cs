using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using FOBackend.Protocol.Messages;

namespace FOBackend.Persistence.Repositories;

/// <summary>
/// 玩家仓库接口
/// </summary>
public interface IPlayerRepository
{
    Task<PlayerRow?> GetByIdAsync(string playerId);
    Task<PlayerRow> GetOrCreateAsync(string playerId, string playerName);
    Task UpdateAsync(PlayerRow player);
}

/// <summary>
/// 对战历史仓库接口
/// </summary>
public interface IMatchHistoryRepository
{
    Task SaveMatchResultAsync(MatchRecord record);
    Task<IReadOnlyList<MatchRecord>> GetPlayerHistoryAsync(string playerId, int limit = 20);
}

/// <summary>
/// 数据库行模型（对应 players 表）
/// </summary>
public class PlayerRow
{
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LastClientVersion { get; set; }
    public int TotalGamesPlayed { get; set; }
    public int TotalWins { get; set; }
}

/// <summary>
/// SQLite 实现的玩家仓库
/// 开发环境使用，生产环境可替换为 PostgreSQL 版本
/// </summary>
public class SqlitePlayerRepository : IPlayerRepository, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<SqlitePlayerRepository> _logger;
    
    public SqlitePlayerRepository(string connectionString, ILogger<SqlitePlayerRepository> logger)
    {
        _db = new SqliteConnection(connectionString);
        _logger = logger;
        
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        _db.Open();
        
        _db.Execute(@"
            CREATE TABLE IF NOT EXISTS players (
                player_id TEXT PRIMARY KEY,
                player_name TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                last_login_at TEXT,
                last_client_version TEXT,
                total_games_played INTEGER DEFAULT 0,
                total_wins INTEGER DEFAULT 0
            );
        ");
        
        _logger.LogInformation("SQLite database initialized and schema verified");
    }

    public async Task<PlayerRow?> GetByIdAsync(string playerId)
    {
        const string sql = "SELECT * FROM players WHERE player_id = @PlayerId";
        return await _db.QueryFirstOrDefaultAsync<PlayerRow>(sql, new { PlayerId = playerId });
    }

    public async Task<PlayerRow> GetOrCreateAsync(string playerId, string playerName)
    {
        var existing = await GetByIdAsync(playerId);
        
        if (existing != null)
        {
            // 更新名称和登录时间
            await _db.ExecuteAsync(@"
                UPDATE players 
                SET player_name = @Name, 
                    last_login_at = datetime('now') 
                WHERE player_id = @PlayerId",
                new { Name = playerName, PlayerId = playerId });
            
            existing.PlayerName = playerName;
            existing.LastLoginAt = DateTime.UtcNow;
            return existing;
        }
        
        // 创建新记录
        var newRow = new PlayerRow
        {
            PlayerId = playerId,
            PlayerName = playerName,
            CreateTime = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            TotalGamesPlayed = 0,
            TotalWins = 0
        };
        
        const string insertSql = @"
            INSERT INTO players (player_id, player_name, created_at, last_login_at) 
            VALUES (@PlayerId, @PlayerName, @CreateTime, @LastLoginAt)";
        
        await _db.ExecuteAsync(insertSql, newRow);
        _logger.LogDebug("New player created in DB: {Name} ({ID})", playerName, playerId);
        
        return newRow;
    }

    public async Task UpdateAsync(PlayerRow player)
    {
        const string sql = @"
            UPDATE players SET 
                player_name = @PlayerName,
                last_login_at = @LastLoginAt,
                last_client_version = @ClientVersion,
                total_games_played = @GamesPlayed,
                total_wins = @Wins
            WHERE player_id = @PlayerId";
        
        await _db.ExecuteAsync(sql, new
        {
            player.PlayerId,
            player.PlayerName,
            player.LastLoginAt,
            player.LastClientVersion,
            GamesPlayed = player.TotalGamesPlayed,
            Wins = player.TotalWins
        });
    }

    public void Dispose()
    {
        _db?.Close();
        _db?.Dispose();
    }
}

/// <summary>
/// SQLite 实现的对战历史仓库
/// </summary>
public class SqliteMatchHistoryRepository : IMatchHistoryRepository, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<SqliteMatchHistoryRepository> _logger;

    public SqliteMatchHistoryRepository(string connectionString, ILogger<SqliteMatchHistoryRepository> logger)
    {
        _db = new SqliteConnection(connectionString);
        _logger = logger;
        
        _db.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        _db.Execute(@"
            CREATE TABLE IF NOT EXISTS match_history (
                match_id TEXT PRIMARY KEY,
                room_id TEXT NOT NULL,
                game_mode INTEGER NOT NULL,
                player1_id TEXT REFERENCES players(player_id),
                player2_id TEXT REFERENCES players(player_id),
                winner_id TEXT REFERENCES players(player_id),
                total_frames INTEGER,
                duration_seconds REAL,
                end_reason INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            
            CREATE INDEX IF NOT EXISTS idx_match_history_player 
            ON match_history(player1_id, player2_id);
            
            CREATE INDEX IF NOT EXISTS idx_match_history_created 
            ON match_history(created_at DESC);
        ");
    }

    public async Task SaveMatchResultAsync(MatchRecord record)
    {
        const string sql = @"
            INSERT INTO match_history (
                match_id, room_id, game_mode, player1_id, player2_id, 
                winner_id, total_frames, duration_seconds, end_reason, created_at
            ) VALUES (
                @MatchId, @RoomId, @GameMode, @P1, @P2, @Winner, 
                @Frames, @Duration, @EndReason, @CreatedAt
            )";

        await _db.ExecuteAsync(sql, new
        {
            record.MatchId,
            record.RoomId,
            GameMode = (int)record.GameMode,
            P1 = record.Player1Id,
            P2 = record.Player2Id,
            Winner = record.WinnerId,
            Frames = record.TotalFrames,
            Duration = record.DurationSeconds,
            EndReason = (int)record.EndReason,
            CreatedAt = record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        });

        _logger.LogInformation("Match saved to database: {MatchId}, Duration={Duration:F1}s",
            record.MatchId, record.DurationSeconds);
    }

    public async Task<IReadOnlyList<MatchRecord>> GetPlayerHistoryAsync(string playerId, int limit = 20)
    {
        const string sql = @"
            SELECT * FROM match_history 
            WHERE player1_id = @PlayerId OR player2_id = @PlayerId
            ORDER BY created_at DESC
            LIMIT @Limit";

        var rows = await _db.QueryAsync<MatchHistoryRow>(sql, new { PlayerId = playerId, Limit = limit });
        
        return rows.Select(MapToRecord).ToList().AsReadOnly();
    }

    private static MatchRecord MapToRecord(MatchHistoryRow row) => new()
    {
        MatchId = row.MatchId,
        RoomId = row.RoomId,
        GameMode = (GameMode)row.GameMode,
        Player1Id = row.Player1Id,
        Player2Id = row.Player2Id,
        WinnerId = row.WinnerId,
        TotalFrames = row.TotalFrames ?? 0,
        DurationSeconds = row.DurationSeconds ?? 0,
        EndReason = (EndReason)(row.EndReason ?? 0)
    };

    // Dapper 映射辅助类
    private class MatchHistoryRow
    {
        public string MatchId { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public int GameMode { get; set; }
        public string? Player1Id { get; set; }
        public string? Player2Id { get; set; }
        public string? WinnerId { get; set; }
        public int? TotalFrames { get; set; }
        public double? DurationSeconds { get; set; }
        public int? EndReason { get; set; }
    }

    public void Dispose()
    {
        _db?.Close();
        _db?.Dispose();
    }
}
