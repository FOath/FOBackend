using System.Text.Json;
using StackExchange.Redis;

namespace MatchmakingService.Services;

/// <summary>
/// Redis 房间管理器实现
/// </summary>
public class RedisRoomManager : IRoomManager
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRoomManager> _logger;
    private IDatabase Db => _redis.GetDatabase();

    public RedisRoomManager(IConnectionMultiplexer redis, ILogger<RedisRoomManager> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<Room> CreateRoomAsync(string hostPlayerId, string hostPlayerName, int gameMode, 
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

        var json = JsonSerializer.Serialize(room);
        var txn = Db.CreateTransaction();
        txn.StringSetAsync($"room:{roomId}", json, TimeSpan.FromHours(1));
        txn.StringSetAsync($"invite:{inviteCode}", roomId, TimeSpan.FromHours(1));
        txn.StringSetAsync($"player_room:{hostPlayerId}", roomId, TimeSpan.FromHours(1));
        await txn.ExecuteAsync();

        _logger.LogInformation("Created room {RoomId} with invite {InviteCode}", roomId, inviteCode);
        return room;
    }

    public async Task<Room?> GetRoomAsync(string roomId)
    {
        var json = await Db.StringGetAsync($"room:{roomId}");
        return json.HasValue ? JsonSerializer.Deserialize<Room>(json!) : null;
    }

    public async Task<Room?> GetRoomByInviteCodeAsync(string inviteCode)
    {
        var roomId = await Db.StringGetAsync($"invite:{inviteCode.ToUpperInvariant()}");
        if (!roomId.HasValue) return null;
        return await GetRoomAsync(roomId!);
    }

    public async Task<bool> AddPlayerAsync(string roomId, string playerId, string playerName)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null || room.Players.Count >= 2) return false;

        room.Players.Add(new PlayerSlot
        {
            PlayerId = playerId,
            PlayerName = playerName,
            SlotNumber = room.Players.Count + 1,
            IsReady = false,
            JoinTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var txn = Db.CreateTransaction();
        txn.StringSetAsync($"room:{roomId}", JsonSerializer.Serialize(room), TimeSpan.FromHours(1));
        txn.StringSetAsync($"player_room:{playerId}", roomId, TimeSpan.FromHours(1));
        return await txn.ExecuteAsync();
    }

    public async Task<bool> RemovePlayerAsync(string roomId, string playerId)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null) return false;

        room.Players.RemoveAll(p => p.PlayerId == playerId);
        await Db.StringSetAsync($"room:{roomId}", JsonSerializer.Serialize(room), TimeSpan.FromHours(1));
        await Db.KeyDeleteAsync($"player_room:{playerId}");
        return true;
    }

    public async Task<bool> SetPlayerReadyAsync(string roomId, string playerId, bool isReady)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null) return false;

        var player = room.Players.FirstOrDefault(p => p.PlayerId == playerId);
        if (player == null) return false;

        player.IsReady = isReady;
        await Db.StringSetAsync($"room:{roomId}", JsonSerializer.Serialize(room), TimeSpan.FromHours(1));
        return true;
    }

    public async Task<Room?> GetPlayerRoomAsync(string playerId)
    {
        var roomId = await Db.StringGetAsync($"player_room:{playerId}");
        if (!roomId.HasValue) return null;
        return await GetRoomAsync(roomId!);
    }

    public async Task<bool> UpdateRoomStatusAsync(string roomId, RoomStatus status)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null) return false;
        room.Status = status;
        await Db.StringSetAsync($"room:{roomId}", JsonSerializer.Serialize(room), TimeSpan.FromHours(1));
        return true;
    }

    public async Task<bool> CloseRoomAsync(string roomId)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null) return false;

        var txn = Db.CreateTransaction();
        txn.KeyDeleteAsync($"room:{roomId}");
        txn.KeyDeleteAsync($"invite:{room.InviteCode}");
        foreach (var player in room.Players)
        {
            txn.KeyDeleteAsync($"player_room:{player.PlayerId}");
        }
        return await txn.ExecuteAsync();
    }

    private static string GenerateInviteCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Random.Shared;
        return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
    }
}
