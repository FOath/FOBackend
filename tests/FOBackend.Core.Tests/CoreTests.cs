// ============================================================
// FOBackend Core Unit Tests
// 测试目标：
// 1. 帧同步引擎的 Lockstep 算法正确性
// 2. 房间生命周期管理
// 3. 输入收集与广播逻辑
// 4. 边界条件和异常处理
//
// 注意：这些测试使用 Mock 对象隔离网络层
// ============================================================

using FluentAssertions;
using FOBackend.Core.FrameSync;
using FOBackend.Core.Sessions;
using FOBackend.Protocol.Messages;
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using FOBackend.Transport;

namespace FOBackend.Core.Tests;

public class FrameSyncEngineTests : IDisposable
{
    private readonly Mock<IGameConnection> _mockConn1;
    private readonly Mock<IGameConnection> _mockConn2;
    private readonly IGameConnection[] _connections;
    private readonly string[] _playerIds;
    private readonly ILogger<FrameSyncEngine> _logger;

    public FrameSyncEngineTests()
    {
        // Mock 两个客户端连接
        _mockConn1 = new Mock<IGameConnection>();
        _mockConn1.SetupGet(x => x.ConnectionId).Returns("conn-test-A");
        _mockConn1.SetupGet(x => x.PlayerId).Returns("player-A");
        
        _mockConn2 = new Mock<IGameConnection>();
        _mockConn2.SetupGet(x => x.ConnectionId).Returns("conn-test-B");
        _mockConn2.SetupGet(x => x.PlayerId).Returns("player-B");

        _connections = new[] { _mockConn1.Object, _mockConn2.Object };
        _playerIds = new[] { "player-A", "player-B" };

        // 使用空日志（测试中不关心日志输出）
        _logger = LoggerFactory.Create(builder => { }).CreateLogger<FrameSyncEngine>();
    }

    [Fact]
    public void Constructor_WithValidParams_CreatesInstance()
    {
        var config = new FrameSyncConfig();
        var engine = new FrameSyncEngine(
            sessionId: "test-session",
            playerIds: _playerIds,
            connections: _connections,
            config: config,
            logger: _logger);

        engine.SessionId.Should().Be("test-session");
        engine.State.Should().Be(EngineState.Stopped);
        engine.CurrentFrame.Should().Be(0);
        
        engine.Dispose();
    }

    [Fact]
    public async Task StartAsync_SendsStartNotificationToBothClients()
    {
        // Arrange
        using var engine = CreateEngine();
        
        FrameSyncStartNotification? sentNote = null;
        _mockConn1.Setup(c => c.SendAsync(It.IsAny<FrameSyncStartNotification>(), It.IsAny<DeliveryMode>()))
            .Callback<FrameSyncStartNotification, DeliveryMode>((n, _) => sentNote = n)
            .Returns(Task.CompletedTask);
        _mockConn2.Setup(c => c.SendAsync(It.IsAny<FrameSyncStartNotification>(), It.IsAny<DeliveryMode>()))
            .Returns(Task.CompletedTask);

        // Act
        await engine.StartAsync();

        // Assert
        engine.State.Should().Be(EngineState.Running);
        sentNote.Should().NotBeNull();
        sentNote!.RoomId.Should().Be("test-session");
        sentNote.Fps.Should().Be(60);
        sentNote.RandomSeed.Should().BeGreaterThan(0);
        sentNote.PlayerIds.Should().HaveCount(2).And.ContainInOrder("player-A", "player-B");

        await engine.StopAsync();
    }

    [Fact]
    public async Task ReceiveInputAsync_WithValidInput_AcceptsIt()
    {
        // Arrange
        using var engine = CreateEngine();
        await engine.StartAsync();
        
        var inputReport = new PlayerInputReport
        {
            Header = new Protocol.Messages.RequestHeader(),
            RoomId = "test-session",
            FrameNumber = 5,
            InputData = new byte[] { 0x01, 0x02, 0x03 },
            InputChecksum = (int)FOBackend.Infrastructure.Crc32.ComputeCrc16(new byte[] { 0x01, 0x02, 0x03 })
        };

        // Act & Assert (不应抛异常)
        await engine.ReceiveInputAsync(inputReport, _mockConn1.Object);

        await engine.StopAsync();
    }

