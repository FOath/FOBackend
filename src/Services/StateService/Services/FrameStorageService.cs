using System.IO.Compression;
using System.Text.Json;

namespace StateService.Services;

/// <summary>
/// 帧存储服务实现
/// 使用对象存储 + 本地缓存
/// </summary>
public class FrameStorageService : IFrameStorageService
{
    private readonly IObjectStorage _objectStorage;
    private readonly ILogger<FrameStorageService> _logger;
    private readonly string _localCachePath;

    public FrameStorageService(IObjectStorage objectStorage, IConfiguration config, ILogger<FrameStorageService> logger)
    {
        _objectStorage = objectStorage;
        _logger = logger;
        _localCachePath = config.GetValue<string>("LocalCachePath") ?? "cache/frames";
        Directory.CreateDirectory(_localCachePath);
    }

    public async Task<string> AppendFramesAsync(string matchId, int startFrame, List<FrameData> frames, bool isFinal)
    {
        var key = $"matches/{matchId}/frames.bin.zst";
        var localPath = Path.Combine(_localCachePath, $"{matchId}.frames");

        // 追加到本地文件
        await using var fs = new FileStream(localPath, FileMode.Append, FileAccess.Write);
        await using var writer = new BinaryWriter(fs);
        
        foreach (var frame in frames)
        {
            writer.Write(frame.FrameNumber);
            writer.Write(frame.ServerTime);
            writer.Write(frame.Inputs.Count);
            foreach (var input in frame.Inputs)
            {
                writer.Write(input.PlayerId);
                writer.Write(input.InputData.Length);
                writer.Write(input.InputData);
                writer.Write(input.Checksum);
            }
        }

        // 如果是最后一批，上传到对象存储
        if (isFinal)
        {
            var data = await File.ReadAllBytesAsync(localPath);
            var compressed = Compress(data);
            await _objectStorage.PutAsync(key, compressed);
            
            // 清理本地文件
            File.Delete(localPath);
        }

        return key;
    }

    public async Task<List<FrameData>> ReadFramesAsync(string matchId, int fromFrame, int toFrame)
    {
        var key = $"matches/{matchId}/frames.bin.zst";
        var data = await _objectStorage.GetAsync(key);
        if (data == null) return new List<FrameData>();

        var decompressed = Decompress(data);
        return ParseFrames(decompressed, fromFrame, toFrame);
    }

    public async Task<string> GenerateReplayAsync(string matchId)
    {
        var key = $"matches/{matchId}/frames.bin.zst";
        var replayKey = $"matches/{matchId}/replay.bin";
        
        // 如果回放已存在，直接返回
        if (await _objectStorage.ExistsAsync(replayKey))
        {
            return await _objectStorage.GetPresignedUrlAsync(replayKey, TimeSpan.FromHours(1));
        }

        var data = await _objectStorage.GetAsync(key);
        if (data == null) throw new FileNotFoundException("Frames not found");

        // 回放格式 = 元数据 + 帧数据
        var frames = await ReadFramesAsync(matchId, 0, int.MaxValue);
        var replayData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            matchId,
            totalFrames = frames.Count,
            frames = frames.Select(f => new
            {
                f.FrameNumber,
                f.ServerTime,
                inputs = f.Inputs.Select(i => new { i.PlayerId, i.Checksum })
            })
        });

        await _objectStorage.PutAsync(replayKey, replayData);
        return await _objectStorage.GetPresignedUrlAsync(replayKey, TimeSpan.FromHours(1));
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Fastest))
        {
            zs.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zs = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zs.CopyTo(output);
        return output.ToArray();
    }

    private static List<FrameData> ParseFrames(byte[] data, int fromFrame, int toFrame)
    {
        var frames = new List<FrameData>();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        while (ms.Position < ms.Length)
        {
            var frameNumber = reader.ReadInt32();
            var serverTime = reader.ReadInt64();
            var inputCount = reader.ReadInt32();
            
            if (frameNumber < fromFrame)
            {
                // 跳过不需要的帧
                for (int i = 0; i < inputCount; i++)
                {
                    reader.ReadString();
                    var len = reader.ReadInt32();
                    ms.Position += len + 4; // skip data + checksum
                }
                continue;
            }
            
            if (toFrame > 0 && frameNumber > toFrame) break;

            var inputs = new List<PlayerInput>();
            for (int i = 0; i < inputCount; i++)
            {
                var playerId = reader.ReadString();
                var dataLen = reader.ReadInt32();
                var inputData = reader.ReadBytes(dataLen);
                var checksum = reader.ReadInt32();
                inputs.Add(new PlayerInput(playerId, inputData, checksum));
            }

            frames.Add(new FrameData(frameNumber, serverTime, inputs));
        }

        return frames;
    }
}
