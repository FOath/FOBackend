using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AuthService.Services;

/// <summary>
/// SQLite 玩家数据仓库实现
/// </summary>
public class SqlitePlayerRepository : IPlayerRepository
{
    private readonly string _connectionString;

    public SqlitePlayerRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsureTableExists();
    }

    private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

    private void EnsureTableExists()
    {
        using var connection = CreateConnection();
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS players (
                player_id TEXT PRIMARY KEY,
                player_name TEXT NOT NULL UNIQUE,
                password_hash TEXT,
                created_at TEXT NOT NULL,
                last_login_at TEXT,
                total_games INTEGER DEFAULT 0,
                total_wins INTEGER DEFAULT 0,
                rating INTEGER DEFAULT 1000,
                refresh_token TEXT,
                refresh_token_expires_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_players_name ON players(player_name);
            CREATE INDEX IF NOT EXISTS idx_players_refresh ON players(refresh_token);
        ");
    }

    public async Task<PlayerRecord?> GetByIdAsync(string playerId)
    {
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<PlayerRecord>(
            "SELECT * FROM players WHERE player_id = @playerId", new { playerId });
    }

    public async Task<PlayerRecord?> GetByNameAsync(string playerName)
    {
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<PlayerRecord>(
            "SELECT * FROM players WHERE player_name = @playerName", new { playerName });
    }

    public async Task<PlayerRecord?> GetByRefreshTokenAsync(string refreshToken)
    {
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<PlayerRecord>(
            "SELECT * FROM players WHERE refresh_token = @refreshToken", new { refreshToken });
    }

    public async Task CreateAsync(PlayerRecord player)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(@"
            INSERT INTO players (player_id, player_name, password_hash, created_at, last_login_at, total_games, total_wins, rating)
            VALUES (@PlayerId, @PlayerName, @PasswordHash, @CreatedAt, @LastLoginAt, @TotalGames, @TotalWins, @Rating)", player);
    }

    public async Task UpdateLastLoginAsync(string playerId, DateTime lastLoginAt)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE players SET last_login_at = @lastLoginAt WHERE player_id = @playerId",
            new { playerId, lastLoginAt });
    }

    public async Task UpdateRefreshTokenAsync(string playerId, string refreshToken, DateTime expiresAt)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE players SET refresh_token = @refreshToken, refresh_token_expires_at = @expiresAt WHERE player_id = @playerId",
            new { playerId, refreshToken, expiresAt });
    }

    public async Task<bool> UpdateNameAsync(string playerId, string playerName)
    {
        using var connection = CreateConnection();
        var rows = await connection.ExecuteAsync(
            "UPDATE players SET player_name = @playerName WHERE player_id = @playerId",
            new { playerId, playerName });
        return rows > 0;
    }
}
