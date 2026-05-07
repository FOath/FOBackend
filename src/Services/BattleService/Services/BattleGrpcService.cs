using Grpc.Core;

namespace BattleService.Services;

/// <summary>
/// Battle Service gRPC 内部 API
/// 供 Matchmaking Service 调用，控制房间生命周期
/// </summary>
public class BattleGrpcService : global::Battle.BattleService.BattleServiceBase
{
    private readonly IBattleRoomManager _roomManager;
    private readonly ILogger<BattleGrpcService> _logger;

    public BattleGrpcService(IBattleRoomManager roomManager, ILogger<BattleGrpcService> logger)
    {
        _roomManager = roomManager;
        _logger = logger;
    }

    public override async Task<global::Battle.StartRoomResponse> StartRoom(
        global::Battle.StartRoomRequest request, ServerCallContext context)
    {
        var success = await _roomManager.StartRoomAsync(
            request.RoomId,
            request.Players.Select(p => p.PlayerId).ToList(),
            request.GameMode,
            request.RandomSeed);

        return new global::Battle.StartRoomResponse
        {
            Success = success,
            ErrorMessage = success ? "" : "Failed to start room"
        };
    }

    public override async Task<global::Battle.StopRoomResponse> StopRoom(
        global::Battle.StopRoomRequest request, ServerCallContext context)
    {
        var finalFrame = await _roomManager.StopRoomAsync(request.RoomId, request.EndReason);
        return new global::Battle.StopRoomResponse
        {
            Success = true,
            FinalFrame = finalFrame
        };
    }

    public override Task<global::Battle.NodeStatsResponse> GetNodeStats(
        global::Battle.NodeStatsRequest request, ServerCallContext context)
    {
        var stats = _roomManager.GetNodeStats();
        return Task.FromResult(new global::Battle.NodeStatsResponse
        {
            NodeId = stats.NodeId,
            ActiveRooms = stats.ActiveRooms,
            ActiveConnections = stats.ActiveConnections,
            CpuPercent = stats.CpuPercent,
            MemoryMb = stats.MemoryMb
        });
    }

    public override Task<global::Battle.HealthCheckResponse> HealthCheck(
        global::Battle.HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(new global::Battle.HealthCheckResponse { Healthy = true });
    }

    public override Task<global::Battle.GetRoomStateResponse> GetRoomState(
        global::Battle.GetRoomStateRequest request, ServerCallContext context)
    {
        var state = _roomManager.GetRoomState(request.RoomId);
        if (state == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Room not found"));

        return Task.FromResult(new global::Battle.GetRoomStateResponse
        {
            RoomId = state.RoomId,
            Status = (int)state.Status,
            CurrentFrame = state.CurrentFrame,
            PlayerIds = { state.PlayerIds }
        });
    }
}
