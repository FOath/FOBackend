namespace AuthService.Services;

/// <summary>
/// 认证服务接口
/// </summary>
public interface IAuthService
{
    Task<AuthResult> AuthenticateAsync(string playerName, string password, string clientVersion, string deviceId);
    Task<TokenValidationResult> ValidateTokenAsync(string accessToken);
    Task<TokenRefreshResult> RefreshTokenAsync(string refreshToken);
    Task<bool> RevokeTokenAsync(string accessToken);
    Task<PlayerProfile?> GetProfileAsync(string playerId);
    Task<bool> UpdateProfileAsync(string playerId, string? playerName);
}

public record AuthResult(
    string PlayerId,
    string PlayerName,
    string AccessToken,
    string RefreshToken,
    long ExpiresAt,
    bool IsNewPlayer);

public record TokenValidationResult(bool Valid, string PlayerId, string PlayerName, long ExpiresAt);

public record TokenRefreshResult(string AccessToken, string RefreshToken, long ExpiresAt);

public record PlayerProfile(
    string PlayerId,
    string PlayerName,
    int TotalGames,
    int TotalWins,
    int Rating,
    long CreatedAt,
    long LastLoginAt);
