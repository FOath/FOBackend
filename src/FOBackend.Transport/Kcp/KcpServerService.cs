using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FOBackend.Transport.Kcp;

/// <summary>
/// KCP 服务器服务实现
/// 管理 UDP 监听、KCP 会话生命周期、握手流程、定时 Update 驱动
/// </summary>
public sealed class KcpServerService : IKcpServerService
{
    private readonly KcpConfig _config;
    private readonly ILogger<KcpServerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private UdpClient? _udpClient;
    private long _convCounter;

    private readonly ConcurrentDictionary<uint, KcpSession> _sessions = new();

    public IPEndPoint? LocalEndpoint => _udpClient?.Client.LocalEndPoint as IPEndPoint;
    public bool IsRunning => _isRunning;

    public event Func<IGameConnection, Task>? OnNewConnection;

    public KcpServerService(
        IOptions<KcpConfig> config,
        ILogger<KcpServerService> logger,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _config = config.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("KCP server is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var endPoint = new IPEndPoint(
                IPAddress.Parse(_config.ListenAddress),
                _config.Port);

            _udpClient = new UdpClient(endPoint);
            _isRunning = true;

            _logger.LogInformation(
                "🎮 FOBackend KCP Server started on {Address}:{Port}" +
                "\n   Config: SND_WND={SndWnd}, RCV_WND={RcvWnd}, NoCG={NoCg}, MinRTO={MinRto}ms",
                _config.ListenAddress,
                _config.Port,
                _config.SendWindowSize,
                _config.ReceiveWindowSize,
                _config.NoCongestionWindow,
                _config.MinRtoMs);

            // 启动 UDP 数据接收循环
            _ = ListenLoopAsync(_cts.Token);
            // 启动 KCP Update 驱动循环
            _ = UpdateLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start KCP server");
            throw;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                await HandleUdpPacketAsync(result.RemoteEndPoint, result.Buffer, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    _logger.LogError(ex, "Error in UDP listen loop");
            }
        }
    }

    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            uint current = (uint)Environment.TickCount;

            // 1. 驱动所有 KCP 会话的内部状态机
            foreach (var session in _sessions.Values.ToList())
            {
                try
                {
                    session.Update(current);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "KCP update error for conv {Conv}", session.Conv);
                }
            }

            // 2. 从所有会话中取出已就绪的应用层数据，交给 Adapter
            foreach (var session in _sessions.Values.ToList())
            {
                var adapter = session.Adapter;
                if (adapter == null) continue;

                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    while (session.TryReceive(buffer, out int received) && received > 0)
                    {
                        var copy = new byte[received];
                        Buffer.BlockCopy(buffer, 0, copy, 0, received);

                        _ = adapter.HandleReceivedDataAsync(copy).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                _logger.LogError(t.Exception, "Adapter receive error for conv {Conv}", session.Conv);
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            // 3. 计算下一次最早需要唤醒的时间
            uint nextCheck = current + 5;
            foreach (var session in _sessions.Values)
            {
                var chk = session.Check(current);
                if (chk < nextCheck) nextCheck = chk;
            }

            int delay = (int)((long)nextCheck - (long)current);
            if (delay < 1) delay = 1;
            if (delay > 10) delay = 10;

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleUdpPacketAsync(IPEndPoint remoteEndPoint, byte[] data, CancellationToken ct)
    {
        // 握手检测：ClientHello
        if (data.Length == 5 && data[0] == 0x01)
        {
            await HandleHandshakeAsync(remoteEndPoint, data);
            return;
        }

        // 最小 KCP 包长度检查
        if (data.Length < KcpCore.IKCP_OVERHEAD)
        {
            _logger.LogTrace("Dropping short packet ({Length} bytes) from {Remote}", data.Length, remoteEndPoint);
            return;
        }

        // 提取 conv
        uint conv = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        if (conv == 0)
        {
            _logger.LogTrace("Dropping packet with zero conv from {Remote}", remoteEndPoint);
            return;
        }

        if (_sessions.TryGetValue(conv, out var session))
        {
            session.Input(data);

            // 立即尝试提取应用数据（Input 可能刚好补齐了接收队列）
            if (session.Adapter != null)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    while (session.TryReceive(buffer, out int received) && received > 0)
                    {
                        var copy = new byte[received];
                        Buffer.BlockCopy(buffer, 0, copy, 0, received);

                        _ = session.Adapter.HandleReceivedDataAsync(copy).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                _logger.LogError(t.Exception, "Adapter receive error for conv {Conv}", conv);
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        else
        {
            _logger.LogDebug("Received KCP packet for unknown conv {Conv} from {Remote}", conv, remoteEndPoint);
        }
    }

    private async Task HandleHandshakeAsync(IPEndPoint remoteEndPoint, byte[] data)
    {
        if (data.Length < 5) return;

        uint clientRandom = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, 4));

        // 分配 conv（避免 0）
        uint conv;
        do
        {
            conv = (uint)Interlocked.Increment(ref _convCounter);
        } while (conv == 0);

        var session = new KcpSession(conv, remoteEndPoint, _udpClient!, _config, _logger);
        if (!_sessions.TryAdd(conv, session))
        {
            _logger.LogWarning("Failed to register session for conv {Conv}", conv);
            return;
        }

        var adapter = new KcpConnectionAdapter(session, _loggerFactory.CreateLogger<KcpConnectionAdapter>());
        session.Adapter = adapter;

        // 发送 ServerHello [0x02][conv:4][timestamp:4]
        var response = new byte[9];
        response[0] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(1, 4), conv);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(5, 4), (uint)Environment.TickCount);

        try
        {
            _udpClient!.Send(response, response.Length, remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send handshake response to {Remote}", remoteEndPoint);
            _sessions.TryRemove(conv, out _);
            return;
        }

        _logger.LogInformation("New KCP connection: Conv={Conv} from {Remote}", conv, remoteEndPoint);

        if (OnNewConnection != null)
        {
            await OnNewConnection(adapter);
        }
    }

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return;

        _isRunning = false;
        _logger.LogInformation("Stopping KCP server...");

        _cts?.Cancel();

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing UDP client during shutdown");
        }

        foreach (var session in _sessions.Values)
        {
            try
            {
                session.Adapter?.Disconnect("Server stopping");
                session.Adapter?.Dispose();
            }
            catch { }
        }
        _sessions.Clear();

        _logger.LogInformation("KCP server stopped");
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }
}

// ============================================================
// 扩展方法：DI 注册辅助
// ============================================================

public static class TransportServiceExtensions
{
    /// <summary>
    /// 注册传输层服务（在 DI 容器中使用）
    /// </summary>
    public static IServiceCollection AddTransportServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KcpConfig>(configuration.GetSection("Connection"));
        services.Configure<Security.HeartbeatConfig>(configuration.GetSection("Security"));
        services.Configure<Security.RateLimitConfig>(configuration.GetSection("RateLimit"));

        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<Security.HeartbeatManager>();
        services.AddSingleton<Security.RateLimiter>();
        services.AddSingleton<IKcpServerService, KcpServerService>();

        return services;
    }
}
