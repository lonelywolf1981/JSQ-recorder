using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JSQ.Core.Models;
using JSQ.Export;
using JSQ.Storage;
using Xunit;

namespace JSQ.Tests;

public class LegacyExportServiceTests
{
    [Fact]
    public void ExportFromData_CreatesLegacyPackageWithExpectedStructure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "jsq-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var outputPath = Path.Combine(tempRoot, "out");
            var exporter = new LegacyExportService(new NoOpDatabaseService());

            var t1 = DateTime.Parse("2025-10-30T14:56:52.4230000", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            var t2 = DateTime.Parse("2025-10-30T14:57:12.4230000", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);

            var samples = new List<LegacyExportSample>
            {
                new() { Timestamp = t1, ChannelIndex = 4, Value = 16.4631 },  // C-Pc
                new() { Timestamp = t1, ChannelIndex = 5, Value = 2.9676 },   // C-Pe
                new() { Timestamp = t1, ChannelIndex = 14, Value = 65.896 },  // UR-sie
                new() { Timestamp = t1, ChannelIndex = 100, Value = 84.540 }, // C-Tc
                new() { Timestamp = t1, ChannelIndex = 132, Value = 17.4271 }, // C-I
                new() { Timestamp = t1, ChannelIndex = 135, Value = 1200.55 }, // C-W
                new() { Timestamp = t1, ChannelIndex = 54, Value = 29.650 },  // B-Tc

                new() { Timestamp = t2, ChannelIndex = 4, Value = 16.4652 },
                new() { Timestamp = t2, ChannelIndex = 5, Value = 2.9663 },
                new() { Timestamp = t2, ChannelIndex = 14, Value = 65.414 },
                new() { Timestamp = t2, ChannelIndex = 100, Value = 84.560 },
                new() { Timestamp = t2, ChannelIndex = 132, Value = 17.4251 },
                new() { Timestamp = t2, ChannelIndex = 135, Value = 1205.15 },
                new() { Timestamp = t2, ChannelIndex = 54, Value = 29.645 }
            };

            var result = exporter.ExportFromData(
                outputPath,
                "Prova001",
                "modelC",
                "345C",
                "op3",
                "R134A",
                samples);

            Assert.True(File.Exists(result.DbfPath));
            Assert.True(File.Exists(result.DatPath));
            Assert.True(File.Exists(result.IniPath));
            Assert.True(File.Exists(result.DefPath));
            Assert.True(File.Exists(result.CalPath));

            var datText = File.ReadAllText(result.DatPath, Encoding.GetEncoding(1252));
            Assert.Contains("MODEL/TYPE;modelC", datText);
            Assert.Contains("PART NUMBER;345C", datText);
            Assert.Contains("USER;op3", datText);

            var defLines = File.ReadAllLines(result.DefPath, Encoding.GetEncoding(1252));
            Assert.Equal("7", defLines[0]);

            var calLines = File.ReadAllLines(result.CalPath, Encoding.GetEncoding(1252));
            Assert.Equal(7, calLines.Length);
            Assert.Contains(calLines, l => l.StartsWith("C-Pc;;-1.0000000000E+1;2.5000000000E+3;", StringComparison.Ordinal));

            var dbf = ReadDbfMetadata(result.DbfPath);
            Assert.Equal(2, dbf.RecordCount);
            Assert.Equal(12, dbf.Fields.Count); // 5 time + 7 channels

            var cPcField = Assert.Single(dbf.Fields.Where(f => f.Name == "C-Pc"));
            Assert.Equal(9, cPcField.Length);
            Assert.Equal(4, cPcField.DecimalCount);

            var cIField = Assert.Single(dbf.Fields.Where(f => f.Name == "C-I"));
            Assert.Equal(8, cIField.Length);
            Assert.Equal(4, cIField.DecimalCount);

            var cWField = Assert.Single(dbf.Fields.Where(f => f.Name == "C-W"));
            Assert.Equal(9, cWField.Length);
            Assert.Equal(2, cWField.DecimalCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ExportFromData_WhenUsingEtalonChannelSet_ProducesEtalonDbfStructure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "jsq-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var outputPath = Path.Combine(tempRoot, "out");
            var exporter = new LegacyExportService(new NoOpDatabaseService());

            var etalonNames = new List<string>
            {
                "C-Pc", "C-Pe", "UR-sie", "T-sie", "C-Tc", "C-Te",
                "C-T1", "C-T2", "C-T3", "C-T4", "C-T5", "C-T6", "C-T7", "C-T8", "C-T9", "C-T10",
                "C-T11", "C-T12", "C-T13", "C-T14", "C-T15", "C-T16", "C-T17", "C-T18", "C-T19", "C-T20",
                "C-T21", "C-T22", "C-T23", "C-T24", "C-T25", "C-T26", "C-T27", "C-T28",
                "C-I", "C-F", "C-V", "C-W",
                "B-Tc", "B-Te", "B-T1", "B-T2", "B-T3", "B-T4", "B-T5", "B-T6", "B-T7", "B-T8"
            };

            var channelIndices = ChannelRegistry.All
                .Where(kvp => etalonNames.Contains(kvp.Value.Name))
                .Select(kvp => kvp.Key)
                .ToList();

            Assert.Equal(48, channelIndices.Count);

            var ts = DateTime.Parse("2025-10-30T14:56:52.4230000", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);

            var samples = channelIndices
                .Select((idx, i) => new LegacyExportSample
                {
                    Timestamp = ts,
                    ChannelIndex = idx,
                    Value = 10.0 + i,
                    IsValid = true
                })
                .ToList();

            var result = exporter.ExportFromData(
                outputPath,
                "Prova001",
                "modelC",
                "345C",
                "op3",
                "R134A",
                samples);

            var dbf = ReadDbfMetadata(result.DbfPath);
            Assert.Equal(1, dbf.RecordCount);
            Assert.Equal(53, dbf.Fields.Count);
            Assert.Equal(405, dbf.RecordLength);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static DbfMetadata ReadDbfMetadata(string dbfPath)
    {
        using var stream = File.OpenRead(dbfPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var header = reader.ReadBytes(32);
        var recordCount = BitConverter.ToInt32(header, 4);
        var headerLength = BitConverter.ToInt16(header, 8);
        var recordLength = BitConverter.ToInt16(header, 10);

        var fields = new List<DbfFieldMeta>();
        stream.Position = 32;
        while (stream.Position < headerLength)
        {
            var descriptor = reader.ReadBytes(32);
            if (descriptor.Length == 0 || descriptor[0] == 0x0D)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(descriptor, 0, 11).TrimEnd('\0', ' ');
            fields.Add(new DbfFieldMeta
            {
                Name = name,
                Length = descriptor[16],
                DecimalCount = descriptor[17]
            });
        }

        return new DbfMetadata
        {
            RecordCount = recordCount,
            RecordLength = recordLength,
            Fields = fields
        };
    }

    private class DbfMetadata
    {
        public int RecordCount { get; set; }
        public int RecordLength { get; set; }
        public List<DbfFieldMeta> Fields { get; set; } = new();
    }

    private class DbfFieldMeta
    {
        public string Name { get; set; } = string.Empty;
        public int Length { get; set; }
        public int DecimalCount { get; set; }
    }

    private sealed class NoOpDatabaseService : IDatabaseService
    {
        public string DbPath => string.Empty;
        public Task InitializeAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public System.Data.IDbConnection GetConnection() => throw new NotSupportedException();
        public Task CheckpointAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public long GetDatabaseSize() => 0;
        public void Dispose() { }
    }
}
