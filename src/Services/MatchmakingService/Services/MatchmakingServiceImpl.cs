namespace MatchmakingService.Services;

/// <summary>
/// 匹配服务实现
/// </summary>
public class MatchmakingServiceImpl : IMatchmakingService
{
    private readonly IRoomManager _roomManager;
    private readonly IBattleNodeRegistry _battleNodeRegistry;
    private readonly global::Auth.AuthService.AuthServiceClient _authClient;
    private readonly global::Battle.BattleService.BattleServiceClient _battleClient;
    private readonly ILogger<MatchmakingServiceImpl> _logger;

    public MatchmakingServiceImpl(
        IRoomManager roomManager,
        IBattleNodeRegistry battleNodeRegistry,
        global::Auth.AuthService.AuthServiceClient authClient,
        global::Battle.BattleService.BattleServiceClient battleClient,
        ILogger<MatchmakingServiceImpl> logger)
    {
        _roomManager = roomManager;
        _battleNodeRegistry = battleNodeRegistry;
        _authClient = authClient;
        _battleClient = battleClient;
        _logger = logger;
    }

    public async Task<CreateRoomResult> CreateRoomAsync(string playerId, string playerName, int gameMode, Dictionary<string, string> options)
    {
        // 检查玩家是否已在房间中
        var existingRoom = await _roomManager.GetPlayerRoomAsync(playerId);
        if (existingRoom != null)
            throw new InvalidOperationException("Player already in a room");

        // 选择 Battle Node
        var node = await _battleNodeRegistry.SelectNodeAsync("") 
            ?? throw new InvalidOperationException("No available battle node");

        var room = await _roomManager.CreateRoomAsync(
            playerId, playerName, gameMode, options,
            node.NodeId, node.Host, node.Port);

        return new CreateRoomResult(
            room.RoomId, room.InviteCode, room.BattleNodeHost, room.BattleNodePort, room.BattleNodeId);
    }

    public async Task<JoinRoomResult> JoinRoomAsync(string playerId, string playerName, string? roomId, string? inviteCode)
    {
        Room? room = null;
        
        if (!string.IsNullOrEmpty(roomId))
            room = await _roomManager.GetRoomAsync(roomId);
        else if (!string.IsNullOrEmpty(inviteCode))
            room = await _roomManager.GetRoomByInviteCodeAsync(inviteCode);

        if (room == null)
            return new JoinRoomResult(false, "", null, 0, "Room not found");

        if (room.Status != RoomStatus.Waiting)
            return new JoinRoomResult(false, room.RoomId, null, 0, "Room is not waiting");

        if (room.Players.Count >= 2)
            return new JoinRoomResult(false, room.RoomId, null, 0, "Room is full");

        var success = await _roomManager.AddPlayerAsync(room.RoomId, playerId, playerName);
        if (!success)
            return new JoinRoomResult(false, room.RoomId, null, 0, "Failed to join room");

        // 检查是否满员，更新状态
        var updatedRoom = await _roomManager.GetRoomAsync(room.RoomId);
        if (updatedRoom?.Players.Count == 2)
        {
            await _roomManager.UpdateRoomStatusAsync(room.RoomId, RoomStatus.Ready);
        }

        return new JoinRoomResult(true, room.RoomId, room.BattleNodeHost, room.BattleNodePort, null);
    }

    public async Task<bool> LeaveRoomAsync(string playerId, string roomId)
    {
        var room = await _roomManager.GetRoomAsync(roomId);
        if (room == null) return false;

        var success = await _roomManager.RemovePlayerAsync(roomId, playerId);
        
        // 如果房主离开或房间空了，关闭房间
        if (room.HostPlayerId == playerId || room.Players.Count <= 1)
        {
            await _roomManager.CloseRoomAsync(roomId);
        }

        return success;
    }

    public async Task<SetReadyResult> SetReadyAsync(string playerId, string roomId, bool isReady)
    {
        var success = await _roomManager.SetPlayerReadyAsync(roomId, playerId, isReady);
        if (!success)
            return new SetReadyResult(false, false);

        var room = await _roomManager.GetRoomAsync(roomId);
        if (room == null)
            return new SetReadyResult(false, false);

        var allReady = room.Players.Count == 2 && room.Players.All(p => p.IsReady);
        
        if (allReady && room.Status == RoomStatus.Ready)
        {
            // 通知 Battle Node 启动房间
            try
            {
                await _battleClient.StartRoomAsync(new global::Battle.StartRoomRequest
                {
                    RoomId = room.RoomId,
                    GameMode = room.GameMode,
                    RandomSeed = Random.Shared.Next()
                });
                await _roomManager.UpdateRoomStatusAsync(roomId, RoomStatus.Playing);
                _logger.LogInformation("Room {RoomId} started on battle node {NodeId}", room.RoomId, room.BattleNodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start room {RoomId} on battle node", room.RoomId);
                return new SetReadyResult(false, false);
            }
        }

        return new SetReadyResult(true, allReady);
    }

    public Task<Room?> GetRoomAsync(string roomId) => _roomManager.GetRoomAsync(roomId);
    public Task<Room?> GetPlayerRoomAsync(string playerId) => _roomManager.GetPlayerRoomAsync(playerId);

    public async Task ReportRoomClosedAsync(string roomId, string battleNodeId, int endReason, int totalFrames)
    {
        await _roomManager.UpdateRoomStatusAsync(roomId, RoomStatus.Finished);
        await _roomManager.CloseRoomAsync(roomId);
        _logger.LogInformation("Room {RoomId} closed. EndReason={EndReason}, TotalFrames={TotalFrames}", 
            roomId, endReason, totalFrames);
    }
}
