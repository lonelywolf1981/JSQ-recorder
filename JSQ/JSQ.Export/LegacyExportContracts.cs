using System;
using System.Threading;
using System.Threading.Tasks;

namespace JSQ.Export;

public interface ILegacyExportService
{
    Task<LegacyExportResult> ExportExperimentAsync(
        string experimentId,
        string outputRoot,
        string? packageName = null,
        CancellationToken ct = default);
}

public class LegacyExportResult
{
    public string PackageName { get; set; } = string.Empty;
    public string PackageDirectory { get; set; } = string.Empty;
    public string DbfPath { get; set; } = string.Empty;
    public string DatPath { get; set; } = string.Empty;
    public string IniPath { get; set; } = string.Empty;
    public string DefPath { get; set; } = string.Empty;
    public string CalPath { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public int ChannelCount { get; set; }
}

public class LegacyExportSample
{
    public DateTime Timestamp { get; set; }
    public int ChannelIndex { get; set; }
    public double Value { get; set; }
    public bool IsValid { get; set; } = true;
}

internal class DbfField
{
    public string Name { get; set; } = string.Empty;
    public char Type { get; set; }
    public byte Length { get; set; }
    public byte DecimalCount { get; set; }
}

internal class LegacyChannel
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public byte DbfLength { get; set; }
    public byte DbfDecimals { get; set; }
}

internal class ExportSampleRow
{
    public DateTime Timestamp { get; set; }
    public string TimestampKey { get; set; } = string.Empty;
    public System.Collections.Generic.Dictionary<int, double> ValuesByChannel { get; } = new();
}
