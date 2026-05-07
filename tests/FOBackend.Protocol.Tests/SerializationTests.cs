// ============================================================
// Protocol Layer Unit Tests
// 测试目标：
// 1. 消息序列化/反序列化正确性
// 2. 数据包组装/解析正确性
// 3. CRC校验准确性
// 4. 版本兼容性（前向兼容）
// ============================================================

using FluentAssertions;
using FOBackend.Infrastructure;
using FOBackend.Protocol;
using FOBackend.Protocol.Messages;
using Xunit;

namespace FOBackend.Protocol.Tests;

public class SerializationTests
{
    #region 基础类型序列化测试
    
    [Fact]
    public void Serialize_AuthenticateRequest_Roundtrip()
    {
        // Arrange
        var original = new AuthenticateRequest
        {
            Header = new RequestHeader { RequestId = 12345L },
            PlayerName = "TestPlayer",
            ClientVersion = "1.0.0-test"
        };

        // Act
        var bytes = ProtoSerializer.Serialize(original);
        var deserialized = ProtoSerializer.Deserialize<AuthenticateRequest>(bytes)!;

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Header!.RequestId.Should().Be(original.Header.RequestId);
        deserialized.PlayerName.Should().Be("TestPlayer");
        deserialized.ClientVersion.Should().Be("1.0.0-test");
    }

    [Fact]
    public void Serialize_PlayerInputReport_PreservesBinaryData()
    {
        // Arrange: 模拟客户端自定义的输入格式
        var inputData = new byte[] {
            0x01,           // 方向键掩码
            0x05,           // 动作键掩码
            0x7F, 0x00,     // 摇杆X (short, little-endian)
            0x00, 0x80,     // 摇杆Y (short, little-endian)
            0xAA, 0xBB, 0xCC  // 自定义扩展数据
        };

        var original = new PlayerInputReport
        {
            Header = new RequestHeader(),
            RoomId = "room-123",
            FrameNumber = 1000,
            InputData = inputData,
            InputChecksum = 12345
        };

        // Act
        var bytes = ProtoSerializer.Serialize(original);
        var deserialized = ProtoSerializer.Deserialize<PlayerInputReport>(bytes)!;

        // Assert: 二进制数据必须原样保留！这是帧同步的核心要求
        deserialized.InputData.Should().Equal(inputData);
        deserialized.FrameNumber.Should().Be(1000);
        deserialized.InputChecksum.Should().Be(12345);
    }

    [Fact]
    public void Serialize_FrameSyncPackage_WithMultipleInputs()
    {
        // Arrange
        var original = new FrameSyncPackage
        {
            FrameNumber = 5000,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Inputs = new List<FramePlayerInput>
            {
                new()
                {
                    PlayerId = "player-A",
                    FrameNumber = 5000,
                    InputData = new byte[] { 0x01, 0x02, 0x03 },
                    InputChecksum = 111
                },
                new()
                {
                    PlayerId = "player-B",
                    FrameNumber = 5000,
                    InputData = new byte[] { 0xAA, 0xBB, 0xCC },
                    InputChecksum = 222
                }
            },
            LatencyInfo = new LatencyInfo
            {
                MaxRttMs = 50,
                RecommendedBufferFrames = 3,
                IsLagging = false
            },
            SyncFlags = new SyncFlags
            {
                IsKeyFrame = true,
                ForceResync = false
            }
        };

        // Act
        var bytes = ProtoSerializer.Serialize(original);
        var deserialized = ProtoSerializer.Deserialize<FrameSyncPackage>(bytes)!;

        // Assert
        deserialized.FrameNumber.Should().Be(5000);
        deserialized.Inputs.Should().HaveCount(2);
        deserialized.Inputs[0].PlayerId.Should().Be("player-A");
        deserialized.Inputs[0].InputData.Should().Equal(new byte[] { 0x01, 0x02, 0x03 });
        deserialized.Inputs[1].PlayerId.Should().Be("player-B");
        deserialized.LatencyInfo!.MaxRttMs.Should().Be(50);
        deserialized.SyncFlags!.IsKeyFrame.Should().BeTrue();
    }

    #endregion

    #region 数据包构建与解析测试

    [Fact]
    public void PacketBuild_And_Parse_Roundtrip()
    {
        // Arrange
        var message = new HeartbeatRequest
        {
            Header = new RequestHeader { RequestId = 999 }
        };
        var payload = ProtoSerializer.Serialize(message);

        // Act
        var packet = PacketBuilder.Build(MessageId.HeartbeatRequest, payload);
        var success = PacketBuilder.TryParse(packet, out var parsedMsgId, out var parsedPayload);

        // Assert
        success.Should().BeTrue();
        parsedMsgId.Should().Be(MessageId.HeartbeatRequest);
        parsedPayload.Should().Equal(payload);
    }

