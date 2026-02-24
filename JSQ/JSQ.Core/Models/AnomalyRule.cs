using System;

namespace JSQ.Core.Models;

/// <summary>
/// Тип аномалии
/// </summary>
public enum AnomalyType
{
    None,
    MinViolation,      // Ниже минимума
    MaxViolation,      // Выше максимума
    DeltaSpike,        // Резкий скачок
    NoData,            // Нет данных
    DataRestored,      // Данные восстановлены после отсутствия
    LimitsRestored,    // Значение вернулось в допустимые пределы
    QualityDegraded,   // Ухудшение качества
    QualityBad         // Плохое качество
}

/// <summary>
/// Конфигурация правил для канала
/// </summary>
public class AnomalyRule
{
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    
    /// <summary>
    /// Минимальное допустимое значение
    /// </summary>
    public double? MinLimit { get; set; }
    
    /// <summary>
    /// Максимальное допустимое значение
    /// </summary>
    public double? MaxLimit { get; set; }
    
    /// <summary>
    /// Максимальное изменение за интервал (delta)
    /// </summary>
    public double? MaxDelta { get; set; }
    
    /// <summary>
    /// Таймаут отсутствия данных (сек)
    /// </summary>
    public int? NoDataTimeoutSec { get; set; }
    
    /// <summary>
    /// Гистерезис для минимума (для подавления дребезга)
    /// </summary>
    public double? MinHysteresis { get; set; }
    
    /// <summary>
    /// Гистерезис для максимума
    /// </summary>
    public double? MaxHysteresis { get; set; }
    
    /// <summary>
    /// Требуемое количество последовательных нарушений для срабатывания
    /// </summary>
    public int DebounceCount { get; set; } = 1;
    
    /// <summary>
    /// Канал включен для мониторинга
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Событие аномалии
/// </summary>
public class AnomalyEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ExperimentId { get; set; } = string.Empty;
    
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    
    public AnomalyType AnomalyType { get; set; }
    public string Severity { get; set; } = "Warning"; // Warning, Critical
    
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    public double? Delta { get; set; }
    
    public string Message { get; set; } = string.Empty;
    public string? Context { get; set; }
    
    public bool IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    
    public DateTime? EndTime { get; set; }
    public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - Timestamp : TimeSpan.Zero;
}
