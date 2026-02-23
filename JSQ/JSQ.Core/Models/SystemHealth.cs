using System;

namespace JSQ.Core.Models;

/// <summary>
/// Статус здоровья системы
/// </summary>
public enum HealthStatus
{
    OK,         // Все нормально
    Warning,    // Есть предупреждения
    Alarm,      // Критическая ситуация
    NoData      // Нет данных
}

/// <summary>
/// Статус канала
/// </summary>
public class ChannelStatus
{
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    
    public double? CurrentValue { get; set; }
    public DateTime LastUpdateTime { get; set; }
    
    public HealthStatus Status { get; set; } = HealthStatus.NoData;
    public string? StatusMessage { get; set; }
    
    public double? MinLimit { get; set; }
    public double? MaxLimit { get; set; }
}

/// <summary>
/// Сводная статистика системы
/// </summary>
public class SystemHealth
{
    public HealthStatus OverallStatus { get; set; } = HealthStatus.OK;
    
    public int TotalChannels { get; set; }
    public int ChannelsOK { get; set; }
    public int ChannelsWarning { get; set; }
    public int ChannelsAlarm { get; set; }
    public int ChannelsNoData { get; set; }
    
    public ulong TotalSamplesReceived { get; set; }
    public ulong SamplesPerSecond { get; set; }
    
    public int ActiveAnomalies { get; set; }
    public TimeSpan ExperimentDuration { get; set; }
    
    public string? ErrorMessage { get; set; }
}
