namespace BattleService.Services;

/// <summary>
/// 通过 gRPC 向 State Service 上报帧数据
/// </summary>
public class GrpcFrameUploader : IFrameUploader
{
    private readonly global::State.StateService.StateServiceClient _stateClient;
    private readonly ILogger<GrpcFrameUploader> _logger;

    public GrpcFrameUploader(
        global::State.StateService.StateServiceClient stateClient,
        ILogger<GrpcFrameUploader> logger)
    {
        _stateClient = stateClient;
        _logger = logger;
    }

    public async Task UploadFramesAsync(string matchId, int startFrame, List<FrameData> frames, bool isFinal)
    {
        if (frames.Count == 0) return;

        try
        {
            using var call = _stateClient.SaveFrames();
            
            foreach (var frame in frames)
            {
                var request = new global::State.SaveFramesRequest
                {
                    MatchId = matchId,
                    StartFrame = frame.FrameNumber,
                    IsFinalBatch = isFinal
                };
                
                foreach (var input in frame.Inputs)
                {
                    request.Frames.Add(new global::State.FrameSyncPackage
                    {
                        FrameNumber = frame.FrameNumber,
                        ServerTime = frame.ServerTime,
                        Inputs =
                        {
                            new global::State.FramePlayerInput
                            {
                                PlayerId = input.PlayerId,
                                InputData = Google.Protobuf.ByteString.CopyFrom(input.InputData),
                                InputChecksum = input.Checksum
                            }
                        }
                    });
                }
                
                await call.RequestStream.WriteAsync(request);
            }
            
            await call.RequestStream.CompleteAsync();
            var response = await call.ResponseAsync;
            
            if (response.Success)
            {
                _logger.LogDebug("Uploaded {Count} frames for match {MatchId}", frames.Count, matchId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload frames for match {MatchId}", matchId);
        }
    }
}
