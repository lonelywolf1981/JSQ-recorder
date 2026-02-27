using System;
using System.Collections.Generic;
using System.Text;
using JSQ.Core.Models;

namespace JSQ.Decode;

/// <summary>
/// Результат декодирования одного значения канала
/// </summary>
public readonly struct ChannelValue
{
    public int Index { get; }
    public double Value { get; }
    public DateTime Timestamp { get; }

    public ChannelValue(int index, double value, DateTime timestamp)
    {
        Index = index;
        Value = value;
        Timestamp = timestamp;
    }
}

/// <summary>
/// Декодер бинарного потока данных от передатчика.
///
/// Поддерживаемые форматы:
///
/// 1. Tagged (datiacquisiti) — реальный протокол передатчика:
///    [13 bytes: "datiacquisiti"][24 bytes: metadata][2 bytes: 0x00 0x0D]
///    [13 bytes: "datiacquisiti" (inner)][8 bytes: count-tag {00 01 00 01 00 00 00 86}]
///    [134 × 8 bytes: float64 BE — значения каналов в порядке протокола]
///    Полный размер блока от outer marker: 1132 байта.
///    Порядок каналов в пакете НЕ совпадает с реестровыми индексами.
///    Применяется таблица ProtocolPositionToRegistryIndex.
///
/// 2. Legacy (binary) — используется в unit-тестах:
///    [4 bytes BE: total_length][20 bytes: header][4 bytes BE: N]
///    [N × 8 bytes: float64 BE][4 bytes BE: trailer = total_length]
///    Индексы совпадают с позицией (маппинг не применяется).
///
/// Специальные значения: -99.0 означает "нет данных / канал отключён" → NaN.
/// </summary>
public class ChannelDecoder
{
    private const double NoDataSentinel = -99.0;
    private const double NoDataTolerance = 0.01;
    private const int MaxChannels = 256;
    private const int MinLegacyPacketSize = 32;

    // datiacquisiti tagged format
    private static readonly byte[] DatiMarker = Encoding.ASCII.GetBytes("datiacquisiti");
    private const int DatiBlockSize    = 1132; // 13+24+2+13+8+(134*8)
    private const int DatiCountOffset  = 56;   // uint32 BE channel count from block start
    private const int DatiValuesOffset = 60;   // float64 array from block start
    private const int DatiExpectedCount = 134;

    /// <summary>
    /// Таблица маппинга: протокольная позиция → реестровый индекс канала.
    /// Позиции в пакете НЕ соответствуют реестровым индексам начиная с pos 48.
    ///
    /// Источник: PROTOCOL_ANALYSIS.md / NomiCanaliDef (port 3363).
    ///   pos   0– 5  → reg   0– 5  (давления A-Pc/Pe, B-Pc/Pe, C-Pc/Pe)
    ///   pos   6–15  → reg   6–15  (Common: VEL, UR, mA1-5, Flux, UR-sie, T-sie)
    ///   pos  16–47  → reg  16–47  (Post A температуры: A-Tc, A-Te, A-T1..A-T30)
    ///   pos  48–79  → reg  54–85  (Post B температуры: B-Tc, B-Te, B-T1..B-T30)
    ///   pos  80–111 → reg 100–131 (Post C температуры: C-Tc, C-Te, C-T1..C-T30)
    ///   pos 112–117 → reg  48–53  (Post A электрика: A-I, A-F, A-V, A-W, A-PF, A-MaxI)
    ///   pos 118–123 → reg  86–91  (Post B электрика: B-I..B-MaxI)
    ///   pos 124–129 → reg 132–137 (Post C электрика: C-I..C-MaxI)
    ///   pos 130–133 → reg 146–149 (System: SetL01, SetL02, U01, U02)
    /// </summary>
    private static readonly int[] ProtocolPositionToRegistryIndex = BuildPositionMap();

    private static int[] BuildPositionMap()
    {
        var map = new int[134];
        int p = 0;
        for (int i = 0; i < 6;  i++) map[p++] = i;        // pos  0– 5 → reg   0– 5
        for (int i = 0; i < 10; i++) map[p++] = 6   + i;  // pos  6–15 → reg   6–15
        for (int i = 0; i < 32; i++) map[p++] = 16  + i;  // pos 16–47 → reg  16–47
        for (int i = 0; i < 32; i++) map[p++] = 54  + i;  // pos 48–79 → reg  54–85
        for (int i = 0; i < 32; i++) map[p++] = 100 + i;  // pos 80–111→ reg 100–131
        for (int i = 0; i < 6;  i++) map[p++] = 48  + i;  // pos 112–117→ reg 48–53
        for (int i = 0; i < 6;  i++) map[p++] = 86  + i;  // pos 118–123→ reg 86–91
        for (int i = 0; i < 6;  i++) map[p++] = 132 + i;  // pos 124–129→ reg 132–137
        for (int i = 0; i < 4;  i++) map[p++] = 146 + i;  // pos 130–133→ reg 146–149
        // p == 134
        return map;
    }

