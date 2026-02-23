using System;
using System.Collections.Generic;

namespace JSQ.Core.Models;

/// <summary>
/// Состояния эксперимента
/// </summary>
public enum ExperimentState
{
    Idle,
    Running,
    Paused,
    Stopped,
    Finalized,
    Recovered
}

/// <summary>
/// Модель эксперимента
/// </summary>
public class Experiment
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string Refrigerant { get; set; } = string.Empty;
    
    public ExperimentState State { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.Now - StartTime;
    
    // Настройки постов
    public bool PostAEnabled { get; set; } = true;
    public bool PostBEnabled { get; set; } = true;
    public bool PostCEnabled { get; set; } = true;
    
    // Настройки записи
    public int BatchSize { get; set; } = 500;
    public int AggregationIntervalSec { get; set; } = 20;
    public int CheckpointIntervalSec { get; set; } = 30;
    
    // Выбранные каналы
    public List<int> SelectedChannelIndices { get; set; } = new();
}
