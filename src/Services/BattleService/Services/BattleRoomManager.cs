using System.Collections.Concurrent;
using FOBackend.Core.FrameSync;
using FOBackend.Protocol.Messages;
using FOBackend.Transport;

namespace BattleService.Services;

/// <summary>
/// Battle 房间管理器实现
/// 管理 KCP 连接、帧同步引擎生命周期
/// </summary>
public class BattleRoomManager : IBattleRoomManager
{
    private readonly ConcurrentDictionary<string, RoomContext> _rooms = new();
    private readonly ConcurrentDictionary<uint, KcpSessionInfo> _sessions = new();
    private readonly global::Auth.AuthService.AuthServiceClient _authClient;
    private readonly IFrameUploader _frameUploader;
    private readonly ILogger<BattleRoomManager> _logger;
    private readonly string _nodeId;

    public BattleRoomManager(
        global::Auth.AuthService.AuthServiceClient authClient,
        IFrameUploader frameUploader,
        IConfiguration config,
        ILogger<BattleRoomManager> logger)
    {
        _authClient = authClient;
        _frameUploader = frameUploader;
        _logger = logger;
        _nodeId = config.GetValue<string>("NodeId") ?? "battle-local";
    }

    public async Task<bool> StartRoomAsync(string roomId, List<string> playerIds, int gameMode, int randomSeed)
    {
        if (_rooms.ContainsKey(roomId))
        {
            _logger.LogWarning("Room {RoomId} already exists", roomId);
            return false;
        }

        var room = new RoomContext
        {
            RoomId = roomId,
            PlayerIds = playerIds,
            GameMode = gameMode,
            RandomSeed = randomSeed,
            Status = RoomEngineStatus.Stopped
        };

        _rooms[roomId] = room;
        _logger.LogInformation("Room {RoomId} created, waiting for KCP connections", roomId);
        return true;
    }

    public async Task<int> StopRoomAsync(string roomId, int endReason)
    {
        if (!_rooms.TryRemove(roomId, out var room))
            return 0;

        room.Engine?.StopAsync((FOBackend.Protocol.Messages.EndReason)endReason).Wait(TimeSpan.FromSeconds(5));
        room.Engine?.Dispose();

        // 上传最终帧数据
        if (room.Frames.Count > 0)
        {
            await _frameUploader.UploadFramesAsync(roomId, room.LastUploadedFrame + 1, room.Frames, true);
        }

        _logger.LogInformation("Room {RoomId} stopped at frame {Frame}", roomId, room.CurrentFrame);
        return room.CurrentFrame;
    }

    public async Task<bool> AuthenticatePlayerAsync(string playerId, string token)
    {
        try
        {
            // 本地缓存或调用 Auth Service
            var result = await _authClient.ValidateTokenAsync(
                new global::Auth.ValidateTokenRequest { AccessToken = token });
            return result.Valid && result.PlayerId == playerId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token validation failed for player {PlayerId}", playerId);
            return false;
        }
    }

    public Task<bool> ReconnectPlayerAsync(string playerId, string roomId, int lastFrame)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            return Task.FromResult(false);

        // 更新会话绑定
        room.PlayerSessionMap[playerId] = null; // 将在 KCP 连接时填充
        
        _logger.LogInformation("Player {PlayerId} reconnected to room {RoomId} at frame {Frame}", 
            playerId, roomId, lastFrame);
        return Task.FromResult(true);
    }

    public async Task HandlePlayerInputAsync(string playerId, string roomId, byte[] inputData, int frameNumber, int checksum)
    {
        if (!_rooms.TryGetValue(roomId, out var room) || room.Engine == null)
            return;

        var report = new PlayerInputReport
        {
            PlayerId = playerId,
            FrameNumber = frameNumber,
            InputData = inputData,
            InputChecksum = checksum
        };

        // TODO: 需要适配 FrameSyncEngine 的接口
        // room.Engine.ReceiveInputAsync(report, new DummyConnection(playerId));
    }

    public Task HandleResendRequestAsync(string playerId, string roomId, List<int> missingFrames)
    {
        if (!_rooms.TryGetValue(roomId, out var room) || room.Engine == null)
            return Task.CompletedTask;

        // TODO: 从 HistoryBuffer 获取缺失帧
        return Task.CompletedTask;
    }

    public NodeStats GetNodeStats()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        return new NodeStats(
            _nodeId,
            _rooms.Count,
            _sessions.Count,
            0, // TODO: 获取真实 CPU
            (float)(process.WorkingSet64 / 1024 / 1024));
    }

    public RoomState? GetRoomState(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            return null;

        return new RoomState
        {
            RoomId = room.RoomId,
            Status = room.Status,
            CurrentFrame = room.CurrentFrame,
            PlayerIds = room.PlayerIds
        };
    }
}

public class RoomContext
{
    public string RoomId { get; set; } = "";
    public List<string> PlayerIds { get; set; } = new();
    public int GameMode { get; set; }
    public int RandomSeed { get; set; }
    public RoomEngineStatus Status { get; set; }
    public FrameSyncEngine? Engine { get; set; }
    public int CurrentFrame => Engine?.CurrentFrame ?? 0;
    public List<FrameData> Frames { get; set; } = new();
    public int LastUploadedFrame { get; set; } = -1;
    public Dictionary<string, object?> PlayerSessionMap { get; set; } = new();
}

public class KcpSessionInfo
{
    public uint Conv { get; set; }
    public string? PlayerId { get; set; }
    public string? RoomId { get; set; }
}
