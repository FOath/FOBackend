using Grpc.Core;

namespace MatchmakingService.Services;

public class MatchmakingGrpcService : global::Matchmaking.MatchmakingService.MatchmakingServiceBase
{
    private readonly IMatchmakingService _matchmakingService;
    private readonly ILogger<MatchmakingGrpcService> _logger;

    public MatchmakingGrpcService(IMatchmakingService matchmakingService, ILogger<MatchmakingGrpcService> logger)
    {
        _matchmakingService = matchmakingService;
        _logger = logger;
    }

    public override async Task<global::Matchmaking.CreateRoomResponse> CreateRoom(
        global::Matchmaking.CreateRoomRequest request, ServerCallContext context)
    {
        var result = await _matchmakingService.CreateRoomAsync(
            request.PlayerId, request.PlayerName, request.GameMode, request.Options.ToDictionary());

        return new global::Matchmaking.CreateRoomResponse
        {
            RoomId = result.RoomId,
            InviteCode = result.InviteCode,
            BattleNodeHost = result.BattleNodeHost,
            BattleNodePort = result.BattleNodePort,
            BattleNodeId = result.BattleNodeId
        };
    }

    public override async Task<global::Matchmaking.JoinRoomResponse> JoinRoom(
        global::Matchmaking.JoinRoomRequest request, ServerCallContext context)
    {
        var result = await _matchmakingService.JoinRoomAsync(
            request.PlayerId, request.PlayerName,
            !string.IsNullOrEmpty(request.RoomId) ? request.RoomId : null,
            !string.IsNullOrEmpty(request.InviteCode) ? request.InviteCode : null);

        return new global::Matchmaking.JoinRoomResponse
        {
            Success = result.Success,
            RoomId = result.RoomId,
            BattleNodeHost = result.BattleNodeHost,
            BattleNodePort = result.BattleNodePort,
            ErrorMessage = result.ErrorMessage
        };
    }

    public override async Task<global::Matchmaking.LeaveRoomResponse> LeaveRoom(
        global::Matchmaking.LeaveRoomRequest request, ServerCallContext context)
    {
        var success = await _matchmakingService.LeaveRoomAsync(request.PlayerId, request.RoomId);
        return new global::Matchmaking.LeaveRoomResponse { Success = success };
    }

    public override async Task<global::Matchmaking.SetReadyResponse> SetReady(
        global::Matchmaking.SetReadyRequest request, ServerCallContext context)
    {
        var result = await _matchmakingService.SetReadyAsync(request.PlayerId, request.RoomId, request.IsReady);
        return new global::Matchmaking.SetReadyResponse
        {
            Success = result.Success,
            AllReady = result.AllReady
        };
    }

    public override async Task<global::Matchmaking.GetRoomResponse> GetRoom(
        global::Matchmaking.GetRoomRequest request, ServerCallContext context)
    {
        var room = await _matchmakingService.GetRoomAsync(request.RoomId);
        if (room == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Room not found"));

        return new global::Matchmaking.GetRoomResponse { Room = MapRoomInfo(room) };
    }

    public override async Task<global::Matchmaking.GetPlayerRoomResponse> GetPlayerRoom(
        global::Matchmaking.GetPlayerRoomRequest request, ServerCallContext context)
    {
        var room = await _matchmakingService.GetPlayerRoomAsync(request.PlayerId);
        return new global::Matchmaking.GetPlayerRoomResponse
        {
            RoomId = room?.RoomId ?? "",
            Room = room != null ? MapRoomInfo(room) : null
        };
    }

    public override async Task<global::Matchmaking.ReportRoomClosedResponse> ReportRoomClosed(
        global::Matchmaking.ReportRoomClosedRequest request, ServerCallContext context)
    {
        await _matchmakingService.ReportRoomClosedAsync(
            request.RoomId, request.BattleNodeId, request.EndReason, request.TotalFrames);
        return new global::Matchmaking.ReportRoomClosedResponse { Success = true };
    }

    private static global::Matchmaking.RoomInfo MapRoomInfo(Room room)
    {
        var info = new global::Matchmaking.RoomInfo
        {
            RoomId = room.RoomId,
            GameMode = room.GameMode,
            Status = (int)room.Status,
            HostPlayerId = room.HostPlayerId,
            BattleNodeId = room.BattleNodeId,
            CreatedAt = room.CreatedAt
        };
        foreach (var slot in room.Players)
        {
            info.Players.Add(new global::Matchmaking.PlayerSlotInfo
            {
                PlayerId = slot.PlayerId,
                PlayerName = slot.PlayerName,
                SlotNumber = slot.SlotNumber,
                IsReady = slot.IsReady,
                JoinTime = slot.JoinTime
            });
        }
        return info;
    }
}
