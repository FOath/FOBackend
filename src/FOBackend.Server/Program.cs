using Serilog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FOBackend.Protocol;
using FOBackend.Protocol.Messages;
using FOBackend.Transport;
using FOBackend.Transport.Kcp;
using FOBackend.Transport.Security;
using FOBackend.Core.Players;
using FOBackend.Core.Sessions;
using FOBackend.Persistence.Repositories;

// ============================================================
// FOBackend Server - 主程序入口
// 职责：
// 1. 配置日志系统（Serilog）
// 2. 注册所有服务到 DI 容器
// 3. 启动 KCP 服务器监听
// 4. 处理优雅关闭
// ============================================================

Serilog.Log.Logger = new Serilog.LoggerConfiguration()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        "logs/fobackend-.log",
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 7)
    .MinimumLevel.Debug()  // 开发阶段全量日志；生产改为 Information
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    Serilog.Log.Information("🎮 Starting FOBackend Server...");
    
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            #region ======== 配置绑定 ========
            
            services.Configure<KcpConfig>(context.Configuration.GetSection("Connection"));
            services.Configure<HeartbeatConfig>(context.Configuration.GetSection("Security"));
            services.Configure<RateLimitConfig>(context.Configuration.GetSection("Security"));
            services.Configure<FOBackend.Core.FrameSync.FrameSyncConfig>(
                context.Configuration.GetSection("FrameSync"));

            #endregion

            #region ======== 核心服务注册 ========
            
            // 传输层
            services.AddSingleton<ConnectionManager>();
            services.AddSingleton<HeartbeatManager>();
            services.AddSingleton<RateLimiter>();
            services.AddSingleton<IKcpServerService, KcpServerService>();
            
            // 业务层 - 玩家管理
            services.AddSingleton<IPlayerManager, PlayerManagerImpl>();
            
            // 业务层 - 房间管理
            services.AddSingleton<ISessionManager, SessionManagerImpl>();
            
            // 持久化层（开发环境使用 SQLite）
            var connectionString = context.Configuration.GetValue<string>("Database:ConnectionString") 
                ?? "Data Source=data/fobackend.db";
            
            // 确保目录存在
            var dbDir = Path.GetDirectoryName(connectionString.Replace("Data Source=", ""));
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
            
            services.AddSingleton<IPlayerRepository>(sp => 
                new SqlitePlayerRepository(connectionString, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlitePlayerRepository>>()));
            
            services.AddSingleton<IMatchHistoryRepository>(sp => 
                new SqliteMatchHistoryRepository(connectionString, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteMatchHistoryRepository>>()));

            #endregion

            #region ======== 托管服务（后台任务）=====

            services.AddHostedService<GameServerHostedService>();

            #endregion
        })
        .Build();

    // 启动应用
    await host.RunAsync();
}
catch (Exception ex)
{
    Serilog.Log.Fatal(ex, "💥 Application terminated unexpectedly");
}
finally
{
    await Serilog.Log.CloseAndFlushAsync();
}

// ============================================================
/// <summary>
/// 游戏服务器托管服务
/// 负责：
/// 1. 启动 KCP 监听
/// 2. 初始化数据库
/// 3. 连接事件处理
/// 4. 优雅停止
/// </summary>
public class GameServerHostedService : IHostedService, IDisposable
{
    private readonly IKcpServerService _kcpServer;
    private readonly ConnectionManager _connectionManager;
    private readonly HeartbeatManager _heartbeatManager;
    private readonly ISessionManager _sessionManager;
    private readonly IPlayerManager _playerManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GameServerHostedService> _logger;
    private readonly Microsoft.Extensions.Options.IOptions<KcpConfig> _kcpOptions;

