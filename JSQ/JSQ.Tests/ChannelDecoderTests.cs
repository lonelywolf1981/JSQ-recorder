using System;
using System.Linq;
using JSQ.Decode;
using Xunit;

namespace JSQ.Tests;

/// <summary>
/// Тесты декодера бинарного протокола.
///
/// Формат пакета:
///   [4 bytes BE: total_length]      total_length = 20 + 4 + N*8 + 4 = 28 + N*8
///   [20 bytes: header (zeros)]
///   [4 bytes BE: N — количество каналов]
///   [N × 8 bytes: float64 BE — значения каналов 0..N-1]
///   [4 bytes BE: trailer = total_length]
/// </summary>
public class ChannelDecoderTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Строит корректный бинарный пакет с заданными значениями каналов.</summary>
    private static byte[] MakePacket(params double[] values)
    {
        int n = values.Length;
        uint totalLength = (uint)(20 + 4 + n * 8 + 4);
        byte[] packet = new byte[4 + totalLength];

        WriteU32BE(packet, 0, totalLength);
        // bytes 4..23: header (zeros)
        WriteU32BE(packet, 24, (uint)n);
        for (int i = 0; i < n; i++)
            WriteF64BE(packet, 28 + i * 8, values[i]);
        WriteU32BE(packet, 28 + n * 8, totalLength); // trailer

        return packet;
    }

    private static void WriteU32BE(byte[] buf, int off, uint v)
    {
        buf[off]     = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >>  8);
        buf[off + 3] = (byte)v;
    }

    private static void WriteF64BE(byte[] buf, int off, double v)
    {
        var le = BitConverter.GetBytes(v);   // little-endian on Windows
        for (int i = 0; i < 8; i++)
            buf[off + i] = le[7 - i];        // → big-endian
    }

    // ── Тесты ────────────────────────────────────────────────────────────────

    [Fact]
    public void Feed_EmptyData_ReturnsEmpty()
    {
        var dec = new ChannelDecoder();
        var result = dec.Feed(new byte[0], 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Feed_NullData_ReturnsEmpty()
    {
        var dec = new ChannelDecoder();
        var result = dec.Feed(null!, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Feed_SinglePacket_TwoChannels_ReturnsCorrectValues()
    {
        var dec = new ChannelDecoder();
        var pkt = MakePacket(12.5, 99.0);
        var result = dec.Feed(pkt, pkt.Length);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Index);
        Assert.Equal(1, result[1].Index);
        Assert.Equal(12.5,  result[0].Value, 6);
        Assert.Equal(99.0,  result[1].Value, 6);
    }

    [Fact]
    public void Feed_SentinelValue_MapsToNaN()
    {
        var dec = new ChannelDecoder();
        var pkt = MakePacket(-99.0, 10.0);
        var result = dec.Feed(pkt, pkt.Length);

        Assert.Equal(2, result.Count);
        Assert.True(double.IsNaN(result[0].Value), "Sentinel -99 должен стать NaN");
        Assert.Equal(10.0, result[1].Value, 6);
    }

    [Fact]
    public void Feed_PartialPacket_ReturnsEmpty_ThenResultOnCompletion()
    {
        var dec = new ChannelDecoder();
        var pkt = MakePacket(1.0, 2.0, 3.0);

        // Первая половина — неполный пакет, результата нет
        int half = pkt.Length / 2;
        var r1 = dec.Feed(pkt, half);
        Assert.Empty(r1);

        // Вторая половина — пакет завершён
        var remaining = new byte[pkt.Length - half];
        Array.Copy(pkt, half, remaining, 0, remaining.Length);
        var r2 = dec.Feed(remaining, remaining.Length);

        Assert.Equal(3, r2.Count);
        Assert.Equal(1.0, r2[0].Value, 6);
        Assert.Equal(2.0, r2[1].Value, 6);
        Assert.Equal(3.0, r2[2].Value, 6);
    }

    [Fact]
    public void Feed_TwoPacketsConcatenated_ReturnsBothSets()
    {
        var dec = new ChannelDecoder();
        var p1 = MakePacket(10.0);
        var p2 = MakePacket(20.0, 30.0);

        var combined = p1.Concat(p2).ToArray();
        var result = dec.Feed(combined, combined.Length);

        Assert.Equal(3, result.Count);
        Assert.Equal(0, result[0].Index); Assert.Equal(10.0, result[0].Value, 6);
        Assert.Equal(0, result[1].Index); Assert.Equal(20.0, result[1].Value, 6);
        Assert.Equal(1, result[2].Index); Assert.Equal(30.0, result[2].Value, 6);
    }

    [Fact]
    public void Feed_InvalidHeader_SkipsAndFindsNextPacket()
    {
        var dec = new ChannelDecoder();

        // Один мусорный байт перед корректным пакетом
        var good = MakePacket(42.0);
        var corrupted = new byte[1 + good.Length];
        corrupted[0] = 0xFF; // мусор
        Array.Copy(good, 0, corrupted, 1, good.Length);

        var result = dec.Feed(corrupted, corrupted.Length);

        // Декодер должен пропустить мусорный байт и найти корректный пакет
        Assert.Single(result);
        Assert.Equal(42.0, result[0].Value, 6);
    }

    [Fact]
    public void Feed_ZeroChannels_ReturnsEmptyResultAndConsumesPacket()
    {
        // N=0 — пустой пакет. Проверяем что декодер его принимает без зависания.
        var dec = new ChannelDecoder();
        var pkt = MakePacket(); // N=0
        var r = dec.Feed(pkt, pkt.Length);
        Assert.Empty(r);

        // Следующий нормальный пакет должен декодироваться
        var good = MakePacket(5.5);
        var r2 = dec.Feed(good, good.Length);
        Assert.Single(r2);
        Assert.Equal(5.5, r2[0].Value, 6);
    }

    [Fact]
    public void Feed_TimestampsAreReasonable()
    {
        var dec = new ChannelDecoder();
        var before = DateTime.Now;
        var pkt = MakePacket(1.0);
        var result = dec.Feed(pkt, pkt.Length);
        var after = DateTime.Now;

        Assert.Single(result);
        Assert.True(result[0].Timestamp >= before && result[0].Timestamp <= after,
            $"Timestamp {result[0].Timestamp} выходит за пределы [{before}, {after}]");
    }

    [Fact]
    public void Feed_134Channels_AllDecodedCorrectly()
    {
        // Реальный сценарий: 134 канала в одном пакете
        var dec = new ChannelDecoder();
        var values = Enumerable.Range(0, 134).Select(i => (double)i * 0.1).ToArray();
        var pkt = MakePacket(values);
        var result = dec.Feed(pkt, pkt.Length);

        Assert.Equal(134, result.Count);
        for (int i = 0; i < 134; i++)
        {
            Assert.Equal(i, result[i].Index);
            Assert.Equal(i * 0.1, result[i].Value, 5);
        }
    }
}
