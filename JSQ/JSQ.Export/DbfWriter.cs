using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JSQ.Export;

internal static class DbfWriter
{
    public static void Write(
        string path,
        IReadOnlyList<DbfField> fields,
        IReadOnlyList<ExportSampleRow> rows,
        IReadOnlyList<LegacyChannel> channels,
        DateTime fileDate)
    {
        var headerLength = (short)(32 + (fields.Count * 32) + 1);
        var recordLength = (short)(1 + fields.Sum(f => f.Length));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        WriteHeader(writer, rows.Count, headerLength, recordLength, fileDate);
        foreach (var field in fields)
        {
            WriteFieldDescriptor(writer, field);
        }

        writer.Write((byte)0x0D);

        foreach (var row in rows)
        {
            WriteRecord(writer, fields, row, channels);
        }

        writer.Write((byte)0x1A);
    }

    private static void WriteHeader(BinaryWriter writer, int recordCount, short headerLength, short recordLength, DateTime fileDate)
    {
        writer.Write((byte)0x03); // dBase III
        writer.Write((byte)(fileDate.Year - 1900));
        writer.Write((byte)fileDate.Month);
        writer.Write((byte)fileDate.Day);
        writer.Write(recordCount);
        writer.Write(headerLength);
        writer.Write(recordLength);

        for (var i = 0; i < 20; i++)
        {
            writer.Write((byte)0x00);
        }
    }

    private static void WriteFieldDescriptor(BinaryWriter writer, DbfField field)
    {
        var fieldNameBytes = Encoding.ASCII.GetBytes(TrimFieldName(field.Name));
        var nameBuffer = new byte[11];
        Array.Copy(fieldNameBytes, nameBuffer, Math.Min(fieldNameBytes.Length, nameBuffer.Length));

        writer.Write(nameBuffer);
        writer.Write((byte)field.Type);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write(field.Length);
        writer.Write(field.DecimalCount);

        for (var i = 0; i < 14; i++)
        {
            writer.Write((byte)0x00);
        }
    }

    private static void WriteRecord(
        BinaryWriter writer,
        IReadOnlyList<DbfField> fields,
        ExportSampleRow row,
        IReadOnlyList<LegacyChannel> channels)
    {
        writer.Write((byte)0x20); // active record

        foreach (var field in fields)
        {
            string raw;

            switch (field.Name)
            {
                case "Data":
                    raw = row.Timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    break;
                case "Ore":
                    raw = FormatInteger(row.Timestamp.Hour, field.Length);
                    break;
                case "Minuti":
                    raw = FormatInteger(row.Timestamp.Minute, field.Length);
                    break;
                case "Secondi":
                    raw = FormatInteger(row.Timestamp.Second, field.Length);
                    break;
                case "mSecondi":
                    raw = FormatInteger(row.Timestamp.Millisecond, field.Length);
                    break;
                default:
                    raw = FormatChannelValue(field, row, channels);
                    break;
            }

            var bytes = Encoding.ASCII.GetBytes(raw);
            if (bytes.Length != field.Length)
            {
                throw new InvalidOperationException($"DBF field '{field.Name}' expected length {field.Length}, got {bytes.Length}.");
            }

            writer.Write(bytes);
        }
    }

    private static string FormatChannelValue(DbfField field, ExportSampleRow row, IReadOnlyList<LegacyChannel> channels)
    {
        var channel = channels.First(c => c.Name == field.Name);
        if (!row.ValuesByChannel.TryGetValue(channel.Index, out var value))
        {
            value = -99;
        }

        return FormatNumeric(value, field.Length, field.DecimalCount);
    }

    private static string FormatInteger(int value, byte width)
    {
        return value.ToString(CultureInfo.InvariantCulture).PadLeft(width, ' ');
    }

    private static string FormatNumeric(double value, byte width, byte decimals)
    {
        var format = decimals > 0
            ? $"F{decimals}"
            : "F0";

        var text = value.ToString(format, CultureInfo.InvariantCulture);
        if (text.Length > width)
        {
            return new string('*', width);
        }

        return text.PadLeft(width, ' ');
    }

    private static string TrimFieldName(string name)
    {
        if (name.Length <= 11)
        {
            return name;
        }

        return name.Substring(0, 11);
    }
}
