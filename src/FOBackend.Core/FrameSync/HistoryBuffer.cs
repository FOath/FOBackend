using System.Diagnostics.CodeAnalysis;

namespace FOBackend.Core.FrameSync;

/// <summary>
/// 环形缓冲区（用于历史帧同步包缓存）
/// 支持按帧号快速索引查找
/// 
/// 设计要点：
/// - 大小必须是2的幂（便于位运算取模）
/// - O(1) 时间复杂度的添加和查询
/// - 线程不安全（由外部锁保护或单线程写入）
/// 
/// 用途：
/// - 响应客户端的丢包重传请求
/// - 对局结束后保存完整回放数据
/// </summary>
internal class CircularBuffer<T> where T : class
{
    private readonly T?[] _buffer;
    private readonly int _mask;
    private volatile int _headIndex;
    private volatile int _tailIndex;
    private readonly int _capacity;
    
    /// <summary>
    /// 构造缓冲区
    /// </summary>
    /// <param name="sizePowerOf2">容量（必须为2的幂）</param>
    public CircularBuffer(int sizePowerOf2)
    {
        if (sizePowerOf2 <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizePowerOf2), "Size must be positive");
        
        // 向上对齐到最近的2的幂
        var alignedSize = 1;
        while (alignedSize < sizePowerOf2)
            alignedSize <<= 1;
        
        _capacity = alignedSize;
        _buffer = new T?[alignedSize];
        _mask = alignedSize - 1;
        _headIndex = 0;
        _tailIndex = 0;
    }
    
    /// <summary>
    /// 实际容量
    /// </summary>
    public int Capacity => _capacity;
    
    /// <summary>
    /// 当前存储的元素数量
    /// </summary>
    public int Count
    {
        get
        {
            var head = Volatile.Read(ref _headIndex);
            var tail = Volatile.Read(ref _tailIndex);
            return tail - head;
        }
    }
    
    /// <summary>
    /// 添加元素到缓冲区尾部
    /// 使用帧号作为索引（自动处理溢出）
    /// </summary>
    public void Add(int frameNumber, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        
        var index = frameNumber & _mask;  // 位运算取模（超快！）
        
        // 写入数据
        Volatile.Write(ref _buffer[index], item);
        
        // 更新尾指针（仅当新frame > 当前tail时）
        var currentTail = Volatile.Read(ref _tailIndex);
        while (true)
        {
            if (frameNumber <= currentTail)
                break;  // 不允许插入比当前尾部更早的帧
            
            var prevTail = Interlocked.CompareExchange(
                ref _tailIndex, 
                frameNumber, 
                currentTail);
            
            if (prevTail == currentTail)
                break;  // CAS成功
                
            currentTail = prevTail;  // CAS失败，重试
        }
        
        // 如果覆盖了旧数据，更新头指针
        var currentHead = Volatile.Read(ref _headIndex);
        if (frameNumber - currentHead >= _capacity)
        {
            Interlocked.CompareExchange(
                ref _headIndex, 
                frameNumber - _capacity + 1, 
                currentHead);
        }
    }
    
    /// <summary>
    /// 尝试获取指定帧号的元素
    /// </summary>
    /// <returns>true 表示找到且未过期；false表示不存在或已被覆盖</returns>
    public bool TryGet(int frameNumber, [NotNullWhen(true)] out T? item)
    {
        item = null;
        
        var head = Volatile.Read(ref _headIndex);
        var tail = Volatile.Read(ref _tailIndex);
        
        // 检查帧号是否在有效范围内 [head, tail]
        if (frameNumber < head || frameNumber >= head + _capacity)
            return false;
        
        var index = frameNumber & _mask;
        item = Volatile.Read(ref _buffer[index]);
        
        // 双重验证：确保返回的不是null或过期数据
        if (item != null && frameNumber >= head)
            return true;
            
        return false;
    }
    
    /// <summary>
    /// 获取当前缓冲区中最早的帧号
    /// </summary>
    public int? MinFrame => Volatile.Read(ref _headIndex);
    
    /// <summary>
    /// 获取当前缓冲区中最新的帧号
    /// </summary>
    public int? MaxFrame => Volatile.Read(ref _tailIndex) - 1;
    
    /// <summary>
    /// 清空缓冲区
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _capacity; i++)
        {
            _buffer[i] = default;
        }
        
        Volatile.Write(ref _headIndex, 0);
        Volatile.Write(ref _tailIndex, 0);
    }
}
