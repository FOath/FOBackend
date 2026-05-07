using FOBackend.Protocol.Messages;
using Microsoft.Extensions.Logging;

namespace FOBackend.Core.FrameSync;

/// <summary>
/// 帧同步配置参数（针对 60 FPS 实时对战优化）
/// </summary>
public class FrameSyncConfig
{
    /// <summary>
    /// 目标帧率 - 60 FPS（固定！）
    /// 帧间隔 = 1000 / 60 ≈ 16.666ms
    /// </summary>
    public int TargetFPS { get; set; } = 60;
    
    /// <summary>
    /// 帧间隔（毫秒）- 只读计算属性
    /// </summary>
    public double FrameIntervalMs => 1000.0 / TargetFPS;
    
    /// <summary>
    /// 单帧收集输入的超时时间
    /// 如果某玩家在此时限内没发送输入，跳过或使用默认空输入
    /// 推荐：100-200ms（约6-12帧的延迟容忍度）
    /// 太短会导致频繁丢帧，太长会增加延迟感知
    /// </summary>
    public int InputCollectTimeoutMs { get; set; } = 150;
    
    /// <summary>
    /// 历史输入缓存帧数（用于丢包重传请求响应）
    /// 缓存约5秒的数据（300帧 @ 60FPS）
    /// 内存开销：300帧 × 2玩家 × ~100bytes/包 ≈ 60KB（可忽略不计）
    /// </summary>
    public int HistoryBufferSize { get; set; } = 300;
    
    /// <summary>
    /// 关键帧间隔（每隔多少帧发送一次标记）
    /// 关键帧用于：
    /// - 客户端保存游戏状态快照检查点
    /// - 异常恢复时的回滚目标点
    /// 建议：每秒1次（60帧时即60帧间隔）
    /// </summary>
    public int KeyFrameInterval { get; set; } = 60;
    
    /// <summary>
    /// 最大允许的 RTT 差异（毫秒）
    /// 超过此值会触发 IsLagging 警告标志
    /// 如果两玩家RTT差异过大，体验会很差
    /// 建议：50-100ms
    /// </summary>
    public int MaxRttDiffMs { get; set; } = 100;
    
    /// <summary>
    /// 是否启用自适应帧率（实验性功能）
    /// 网络状况差时可临时降低帧率以保证流畅性
    /// 生产环境建议关闭（固定帧率更易调试和优化）
    /// </summary>
    public bool EnableAdaptiveFps { get; set; } = false;
}

/// <summary>
/// 帧同步引擎状态枚举
/// </summary>
public enum EngineState
{
    /// <summary>未启动</summary>
    Stopped,
    
    /// <summary>运行中</summary>
    Running,
    
    /// <summary>正在停止</summary>
    Stopping,
    
    /// <summary>错误状态</summary>
    Error,
    
    /// <summary>已暂停（如等待重连）</summary>
    Paused
}