    /// <summary>
    /// Возвращает копию таблицы маппинга позиций протокола в реестровые индексы.
    /// </summary>
    public static int[] BuildProtocolChannelOrder() =>
        (int[])ProtocolPositionToRegistryIndex.Clone();

    private readonly List<byte> _buf = new List<byte>(16384);
    private readonly object _sync = new object();

    public void Reset()
    {
        lock (_sync)
        {
            _buf.Clear();
        }
    }

    /// <summary>
    /// Добавить байты из TCP-потока и вернуть декодированные значения каналов.
    /// Неполные пакеты хранятся во внутреннем буфере до следующего вызова.
    /// </summary>
    public IReadOnlyList<ChannelValue> Feed(byte[] data, int length)
    {
        lock (_sync)
        {
            if (data == null || length <= 0)
                return Array.Empty<ChannelValue>();

            for (int i = 0; i < length; i++)
                _buf.Add(data[i]);

            var result = new List<ChannelValue>();

            bool progress;
            do
            {
                progress = false;

                // ── 1. Tagged (datiacquisiti) format ─────────────────────────
                int markerPos = FindSequence(DatiMarker, 0);

                if (markerPos == 0)
                {
                    // Marker at buffer head
                    if (_buf.Count < DatiBlockSize)
                        break; // wait for rest of block

                    long channelCount = ReadUInt32BE(DatiCountOffset);
                    if (channelCount == DatiExpectedCount)
                    {
                        var now = JsqClock.Now;
                        for (int i = 0; i < DatiExpectedCount; i++)
                        {
                            double val = ReadFloat64BE(DatiValuesOffset + i * 8);
                            if (IsNoData(val)) val = double.NaN;
                            result.Add(new ChannelValue(ProtocolPositionToRegistryIndex[i], val, now));
                        }
                        _buf.RemoveRange(0, DatiBlockSize);
                        progress = true;
                    }
                    else
                    {
                        // Marker found but block malformed — advance past it
                        _buf.RemoveAt(0);
                        progress = true;
                    }
                }
                else if (markerPos > 0)
                {
                    // Skip leading non-tagged bytes (other protocol frames between blocks)
                    _buf.RemoveRange(0, markerPos);
                    progress = true;
                }
                else
                {
                    // ── 2. Legacy (binary) format — fallback for unit tests ───
                    if (_buf.Count < MinLegacyPacketSize)
                        break;

                    long totalLength = ReadUInt32BE(0);
                    if (totalLength < 28 || totalLength > 65536)
                    {
                        _buf.RemoveAt(0);
                        progress = true;
                        continue;
                    }

                    int fullSize = (int)(4 + totalLength);
                    if (_buf.Count < fullSize)
                        break;

                    long count = ReadUInt32BE(24);
                    long expectedTotal = 20 + 4 + count * 8 + 4;
                    if (count > MaxChannels || totalLength != expectedTotal)
                    {
                        _buf.RemoveAt(0);
                        progress = true;
                        continue;
                    }

                    // No position mapping for legacy — indices are sequential
                    var now = JsqClock.Now;
                    for (int i = 0; i < (int)count; i++)
                    {
                        double val = ReadFloat64BE(28 + i * 8);
                        if (IsNoData(val)) val = double.NaN;
                        result.Add(new ChannelValue(i, val, now));
                    }
                    _buf.RemoveRange(0, fullSize);
                    progress = true;
                }

            } while (progress && _buf.Count > 0);

            // Guard against buffer bloat on sustained desync
            if (_buf.Count > 16384)
                _buf.RemoveRange(0, _buf.Count - 512);

            return result;
        }
    }

    private int FindSequence(byte[] seq, int startIndex)
    {
        int limit = _buf.Count - seq.Length;
        for (int i = startIndex; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < seq.Length; j++)
            {
                if (_buf[i + j] != seq[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private long ReadUInt32BE(int offset)
    {
        return ((long)_buf[offset]     << 24) |
               ((long)_buf[offset + 1] << 16) |
               ((long)_buf[offset + 2] <<  8) |
               (long)_buf[offset + 3];
    }

    private double ReadFloat64BE(int offset)
    {
        byte[] bytes = new byte[8];
        for (int i = 0; i < 8; i++)
            bytes[7 - i] = _buf[offset + i];
        return BitConverter.ToDouble(bytes, 0);
    }

    private static bool IsNoData(double val) =>
        Math.Abs(val - NoDataSentinel) < NoDataTolerance;
}
