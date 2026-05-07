namespace StateService.Services;

public interface IStateService
{
    Task<string> SaveFramesAsync(string matchId, int startFrame, List<FrameData> frames, bool isFinalBatch);
    Task<List<FrameData>> GetFramesForReconnectAsync(string matchId, int fromFrame, int toFrame);
    Task<ReplayInfo?> GetReplayAsync(string matchId, string requesterPlayerId);
    Task<(List<MatchSummary> Matches, int TotalCount)> ListMatchesAsync(string playerId, int page, int pageSize, int gameMode);
    Task SaveMatchMetadataAsync(string matchId, string roomId, int gameMode, string player1Id, string player2Id, string winnerId, int totalFrames, int durationSec, int endReason);
}

public interface IFrameStorageService
{
    Task<string> AppendFramesAsync(string matchId, int startFrame, List<FrameData> frames, bool isFinal);
    Task<List<FrameData>> ReadFramesAsync(string matchId, int fromFrame, int toFrame);
    Task<string> GenerateReplayAsync(string matchId);
}

public interface IMatchHistoryRepository
{
    Task<MatchRecord?> GetByIdAsync(string matchId);
    Task<List<MatchRecord>> ListByPlayerAsync(string playerId, int page, int pageSize, int gameMode);
    Task<int> CountByPlayerAsync(string playerId, int gameMode);
    Task CreateAsync(MatchRecord record);
}

public interface IObjectStorage
{
    Task PutAsync(string key, byte[] data);
    Task<byte[]?> GetAsync(string key);
    Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry);
    Task<bool> ExistsAsync(string key);
}

public record FrameData(int FrameNumber, long ServerTime, List<PlayerInput> Inputs);
public record PlayerInput(string PlayerId, byte[] InputData, int Checksum);
public record ReplayInfo(string MatchId, string DownloadUrl, long ExpiresAt, int TotalFrames, long FileSizeBytes);
public record MatchSummary(string MatchId, int GameMode, string Player1Id, string Player2Id, string WinnerId, int TotalFrames, int DurationSec, long PlayedAt, bool HasReplay);
public record MatchRecord(string MatchId, string RoomId, int GameMode, string Player1Id, string Player2Id, string? WinnerId, int TotalFrames, int DurationSec, int EndReason, string StoragePath, bool HasReplay, DateTime CreatedAt);