    [Fact]
    public void PacketParse_RejectsTamperedData()
    {
        // Arrange: 构建正常数据包
        var packet = PacketBuilder.Build(MessageId.AuthRequest, new byte[] { 0x42 });
        
        // Act: 篡改中间的一个字节
        var tampered = packet.ToArray();
        tampered[5] ^= 0xFF;  // 翻转载荷第一个字节
        
        // Assert: 应该解析失败
        var success = PacketBuilder.TryParse(tampered, out _, out _);
        success.Should().BeFalse();
    }

    [Fact]
    public void PacketBuild_CorrectStructure()
    {
        // Arrange
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        
        // Act
        var packet = PacketBuilder.Build(MessageId.PlayerInputReport, payload);

        // Assert: 检查结构
        packet.Length.Should().Be(payload.Length + 10);  // 4(length) + 2(id) + N(payload) + 4(crc)
        
        // 长度字段（小端序）
        var length = BitConverter.ToInt32(packet, 0);
        length.Should().Be(payload.Length + 6);  // 2(id) + N(payload) + 4(crc)
        
        // 消息ID字段
        var msgId = (MessageId)BitConverter.ToUInt16(packet, 4);
        msgId.Should().Be(MessageId.PlayerInputReport);
    }

    #endregion

    #region 性能基准测试

    [Fact]
    public void Performance_Serialize_InputReport_Under_1ms()
    {
        // Arrange
        var input = new PlayerInputReport
        {
            Header = new RequestHeader(),
            RoomId = "perf-test-room",
            FrameNumber = 60000,  // 1秒@60FPS
            InputData = new byte[64],  // 最大推荐尺寸
            InputChecksum = 99999
        };

        // Act & Assert: 10000次序列化应在合理时间内完成
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 10000;
        
        for (int i = 0; i < iterations; i++)
        {
            ProtoSerializer.Serialize(input);
        }
        
        sw.Stop();
        
        // 平均每次应远小于1ms（protobuf-net很快）
        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine($"Average serialize time: {avgMs:F4}ms ({iterations} iterations)");
        avgMs.Should().BeLessThan(0.5);  // 宽松阈值
    }

    [Fact]
    public void Performance_BuildPacket_Under_1ms()
    {
        // Arrange
        var input = new byte[64];

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 10000;
        
        for (int i = 0; i < iterations; i++)
        {
            PacketBuilder.Build(MessageId.PlayerInputReport, input);
        }
        
        sw.Stop();
        
        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine($"Average packet build time: {avgMs:F4}ms");
        avgMs.Should().BeLessThan(0.5);
    }

    #endregion

    #region 边界情况测试

    [Fact]
    public void Serialize_EmptyPayload_WorksCorrectly()
    {
        // 空输入数据（玩家未操作时可能发送空输入）
        var input = new PlayerInputReport
        {
            FrameNumber = 1,
            InputData = Array.Empty<byte>()
        };
        
        var bytes = ProtoSerializer.Serialize(input);
        var result = ProtoSerializer.Deserialize<PlayerInputReport>(bytes)!;
        
        result.InputData.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Serialize_LargeInputData_UpTo256Bytes()
    {
        // 测试较大的输入数据
        var largeInput = new byte[256];
        Random.Shared.NextBytes(largeInput);

        var original = new PlayerInputReport
        {
            FrameNumber = 100,
            InputData = largeInput,
            InputChecksum = (int)Crc32.ComputeCrc16(largeInput)
        };

        var bytes = ProtoSerializer.Serialize(original);
        var result = ProtoSerializer.Deserialize<PlayerInputReport>(bytes)!;

        result.InputData.Should().HaveCount(256).And.Equal(largeInput);
    }

    [Fact]
    public void PacketParse_TooShort_FailsGracefully()
    {
        var tooShort = new byte[5];  // 小于最小10字节
        var success = PacketBuilder.TryParse(tooShort, out _, out _);
        success.Should().BeFalse();
    }

    #endregion
}

/// <summary>
/// CRC32 单元测试
/// </summary>
public class Crc32Tests
{
    [Theory]
    [InlineData(new byte[] { 0x00 }, 0xD202EF8D)]
    [InlineData(new byte[] { 0xFF }, 0xFF000000)]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 }, 0xB63CFBCD)]
    public void Compute_ReturnsExpectedValue(byte[] data, uint expectedCrc)
    {
        var result = Crc32.Compute(data);
        result.Should().Be(expectedCrc);
    }

    [Fact]
    public void Compute_SameInput_SameOutput()
    {
        var data = new byte[100];
        Random.Shared.NextBytes(data);
        
        var crc1 = Crc32.Compute(data);
        var crc2 = Crc32.Compute(data);
        
        crc1.Should().Be(crc2);
    }

    [Fact]
    public void Compute_DifferentInput_DifferentOutput()
    {
        var data1 = new byte[] { 0x01, 0x02, 0x03 };
        var data2 = new byte[] { 0x01, 0x02, 0x04 };  // 仅最后一位不同
        
        var crc1 = Crc32.Compute(data1);
        var crc2 = Crc32.Compute(data2);
        
        crc1.Should().NotBe(crc2);
    }
}
