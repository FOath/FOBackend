using Minio;

namespace StateService.Services;

/// <summary>
/// 本地文件系统对象存储（开发环境）
/// </summary>
public class LocalFileObjectStorage : IObjectStorage
{
    private readonly string _basePath;

    public LocalFileObjectStorage()
    {
        _basePath = "storage/objects";
        Directory.CreateDirectory(_basePath);
    }

    public Task PutAsync(string key, byte[] data)
    {
        var path = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key)
    {
        var path = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return Task.FromResult<byte[]?>(null);
        return Task.FromResult<byte[]?>(File.ReadAllBytes(path));
    }

    public Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry)
    {
        var path = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult($"file://{Path.GetFullPath(path)}");
    }

    public Task<bool> ExistsAsync(string key)
    {
        var path = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult(File.Exists(path));
    }
}

/// <summary>
/// MinIO 对象存储实现
/// </summary>
public class MinioObjectStorage : IObjectStorage
{
    private readonly Minio.IMinioClient _client;
    private readonly string _bucket;
    private readonly ILogger<MinioObjectStorage> _logger;

    public MinioObjectStorage(string endpoint, string accessKey, string secretKey, string bucket, ILogger<MinioObjectStorage> logger)
    {
        _bucket = bucket;
        _logger = logger;
        _client = new Minio.MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .Build();
    }

    public async Task PutAsync(string key, byte[] data)
    {
        using var ms = new MemoryStream(data);
        await _client.PutObjectAsync(new Minio.DataModel.Args.PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(key)
            .WithStreamData(ms)
            .WithObjectSize(data.Length));
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        using var ms = new MemoryStream();
        try
        {
            await _client.GetObjectAsync(new Minio.DataModel.Args.GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithCallbackStream(stream => stream.CopyTo(ms)));
            return ms.ToArray();
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return null;
        }
    }

    public async Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry)
    {
        return await _client.PresignedGetObjectAsync(new Minio.DataModel.Args.PresignedGetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(key)
            .WithExpiry((int)expiry.TotalSeconds));
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            await _client.StatObjectAsync(new Minio.DataModel.Args.StatObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
