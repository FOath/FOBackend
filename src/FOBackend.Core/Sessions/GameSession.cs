using System.Collections.Concurrent;
using FOBackend.Infrastructure;
using FOBackend.Protocol;
using FOBackend.Protocol.Messages;
using FOBackend.Transport;
using Microsoft.Extensions.Logging;
using FOBackend.Core.FrameSync;

namespace FOBackend.Core.Sessions;

/// <summary>
/// 游戏会话/房间实体（1v1 对战单元）
/// 
/// 职责：
/// - 管理房间的生命周期（创建→等待→Ready→游戏中→结束→关闭）
/// - 维护两个玩家的槽位信息
/// - 协调帧同步引擎的启动与停止
/// - 处理玩家加入/离开/断线事件
/// </summary>
public class GameSession : IDisposable
{
    #region 基础属性
    
    public string Id { get; init; }
    public GameMode Mode { get; init; }
    public SessionState State { get; private set; } = SessionState.Waiting;
    
    // 固定的两个玩家位置（1v1）
    public PlayerSlot? Player1 { get; private set; }  // 房主
    public PlayerSlot? Player2 { get; private set; }  // 加入者
    
    public DateTime CreateTime { get; init; }
    public DateTime? StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }
    
    // 帧同步引擎引用（Playing状态下有效）
    public FrameSyncEngine? FrameSyncEngine { get; private set; }
    
    // 自定义选项（如地图ID等）
    public Dictionary<string, string> Options { get; init; } = new();
    
    // 邀请码（便于分享）
    public string InviteCode { get; init; } = string.Empty;

    #endregion

    #region 事件
    
    /// <summary>
    /// 房间状态变化时触发
    /// 参数：当前实例, 旧状态, 新状态
    /// </summary>
    public event Action<GameSession, SessionState, SessionState>? OnStateChanged;
    
    /// <summary>
    /// 有新玩家加入时触发
    /// </summary>
    public event Action<GameSession, PlayerSlot>? OnPlayerJoined;
    
    /// <summary>
    /// 有玩家离开时触发
    /// </summary>
    public event Action<GameSession, string, LeaveReason>? OnPlayerLeft;
    
    /// <summary>
    /// 对局开始时触发
    /// </summary>
    public event Action<GameSession>? OnGameStart;
    
    /// <summary>
    /// 对局结束时触发
    /// </summary>
    public event Action<GameSession, EndReason>? OnGameEnd;

    #endregion

    #region 私有字段
    
    private readonly ILogger _logger;
    private readonly object _lockObj = new();
    private bool _disposed;
    
    // 用于通知其他玩家的连接列表（缓存）
    private List<IGameConnection> _notificationTargets => new[]
    {
        Player1?.Connection,
        Player2?.Connection
    }.Where(c => c != null).Cast<IGameConnection>().ToList();

    #endregion

    #region 构造函数

    public GameSession(string id, GameMode mode, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        
        Id = id;
        Mode = mode;
        CreateTime = DateTime.UtcNow;
        InviteCode = IdGenerator.InviteCode();
        _logger = logger;
        
        _logger.LogInformation(
            "🏠 GameSession created: {RoomId}, Mode={Mode}, InviteCode={Code}",
            id, mode, InviteCode);
    }

    #endregion

    #region 公共方法：玩家操作

    /// <summary>
    /// 将玩家添加到房间（返回槽位号和结果）
    /// </summary>
    /// <returns>(slotNumber: 槽位号(1或2), result: 操作结果)</returns>
    public (int slot, JoinResult result) AddPlayer(PlayerInfo playerInfo, IGameConnection connection)
    {
        ArgumentNullException.ThrowIfNull(playerInfo);
        ArgumentNullException.ThrowIfNull(connection);
        
        lock (_lockObj)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 状态检查
            if (State == SessionState.Playing || State == SessionState.Finished || State == SessionState.Closed)
                return (0, JoinResult.InvalidState);

            // 重复检查
            var existingSlot = GetPlayerSlot(playerInfo.PlayerId);
            if (existingSlot != null)
                return (existingSlot.SlotNumber, JoinResult.AlreadyInRoom);

            // 分配槽位
            PlayerSlot slot;
            int slotNum;

            if (Player1 == null)
            {
                // 第一个玩家成为房主
                slotNum = 1;
                slot = new PlayerSlot(playerInfo.PlayerId, playerInfo.PlayerName, slotNum);
                slot.Connection = connection;
                slot.IsReady = false;  // 初始未准备
                Player1 = slot;
                
                _logger.LogDebug("Player {Name} joined as P1 in room {RoomId}", 
                    playerInfo.PlayerName, Id);
            }
            else if (Player2 == null)
            {
                // 第二个玩家加入
                slotNum = 2;
                slot = new PlayerSlot(playerInfo.PlayerId, playerInfo.PlayerName, slotNum);
                slot.Connection = connection;
                slot.IsReady = false;
                Player2 = slot;
                
                _logger.LogInformation(
                    "✅ Room {RoomId} is now full! P1={P1}, P2={P2}", 
                    Id, Player1.PlayerName, playerInfo.PlayerName);
                
                // 双方到齐 → 自动转为 Ready 状态
                TransitionTo(SessionState.Ready);
            }
            else
            {
                // 已满员
                return (0, JoinResult.RoomFull);
            }

            // 触发事件
            OnPlayerJoined?.Invoke(this, slot);

            return (slotNum, JoinResult.Success);
        }
    }

    /// <summary>
    /// 移除玩家
    /// </summary>
    public void RemovePlayer(string playerId, LeaveReason reason = LeaveReason.NormalLeave)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        
        lock (_lockObj)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var slot = GetPlayerSlot(playerId);
            if (slot == null) return;

            _logger.LogInformation("Player {Player} leaving room {RoomId}, Reason={Reason}",
                playerId, Id, reason);

            // 清除槽位
            if (slot.SlotNumber == 1) Player1 = null;
            else if (slot.SlotNumber == 2) Player2 = null;

            // 触发离开事件
            OnPlayerLeft?.Invoke(this, playerId, reason);

            // 根据当前状态决定后续动作
            HandlePlayerLeaveDuringGame(reason);
            
            // 如果房间空了，回归 Waiting 或关闭
            if (Player1 == null && Player2 == null)
            {
                if (State == SessionState.Playing || State == SessionState.Finished)
                    Close();
                else
                    TransitionTo(SessionState.Waiting);  // 允许重新加入
            }
            else if (State == SessionState.Ready)
            {
                // 有人离开，取消 Ready
                TransitionTo(SessionState.Waiting);
            }
        }
    }

    /// <summary>
    /// 设置玩家准备状态
    /// 当双方都 Ready 后自动开始对局！
    /// </summary>
    public SetReadyResult SetReady(string playerId, bool isReady)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        
        lock (_lockObj)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var slot = GetPlayerSlot(playerId);
            if (slot == null)
                return SetReadyResult.PlayerNotFound;

            slot.IsReady = isReady;

            _logger.LogDebug("Player {Player} Ready={Ready} in room {RoomId}",
                playerId, isReady, Id);

            // 检查是否双方都准备好了
            if (isReady && Player1?.IsReady == true && Player2?.IsReady == true)
            {
                if (State == SessionState.Ready)
                {
                    // 🚀 所有条件满足 → 开始帧同步！
                    StartGameAsync().ConfigureAwait(false);
                }
            }

            return SetReadyResult.Success;
        }
    }

    /// <summary>
    /// 关闭房间（清理资源）
    /// </summary>
    public void Close()
    {
        lock (_lockObj)
        {
            if (_disposed || State == SessionState.Closed) return;

            _logger.LogInformation("🔒 Closing room {RoomId}, Final state was {State}", Id, State);

            // 停止帧同步引擎（如果正在运行）
            if (FrameSyncEngine != null)
            {
                try
                {
                    FrameSyncEngine.Dispose();
                    FrameSyncEngine = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing frame sync engine for room {RoomId}", Id);
                }
            }

            EndTime ??= DateTime.UtcNow;
            TransitionTo(SessionState.Closed);
        }
    }

    #endregion

    #region 私有方法：内部逻辑

    /// <summary>
    /// 开始游戏（启动帧同步引擎）
    /// </summary>
    private async Task StartGameAsync()
    {
        lock (_lockObj)
        {
            if (_disposed) return;
            
            _logger.LogInformation(
                "🎮 Starting game in room {RoomId}: P1={P1} vs P2={P2}",
                Id, Player1?.PlayerName, Player2?.PlayerName);

            TransitionTo(SessionState.Playing);
            StartTime = DateTime.UtcNow;
        }

        try
        {
            // 创建并配置帧同步引擎
            var config = new FrameSyncConfig();  // 使用默认60FPS配置
            
            var connections = new[] 
            { 
                Player1!.Connection!, 
                Player2!.Connection! 
            };
            
            var playerIds = new[] 
            { 
                Player1.PlayerId, 
                Player2.PlayerId 
            };

            FrameSyncEngine = new FrameSyncEngine(Id, playerIds, connections, config, _logger);

            // 注册引擎事件
            FrameSyncEngine.OnStopped += reason =>
            {
                _logger.LogInformation("Frame sync stopped for room {RoomId}, Reason={Reason}", Id, reason);
                HandleGameEnd(reason);
            };
            FrameSyncEngine.OnError += ex =>
            {
                _logger.LogError(ex, "Frame sync error in room {RoomId}", Id);
            };

            // 启动引擎！
            await FrameSyncEngine.StartAsync();

            OnGameStart?.Invoke(this);
            
            _logger.LogInformation(
                "🚀 Game started! Room={RoomId}, FPS={FPS}",
                Id, config.TargetFPS);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start game in room {RoomId}", Id);
            State = SessionState.Ready;  // 回退到Ready状态以便重试
        }
    }

    /// <summary>
    /// 处理游戏中的玩家掉线/离开
    /// </summary>
    private void HandlePlayerLeaveDuringGame(LeaveReason reason)
    {
        if (State == SessionState.Playing)
        {
            // 游戏中断处理
            _logger.LogWarning(
                "⚠️ Player left during game! Room={RoomId}, Reason={Reason}. Stopping frame sync...",
                Id, reason);

            // 异步停止帧同步引擎
            Task.Run(async () =>
            {
                try
                {
                    if (FrameSyncEngine != null)
                    {
                        await FrameSyncEngine.StopAsync(EndReason.PlayerDisconnect);
                        FrameSyncEngine = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping frame sync on player leave");
                }
            });
        }
    }

    /// <summary>
    /// 处理游戏结束
    /// </summary>
    private void HandleGameEnd(EndReason endReason)
    {
        EndTime ??= DateTime.UtcNow;
        TransitionTo(SessionState.Finished);
        
        _logger.LogInformation(
            "🏁 Game ended in room {RoomId}, Reason={Reason}, Duration={Duration:F1}s",
            Id, endReason, (EndTime.Value - StartTime)?.TotalSeconds ?? 0);

        OnGameEnd?.Invoke(this, endReason);
    }

    /// <summary>
    /// 状态转换（带日志和事件）
    /// </summary>
    private void TransitionTo(SessionState newState)
    {
        var oldState = State;
        State = newState;
        
        _logger.LogDebug(
            "Room {RoomId} state transition: {Old} -> {New}",
            Id, oldState, newState);
        
        OnStateChanged?.Invoke(this, oldState, newState);
    }

    /// <summary>
    /// 根据玩家ID查找其槽位
    /// </summary>
    private PlayerSlot? GetPlayerSlot(string playerId)
    {
        if (Player1?.PlayerId == playerId) return Player1;
        if (Player2?.PlayerId == playerId) return Player2;
        return null;
    }

    #endregion

    #region 辅助查询方法

    /// <summary>
    /// 获取房间内所有玩家ID
    /// </summary>
    public IReadOnlyList<string> GetAllPlayerIds()
    {
        lock (_lockObj)
        {
            var ids = new List<string>();
            if (Player1?.PlayerId != null) ids.Add(Player1.PlayerId);
            if (Player2?.PlayerId != null) ids.Add(Player2.PlayerId);
            return ids.AsReadOnly();
        }
    }

    /// <summary>
    /// 构建房间信息的 Protobuf 消息
    /// </summary>
    public RoomInfo BuildRoomInfo()
    {
        lock (_lockObj)
        {
            return new RoomInfo
            {
                RoomId = Id,
                GameMode = Mode,
                Status = (Protocol.Messages.RoomStatus)(int)State,  // 需要转换
                MaxPlayers = 2,
                CurrentPlayerCount = (Player1 != null ? 1 : 0) + (Player2 != null ? 1 : 0),
                HostPlayerId = Player1?.PlayerId ?? string.Empty,
                CreateTime = CreateTime.Ticks > 0 ? new DateTimeOffset(CreateTime).ToUnixTimeMilliseconds() : 0,
                Options = new Dictionary<string, string>(Options)
            };
        }
    }

    /// <summary>
    /// 获取所有已加入的玩家信息列表
    /// </summary>
    public List<PlayerInfo> BuildPlayerList()
    {
        lock (_lockObj)
        {
            var list = new List<PlayerInfo>();
            
            if (Player1 != null)
            {
                list.Add(new PlayerInfo
                {
                    PlayerId = Player1.PlayerId,
                    PlayerName = Player1.PlayerName,
                    IsReady = Player1.IsReady,
                    PingMs = (int)Player1.PingTracker.Average,
                    JoinTime = CreateTime.Ticks > 0 ? new DateTimeOffset(CreateTime).ToUnixTimeMilliseconds() : 0
                });
            }
            
            if (Player2 != null)
            {
                list.Add(new PlayerInfo
                {
                    PlayerId = Player2.PlayerId,
                    PlayerName = Player2.PlayerName,
                    IsReady = Player2.IsReady,
                    PingMs = (int)Player2.PingTracker.Average,
                    JoinTime = CreateTime.Ticks > 0 ? new DateTimeOffset(CreateTime).ToUnixTimeMilliseconds() : 0
                });
            }
            
            return list;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        lock (_lockObj)
        {
            if (_disposed) return;
            
            Close();
            _disposed = true;
        }
        
        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>
/// 加入房间的操作结果
/// </summary>
public enum JoinResult
{
    Success,           // 成功
    AlreadyInRoom,     // 已经在房间中
    RoomFull,          // 房间已满
    InvalidState       // 房间状态不允许加入（如已在游戏中）
}

/// <summary>
/// 准备就绪的操作结果
/// </summary>
public enum SetReadyResult
{
    Success,           // 成功
    PlayerNotFound     // 玩家不在房间中
}
