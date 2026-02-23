namespace JSQ.Storage.Entities;

/// <summary>
/// Сущность эксперимента
/// </summary>
public class ExperimentEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string Refrigerant { get; set; } = string.Empty;
    public string State { get; set; } = "Idle";
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public bool PostAEnabled { get; set; } = true;
    public bool PostBEnabled { get; set; } = true;
    public bool PostCEnabled { get; set; } = true;
    public int BatchSize { get; set; } = 500;
    public int AggregationIntervalSec { get; set; } = 20;
    public int CheckpointIntervalSec { get; set; } = 30;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Сущность оператора
/// </summary>
public class OperatorEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Конфигурация канала
/// </summary>
public class ChannelConfigEntity
{
    public int Id { get; set; }
    public string ExperimentId { get; set; } = string.Empty;
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string? ChannelUnit { get; set; }
    public string? ChannelGroup { get; set; }
    public string? ChannelType { get; set; }
    public double? MinLimit { get; set; }
    public double? MaxLimit { get; set; }
    public bool Enabled { get; set; } = true;
}
