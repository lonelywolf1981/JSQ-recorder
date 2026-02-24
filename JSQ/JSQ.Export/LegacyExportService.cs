using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JSQ.Core.Models;
using JSQ.Storage;

namespace JSQ.Export;

public class LegacyExportService : ILegacyExportService
{
    private static readonly string[] PreferredOrder =
    {
        "C-Pc", "C-Pe", "UR-sie", "T-sie",
        "C-Tc", "C-Te",
        "C-T1", "C-T2", "C-T3", "C-T4", "C-T5", "C-T6", "C-T7", "C-T8", "C-T9", "C-T10",
        "C-T11", "C-T12", "C-T13", "C-T14", "C-T15", "C-T16", "C-T17", "C-T18", "C-T19", "C-T20",
        "C-T21", "C-T22", "C-T23", "C-T24", "C-T25", "C-T26", "C-T27", "C-T28",
        "C-I", "C-F", "C-V", "C-W",
        "B-Tc", "B-Te", "B-T1", "B-T2", "B-T3", "B-T4", "B-T5", "B-T6", "B-T7", "B-T8"
    };

    private readonly IDatabaseService _dbService;

    public LegacyExportService(IDatabaseService dbService)
    {
        _dbService = dbService;
    }

    public async Task<LegacyExportResult> ExportExperimentAsync(
        string experimentId,
        string outputRoot,
        string? packageName = null,
        string? innerFileBaseName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(experimentId))
        {
            throw new ArgumentException("Experiment ID is required.", nameof(experimentId));
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = "export";
        }

        using var conn = _dbService.GetConnection();

        var meta = await conn.QuerySingleOrDefaultAsync<ExperimentMeta>(
            "SELECT name AS Name, part_number AS PartNumber, operator AS Operator, refrigerant AS Refrigerant FROM experiments WHERE id = @Id;",
            new { Id = experimentId });

        if (meta == null)
        {
            throw new InvalidOperationException($"Эксперимент '{experimentId}' не найден.");
        }

        var sampleRows = (await conn.QueryAsync<RawSampleRow>(
            @"SELECT timestamp AS Timestamp,
                     channel_index AS ChannelIndex,
                     COALESCE(value_avg, value_max, value_min) AS Value,
                     CASE
                        WHEN sample_count > 0 AND COALESCE(value_avg, value_max, value_min) IS NOT NULL THEN 1
                        ELSE 0
                     END AS IsValid
              FROM agg_samples_20s
              WHERE experiment_id = @ExperimentId
              ORDER BY timestamp ASC, channel_index ASC;",
            new { ExperimentId = experimentId })).ToList();

        if (sampleRows.Count == 0)
        {
            throw new InvalidOperationException("Для выбранного эксперимента нет агрегированных данных для экспорта.");
        }

        var resolvedPackageName = string.IsNullOrWhiteSpace(packageName)
            ? BuildNextPackageName(outputRoot)
            : packageName!;

        var resolvedInnerBaseName = string.IsNullOrWhiteSpace(innerFileBaseName)
            ? resolvedPackageName
            : innerFileBaseName!;

