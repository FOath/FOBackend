using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FOBackend.Transport.Kcp;

/// <summary>
/// KCP 协议配置
/// 针对 60 FPS 实时对战游戏优化
/// </summary>
public class KcpConfig
{
    /// <summary>
    /// UDP 监听端口
    /// 默认：7777
    /// </summary>
    public int Port { get; set; } = 7777;
    
    /// <summary>
    /// 监听地址
    /// 默认：0.0.0.0（监听所有网卡）
    /// </summary>
    public string ListenAddress { get; set; } = "0.0.0.0";
    
    // ========== KCP 参数配置 ==========
    
    /// <summary>
    /// 发送窗口大小
    /// 60 FPS 下建议增大以减少阻塞
    /// 默认值：128（标准32的4倍）
    /// </summary>
    public int SendWindowSize { get; set; } = 128;
    
    /// <summary>
    /// 接收窗口大小
    /// 默认值：128
    /// </summary>
    public int ReceiveWindowSize { get; set; } = 128;
    
    /// <summary>
    /// KCP 内部更新间隔（毫秒）
    /// 影响协议响应速度和CPU占用
    /// 10ms 是较好的平衡点
    /// </summary>
    public int UpdateIntervalMs { get; set; } = 10;
    
    /// <summary>
    /// 是否禁用拥塞控制（快速模式）
    /// 游戏场景推荐开启（降低延迟优先于吞吐量）
    /// true: 不启用标准TCP拥塞控制算法
    /// </summary>
    public bool NoCongestionWindow { get; set; } = true;
    
    /// <summary>
    /// 最小 RTO（Retransmission Timeout，重传超时时间，毫秒）
    /// 降低此值可加快丢包恢复速度
    /// 标准 TCP 为 200ms，游戏建议 30-50ms
    /// </summary>
    public int MinRtoMs { get; set; } = 30;
    
    /// <summary>
    /// 最大重传次数
    /// 超过后判定为连接中断
    /// </summary>
    public int MaxResend { get; set; } = 4;
    
    /// <summary>
    /// 是否禁用流控（Flow Control）
    /// 游戏场景推荐关闭（减少延迟波动）
    /// </summary>
    public bool DisableFlowControl { get; set; } = true;
    
    // ========== 安全配置 ==========
    
    /// <summary>
    /// 最大并发连接数
    /// 1v1 对战服务器通常不需要太多
    /// 可根据硬件调整
    /// </summary>
    public int MaxConnections { get; set; } = 500;
    
    /// <summary>
    /// 是否启用 Syn Cookie 握手防 DDoS
    /// 强烈推荐开启！
    /// </summary>
    public bool EnableSynCookie { get; set; } = true;
}

/// <summary>
/// KCP 服务器服务
/// 封装了 KCP 的启动、停止、连接管理
/// </summary>
public interface IKcpServerService : IAsyncDisposable
{
    /// <summary>
    /// 当前监听的端点信息
    /// </summary>
    System.Net.IPEndPoint? LocalEndpoint { get; }
    
    /// <summary>
    /// 是否正在运行
    /// </summary>
    bool IsRunning { get; }
    
    /// <summary>
    /// 新连接事件
    /// 当有新的客户端建立KCP连接时触发
    /// </summary>
    event Func<IGameConnection, Task>? OnNewConnection;
    
    /// <summary>
    /// 启动 KCP 服务器
    /// </summary>
    ValueTask StartAsync(CancellationToken ct = default);
    
    /// <summary>
    /// 停止 KCP 服务器
    /// </summary>
    ValueTask StopAsync(CancellationToken ct = default);
}
