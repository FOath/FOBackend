using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FOBackend.Transport;

/// <summary>
/// 全局连接管理器
/// 负责管理所有客户端连接的生命周期
/// </summary>
public class ConnectionManager : IDisposable
{
    private readonly ILogger<ConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, IGameConnection> _connections = new();
    private readonly ConcurrentDictionary<string, string> _playerToConnectionMap = new();  // PlayerId -> ConnectionId
    
    public int TotalConnections => _connections.Count;
    
    public ConnectionManager(ILogger<ConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 注册新连接
    /// </summary>
    public bool TryRegister(IGameConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        
        if (string.IsNullOrEmpty(connection.ConnectionId))
            return false;
        
        return _connections.TryAdd(connection.ConnectionId, connection);
    }

    /// <summary>
    /// 移除连接（断开时调用）
    /// </summary>
    public bool TryRemove(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var conn))
        {
            // 清理玩家映射
            if (!string.IsNullOrEmpty(conn.PlayerId))
            {
                _playerToConnectionMap.TryRemove(conn.PlayerId, out _);
            }

            _logger.LogInformation("Connection removed: {ConnId}, Player: {Player}", 
                connectionId, conn.PlayerId ?? "N/A");
            
            try
            {
                conn.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection {ConnId}", connectionId);
            }
            
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// 获取连接
    /// </summary>
    public IGameConnection? GetConnection(string connectionId)
    {
        _connections.TryGetValue(connectionId, out var conn);
        return conn;
    }

    /// <summary>
    /// 通过玩家ID获取连接
    /// </summary>
    public IGameConnection? GetConnectionByPlayerId(string playerId)
    {
        if (_playerToConnectionMap.TryGetValue(playerId, out var connId))
        {
            return GetConnection(connId);
        }
        return null;
    }

    /// <summary>
    /// 绑定玩家到连接（认证成功后调用）
    /// </summary>
    public bool BindPlayer(string connectionId, string playerId)
    {
        var conn = GetConnection(connectionId);
        if (conn == null) return false;
        
        conn.PlayerId = playerId;
        conn.State = ConnectionState.Authenticated;
        
        _playerToConnectionMap[playerId] = connectionId;
        
        _logger.LogInformation("Player {Player} bound to connection {ConnId}", playerId, connectionId);
        return true;
    }

    /// <summary>
    /// 解除玩家绑定（离开房间或断线时）
    /// </summary>
    public void UnbindPlayer(string playerId)
    {
        if (_playerToConnectionMap.TryRemove(playerId, out var connId))
        {
            var conn = GetConnection(connId);
            if (conn != null)
            {
                conn.PlayerId = null;
                conn.SessionToken = null;
                
                // 如果在游戏中，回退到Authenticated；否则保持Connected
                if (conn.State == ConnectionState.InGame)
                    conn.State = ConnectionState.Authenticated;
            }
        }
    }

    /// <summary>
    /// 获取所有连接（用于广播、监控等）
    /// </summary>
    public IEnumerable<IGameConnection> GetAllConnections()
    {
        return _connections.Values;
    }

    /// <summary>
    /// 获取指定房间内的连接列表
    /// </summary>
    public IEnumerable<IGameConnection> GetConnectionsByRoom(string roomId, ISessionLookup sessionLookup)
    {
        // 通过Session查找房间内玩家，再找到对应连接
        var playerIds = sessionLookup.GetPlayerIdsInRoom(roomId);
        var connections = new List<IGameConnection>();
        
        foreach (var pid in playerIds)
        {
            var conn = GetConnectionByPlayerId(pid);
            if (conn != null && conn.State != ConnectionState.Disconnected)
            {
                connections.Add(conn);
            }
        }
        
        return connections;
    }

    /// <summary>
    /// 清理超时或异常的连接
    /// </summary>
    public int CleanupStaleConnections(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        var staleCount = 0;
        
        foreach (var kv in _connections)
        {
            var conn = kv.Value;
            
            if (now - conn.LastActivityTime > timeout)
            {
                _logger.LogWarning("Cleaning up stale connection: {ConnId}, LastActivity: {LastActivity}",
                    conn.ConnectionId, conn.LastActivityTime);
                
                if (TryRemove(kv.Key))
                {
                    staleCount++;
                }
            }
        }
        
        return staleCount;
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    public ConnectionStats GetStats()
    {
        var stats = new ConnectionStats
        {
            TotalConnections = _connections.Count,
            AuthenticatedPlayers = _playerToConnectionMap.Count,
            ByState = new Dictionary<ConnectionState, int>()
        };
        
        foreach (var conn in _connections.Values)
        {
            var state = conn.State;
            if (!stats.ByState.ContainsKey(state))
                stats.ByState[state] = 0;
            stats.ByState[state]++;
        }
        
        return stats;
    }

    public void Dispose()
    {
        // 关闭所有连接
        foreach (var conn in _connections.Values)
        {
            try
            {
                conn.Dispose();
            }
            catch { /* 忽略 */ }
        }
        
        _connections.Clear();
        _playerToConnectionMap.Clear();
    }
}

/// <summary>
/// 连接统计信息
/// </summary>
public class ConnectionStats
{
    public int TotalConnections { get; init; }
    public int AuthenticatedPlayers { get; init; }
    public Dictionary<ConnectionState, int> ByState { get; init; } = new();
}

/// <summary>
/// 房间查找接口（用于解耦依赖）
/// </summary>
public interface ISessionLookup
{
    IEnumerable<string> GetPlayerIdsInRoom(string roomId);
}
