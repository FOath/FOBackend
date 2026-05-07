namespace BattleService.Services;

/// <summary>
/// Battle 房间管理器接口
/// </summary>
public interface IBattleRoomManager
{
    Task<bool> StartRoomAsync(string roomId, List<string> playerIds, int gameMode, int randomSeed);
    Task<int> StopRoomAsync(string roomId, int endReason);
    Task<bool> AuthenticatePlayerAsync(string playerId, string token);
    Task<bool> ReconnectPlayerAsync(string playerId, string roomId, int lastFrame);
    Task HandlePlayerInputAsync(string playerId, string roomId, byte[] inputData, int frameNumber, int checksum);
    Task HandleResendRequestAsync(string playerId, string roomId, List<int> missingFrames);
    NodeStats GetNodeStats();
    RoomState? GetRoomState(string roomId);
}

public interface IFrameUploader
{
    Task UploadFramesAsync(string matchId, int startFrame, List<FrameData> frames, bool isFinal);
}

public record FrameData(int FrameNumber, long ServerTime, List<PlayerInput> Inputs);
public record PlayerInput(string PlayerId, byte[] InputData, int Checksum);

public class RoomState
{
    public string RoomId { get; set; } = "";
    public RoomEngineStatus Status { get; set; } = RoomEngineStatus.Stopped;
    public int CurrentFrame { get; set; }
    public List<string> PlayerIds { get; set; } = new();
}

public enum RoomEngineStatus { Stopped = 0, Running = 1, Paused = 2 }

public record NodeStats(string NodeId, int ActiveRooms, int ActiveConnections, float CpuPercent, float MemoryMb);
