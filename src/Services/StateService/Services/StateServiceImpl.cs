namespace StateService.Services;

/// <summary>
/// State Service 实现
/// </summary>
public class StateServiceImpl : IStateService
{
    private readonly IFrameStorageService _frameStorage;
    private readonly IMatchHistoryRepository _matchHistoryRepository;
    private readonly global::Auth.AuthService.AuthServiceClient _authClient;
    private readonly ILogger<StateServiceImpl> _logger;

    public StateServiceImpl(
        IFrameStorageService frameStorage,
        IMatchHistoryRepository matchHistoryRepository,
        global::Auth.AuthService.AuthServiceClient authClient,
        ILogger<StateServiceImpl> logger)
    {
        _frameStorage = frameStorage;
        _matchHistoryRepository = matchHistoryRepository;
        _authClient = authClient;
        _logger = logger;
    }

    public async Task<string> SaveFramesAsync(string matchId, int startFrame, List<FrameData> frames, bool isFinalBatch)
    {
        var path = await _frameStorage.AppendFramesAsync(matchId, startFrame, frames, isFinalBatch);
        _logger.LogDebug("Saved {Count} frames for match {MatchId} to {Path}", frames.Count, matchId, path);
        return path;
    }

    public async Task<List<FrameData>> GetFramesForReconnectAsync(string matchId, int fromFrame, int toFrame)
    {
        return await _frameStorage.ReadFramesAsync(matchId, fromFrame, toFrame);
    }

    public async Task<ReplayInfo?> GetReplayAsync(string matchId, string requesterPlayerId)
    {
        var match = await _matchHistoryRepository.GetByIdAsync(matchId);
        if (match == null) return null;

        // 鉴权：只允许参与玩家下载
        if (match.Player1Id != requesterPlayerId && match.Player2Id != requesterPlayerId)
            return null;

        if (!match.HasReplay)
        {
            // 生成回放
            await _frameStorage.GenerateReplayAsync(matchId);
            match = match with { HasReplay = true };
        }

        var url = await _frameStorage.GenerateReplayAsync(matchId);
        return new ReplayInfo(matchId, url,
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            match.TotalFrames, 0);
    }

    public async Task<(List<MatchSummary> Matches, int TotalCount)> ListMatchesAsync(string playerId, int page, int pageSize, int gameMode)
    {
        var records = await _matchHistoryRepository.ListByPlayerAsync(playerId, page, pageSize, gameMode);
        var total = await _matchHistoryRepository.CountByPlayerAsync(playerId, gameMode);

        var matches = records.Select(r => new MatchSummary(
            r.MatchId, r.GameMode, r.Player1Id, r.Player2Id,
            r.WinnerId ?? "", r.TotalFrames, r.DurationSec,
            new DateTimeOffset(r.CreatedAt).ToUnixTimeMilliseconds(), r.HasReplay)).ToList();

        return (matches, total);
    }

    public async Task SaveMatchMetadataAsync(string matchId, string roomId, int gameMode, string player1Id, string player2Id, string winnerId, int totalFrames, int durationSec, int endReason)
    {
        var record = new MatchRecord(
            matchId, roomId, gameMode, player1Id, player2Id,
            string.IsNullOrEmpty(winnerId) ? null : winnerId,
            totalFrames, durationSec, endReason,
            $"matches/{matchId}/frames.bin.zst", false, DateTime.UtcNow);

        await _matchHistoryRepository.CreateAsync(record);
        _logger.LogInformation("Match metadata saved: {MatchId}", matchId);
    }
}
