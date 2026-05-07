using System.Collections.Concurrent;
using FOBackend.Infrastructure;
using Microsoft.Extensions.Logging;
using FOBackend.Protocol.Messages;
using FOBackend.Transport;

namespace FOBackend.Core.Sessions;

/// <summary>
/// 会话管理器接口
/// 定义房间管理的核心操作
/// </summary>
public interface ISessionManager
{
    /// <summary>
    /// 创建新房间（由房主发起）
    /// </summary>
    Task<(GameSession session, JoinResult result)> CreateSessionAsync(
        string hostPlayerId,
        string hostPlayerName,
        IGameConnection hostConnection,
        GameMode gameMode,
        Dictionary<string, string>? options = null);
    
    /// <summary>
    /// 加入现有房间
    /// </summary>
    Task<(GameSession session, JoinResult result)> JoinSessionAsync(
        string sessionId,
        PlayerInfo playerInfo,
        IGameConnection connection);
    
    /// <summary>
    /// 通过邀请码加入
    /// </summary>
    Task<(GameSession session, JoinResult result)> JoinByInviteCodeAsync(
        string inviteCode,
        PlayerInfo playerInfo,
        IGameConnection connection);
    
    /// <summary>
    /// 离开房间
    /// </summary>
    Task<bool> LeaveSessionAsync(string sessionId, string playerId, LeaveReason reason = LeaveReason.NormalLeave);
    
    /// <summary>
    /// 设置准备状态
    /// </summary>
    Task<SetReadyResult> SetReadyAsync(string sessionId, string playerId, bool isReady);
    
    /// <summary>
    /// 获取房间信息
    /// </summary>
    GameSession? GetSession(string sessionId);
    
    /// <summary>
    /// 获取玩家所在的房间
    /// </summary>
    GameSession? GetSessionByPlayerId(string playerId);
    
    /// <summary>
    /// 关闭房间
    /// </summary>
    void CloseSession(string sessionId);
    
    /// <summary>
    /// 统计信息
    /// </summary>
    SessionStats GetStats();
}

/// <summary>
/// 会话统计信息
/// </summary>
public record SessionStats(int TotalSessions, int ActiveSessions, int PlayingSessions);