        return CreatePackage(outputRoot, resolvedPackageName, resolvedInnerBaseName, meta, sampleRows);
    }

    public LegacyExportResult ExportFromData(
        string outputRoot,
        string packageName,
        string modelType,
        string partNumber,
        string operatorName,
        string refrigerant,
        IReadOnlyList<LegacyExportSample> samples)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("Package name is required.", nameof(packageName));
        }

        if (samples == null || samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        var meta = new ExperimentMeta
        {
            Name = modelType,
            PartNumber = partNumber,
            Operator = operatorName,
            Refrigerant = refrigerant
        };

        var rawRows = samples
            .OrderBy(s => s.Timestamp)
            .ThenBy(s => s.ChannelIndex)
            .Select(s => new RawSampleRow
            {
                Timestamp = s.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                ChannelIndex = s.ChannelIndex,
                Value = s.Value,
                IsValid = s.IsValid ? 1 : 0
            })
            .ToList();

        return CreatePackage(outputRoot, packageName, packageName, meta, rawRows);
    }

    private static LegacyExportResult CreatePackage(
        string outputRoot,
        string packageName,
        string innerFileBaseName,
        ExperimentMeta meta,
        List<RawSampleRow> sampleRows)
    {
        var channels = BuildChannelList(sampleRows);
        var records = BuildRecords(sampleRows);

        var finalPackageDir = Path.Combine(outputRoot, packageName);
        var tmpPackageDir = Path.Combine(outputRoot, $".tmp_{packageName}_{Guid.NewGuid():N}");
        var tmpSetDir = Path.Combine(tmpPackageDir, "Set");

        Directory.CreateDirectory(tmpSetDir);

        var dbfFileName = innerFileBaseName + ".dbf";
        var datFileName = innerFileBaseName + ".dat";
        var iniFileName = innerFileBaseName + ".ini";

        var tmpDbfPath = Path.Combine(tmpPackageDir, dbfFileName);
        var tmpDatPath = Path.Combine(tmpPackageDir, datFileName);
        var tmpIniPath = Path.Combine(tmpPackageDir, iniFileName);
        var tmpDefPath = Path.Combine(tmpSetDir, "Canali.def");
        var tmpCalPath = Path.Combine(tmpSetDir, "Canali.cal");

        WriteDbf(tmpDbfPath, channels, records);
        WriteDat(tmpDatPath, meta);
        WriteIni(tmpIniPath);
        WriteDef(tmpDefPath, channels);
        WriteCal(tmpCalPath, channels);

        Directory.CreateDirectory(outputRoot);
        if (Directory.Exists(finalPackageDir))
        {
            throw new IOException($"Папка экспорта уже существует: {finalPackageDir}");
        }

        Directory.Move(tmpPackageDir, finalPackageDir);

        return new LegacyExportResult
        {
            PackageName = packageName,
            PackageDirectory = finalPackageDir,
            DbfPath = Path.Combine(finalPackageDir, dbfFileName),
            DatPath = Path.Combine(finalPackageDir, datFileName),
            IniPath = Path.Combine(finalPackageDir, iniFileName),
            DefPath = Path.Combine(finalPackageDir, "Set", "Canali.def"),
            CalPath = Path.Combine(finalPackageDir, "Set", "Canali.cal"),
            RecordCount = records.Count,
            ChannelCount = channels.Count
        };
    }

    private static List<LegacyChannel> BuildChannelList(List<RawSampleRow> sampleRows)
    {
        var firstSeen = new Dictionary<int, int>();
        for (var i = 0; i < sampleRows.Count; i++)
        {
            var channel = sampleRows[i].ChannelIndex;
            if (!firstSeen.ContainsKey(channel))
            {
                firstSeen[channel] = i;
            }
        }

        var channels = firstSeen.Keys
            .Select(idx =>
            {
                var def = ChannelRegistry.GetByIndex(idx);
                var name = def?.Name ?? $"v{idx:D3}";

                var format = ResolveDbfNumericFormat(name, def?.Type);
                return new LegacyChannel
                {
                    Index = idx,
                    Name = name,
                    Unit = def?.Unit ?? string.Empty,
                    Description = def?.Description ?? name,
                    DbfLength = format.length,
                    DbfDecimals = format.decimals
                };
            })
            .ToList();

        channels = channels
            .OrderBy(c => PreferredIndex(c.Name))
            .ThenBy(c => firstSeen[c.Index])
            .ToList();

        return channels;
    }

    private static List<ExportSampleRow> BuildRecords(List<RawSampleRow> sampleRows)
    {
        var recordsByTimestamp = new Dictionary<string, ExportSampleRow>(StringComparer.Ordinal);

        foreach (var sample in sampleRows)
        {
            if (!recordsByTimestamp.TryGetValue(sample.Timestamp, out var row))
            {
                row = new ExportSampleRow
                {
                    TimestampKey = sample.Timestamp,
                    Timestamp = ParseTimestamp(sample.Timestamp)
                };
                recordsByTimestamp[sample.Timestamp] = row;
            }

            row.ValuesByChannel[sample.ChannelIndex] = sample.IsValid == 1 ? sample.Value : -99;
        }

        return recordsByTimestamp.Values
            .OrderBy(r => r.Timestamp)
            .ThenBy(r => r.TimestampKey, StringComparer.Ordinal)
            .ToList();
    }

    private static DateTime ParseTimestamp(string text)
    {
        if (DateTime.TryParse(text, null, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTime.Parse(text, CultureInfo.InvariantCulture);
    }

    private static int PreferredIndex(string channelName)
    {
        var idx = Array.IndexOf(PreferredOrder, channelName);
        return idx >= 0 ? idx : int.MaxValue;
    }

    private static (byte length, byte decimals) ResolveDbfNumericFormat(string channelName, ChannelType? type)
    {
        if (channelName.EndsWith("-Pc", StringComparison.OrdinalIgnoreCase) ||
            channelName.EndsWith("-Pe", StringComparison.OrdinalIgnoreCase) ||
            type == ChannelType.Pressure)
        {
            return (9, 4);
        }

        if (channelName.EndsWith("-I", StringComparison.OrdinalIgnoreCase))
        {
            return (8, 4);
        }

        if (channelName.EndsWith("-W", StringComparison.OrdinalIgnoreCase))
        {
            return (9, 2);
        }

        return (8, 3);
    }

    private static string BuildNextPackageName(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);

        var max = 0;
        foreach (var dir in Directory.GetDirectories(outputRoot, "Prova*"))
        {
            var name = Path.GetFileName(dir);
            if (TryParseProvaNumber(name, out var n) && n > max)
            {
                max = n;
            }
        }

        return $"Prova{(max + 1):D3}";
    }

    private static bool TryParseProvaNumber(string? fileOrDirName, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(fileOrDirName))
        {
            return false;
        }

        var name = fileOrDirName!;

        if (!name.StartsWith("Prova", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var numeric = new string(name.Skip(5).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static void WriteDbf(string path, IReadOnlyList<LegacyChannel> channels, IReadOnlyList<ExportSampleRow> rows)
    {
        var fields = new List<DbfField>
        {
            new() { Name = "Data", Type = 'D', Length = 8, DecimalCount = 0 },
            new() { Name = "Ore", Type = 'N', Length = 2, DecimalCount = 0 },
            new() { Name = "Minuti", Type = 'N', Length = 2, DecimalCount = 0 },
            new() { Name = "Secondi", Type = 'N', Length = 2, DecimalCount = 0 },
            new() { Name = "mSecondi", Type = 'N', Length = 3, DecimalCount = 0 }
        };

        foreach (var channel in channels)
        {
            fields.Add(new DbfField
            {
                Name = channel.Name,
                Type = 'N',
                Length = channel.DbfLength,
                DecimalCount = channel.DbfDecimals
            });
        }

        DbfWriter.Write(path, fields, rows, channels, JsqClock.Now);
    }

    private static void WriteDat(string path, ExperimentMeta meta)
    {
        var lines = new[]
        {
            $"MODEL/TYPE;{EscapeDat(meta.Name)}",
            $"PART NUMBER;{EscapeDat(meta.PartNumber)}",
            $"USER;{EscapeDat(meta.Operator)}",
            $"REFRIGERANT;{EscapeDat(meta.Refrigerant)}",
            "UNIT;C",
            $"NOTE1;{EscapeDat(meta.Name)}",
            "NOTE2;",
            "NOTE3;",
            "NOTE4;",
            "NOTE5;"
        };

        var content = string.Join("\r\n", lines) + "\r\n";
        File.WriteAllText(path, content, Encoding.GetEncoding(1252));
    }

    private static string EscapeDat(string? text)
    {
        return (text ?? string.Empty).Replace(";", ",").Trim();
    }

    private static void WriteIni(string path)
    {
        var lines = new[]
        {
            "[Medie]",
            "nMedie=2",
            "Media1=Ta;Fresh food compartement average;°C;3;1;255;0;0;0;5;0",
            "Media2=Tca;Cellar compartement average;°C;3;1;255;0;0;0;5;0",
            "[Media1]",
            "Abilitato=TRUE",
            "nCanali=2",
            "Canale1=C-T2",
            "Canale2=C-SC",
            "[Media2]",
            "Abilitato=TRUE",
            "nCanali=3",
            "Canale1=B-T10",
            "Canale2=B-T11",
            "Canale3=B-T9"
        };

        var content = string.Join("\r\n", lines) + "\r\n";
        File.WriteAllText(path, content, Encoding.GetEncoding(1252));
    }

    private static void WriteDef(string path, IReadOnlyList<LegacyChannel> channels)
    {
        var lines = new List<string> { channels.Count.ToString(CultureInfo.InvariantCulture) };

        foreach (var channel in channels)
        {
            var profile = ResolveDefProfile(channel);
            lines.Add(string.Join(";", new[]
            {
                channel.Name,
                channel.Description,
                NormalizeUnit(channel.Unit),
                profile.precision.ToString(CultureInfo.InvariantCulture),
                profile.scale.ToString(CultureInfo.InvariantCulture),
                profile.min.ToString(CultureInfo.InvariantCulture),
                profile.offset1.ToString(CultureInfo.InvariantCulture),
                profile.offset2.ToString(CultureInfo.InvariantCulture),
                profile.offset3.ToString(CultureInfo.InvariantCulture),
                profile.divisor.ToString(CultureInfo.InvariantCulture),
                "0"
            }));
        }

        var content = string.Join("\r\n", lines) + "\r\n";
        File.WriteAllText(path, content, Encoding.GetEncoding(1252));
    }

    private static (int precision, int scale, int min, int offset1, int offset2, int offset3, int divisor) ResolveDefProfile(LegacyChannel channel)
    {
        if (channel.Name.EndsWith("-Pc", StringComparison.OrdinalIgnoreCase) ||
            channel.Name.EndsWith("-Pe", StringComparison.OrdinalIgnoreCase))
        {
            return (3, 2, 128, 0, 0, 0, 5);
        }

        if (channel.Name.Equals("UR-sie", StringComparison.OrdinalIgnoreCase))
        {
            return (3, 1, 255, 0, 255, 0, 5);
        }

        if (channel.Name.EndsWith("-I", StringComparison.OrdinalIgnoreCase))
        {
            return (2, 2, 128, 0, 255, 0, 5);
        }

        if (channel.Name.EndsWith("-F", StringComparison.OrdinalIgnoreCase) ||
            channel.Name.EndsWith("-V", StringComparison.OrdinalIgnoreCase))
        {
            return (3, 1, 128, 0, 255, 0, 5);
        }

        if (channel.Name.EndsWith("-W", StringComparison.OrdinalIgnoreCase))
        {
            return (5, 0, 128, 0, 255, 0, 5);
        }

        return (3, 1, 255, 0, 0, 0, 5);
    }

    private static void WriteCal(string path, IReadOnlyList<LegacyChannel> channels)
    {
        var lines = new List<string>(channels.Count);
        foreach (var channel in channels)
        {
            var (min, max) = ResolveCalibrationRange(channel.Name);
            lines.Add($"{channel.Name};;{FormatScientific(min)};{FormatScientific(max)};{FormatScientific(0)};{FormatScientific(0)};;");
        }

        var content = string.Join("\r\n", lines) + "\r\n";
        File.WriteAllText(path, content, Encoding.GetEncoding(1252));
    }

    private static string FormatScientific(double value)
    {
        return value.ToString("0.0000000000E+0", CultureInfo.InvariantCulture);
    }

    private static (double min, double max) ResolveCalibrationRange(string channelName)
    {
        if (channelName.EndsWith("-Pc", StringComparison.OrdinalIgnoreCase))
        {
            return (-10, 2500);
        }

        if (channelName.EndsWith("-Pe", StringComparison.OrdinalIgnoreCase))
        {
            return (-4, 1000);
        }

        if (channelName.Equals("Flux", StringComparison.OrdinalIgnoreCase))
        {
            return (-225, 56250);
        }

        if (channelName.Equals("T-sie", StringComparison.OrdinalIgnoreCase))
        {
            return (-67.5, 6875);
        }

        if (channelName.Equals("VEL", StringComparison.OrdinalIgnoreCase) ||
            channelName.Equals("UR", StringComparison.OrdinalIgnoreCase) ||
            channelName.StartsWith("mA", StringComparison.OrdinalIgnoreCase) ||
            channelName.Equals("UR-sie", StringComparison.OrdinalIgnoreCase))
        {
            return (-25, 6250);
        }

        return (0, 1);
    }

    private static string NormalizeUnit(string unit)
    {
        return string.IsNullOrWhiteSpace(unit) ? string.Empty : unit.Trim();
    }

    private class ExperimentMeta
    {
        public string Name { get; set; } = string.Empty;
        public string PartNumber { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
        public string Refrigerant { get; set; } = string.Empty;
    }

    private class RawSampleRow
    {
        public string Timestamp { get; set; } = string.Empty;
        public int ChannelIndex { get; set; }
        public double Value { get; set; }
        public int IsValid { get; set; }
    }
}
