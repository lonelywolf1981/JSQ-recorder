using System;
using System.Linq;
using JSQ.Core.Models;
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
        // Используем JsqClock.Now (а не DateTime.Now) — декодер метит сэмплы через JsqClock.
        // DateTime.Now даёт LocalKind, JsqClock.Now — Unspecified (+5h); они не равны вне UTC+5.
        var before = JsqClock.Now;
        var pkt = MakePacket(1.0);
        var result = dec.Feed(pkt, pkt.Length);
        var after = JsqClock.Now;

        Assert.Single(result);
        Assert.True(result[0].Timestamp >= before && result[0].Timestamp <= after,
            $"Timestamp {result[0].Timestamp} выходит за пределы [{before}, {after}]");
    }

    [Fact]
    public void Feed_134Channels_AllDecodedCorrectly()
    {
        // Legacy format: 134 канала, индекс == позиция (маппинг не применяется)
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

    // ── Tagged (datiacquisiti) format tests ──────────────────────────────────

    /// <summary>Строит блок datiacquisiti с заданными значениями каналов (134 штуки).</summary>
    private static byte[] MakeTaggedBlock(double[] values)
    {
        if (values.Length != 134)
            throw new ArgumentException("Tagged block requires exactly 134 values");

        // Структура: 13+24+2+13+8+(134*8) = 1132 байта
        var block = new byte[1132];
        int pos = 0;

        // outer marker "datiacquisiti"
        var marker = System.Text.Encoding.ASCII.GetBytes("datiacquisiti");
        Array.Copy(marker, 0, block, pos, 13); pos += 13;

        // 24 bytes metadata (zeros)
        pos += 24;

        // 2 bytes: 0x00 0x0D
        block[pos++] = 0x00;
        block[pos++] = 0x0D;

        // inner marker "datiacquisiti"
        Array.Copy(marker, 0, block, pos, 13); pos += 13;

        // count-tag: {00 01 00 01 00 00 00 86} = 134 channels
        block[pos++] = 0x00; block[pos++] = 0x01;
        block[pos++] = 0x00; block[pos++] = 0x01;
        block[pos++] = 0x00; block[pos++] = 0x00;
        block[pos++] = 0x00; block[pos++] = 0x86; // 0x86 = 134

        // 134 × float64 BE
        for (int i = 0; i < 134; i++)
        {
            WriteF64BE(block, pos, values[i]);
            pos += 8;
        }

        return block; // pos == 1132
    }

    [Fact]
    public void Feed_TaggedBlock_Returns134Values()
    {
        var dec = new ChannelDecoder();
        var values = Enumerable.Range(0, 134).Select(i => (double)i + 1.0).ToArray();
        var block = MakeTaggedBlock(values);

        var result = dec.Feed(block, block.Length);

        Assert.Equal(134, result.Count);
    }

    [Fact]
    public void Feed_TaggedBlock_SentinelMapsToNaN()
    {
        var dec = new ChannelDecoder();
        var values = new double[134];
        values[0] = -99.0; // sentinel
        values[1] = 25.5;
        var block = MakeTaggedBlock(values);

        var result = dec.Feed(block, block.Length);

        Assert.Equal(134, result.Count);
        Assert.True(double.IsNaN(result[0].Value), "Sentinel -99 в tagged-блоке должен стать NaN");
        Assert.Equal(25.5, result[1].Value, 5);
    }

    [Fact]
    public void Feed_TaggedBlock_AppliesPositionToRegistryMapping()
    {
        // Проверяем ключевые точки маппинга из PROTOCOL_ANALYSIS.md:
        //   pos  0 → reg   0  (A-Pc, pressure — прямое совпадение)
        //   pos 16 → reg  16  (A-Tc — прямое совпадение)
        //   pos 48 → reg  54  (B-Tc — первый сдвиг!)
        //   pos 80 → reg 100  (C-Tc — второй сдвиг!)
        //   pos 112→ reg  48  (A-I  — инверсный сдвиг!)
        //   pos 130→ reg 146  (SYS-1)
        var dec = new ChannelDecoder();
        var values = new double[134];
        values[0]   = 1.1;  // pos 0  → reg 0
        values[16]  = 2.2;  // pos 16 → reg 16
        values[48]  = 3.3;  // pos 48 → reg 54
        values[80]  = 4.4;  // pos 80 → reg 100
        values[112] = 5.5;  // pos 112→ reg 48
        values[130] = 6.6;  // pos 130→ reg 146
        var block = MakeTaggedBlock(values);
        var result = dec.Feed(block, block.Length);

        Assert.Equal(134, result.Count);

        var byIndex = result.ToDictionary(v => v.Index, v => v.Value);

        Assert.Equal(1.1, byIndex[0],   5);
        Assert.Equal(2.2, byIndex[16],  5);
        Assert.Equal(3.3, byIndex[54],  5);
        Assert.Equal(4.4, byIndex[100], 5);
        Assert.Equal(5.5, byIndex[48],  5);
        Assert.Equal(6.6, byIndex[146], 5);
    }

    [Fact]
    public void Feed_TaggedBlock_PrecededByGarbage_SkipsToMarker()
    {
        var dec = new ChannelDecoder();
        var values = new double[134];
        values[0] = 42.0;
        var block = MakeTaggedBlock(values);

        // Добавляем 50 байт мусора перед блоком
        var garbage = new byte[50];
        var data = garbage.Concat(block).ToArray();
        var result = dec.Feed(data, data.Length);

        Assert.Equal(134, result.Count);
        Assert.Equal(42.0, result.First(v => v.Index == 0).Value, 5);
    }

    [Fact]
    public void Feed_TaggedBlock_Partial_WaitsForCompletion()
    {
        var dec = new ChannelDecoder();
        var values = Enumerable.Range(0, 134).Select(i => 1.0).ToArray();
        var block = MakeTaggedBlock(values);

        int half = block.Length / 2;
        var r1 = dec.Feed(block, half);
        Assert.Empty(r1); // неполный блок — ждём

        var rest = new byte[block.Length - half];
        Array.Copy(block, half, rest, 0, rest.Length);
        var r2 = dec.Feed(rest, rest.Length);
        Assert.Equal(134, r2.Count);
    }

    [Fact]
    public void BuildProtocolChannelOrder_Returns134Entries()
    {
        var order = ChannelDecoder.BuildProtocolChannelOrder();
        Assert.Equal(134, order.Length);
        // Все реестровые индексы уникальны (нет дублей)
        Assert.Equal(134, order.Distinct().Count());
    }
}