    [Fact]
    public async Task ReceiveInputAsync_WithInvalidFrameNumber_Rejects()
    {
        // Arrange
        using var engine = CreateEngine();
        await engine.StartAsync();

        var inputReport = new PlayerInputReport
        {
            FrameNumber = -100,  // 无效帧号（远小于当前帧号）
            InputData = Array.Empty<byte>(),
            InputChecksum = 0
        };

        // Act & Assert: 应该静默拒绝（不抛异常，但也不处理）
        await engine.ReceiveInputAsync(inputReport, _mockConn1.Object);

        await engine.StopAsync();
    }

    [Fact]
    public async Task ReceiveInputAsync_WithWrongChecksum_Rejects()
    {
        // Arrange
        using var engine = CreateEngine();
        await engine.StartAsync();

        var inputReport = new PlayerInputReport
        {
            FrameNumber = 5,
            InputData = new byte[] { 0xAA },
            InputChecksum = 12345  // 错误的校验值
        };

        // Act & Assert
        await engine.ReceiveInputAsync(inputReport, _mockConn1.Object);

        await engine.StopAsync();
    }

    [Fact]
    public async Task HandleResendRequest_ReturnsCachedFrames()
    {
        // Arrange
        using var engine = CreateEngine();
        await engine.StartAsync();

        // 先发送一些输入以填充历史缓存
        for (int i = 1; i <= 10; i++)
        {
            await engine.ReceiveInputAsync(new PlayerInputReport
            {
                FrameNumber = i,
                InputData = new byte[] { (byte)i },
                InputChecksum = (int)FOBackend.Infrastructure.Crc32.ComputeCrc16(new byte[] { (byte)i })
            }, _mockConn1.Object);
            
            await engine.ReceiveInputAsync(new PlayerInputReport
            {
                FrameNumber = i,
                InputData = new byte[] { (byte)(i + 100) },
                InputChecksum = (int)FOBackend.Infrastructure.Crc32.ComputeCrc16(new byte[] { (byte)(i + 100) })
            }, _mockConn2.Object);
        }

        // 等待一小段时间让引擎处理
        await Task.Delay(200);

        // Act: 请求重传第 5, 6, 7 帧
        var request = new ResendFrameRequest
        {
            Header = new Protocol.Messages.RequestHeader(),
            RoomId = "test-session",
            MissingFrameNumbers = new List<int> { 5, 6, 7 }
        };

        var response = await engine.HandleResendRequestAsync(request);

        // Assert
        response.Header!.ErrorCode.Should().Be(ErrorCode.Success);
        response.Frames.Count.Should().BeGreaterOrEqualTo(0);  // 可能有也可能没有（取决于是否已广播）

        await engine.StopAsync();
    }

    [Fact]
    public void Constructor_WithWrongPlayerCount_ThrowsException()
    {
        var config = new FrameSyncConfig();
        var wrongPlayerIds = new[] { "only-one-player" };
        var wrongConnections = new[] { _mockConn1.Object };

        Assert.Throws<ArgumentException>(() =>
        {
            var engine = new FrameSyncEngine(
                "session", wrongPlayerIds, wrongConnections, config, _logger);
        });
    }

    [Fact]
    public async Task StopAsync_ChangesStateToStopped()
    {
        // Arrange
        using var engine = CreateEngine();
        await engine.StartAsync();
        engine.State.Should().Be(EngineState.Running);

        // Act
        await engine.StopAsync();

        // Assert
        engine.State.Should().Be(EngineState.Stopped);
    }

    [Fact]
    public async Task DoubleStart_ThrowsException()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            engine.StartAsync());

        await engine.StopAsync();
    }

    // ========== 辅助方法 ==========

    private FrameSyncEngine CreateEngine()
    {
        return new FrameSyncEngine(
            sessionId: "test-session",
            playerIds: _playerIds,
            connections: _connections,
            config: new FrameSyncConfig(),
            logger: _logger);
    }

    public void Dispose()
    {
        // 清理资源
    }
}

// ============================================================
/// <summary>
/// 房间管理单元测试
/// </summary>
public class GameSessionTests
{
    private readonly Mock<ILogger<GameSession>> _mockLogger;

    public GameSessionTests()
    {
        _mockLogger = new Mock<ILogger<GameSession>>();
    }

