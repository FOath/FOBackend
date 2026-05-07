using System.Text.Json;

namespace MatchmakingService.Services;

/// <summary>
/// 内存中的 Battle Node 注册表（开发用，生产应使用 Consul/etcd）
/// </summary>
public class InMemoryBattleNodeRegistry : IBattleNodeRegistry
{
    private readonly Dictionary<string, BattleNodeInfo> _nodes = new();
    private readonly Dictionary<string, NodeStats> _stats = new();
    private readonly object _lock = new();

    public InMemoryBattleNodeRegistry()
    {
        // 预注册一个本地 Battle Node（开发环境）
        _nodes["battle-1"] = new BattleNodeInfo("battle-1", "localhost", 7777, 10.0f, 0, 50);
    }

    public Task<BattleNodeInfo?> SelectNodeAsync(string roomId)
    {
        lock (_lock)
        {
            // 优先选择已绑定该房间的节点（断线重连场景）
            // 然后选择负载最低的节点
            var candidates = _nodes.Values
                .Where(n => n.RoomCount < n.MaxRooms)
                .OrderBy(n => n.RoomCount)
                .ToList();

            return Task.FromResult(candidates.FirstOrDefault());
        }
    }

    public Task ReportNodeStatsAsync(string nodeId, NodeStats stats)
    {
        lock (_lock)
        {
            _stats[nodeId] = stats;
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                _nodes[nodeId] = node with { RoomCount = stats.ActiveRooms, CpuPercent = stats.CpuPercent };
            }
        }
        return Task.CompletedTask;
    }

    public Task<List<BattleNodeInfo>> GetHealthyNodesAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_nodes.Values
                .Where(n => n.CpuPercent < 80 && n.RoomCount < n.MaxRooms)
                .ToList());
        }
    }
}

/// <summary>
/// 内存房间管理器（开发环境无 Redis 时的回退实现）
/// </summary>
public class InMemoryRoomManager : IRoomManager
{
    private readonly Dictionary<string, Room> _rooms = new();
    private readonly Dictionary<string, string> _inviteToRoom = new();
    private readonly Dictionary<string, string> _playerToRoom = new();
    private readonly object _lock = new();

    public Task<Room> CreateRoomAsync(string hostPlayerId, string hostPlayerName, int gameMode,
        Dictionary<string, string> options, string battleNodeId, string battleNodeHost, int battleNodePort)
    {
        var roomId = Guid.NewGuid().ToString("N")[..16];
        var inviteCode = GenerateInviteCode();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var room = new Room
        {
            RoomId = roomId,
            GameMode = gameMode,
            Status = RoomStatus.Waiting,
            HostPlayerId = hostPlayerId,
            BattleNodeId = battleNodeId,
            BattleNodeHost = battleNodeHost,
            BattleNodePort = battleNodePort,
            InviteCode = inviteCode,
            CreatedAt = now,
            Options = options,
            Players = new List<PlayerSlot>
            {
                new() { PlayerId = hostPlayerId, PlayerName = hostPlayerName, SlotNumber = 1, IsReady = false, JoinTime = now }
            }
        };

        lock (_lock)
        {
            _rooms[roomId] = room;
            _inviteToRoom[inviteCode] = roomId;
            _playerToRoom[hostPlayerId] = roomId;
        }

        return Task.FromResult(room);
    }

    public Task<Room?> GetRoomAsync(string roomId)
    {
        lock (_lock)
        {
            _rooms.TryGetValue(roomId, out var room);
            return Task.FromResult(room);
        }
    }

    public Task<Room?> GetRoomByInviteCodeAsync(string inviteCode)
    {
        lock (_lock)
        {
            _inviteToRoom.TryGetValue(inviteCode.ToUpperInvariant(), out var roomId);
            if (roomId == null) return Task.FromResult<Room?>(null);
            _rooms.TryGetValue(roomId, out var room);
            return Task.FromResult(room);
        }
    }

    public Task<bool> AddPlayerAsync(string roomId, string playerId, string playerName)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room) || room.Players.Count >= 2)
                return Task.FromResult(false);

            room.Players.Add(new PlayerSlot
            {
                PlayerId = playerId,
                PlayerName = playerName,
                SlotNumber = room.Players.Count + 1,
                IsReady = false,
                JoinTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            _playerToRoom[playerId] = roomId;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RemovePlayerAsync(string roomId, string playerId)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
                return Task.FromResult(false);

            room.Players.RemoveAll(p => p.PlayerId == playerId);
            _playerToRoom.Remove(playerId);
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetPlayerReadyAsync(string roomId, string playerId, bool isReady)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
                return Task.FromResult(false);

            var player = room.Players.FirstOrDefault(p => p.PlayerId == playerId);
            if (player == null) return Task.FromResult(false);

            player.IsReady = isReady;
            return Task.FromResult(true);
        }
    }

    public Task<Room?> GetPlayerRoomAsync(string playerId)
    {
        lock (_lock)
        {
            _playerToRoom.TryGetValue(playerId, out var roomId);
            if (roomId == null) return Task.FromResult<Room?>(null);
            _rooms.TryGetValue(roomId, out var room);
            return Task.FromResult(room);
        }
    }

    public Task<bool> UpdateRoomStatusAsync(string roomId, RoomStatus status)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
                return Task.FromResult(false);

            room.Status = status;
            return Task.FromResult(true);
        }
    }

    public Task<bool> CloseRoomAsync(string roomId)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
                return Task.FromResult(false);

            _inviteToRoom.Remove(room.InviteCode);
            foreach (var player in room.Players)
                _playerToRoom.Remove(player.PlayerId);
            _rooms.Remove(roomId);
            return Task.FromResult(true);
        }
    }

    private static string GenerateInviteCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Random.Shared;
        return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
    }
}

/// <summary>
/// ELO 匹配队列内存实现（开发环境）
/// </summary>
public class EloMatchmakingQueue : IMatchmakingQueue
{
    private readonly Dictionary<string, QueueEntry> _queue = new();
    private readonly object _lock = new();

    public Task<bool> EnqueueAsync(string playerId, int gameMode, int rating)
    {
        lock (_lock)
        {
            _queue[playerId] = new QueueEntry(playerId, gameMode, rating, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        return Task.FromResult(true);
    }

    public Task<bool> DequeueAsync(string playerId)
    {
        lock (_lock)
        {
            return Task.FromResult(_queue.Remove(playerId));
        }
    }

    public Task<(string player1Id, string player2Id)?> TryMatchAsync(int gameMode)
    {
        lock (_lock)
        {
            var candidates = _queue.Values
                .Where(q => q.GameMode == gameMode)
                .OrderBy(q => q.EnqueueTime)
                .Take(2)
                .ToList();

            if (candidates.Count < 2)
                return Task.FromResult<(string player1Id, string player2Id)?>(null);

            var p1 = candidates[0].PlayerId;
            var p2 = candidates[1].PlayerId;
            _queue.Remove(p1);
            _queue.Remove(p2);
            return Task.FromResult<(string player1Id, string player2Id)?>((p1, p2));
        }
    }

    private record QueueEntry(string PlayerId, int GameMode, int Rating, long EnqueueTime);
}
