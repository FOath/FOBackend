using FOBackend.Transport.Kcp;
using Microsoft.Extensions.Options;

namespace BattleService.Services;

/// <summary>
/// KCP 后台托管服务
/// 负责启动 KCP 服务器并处理客户端连接
/// </summary>
public class KcpHostedService : IHostedService, IDisposable
{
    private readonly IKcpServerService _kcpServer;
    private readonly IBattleRoomManager _roomManager;
    private readonly ILogger<KcpHostedService> _logger;

    public KcpHostedService(
        IKcpServerService kcpServer,
        IBattleRoomManager roomManager,
        ILogger<KcpHostedService> logger)
    {
        _kcpServer = kcpServer;
        _roomManager = roomManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting KCP hosted service...");
        await _kcpServer.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping KCP hosted service...");
        await _kcpServer.StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        _kcpServer.DisposeAsync().AsTask().Wait();
    }
}
