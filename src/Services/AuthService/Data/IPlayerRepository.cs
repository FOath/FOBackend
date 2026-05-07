namespace AuthService.Services;

/// <summary>
/// 玩家数据仓库接口
/// </summary>
public interface IPlayerRepository
{
    Task<PlayerRecord?> GetByIdAsync(string playerId);
    Task<PlayerRecord?> GetByNameAsync(string playerName);
    Task<PlayerRecord?> GetByRefreshTokenAsync(string refreshToken);
    Task CreateAsync(PlayerRecord player);
    Task UpdateLastLoginAsync(string playerId, DateTime lastLoginAt);
    Task UpdateRefreshTokenAsync(string playerId, string refreshToken, DateTime expiresAt);
    Task<bool> UpdateNameAsync(string playerId, string playerName);
}

/// <summary>
/// 玩家数据记录
/// </summary>
public class PlayerRecord
{
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string? PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int TotalGames { get; set; }
    public int TotalWins { get; set; }
    public int Rating { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
}
