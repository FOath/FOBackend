using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using FOBackend.Infrastructure;
using FOBackend.Protocol.Messages;
using ProtoBuf;

namespace FOBackend.Protocol;

/// <summary>
/// Protobuf-net 序列化扩展方法
/// 提供高性能的序列化/反序列化功能
/// </summary>
public static class ProtoSerializer
{
    /// <summary>
    /// 将消息对象序列化为字节数组
    /// </summary>
    public static byte[] Serialize<T>(T message) where T : class
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, message);
        return ms.ToArray();
    }

    /// <summary>
    /// 从字节数组反序列化消息对象
    /// </summary>
    public static T? Deserialize<T>(byte[] data) where T : class
    {
        if (data == null || data.Length == 0)
            return default;

        using var ms = new MemoryStream(data);
        return Serializer.Deserialize<T>(ms);
    }

    /// <summary>
    /// 序列化到内存池（减少GC压力）
    /// 返回租借的数组，使用后必须归还！
    /// </summary>
    public static byte[] SerializeToBuffer<T>(T message) where T : class
    {
        // 简化实现：实际生产环境可考虑使用 ArrayPool<byte>.Shared
        return Serialize(message);
    }

    /// <summary>
    /// 计算消息的预估大小（用于缓冲区预分配）
    /// </summary>
    public static long GetSize<T>(T message) where T : class
    {
        if (message == null)
            return 0;

        return Serializer.Measure(message).Length;
    }
}

/// <summary>
/// 数据包格式定义：
/// ┌──────────────┬──────────────┬──────────────┬──────────────┐
/// │ PacketLength │  MessageID   │   Payload    │    CRC32     │
/// │   (4 bytes)  │   (2 bytes)  │  (variable)  │   (4 bytes)  │
/// └──────────────┴──────────────┴──────────────┴──────────────┘
/// 
/// 总开销：10 bytes per packet（可接受）
/// </summary>
public static class PacketBuilder
{
    private const int HeaderSize = 6;       // Length(4) + MessageId(2)
    private const int TrailerSize = 4;      // CRC32
    private const int Overhead = HeaderSize + TrailerSize;  // 10 bytes

    // 包长度字段偏移量
    private const int LengthOffset = 0;
    private const int MessageIdOffset = 4;
    private const int PayloadOffset = 6;

    /// <summary>
    /// 构建完整的数据包（含头、载荷、校验）
    /// </summary>
    public static byte[] Build(MessageId messageId, byte[] payload)
    {
        var packetLength = Overhead + payload.Length;
        var packet = new byte[packetLength];

        // 写入包长度（不包含自身4字节）
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(LengthOffset), payload.Length + TrailerSize + 2);

        // 写入消息ID
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(MessageIdOffset), (ushort)messageId);

        // 写入载荷数据
        Array.Copy(payload, 0, packet, PayloadOffset, payload.Length);

        // 写入CRC32校验
        var crc = Crc32.Compute(packet.AsSpan(0, packetLength - TrailerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(packetLength - TrailerSize), crc);

        return packet;
    }

    /// <summary>
    /// 构建完整数据包（自动序列化消息对象）
    /// </summary>
    public static byte[] Build<T>(MessageId messageId, T message) where T : class
    {
        var payload = ProtoSerializer.Serialize(message);
        return Build(messageId, payload);
    }

    /// <summary>
    /// 解析数据包，提取消息ID和载荷
    /// 返回null表示解析失败（如CRC错误、长度不足等）
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out MessageId messageId, out byte[] payload)
    {
        messageId = default;
        payload = Array.Empty<byte>();

        // 最小长度检查：至少要有头部+空载荷+CRC = 10字节
        if (packet.Length < Overhead)
            return false;

        // 读取包长度（不含Length字段自身的4字节）
        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(LengthOffset));
        
        // 验证实际长度是否匹配声明长度 + 4（Length字段本身）
        if (packet.Length != declaredLength + 4)
        {
            // 允许接收更多数据（粘包情况），但至少要满足声明的最小长度
            if (packet.Length < declaredLength + 4)
                return false;
        }

        // CRC32 校验
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Slice(packet.Length - TrailerSize));
        var actualCrc = Crc32.Compute(packet.Slice(0, packet.Length - TrailerSize));

        if (expectedCrc != actualCrc)
        {
            // CRC校验失败
            return false;
        }

        // 提取消息ID
        messageId = (MessageId)BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(MessageIdOffset));

        // 提取载荷
        var payloadLength = packet.Length - Overhead;
        payload = new byte[payloadLength];
        packet.Slice(PayloadOffset, payloadLength).CopyTo(payload);

        return true;
    }
}
