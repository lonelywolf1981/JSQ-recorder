using System;

namespace JSQ.Core.Models;

/// <summary>
/// Результат агрегации за интервал
/// </summary>
public class AggregatedValue
{
    public int ChannelIndex { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Avg { get; set; }
    public double? First { get; set; }
    public double? Last { get; set; }
    
    public int SampleCount { get; set; }
    public int InvalidCount { get; set; }
    public int TotalCount { get; set; }
    
    /// <summary>
    /// Флаг качества: 1 = OK, 0 = degraded, -1 = bad
    /// </summary>
    public int QualityFlag { get; set; } = 1;
    
    /// <summary>
    /// Стандартное отклонение
    /// </summary>
    public double? StdDev { get; set; }
}