    public GameServerHostedService(
        IKcpServerService kcpServer,
        ConnectionManager connectionManager,
        HeartbeatManager heartbeatManager,
        ISessionManager sessionManager,
        IPlayerManager playerManager,
        IServiceProvider serviceProvider,
        ILogger<GameServerHostedService> logger,
        Microsoft.Extensions.Options.IOptions<KcpConfig> kcpOptions)
    {
        _kcpServer = kcpServer;
        _connectionManager = connectionManager;
        _heartbeatManager = heartbeatManager;
        _sessionManager = sessionManager;
        _playerManager = playerManager;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _kcpOptions = kcpOptions;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("  🎮 FOBackend Server v1.0              ");
        _logger.LogInformation("  Frame Sync Backend for 1v1 Games       ");
        _logger.LogInformation("  Protocol: UDP + KCP                    ");
        _logger.LogInformation("  Target FPS: 60                        ");
        _logger.LogInformation("========================================");

        // 初始化心跳管理器的回调
        _heartbeatManager.Initialize(
            getLastActivityTime: connId => _connectionManager.GetConnection(connId)?.LastActivityTime,
            onTimeout: connId =>
            {
                _logger.LogWarning("⏰ Connection timeout: {ConnId}", connId);
                var conn = _connectionManager.GetConnection(connId);
                if (conn != null)
                {
                    _connectionManager.TryRemove(connId);
                }
            });

        // 注册 KCP 新连接事件处理
        _kcpServer.OnNewConnection += HandleNewConnectionAsync;

        // 启动 KCP 服务器
        await _kcpServer.StartAsync(cancellationToken);

        _logger.LogInformation(
            "✅ Server is listening on {Address}:{Port}",
            _kcpOptions.Value.ListenAddress,
            _kcpOptions.Value.Port);

        _logger.LogInformation("Press Ctrl+C to stop...");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 Stopping server...");

        // 停止 KCP 服务器
        await _kcpServer.StopAsync(cancellationToken);
        
        // 清理所有活跃房间（如果实现了 SessionManager 清理接口）
        if (_sessionManager is IDisposable disposableSession)
        {
            disposableSession.Dispose();
        }

        _logger.LogInformation("✅ Server stopped gracefully");
    }

    /// <summary>
    /// 处理新的客户端连接
    /// </summary>
    private async Task HandleNewConnectionAsync(IGameConnection connection)
    {
        _logger.LogDebug("🔗 New connection established: {ConnId} from {Remote}",
            connection.ConnectionId, connection.RemoteEndPoint);

        // 注册到连接管理器
        _connectionManager.TryRegister(connection);
        
        // 注册断开事件
        connection.OnDisconnected += reason =>
        {
            _logger.LogInformation("🔌 Connection disconnected: {ConnId}, Reason={Reason}",
                connection.ConnectionId, reason);
            
            // 如果玩家已认证，从房间移除
            if (!string.IsNullOrEmpty(connection.PlayerId))
            {
                var session = _sessionManager.GetSessionByPlayerId(connection.PlayerId);
                if (session != null)
                {
                    session.RemovePlayer(connection.PlayerId, LeaveReason.Disconnected);
                }
                
                _connectionManager.UnbindPlayer(connection.PlayerId);
            }
        };

        // 注册数据接收事件（消息路由）
        connection.OnDataReceived = (msgId, payload) => HandleReceivedMessageAsync(connection, msgId, payload);

        // TODO: 这里应该启动一个超时检测计时器
        // 如果在一定时间内未收到 AuthRequest，自动断开连接
    }

    /// <summary>
    /// 处理收到的消息（路由到对应的 Handler）
    /// 这是协议层的核心分发逻辑！
    /// </summary>
    private async Task HandleReceivedMessageAsync(IGameConnection connection, MessageId messageId, byte[] payload)
    {
        try
        {
            switch (messageId)
            {
                case MessageId.AuthRequest:
                    await HandleAuthRequestAsync(connection, payload);
                    break;
                    
                case MessageId.HeartbeatRequest:
                    await HandleHeartbeatRequestAsync(connection, payload);
                    break;
                    
                case MessageId.CreateRoomRequest:
                    await HandleCreateRoomRequestAsync(connection, payload);
                    break;
                    
                case MessageId.JoinRoomRequest:
                    await HandleJoinRoomRequestAsync(connection, payload);
                    break;
                    
                case MessageId.LeaveRoomRequest:
                    await HandleLeaveRoomRequestAsync(connection, payload);
                    break;
                    
                case MessageId.ReadyRequest:
                    await HandleReadyRequestAsync(connection, payload);
                    break;
                    
                case MessageId.PlayerInputReport:
                    await HandlePlayerInputReportAsync(connection, payload);
                    break;
                    
                case MessageId.ResendFrameRequest:
                    await HandleResendFrameRequestAsync(connection, payload);
                    break;
                    
                default:
                    _logger.LogWarning("Unknown message ID received: {MsgId} ({MsgIdValue})", 
                        messageId, (ushort)messageId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message {MsgId}", messageId);
        }
    }

    // ======== 消息处理器（简化版，实际应拆分为独立的Handler类）=======
    
    // 注意：这些方法需要获取当前连接上下文（通过闭包或异步上下文传递）
    // 此处为框架示例，实际实现需要更完善的架构设计
    
    private async Task HandleAuthRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandleHeartbeatRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandleCreateRoomRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandleJoinRoomRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandleLeaveRoomRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandleReadyRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandlePlayerInputReportAsync(IGameConnection connection, byte[] payload) { /* TODO */ }
    private async Task HandleResendFrameRequestAsync(IGameConnection connection, byte[] payload) { /* TODO */ }

    public void Dispose()
    {
        // 清理资源
    }
}