/// <summary>
/// 会话管理器实现（内存存储）
/// 针对1v1场景优化
/// </summary>
public class SessionManagerImpl : ISessionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _playerToSessionMap = new();  // PlayerId -> SessionId
    private readonly ConcurrentDictionary<string, string> _inviteCodeMap = new();   // InviteCode -> SessionId
    private readonly ILogger<SessionManagerImpl> _logger;
    private readonly object _lockObj = new();
    
    public SessionStats Stats => new SessionStats(
        TotalSessions: _sessions.Count,
        ActiveSessions: _sessions.Values.Count(s => s.State is SessionState.Waiting or SessionState.Ready),
        PlayingSessions: _sessions.Values.Count(s => s.State == SessionState.Playing));

    public SessionManagerImpl(ILogger<SessionManagerImpl> logger)
    {
        _logger = logger;
    }

    #region ISessionManager 实现

    public async Task<(GameSession session, JoinResult result)> CreateSessionAsync(
        string hostPlayerId,
        string hostPlayerName,
        IGameConnection hostConnection,
        GameMode gameMode,
        Dictionary<string, string>? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostPlayerId);
        
        // 检查该玩家是否已在其他房间
        if (_playerToSessionMap.ContainsKey(hostPlayerId))
        {
            return (null!, JoinResult.AlreadyInRoom);  // 已在其他房间
        }

        // 创建新房间
        var sessionId = IdGenerator.NewId();
        var session = new GameSession(sessionId, gameMode, _logger);
        
        if (options != null)
        {
            foreach (var kv in options)
                session.Options[kv.Key] = kv.Value;
        }

        // 注册房间
        _sessions.TryAdd(session.Id, session);
        _inviteCodeMap[session.InviteCode] = session.Id;

        // 添加房主为第一个玩家
        var playerInfo = new PlayerInfo
        {
            PlayerId = hostPlayerId,
            PlayerName = hostPlayerName,
            IsReady = false,
            JoinTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        var (slot, result) = session.AddPlayer(playerInfo, hostConnection);
        
        if (result == JoinResult.Success)
        {
            _playerToSessionMap[hostPlayerId] = sessionId;
            
            _logger.LogInformation(
                "🆕 Session created: {SessionId}, Host={Host}, Mode={Mode}",
                sessionId, hostPlayerName, gameMode);
        }
        
        return (session, result);
    }

    public async Task<(GameSession session, JoinResult result)> JoinSessionAsync(
        string sessionId,
        PlayerInfo playerInfo,
        IGameConnection connection)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session == null)
            return (null!, JoinResult.InvalidState);  // 房间不存在
        
        // 检查玩家是否已在其他房间
        if (_playerToSessionMap.ContainsKey(playerInfo.PlayerId))
            return (session, JoinResult.AlreadyInRoom);
        
        var (slot, result) = session.AddPlayer(playerInfo, connection);
        
        if (result == JoinResult.Success)
        {
            _playerToSessionMap[playerInfo.PlayerId] = sessionId;
        }
        
        return (session, result);
    }

    public async Task<(GameSession session, JoinResult result)> JoinByInviteCodeAsync(
        string inviteCode,
        PlayerInfo playerInfo,
        IGameConnection connection)
    {
        ArgumentException.ThrowIfNullOrEmpty(inviteCode);
        
        // 查找邀请码对应的房间ID
        if (!_inviteCodeMap.TryGetValue(inviteCode.ToUpperInvariant(), out var sessionId))
        {
            _logger.LogWarning("Invalid invite code: {Code}", inviteCode);
            return (null!, JoinResult.InvalidState);
        }
        
        return await JoinSessionAsync(sessionId, playerInfo, connection);
    }

    public async Task<bool> LeaveSessionAsync(
        string sessionId, 
        string playerId, 
        LeaveReason reason = LeaveReason.NormalLeave)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session == null) return false;
        
        session.RemovePlayer(playerId, reason);
        
        // 清理映射
        _playerToSessionMap.TryRemove(playerId, out _);
        
        // 如果房间已关闭，从字典移除
        if (session.State == SessionState.Closed)
        {
            _sessions.TryRemove(sessionId, out _);
            _inviteCodeMap.TryRemove(session.InviteCode, out _);
        }
        
        return true;
    }

    public async Task<SetReadyResult> SetReadyAsync(string sessionId, string playerId, bool isReady)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session == null) return SetReadyResult.PlayerNotFound;
        
        return session.SetReady(playerId, isReady);
    }

    public GameSession? GetSession(string sessionId)
    {
        return _sessions.GetValueOrDefault(sessionId);
    }

    public GameSession? GetSessionByPlayerId(string playerId)
    {
        if (_playerToSessionMap.TryGetValue(playerId, out var sessionId))
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
        return null;
    }

    public void CloseSession(string sessionId)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session == null) return;
        
        session.Close();
        _sessions.TryRemove(sessionId, out _);
        _inviteCodeMap.TryRemove(session.InviteCode, out _);
        
        // 清理玩家映射
        var playerIds = session.GetAllPlayerIds();
        foreach (var pid in playerIds)
        {
            _playerToSessionMap.TryRemove(pid, out _);
        }
        
        _logger.LogInformation("Session closed and removed: {SessionId}", sessionId);
    }

    public SessionStats GetStats()
    {
        return Stats;
    }

    #endregion

    #region 清理与维护

    /// <summary>
    /// 定期清理过期或空闲的房间
    /// 应由外部定时任务调用（如每5分钟一次）
    /// </summary>
    public int CleanupStaleSessions(TimeSpan maxIdleTime)
    {
        var cleanedCount = 0;
        var now = DateTime.UtcNow;
        
        foreach (var kv in _sessions.ToList())  // ToList避免并发修改
        {
            var session = kv.Value;
            
            // 清理条件：
            // 1. 已关闭但未从字典移除
            // 2. 空闲时间过长且无玩家
            if (session.State == SessionState.Closed ||
                (session.GetAllPlayerIds().Count == 0 && 
                 now - session.CreateTime > maxIdleTime))
            {
                CloseSession(kv.Key);
                cleanedCount++;
            }
        }
        
        if (cleanedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} stale sessions", cleanedCount);
        }
        
        return cleanedCount;
    }

    #endregion

    public void Dispose()
    {
        // 关闭所有活跃房间
        foreach (var session in _sessions.Values)
        {
            try
            {
                session.Dispose();
            }
            catch { /* 忽略 */ }
        }
        
        _sessions.Clear();
        _playerToSessionMap.Clear();
        _inviteCodeMap.Clear();
    }
}
