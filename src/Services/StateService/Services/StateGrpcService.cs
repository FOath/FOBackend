using Grpc.Core;

namespace StateService.Services;

/// <summary>
/// State Service gRPC 实现
/// 帧数据持久化、断线重连查询、回放管理
/// </summary>
public class StateGrpcService : global::State.StateService.StateServiceBase
{
    private readonly IStateService _stateService;
    private readonly ILogger<StateGrpcService> _logger;

    public StateGrpcService(IStateService stateService, ILogger<StateGrpcService> logger)
    {
        _stateService = stateService;
        _logger = logger;
    }

    public override async Task<global::State.SaveFramesResponse> SaveFrames(
        IAsyncStreamReader<global::State.SaveFramesRequest> requestStream,
        ServerCallContext context)
    {
        int savedCount = 0;
        string? storagePath = null;

        await foreach (var request in requestStream.ReadAllAsync())
        {
            var frames = request.Frames.Select(f => new FrameData(
                f.FrameNumber,
                f.ServerTime,
                f.Inputs.Select(i => new PlayerInput(i.PlayerId, i.InputData.ToByteArray(), i.InputChecksum)).ToList()
            )).ToList();

            storagePath = await _stateService.SaveFramesAsync(
                request.MatchId, request.StartFrame, frames, request.IsFinalBatch);
            savedCount += frames.Count;
        }

        return new global::State.SaveFramesResponse
        {
            Success = true,
            SavedCount = savedCount,
            StoragePath = storagePath ?? ""
        };
    }

    public override async Task GetFramesForReconnect(
        global::State.GetFramesRequest request,
        IServerStreamWriter<global::State.GetFramesResponse> responseStream,
        ServerCallContext context)
    {
        var frames = await _stateService.GetFramesForReconnectAsync(
            request.MatchId, request.FromFrame, request.ToFrame);

        const int batchSize = 60;
        for (int i = 0; i < frames.Count; i += batchSize)
        {
            var batch = frames.Skip(i).Take(batchSize).ToList();
            var response = new global::State.GetFramesResponse
            {
                HasMore = i + batchSize < frames.Count,
                NextFrame = i + batchSize < frames.Count ? frames[i + batchSize].FrameNumber : 0
            };

            foreach (var frame in batch)
            {
                response.Frames.Add(new global::State.FrameSyncPackage
                {
                    FrameNumber = frame.FrameNumber,
                    ServerTime = frame.ServerTime,
                    Inputs =
                    {
                        frame.Inputs.Select(input => new global::State.FramePlayerInput
                        {
                            PlayerId = input.PlayerId,
                            InputData = Google.Protobuf.ByteString.CopyFrom(input.InputData),
                            InputChecksum = input.Checksum
                        })
                    }
                });
            }

            await responseStream.WriteAsync(response);
        }
    }

    public override async Task<global::State.GetReplayResponse> GetReplay(
        global::State.GetReplayRequest request, ServerCallContext context)
    {
        var replay = await _stateService.GetReplayAsync(request.MatchId, request.RequesterPlayerId);
        if (replay == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Replay not found or unauthorized"));

        return new global::State.GetReplayResponse
        {
            Success = true,
            DownloadUrl = replay.DownloadUrl,
            ExpiresAt = replay.ExpiresAt,
            TotalFrames = replay.TotalFrames,
            FileSizeBytes = replay.FileSizeBytes
        };
    }

    public override async Task<global::State.ListMatchesResponse> ListMatches(
        global::State.ListMatchesRequest request, ServerCallContext context)
    {
        var result = await _stateService.ListMatchesAsync(
            request.PlayerId, request.Page, request.PageSize, request.GameMode);

        var response = new global::State.ListMatchesResponse
        {
            TotalCount = result.TotalCount
        };

        foreach (var match in result.Matches)
        {
            response.Matches.Add(new global::State.MatchSummary
            {
                MatchId = match.MatchId,
                GameMode = match.GameMode,
                Player1Id = match.Player1Id,
                Player2Id = match.Player2Id,
                WinnerId = match.WinnerId,
                TotalFrames = match.TotalFrames,
                DurationSec = match.DurationSec,
                PlayedAt = match.PlayedAt,
                HasReplay = match.HasReplay
            });
        }

        return response;
    }

    public override async Task<global::State.SaveMatchMetadataResponse> SaveMatchMetadata(
        global::State.SaveMatchMetadataRequest request, ServerCallContext context)
    {
        await _stateService.SaveMatchMetadataAsync(
            request.MatchId, request.RoomId, request.GameMode,
            request.Player1Id, request.Player2Id, request.WinnerId,
            request.TotalFrames, request.DurationSec, request.EndReason);

        return new global::State.SaveMatchMetadataResponse { Success = true };
    }
}
