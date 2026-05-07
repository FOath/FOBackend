namespace MatchmakingService.Services;

public interface IMatchmakingService
{
    Task<CreateRoomResult> CreateRoomAsync(string playerId, string playerName, int gameMode, Dictionary<string, string> options);
    Task<JoinRoomResult> JoinRoomAsync(string playerId, string playerName, string? roomId, string? inviteCode);
    Task<bool> LeaveRoomAsync(string playerId, string roomId);
    Task<SetReadyResult> SetReadyAsync(string playerId, string roomId, bool isReady);
    Task<Room?> GetRoomAsync(string roomId);
    Task<Room?> GetPlayerRoomAsync(string playerId);
    Task ReportRoomClosedAsync(string roomId, string battleNodeId, int endReason, int totalFrames);
}

public interface IRoomManager
{
    Task<Room> CreateRoomAsync(string hostPlayerId, string hostPlayerName, int gameMode, Dictionary<string, string> options, string battleNodeId, string battleNodeHost, int battleNodePort);
    Task<Room?> GetRoomAsync(string roomId);
    Task<Room?> GetRoomByInviteCodeAsync(string inviteCode);
    Task<bool> AddPlayerAsync(string roomId, string playerId, string playerName);
    Task<bool> RemovePlayerAsync(string roomId, string playerId);
    Task<bool> SetPlayerReadyAsync(string roomId, string playerId, bool isReady);
    Task<Room?> GetPlayerRoomAsync(string playerId);
    Task<bool> UpdateRoomStatusAsync(string roomId, RoomStatus status);
    Task<bool> CloseRoomAsync(string roomId);
}

public interface IBattleNodeRegistry
{
    Task<BattleNodeInfo?> SelectNodeAsync(string roomId);
    Task ReportNodeStatsAsync(string nodeId, NodeStats stats);
    Task<List<BattleNodeInfo>> GetHealthyNodesAsync();
}

public interface IMatchmakingQueue
{
    Task<bool> EnqueueAsync(string playerId, int gameMode, int rating);
    Task<bool> DequeueAsync(string playerId);
    Task<(string player1Id, string player2Id)?> TryMatchAsync(int gameMode);
}

public record CreateRoomResult(string RoomId, string InviteCode, string BattleNodeHost, int BattleNodePort, string BattleNodeId);
public record JoinRoomResult(bool Success, string RoomId, string? BattleNodeHost, int BattleNodePort, string? ErrorMessage);
public record SetReadyResult(bool Success, bool AllReady);

public class Room
{
    public string RoomId { get; set; } = "";
    public int GameMode { get; set; }
    public RoomStatus Status { get; set; } = RoomStatus.Waiting;
    public string HostPlayerId { get; set; } = "";
    public List<PlayerSlot> Players { get; set; } = new();
    public string BattleNodeId { get; set; } = "";
    public string BattleNodeHost { get; set; } = "";
    public int BattleNodePort { get; set; }
    public string InviteCode { get; set; } = "";
    public long CreatedAt { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public class PlayerSlot
{
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public int SlotNumber { get; set; }
    public bool IsReady { get; set; }
    public long JoinTime { get; set; }
}

public enum RoomStatus { Waiting = 0, Ready = 1, Playing = 2, Finished = 3 }

public record BattleNodeInfo(string NodeId, string Host, int Port, float CpuPercent, int RoomCount, int MaxRooms);
public record NodeStats(int ActiveRooms, int ActiveConnections, float CpuPercent, float MemoryMb);
