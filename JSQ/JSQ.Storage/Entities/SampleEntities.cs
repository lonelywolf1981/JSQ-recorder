namespace JSQ.Storage.Entities;

/// <summary>
/// Сырое измерение
/// </summary>
public class RawSampleEntity
{
    public long Id { get; set; }
    public string ExperimentId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public int ChannelIndex { get; set; }
    public double Value { get; set; }
    public bool IsValid { get; set; } = true;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Агрегированное измерение (20с)
/// </summary>
public class AggregatedSampleEntity
{
    public long Id { get; set; }
    public string ExperimentId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public int ChannelIndex { get; set; }
    public double? ValueMin { get; set; }
    public double? ValueMax { get; set; }
    public double? ValueAvg { get; set; }
    public int SampleCount { get; set; }
    public int InvalidCount { get; set; }
    public int QualityFlag { get; set; } = 1;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Событие аномалии
/// </summary>
public class AnomalyEventEntity
{
    public long Id { get; set; }
    public string ExperimentId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string AnomalyType { get; set; } = string.Empty;
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    public int? DurationSec { get; set; }
    public bool IsAcknowledged { get; set; }
    public string? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public string? ContextJson { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Системное событие
/// </summary>
public class SystemEventEntity
{
    public long Id { get; set; }
    public string? ExperimentId { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
}

/// <summary>
/// Чекпоинт для восстановления
/// </summary>
public class CheckpointEntity
{
    public long Id { get; set; }
    public string ExperimentId { get; set; } = string.Empty;
    public string CheckpointTime { get; set; } = string.Empty;
    public string? LastSampleTimestamp { get; set; }
    public long? LastSampleId { get; set; }
    public string? QueueStateJson { get; set; }
    public string? StatisticsJson { get; set; }
}
