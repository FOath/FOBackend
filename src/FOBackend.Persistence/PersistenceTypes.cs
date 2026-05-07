using FOBackend.Infrastructure;
using FOBackend.Protocol.Messages;

namespace FOBackend.Persistence;

/// <summary>
/// 对局记录（用于历史和回放）
/// </summary>
public class MatchRecord
{
    public string MatchId { get; set; } = IdGenerator.NewId();
    public string RoomId { get; set; } = string.Empty;
    public GameMode GameMode { get; set; }
    public string? Player1Id { get; set; }
    public string? Player2Id { get; set; }
    public string? WinnerId { get; set; }  // null=平局/中断
    public int TotalFrames { get; set; }
    public double DurationSeconds { get; set; }
    public EndReason EndReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 回放数据（每帧输入记录）
/// </summary>
public class ReplayDataEntry
{
    public int FrameNumber { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public byte[] InputData { get; set; } = Array.Empty<byte>();
}
