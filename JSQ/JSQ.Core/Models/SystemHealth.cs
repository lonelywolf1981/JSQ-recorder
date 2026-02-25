using System;
using CommunityToolkit.Mvvm.ComponentModel;

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
public partial class ChannelStatus : ObservableObject
{
    [ObservableProperty] private int _channelIndex;
    [ObservableProperty] private string _channelName = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private double? _currentValue;
    [ObservableProperty] private DateTime _lastUpdateTime;
    [ObservableProperty] private HealthStatus _status = HealthStatus.NoData;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private double? _minLimit;
    [ObservableProperty] private double? _maxLimit;
    [ObservableProperty] private bool _highPrecision;

    /// <summary>Пост, которому назначен канал: "A", "B", "C" или пустая строка.</summary>
    [ObservableProperty] private string _post = string.Empty;

    /// <summary>True если запись на этом посту активна.</summary>
    [ObservableProperty] private bool _isRecording;

    /// <summary>
    /// Пометка канала пользователем для записи/массовых операций переноса.
    /// </summary>
    [ObservableProperty] private bool _isSelected = true;
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
