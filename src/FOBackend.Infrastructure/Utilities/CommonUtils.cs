using System;
using System.Collections.Generic;
using System.Numerics;

namespace FOBackend.Infrastructure;

/// <summary>
/// CRC-32 计算器（IEEE 802.3 标准）
/// 用于数据包完整性校验
/// </summary>
public static class Crc32
{
    // 预计算的查找表（性能优化）
    private static readonly uint[] Table;

    static Crc32()
    {
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ 0xEDB88320u;
                else
                    crc >>= 1;
            }
            Table[i] = crc;
        }
    }

    /// <summary>
    /// 计算字节数组的 CRC-32 值
    /// </summary>
    public static uint Compute(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        
        return Compute(data.AsSpan());
    }

    /// <summary>
    /// 计算 ReadOnlySpan 的 CRC-32 值（零拷贝高性能版本）
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        
        foreach (var b in data)
        {
            var index = (byte)((crc ^ b) & 0xFF);
            crc = (crc >> 8) ^ Table[index];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// 计算 CRC-16（可选，用于输入校验的轻量版本）
    /// CCITT-FALSE 多项式: x^16 + x^15 + x^2 + 1
    /// </summary>
    public static ushort ComputeCrc16(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        
        const ushort poly = 0x1021;
        ushort crc = 0xFFFF;

        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ poly);
                else
                    crc <<= 1;
            }
        }

        return crc;
    }
}

/// <summary>
/// 滑动窗口平均值计算器（用于RTT、延迟等指标平滑）
/// </summary>
public class RollingAverage
{
    private readonly Queue<long> _samples;
    private readonly int _windowSize;
    private long _sum;
    private long _count;

    public RollingAverage(int windowSize = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        
        _windowSize = windowSize;
        _samples = new Queue<long>(windowSize);
    }

    /// <summary>
    /// 添加一个样本值
    /// </summary>
    public void Update(long value)
    {
        lock (_samples)
        {
            if (_samples.Count >= _windowSize)
            {
                var oldest = _samples.Dequeue();
                _sum -= oldest;
                _count--;
            }

            _samples.Enqueue(value);
            _sum += value;
            _count++;
        }
    }

    /// <summary>
    /// 获取当前平均值
    /// </summary>
    public long Average
    {
        get
        {
            lock (_samples)
            {
                if (_count == 0) return 0;
                return _sum / _count;
            }
        }
    }

    /// <summary>
    /// 获取最大值（窗口内）
    /// </summary>
    public long Max
    {
        get
        {
            lock (_samples)
            {
                if (_count == 0) return 0;
                long max = long.MinValue;
                foreach (var s in _samples)
                {
                    if (s > max) max = s;
                }
                return max;
            }
        }
    }

    /// <summary>
    /// 获取最小值（窗口内）
    /// </summary>
    public long Min
    {
        get
        {
            lock (_samples)
            {
                if (_count == 0) return 0;
                long min = long.MaxValue;
                foreach (var s in _samples)
                {
                    if (s < min) min = s;
                }
                return min;
            }
        }
    }

    /// <summary>
    /// 清空所有样本
    /// </summary>
    public void Clear()
    {
        lock (_samples)
        {
            _samples.Clear();
            _sum = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// ID 生成器（简化版UUID生成）
/// </summary>
public static class IdGenerator
{
    private static readonly Random Random = new();

    /// <summary>
    /// 生成短格式的唯一ID（22字符，URL安全）
    /// 格式: timestamp(8) + random(14)
    /// </summary>
    public static string NewId()
    {
        // 时间戳部分（十六进制）
        var timePart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x8");
        
        // 随机部分（Base62编码以节省空间）
        var randomBytes = new byte[9];  // 72 bits → ~12 Base62 chars
        Random.NextBytes(randomBytes);
        var randomPart = ToBase62(randomBytes);

        return $"{timePart}{randomPart}";
    }

    /// <summary>
    /// 生成邀请码（6位数字+字母组合）
    /// </summary>
    public static string InviteCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";  // 排除易混淆字符
        var code = new char[6];
        
        lock (Random)
        {
            for (int i = 0; i < code.Length; i++)
            {
                code[i] = chars[Random.Next(chars.Length)];
            }
        }

        return new string(code);
    }

    /// <summary>
    /// 字节数组转 Base62 编码
    /// </summary>
    private static string ToBase62(byte[] data)
    {
        const string base62Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var result = new List<char>();

        // 将字节数组视为大整数
        BigInteger num = new BigInteger(data, isUnsigned: true);

        do
        {
            result.Add(base62Chars[(int)(num % 62)]);
            num /= 62;
        } while (num > 0);

        // 反转并填充至固定长度
        result.Reverse();
        
        while (result.Count < 14)
        {
            result.Insert(0, '0');
        }

        return new string(result.ToArray());
    }
}
