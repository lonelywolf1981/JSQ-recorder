using System;
using System.Collections.Generic;

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
/// Формат пакета:
///   [4 bytes BE: total_length]
///   [20 bytes: header (zeros)]
///   [4 bytes BE: N — количество значений]
///   [N × 8 bytes: float64 BE — значения каналов 0..N-1]
///   [4 bytes BE: trailer = total_length]
///
/// Специальные значения (из протокола): -99.0 означает "нет данных / канал отключён".
/// </summary>
public class ChannelDecoder
{
    // "Нет данных" маркер из протокола эмулятора
    private const double NoDataSentinel = -99.0;
    private const double NoDataTolerance = 0.01;

    // Максимально допустимое число каналов в пакете
    private const int MaxChannels = 256;

    // Минимальный размер пакета при N=0: 4 (length) + 20 (header) + 4 (count) + 4 (trailer) = 32
    private const int MinPacketSize = 32;

    private readonly List<byte> _buf = new List<byte>(4096);
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

            while (_buf.Count >= MinPacketSize)
            {
                // Читаем total_length из первых 4 байт (big-endian uint32)
                long totalLength = ReadUInt32BE(0);

                // Санитарная проверка: разумный диапазон размера пакета
                if (totalLength < 28 || totalLength > 65536)
                {
                    // Рассинхронизация — пропускаем 1 байт и ищем следующий валидный заголовок
                    _buf.RemoveAt(0);
                    continue;
                }

                // Полный пакет в буфере = 4 (поле length) + totalLength байт
                int fullSize = (int)(4 + totalLength);
                if (_buf.Count < fullSize)
                    break; // Неполный пакет — ждём следующих данных

                // Структура пакета (смещения от начала буфера):
                //   0..3:   total_length
                //   4..23:  header (20 байт, не используем)
                //   24..27: N (количество каналов)
                //   28..28+N*8-1: значения float64 BE
                //   28+N*8..28+N*8+3: trailer = total_length
                long count = ReadUInt32BE(24);

                // Проверяем корректность размера: total_length = 20 + 4 + N*8 + 4
                long expectedTotal = 20 + 4 + count * 8 + 4;
                if (count > MaxChannels || totalLength != expectedTotal)
                {
                    _buf.RemoveAt(0);
                    continue;
                }

                // Декодируем значения каналов
                var now = DateTime.Now;
                for (int i = 0; i < (int)count; i++)
                {
                    int offset = 28 + i * 8;
                    double val = ReadFloat64BE(offset);

                    if (IsNoData(val))
                        val = double.NaN;

                    result.Add(new ChannelValue(i, val, now));
                }

                // Удаляем обработанный пакет из буфера
                _buf.RemoveRange(0, fullSize);
            }

            // Защита от разрастания буфера при полном нарушении синхронизации
            if (_buf.Count > 8192)
                _buf.RemoveRange(0, _buf.Count - 512);

            return result;
        }
    }

    // Читает беззнаковое 32-битное целое в big-endian
    private long ReadUInt32BE(int offset)
    {
        return ((long)_buf[offset] << 24) |
               ((long)_buf[offset + 1] << 16) |
               ((long)_buf[offset + 2] << 8) |
               (long)_buf[offset + 3];
    }

    // Читает double (float64) в big-endian
    private double ReadFloat64BE(int offset)
    {
        // BitConverter ожидает little-endian на Windows — переворачиваем байты
        byte[] bytes = new byte[8];
        for (int i = 0; i < 8; i++)
            bytes[7 - i] = _buf[offset + i];
        return BitConverter.ToDouble(bytes, 0);
    }

    private static bool IsNoData(double val) =>
        Math.Abs(val - NoDataSentinel) < NoDataTolerance;
}
