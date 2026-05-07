using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace FOBackend.Transport.Kcp;

/// <summary>
/// KCP 会话：将 KCP 协议核心与 UDP 网络层桥接
/// 每个客户端连接对应一个 KcpSession 实例
/// </summary>
public sealed class KcpSession
{
    public uint Conv { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public KcpConnectionAdapter? Adapter { get; set; }

    private readonly KcpCore _kcp;
    private readonly UdpClient _udpClient;
    private readonly ILogger _logger;

    public KcpSession(uint conv, IPEndPoint remoteEndPoint, UdpClient udpClient, KcpConfig config, ILogger logger)
    {
        Conv = conv;
        RemoteEndPoint = remoteEndPoint;
        _udpClient = udpClient;
        _logger = logger;

        _kcp = new KcpCore(conv, data =>
        {
            try
            {
                udpClient.Send(data, data.Length, remoteEndPoint);
            }
            catch (Exception ex)
            {
                logger.LogTrace(ex, "KCP output failed for conv {Conv}", conv);
            }
        });

        // 快速模式：nodelay=1, interval=10ms, resend=2, nc=1(禁用拥塞控制)
        _kcp.NoDelay(1, config.UpdateIntervalMs, config.MaxResend, config.NoCongestionWindow ? 1 : 0);
        _kcp.SetMinRto(config.MinRtoMs);
        _kcp.WndSize(config.SendWindowSize, config.ReceiveWindowSize);
    }

    /// <summary>
    /// 将收到的 UDP 数据喂给 KCP 协议栈
    /// </summary>
    public void Input(ReadOnlySpan<byte> data) => _kcp.Input(data);

    /// <summary>
    /// 发送应用层数据（经 KCP 可靠传输）
    /// </summary>
    public void Send(ReadOnlySpan<byte> data) => _kcp.Send(data);

    /// <summary>
    /// 驱动 KCP 内部状态机（重传、ACK、窗口探测等）
    /// </summary>
    public void Update(uint current) => _kcp.Update(current);

    /// <summary>
    /// 计算下次需要 Update 的最早时间
    /// </summary>
    public uint Check(uint current) => _kcp.Check(current);

    /// <summary>
    /// 尝试从 KCP 接收队列取出一条完整的应用层消息
    /// </summary>
    public bool TryReceive(Span<byte> buffer, out int received)
    {
        int peek = _kcp.PeekSize();
        if (peek < 0) { received = 0; return false; }
        if (peek > buffer.Length) { received = 0; return false; }
        received = _kcp.Recv(buffer);
        return received >= 0;
    }
}