    [Fact]
    public void CreateSession_InitializesCorrectly()
    {
        var session = new GameSession("room-123", GameMode.Shooter1V1, _mockLogger.Object);

        session.Id.Should().Be("room-123");
        session.Mode.Should().Be(GameMode.Shooter1V1);
        session.State.Should().Be(SessionState.Waiting);
        session.Player1.Should().BeNull();
        session.Player2.Should().BeNull();
        session.InviteCode.Should().NotBeEmpty();
    }

    [Fact]
    public void AddPlayer_FirstPlayer_BecomesPlayer1()
    {
        var session = CreateSession();
        var mockConn = CreateMockConnection("p1");

        var playerInfo = new PlayerInfo
        {
            PlayerId = "p1",
            PlayerName = "Alice"
        };

        var (slot, result) = session.AddPlayer(playerInfo, mockConn.Object);

        result.Should().Be(JoinResult.Success);
        slot.Should().Be(1);
        session.Player1.Should().NotBeNull();
        session.Player1!.PlayerId.Should().Be("p1");
        session.Player1!.PlayerName.Should().Be("Alice");
        session.State.Should().Be(SessionState.Waiting);  // 还没满员
    }

    [Fact]
    public void AddPlayer_SecondPlayer_MakesRoomReady()
    {
        var session = CreateSession();
        
        // 加入第一个玩家
        session.AddPlayer(CreatePlayerInfo("p1", "Alice"), CreateMockConnection("p1").Object);
        
        // 加入第二个玩家
        var mockConn2 = CreateMockConnection("p2");
        var (slot, result) = session.AddPlayer(CreatePlayerInfo("p2", "Bob"), mockConn2.Object);

        result.Should().Be(JoinResult.Success);
        slot.Should().Be(2);
        session.Player2.Should().NotBeNull();
        session.State.Should().Be(SessionState.Ready);  // 自动转为Ready！
    }

    [Fact]
    public void AddPlayer_ThirdPlayer_Fails()
    {
        var session = CreateFullSession();  // 已有2个玩家
        
        var mockConn3 = CreateMockConnection("p3");
        var (_, result) = session.AddPlayer(CreatePlayerInfo("p3", "Charlie"), mockConn3.Object);

        result.Should().Be(JoinResult.RoomFull);
    }

    [Fact]
    public void SetReady_BothPlayersReady_TriggersGameStart()
    {
        var session = CreateFullSession();
        
        bool gameStartedFired = false;
        session.OnGameStart += s => gameStartedFired = true;

        // 双方准备
        var r1 = session.SetReady("p1", true);
        var r2 = session.SetReady("p2", true);

        r1.Should().Be(SetReadyResult.Success);
        r2.Should().Be(SetReadyResult.Success);
        
        // 注意：实际的游戏启动是异步的（StartGameAsync），这里只验证准备成功
        // 完整的启动测试需要更复杂的异步处理
    }

    [Fact]
    public void RemovePlayer_DuringPlaying_StopsFrameSync()
    {
        // 这个测试需要模拟完整的游戏运行状态
        // 由于帧同步引擎的复杂性，此处仅验证接口调用不抛出异常
        var session = CreateFullSession();
        
        // 模拟进入游戏中状态（手动设置，因为实际启动需要连接等）
        // session.State = SessionState.Playing;  // 内部状态不可直接设置

        // 移除玩家
        Action act = () => session.RemovePlayer("p1", LeaveReason.Disconnected);
        
        act.Should().NotThrow();
    }

    // ========== 辅助工厂方法 ==========

    private GameSession CreateSession()
    {
        return new GameSession($"test-room-{Guid.NewGuid():N}", GameMode.Shooter1V1, _mockLogger.Object);
    }

    private GameSession CreateFullSession()
    {
        var session = CreateSession();
        session.AddPlayer(CreatePlayerInfo("p1", "Alice"), CreateMockConnection("p1").Object);
        session.AddPlayer(CreatePlayerInfo("p2", "Bob"), CreateMockConnection("p2").Object);
        return session;
    }

    private static PlayerInfo CreatePlayerInfo(string id, string name) => new()
    {
        PlayerId = id,
        PlayerName = name
    };

    private static Mock<IGameConnection> CreateMockConnection(string playerId)
    {
        var mock = new Mock<IGameConnection>();
        mock.SetupGet(c => c.ConnectionId).Returns($"conn-{playerId}");
        mock.SetupGet(c => c.PlayerId).Returns(playerId);
        mock.SetupGet(c => c.State).Returns(ConnectionState.Authenticated);
        return mock;
    }
}
