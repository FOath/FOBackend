using System.Net;
using FOBackend.Protocol;
using FOBackend.Protocol.Messages;
using Microsoft.Extensions.Logging;

namespace FOBackend.Transport.Kcp;

/// <summary>
/// KCP 连接适配器
/// 将 KCP 会话包装为统一的 IGameConnection 接口
/// </summary>
public sealed class KcpConnectionAdapter : IGameConnection
{
    private readonly KcpSession _session;
    private readonly ILogger<KcpConnectionAdapter> _logger;
    private readonly object _syncRoot = new();

    public string ConnectionId { get; }
    public EndPoint? RemoteEndPoint => _session.RemoteEndPoint;
    public DateTime ConnectedTime { get; }
    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;
    public ConnectionState State { get; set; } = ConnectionState.Connected;
    public string? PlayerId { get; set; }
    public string? SessionToken { get; set; }

    public Func<MessageId, byte[], Task>? OnDataReceived { get; set; }
    public Action<string>? OnDisconnected { get; set; }

    private bool _disposed;

    public KcpConnectionAdapter(KcpSession session, ILogger<KcpConnectionAdapter> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
        ConnectionId = session.Conv.ToString("X8");
        ConnectedTime = DateTime.UtcNow;

        _logger.LogDebug("KCP connection created: {ConnId} from {Remote}",
            ConnectionId, session.RemoteEndPoint);
    }

    #region 发送操作

    public Task SendAsync<TMessage>(TMessage message, DeliveryMode mode = DeliveryMode.Reliable)
        where TMessage : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var payload = ProtoSerializer.Serialize(message);
            var msgId = ResolveMessageId(message);

            if (msgId == MessageId.Unknown)
            {
                _logger.LogWarning("Unknown message type: {Type}", message.GetType().Name);
                return Task.CompletedTask;
            }

            return SendPacketAsync(msgId, payload, mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {ConnId}", ConnectionId);
            return Task.CompletedTask;
        }
    }

    public Task SendRawAsync(byte[] data, DeliveryMode mode = DeliveryMode.Reliable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _session.Send(data);
        return Task.CompletedTask;
    }

    public Task SendPacketAsync(MessageId messageId, byte[] payload, DeliveryMode mode = DeliveryMode.Reliable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var packet = PacketBuilder.Build(messageId, payload);

        _logger.LogTrace("Sending packet to {ConnId}: MsgId={MsgId}, Size={Size}",
            ConnectionId, messageId, packet.Length);

        _session.Send(packet);
        return Task.CompletedTask;
    }

    private static MessageId ResolveMessageId<TMessage>(TMessage message) where TMessage : class
    {
        return message switch
        {
            HeartbeatResponse => MessageId.HeartbeatResponse,
            AuthenticateResponse => MessageId.AuthResponse,
            FrameSyncPackage => MessageId.FrameSyncPackage,
            FrameSyncStartNotification => MessageId.FrameSyncStart,
            FrameSyncEndNotification => MessageId.FrameSyncEnd,
            ResendFrameResponse => MessageId.ResendFrameResponse,
            CreateRoomResponse => MessageId.CreateRoomResponse,
            JoinRoomResponse => MessageId.JoinRoomResponse,
            ReadyResponse => MessageId.ReadyResponse,
            LeaveRoomResponse => MessageId.LeaveRoomResponse,
            RoomStatusChangedNotification => MessageId.RoomStatusChanged,
            PlayerJoinedNotification => MessageId.PlayerJoined,
            PlayerLeftNotification => MessageId.PlayerLeft,
            _ => MessageId.Unknown
        };
    }

    #endregion

    #region 数据接收处理

    /// <summary>
    /// 处理从 KCP 层收到的应用数据
    /// 由 KcpServerService 的 UpdateLoop / ListenLoop 驱动调用
    /// </summary>
    public async Task HandleReceivedDataAsync(ReadOnlyMemory<byte> data)
    {
        LastActivityTime = DateTime.UtcNow;

        try
        {
            var dataArray = data.ToArray();
            if (!PacketBuilder.TryParse(dataArray, out var msgId, out var payload))
            {
                _logger.LogWarning("Invalid packet received from {ConnId}", ConnectionId);
                return;
            }

            _logger.LogTrace("Received from {ConnId}: MsgId={MsgId}, Size={Size}",
                ConnectionId, msgId, payload.Length);

            var handler = OnDataReceived;
            if (handler != null)
            {
                await handler(msgId, payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing data from {ConnId}", ConnectionId);
        }
    }

    #endregion

    #region 生命周期

    public void Disconnect(string reason = "Normal disconnect")
    {
        lock (_syncRoot)
        {
            if (_disposed) return;

            State = ConnectionState.Disconnected;

            _logger.LogInformation("Disconnecting connection {ConnId}: Reason={Reason}",
                ConnectionId, reason);

            OnDisconnected?.Invoke(reason);
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Disconnect("Dispose called");
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion
}
