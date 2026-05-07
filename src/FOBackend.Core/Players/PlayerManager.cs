using System.Collections.Concurrent;
using FOBackend.Protocol.Messages;
using FOBackend.Transport;
using Microsoft.Extensions.Logging;

namespace FOBackend.Core.Players;

/// <summary>
/// 玩家档案信息
/// </summary>
public class PlayerProfile
{
    public string PlayerId { get; init; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime LastLoginTime { get; set; }
    public string? LastClientVersion { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public int TotalGamesPlayed { get; set; }
    public int TotalWins { get; set; }
}

/// <summary>
/// 玩家管理器接口
/// </summary>
public interface IPlayerManager
{
    /// <summary>
    /// 认证/注册玩家（根据名称自动创建或加载）
    /// </summary>
    Task<PlayerProfile> AuthenticateAsync(string playerName, string clientVersion);
    
    /// <summary>
    /// 获取玩家档案
    /// </summary>
    PlayerProfile? GetPlayer(string playerId);
    
    /// <summary>
    /// 验证会话令牌
    /// </summary>
    bool ValidateToken(string playerId, string token);
    
    /// <summary>
    /// 更新玩家数据
    /// </summary>
    Task UpdateAsync(PlayerProfile profile);
}

/// <summary>
/// 玩家管理器实现（内存 + 可选持久化）
/// </summary>
public class PlayerManagerImpl : IPlayerManager
{
    private readonly ConcurrentDictionary<string, PlayerProfile> _players = new();
    private readonly Microsoft.Extensions.Logging.ILogger<PlayerManagerImpl> _logger;
    
    public PlayerManagerImpl(Microsoft.Extensions.Logging.ILogger<PlayerManagerImpl> logger)
    {
        _logger = logger;
    }

    public async Task<PlayerProfile> AuthenticateAsync(string playerName, string clientVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerName);
        
        // 生成或获取玩家ID（基于名称的确定性ID或随机ID）
        var playerId = GeneratePlayerId(playerName);
        
        // 从内存缓存或数据库加载/创建档案
        if (_players.TryGetValue(playerId, out var existing))
        {
            // 已存在的玩家：更新登录时间和版本
            existing.LastLoginTime = DateTime.UtcNow;
            existing.LastClientVersion = clientVersion;
            existing.SessionToken = GenerateToken();
            
            _logger.LogDebug("Existing player authenticated: {Name} ({ID})", playerName, playerId);
        }
        else
        {
            // 新玩家：创建档案
            var newProfile = new PlayerProfile
            {
                PlayerId = playerId,
                PlayerName = playerName,
                CreateTime = DateTime.UtcNow,
                LastLoginTime = DateTime.UtcNow,
                LastClientVersion = clientVersion,
                SessionToken = GenerateToken(),
                TotalGamesPlayed = 0,
                TotalWins = 0
            };
            
            _players[playerId] = newProfile;
            
            _logger.LogInformation("👤 New player registered: {Name} ({ID})", playerName, playerId);
            
            existing = newProfile;
        }
        
        return await Task.FromResult(existing);
    }

    public PlayerProfile? GetPlayer(string playerId)
    {
        return _players.GetValueOrDefault(playerId);
    }

    public bool ValidateToken(string playerId, string token)
    {
        if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(token))
            return false;
        
        return _players.TryGetValue(playerId, out var p) && p.SessionToken == token;
    }

    public Task UpdateAsync(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        
        if (_players.ContainsKey(profile.PlayerId))
        {
            _players[profile.PlayerId] = profile;
        }
        
        return Task.CompletedTask;
    }

    private static string GeneratePlayerId(string playerName)
    {
        // 基于名称生成确定性ID（简化版）
        // 生产环境建议使用 UUID 或雪花算法
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"fobackend_{playerName}_{DateTime.UtcNow:yyyyMMdd}"));
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    private static string GenerateToken()
    {
        // 生成安全的随机令牌
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
}
