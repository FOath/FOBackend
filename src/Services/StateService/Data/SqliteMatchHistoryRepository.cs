using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace StateService.Services;

public class SqliteMatchHistoryRepository : IMatchHistoryRepository
{
    private readonly string _connectionString;

    public SqliteMatchHistoryRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsureTableExists();
    }

    private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

    private void EnsureTableExists()
    {
        using var connection = CreateConnection();
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS match_history (
                match_id TEXT PRIMARY KEY,
                room_id TEXT NOT NULL,
                game_mode INTEGER NOT NULL,
                player1_id TEXT NOT NULL,
                player2_id TEXT NOT NULL,
                winner_id TEXT,
                total_frames INTEGER NOT NULL,
                duration_sec INTEGER,
                end_reason INTEGER NOT NULL,
                storage_path TEXT NOT NULL,
                has_replay INTEGER DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_match_p1 ON match_history(player1_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_match_p2 ON match_history(player2_id, created_at DESC);
        ");
    }

    public async Task<MatchRecord?> GetByIdAsync(string matchId)
    {
        using var connection = CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<MatchHistoryRow>(
            "SELECT * FROM match_history WHERE match_id = @matchId", new { matchId });
        return row == null ? null : Map(row);
    }

    public async Task<List<MatchRecord>> ListByPlayerAsync(string playerId, int page, int pageSize, int gameMode)
    {
        using var connection = CreateConnection();
        var sql = @"
            SELECT * FROM match_history 
            WHERE (player1_id = @playerId OR player2_id = @playerId)
            AND (@gameMode = 0 OR game_mode = @gameMode)
            ORDER BY created_at DESC
            LIMIT @pageSize OFFSET @offset";
        
        var rows = await connection.QueryAsync<MatchHistoryRow>(sql, new 
        { 
            playerId, 
            gameMode, 
            pageSize, 
            offset = (page - 1) * pageSize 
        });
        return rows.Select(Map).ToList();
    }

    public async Task<int> CountByPlayerAsync(string playerId, int gameMode)
    {
        using var connection = CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM match_history 
              WHERE (player1_id = @playerId OR player2_id = @playerId)
              AND (@gameMode = 0 OR game_mode = @gameMode)",
            new { playerId, gameMode });
    }

    public async Task CreateAsync(MatchRecord record)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(@"
            INSERT INTO match_history (match_id, room_id, game_mode, player1_id, player2_id, winner_id, 
                total_frames, duration_sec, end_reason, storage_path, has_replay, created_at)
            VALUES (@MatchId, @RoomId, @GameMode, @Player1Id, @Player2Id, @WinnerId,
                @TotalFrames, @DurationSec, @EndReason, @StoragePath, @HasReplay, @CreatedAt)", record);
    }

    private static MatchRecord Map(MatchHistoryRow r) => new(
        r.match_id, r.room_id, r.game_mode, r.player1_id, r.player2_id,
        r.winner_id, r.total_frames, r.duration_sec, r.end_reason,
        r.storage_path, r.has_replay != 0, DateTime.Parse(r.created_at));

    private class MatchHistoryRow
    {
        public string match_id { get; set; } = "";
        public string room_id { get; set; } = "";
        public int game_mode { get; set; }
        public string player1_id { get; set; } = "";
        public string player2_id { get; set; } = "";
        public string? winner_id { get; set; }
        public int total_frames { get; set; }
        public int duration_sec { get; set; }
        public int end_reason { get; set; }
        public string storage_path { get; set; } = "";
        public int has_replay { get; set; }
        public string created_at { get; set; } = "";
    }
}
