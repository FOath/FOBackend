using System.Security.Cryptography;
using System.Text;

namespace AuthService.Services;

/// <summary>
/// 认证服务实现
/// </summary>
public class AuthServiceImpl : IAuthService
{
    private readonly IPlayerRepository _playerRepository;
    private readonly JwtTokenService _jwtService;
    private readonly ILogger<AuthServiceImpl> _logger;

    public AuthServiceImpl(
        IPlayerRepository playerRepository,
        JwtTokenService jwtService,
        ILogger<AuthServiceImpl> logger)
    {
        _playerRepository = playerRepository;
        _jwtService = jwtService;
        _logger = logger;
    }

    public async Task<AuthResult> AuthenticateAsync(string playerName, string password, string clientVersion, string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerName);

        var existing = await _playerRepository.GetByNameAsync(playerName);
        string playerId;
        bool isNewPlayer = false;

        if (existing != null)
        {
            // 验证密码（如果有）
            if (!string.IsNullOrEmpty(existing.PasswordHash) && !string.IsNullOrEmpty(password))
            {
                if (!VerifyPassword(password, existing.PasswordHash))
                    throw new UnauthorizedAccessException("Invalid credentials");
            }
            playerId = existing.PlayerId;
            await _playerRepository.UpdateLastLoginAsync(playerId, DateTime.UtcNow);
        }
        else
        {
            // 新玩家注册
            playerId = Guid.NewGuid().ToString("N");
            var passwordHash = string.IsNullOrEmpty(password) ? null : HashPassword(password);
            
            await _playerRepository.CreateAsync(new PlayerRecord
            {
                PlayerId = playerId,
                PlayerName = playerName,
                PasswordHash = passwordHash,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow,
                TotalGames = 0,
                TotalWins = 0,
                Rating = 1000
            });
            isNewPlayer = true;
            _logger.LogInformation("New player registered: {PlayerName} ({PlayerId})", playerName, playerId);
        }

        var (accessToken, accessExpiry) = _jwtService.GenerateAccessToken(playerId, playerName);
        var (refreshToken, refreshExpiry) = _jwtService.GenerateRefreshToken(playerId, deviceId);

        await _playerRepository.UpdateRefreshTokenAsync(playerId, refreshToken, refreshExpiry);

        return new AuthResult(
            playerId, playerName, accessToken, refreshToken,
            new DateTimeOffset(accessExpiry).ToUnixTimeMilliseconds(), isNewPlayer);
    }

    public Task<TokenValidationResult> ValidateTokenAsync(string accessToken)
    {
        var result = _jwtService.ValidateAccessToken(accessToken);
        if (result == null)
            return Task.FromResult(new TokenValidationResult(false, "", "", 0));
        
        return Task.FromResult(new TokenValidationResult(
            true, result.PlayerId, result.PlayerName,
            new DateTimeOffset(result.ExpiresAt).ToUnixTimeMilliseconds()));
    }

    public async Task<TokenRefreshResult> RefreshTokenAsync(string refreshToken)
    {
        var player = await _playerRepository.GetByRefreshTokenAsync(refreshToken);
        if (player == null || player.RefreshTokenExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Invalid or expired refresh token");

        var (accessToken, accessExpiry) = _jwtService.GenerateAccessToken(player.PlayerId, player.PlayerName);
        var (newRefreshToken, refreshExpiry) = _jwtService.GenerateRefreshToken(player.PlayerId, "");
        
        await _playerRepository.UpdateRefreshTokenAsync(player.PlayerId, newRefreshToken, refreshExpiry);

        return new TokenRefreshResult(
            accessToken, newRefreshToken,
            new DateTimeOffset(accessExpiry).ToUnixTimeMilliseconds());
    }

    public Task<bool> RevokeTokenAsync(string accessToken)
    {
        // TODO: Add to Redis blacklist with TTL = token remaining lifetime
        return Task.FromResult(true);
    }

    public async Task<PlayerProfile?> GetProfileAsync(string playerId)
    {
        var player = await _playerRepository.GetByIdAsync(playerId);
        if (player == null) return null;

        return new PlayerProfile(
            player.PlayerId, player.PlayerName,
            player.TotalGames, player.TotalWins, player.Rating,
            new DateTimeOffset(player.CreatedAt).ToUnixTimeMilliseconds(),
            player.LastLoginAt.HasValue ? new DateTimeOffset(player.LastLoginAt.Value).ToUnixTimeMilliseconds() : 0);
    }

    public async Task<bool> UpdateProfileAsync(string playerId, string? playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return true;
        return await _playerRepository.UpdateNameAsync(playerId, playerName);
    }

    private static string HashPassword(string password)
    {
        // 简化实现，生产环境应使用 BCrypt 或 Argon2
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(salt) + ":" + Convert.ToHexString(hash);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;
        var salt = Convert.FromHexString(parts[0]);
        var expectedHash = Convert.FromHexString(parts[1]);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA256, 32);
        return expectedHash.SequenceEqual(actualHash);
    }
}
