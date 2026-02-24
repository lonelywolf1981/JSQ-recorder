using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using JSQ.Core.Models;

namespace JSQ.Rules;

/// <summary>
/// Детектор аномалий
/// </summary>
public interface IAnomalyDetector
{
    /// <summary>
    /// Загрузить правила
    /// </summary>
    void LoadRules(IEnumerable<AnomalyRule> rules);
    
    /// <summary>
    /// Проверить значение на аномалии
    /// </summary>
    IEnumerable<AnomalyEvent> CheckValue(int channelIndex, double value, DateTime timestamp);
    
    /// <summary>
    /// Проверить агрегированные данные
    /// </summary>
    IEnumerable<AnomalyEvent> CheckAggregate(AggregatedValue aggregate);
    
    /// <summary>
    /// Проверить таймауты (отсутствие данных)
    /// </summary>
    IEnumerable<AnomalyEvent> CheckTimeouts(DateTime now);
    
    /// <summary>
    /// Подтвердить аномалию
    /// </summary>
    void Acknowledge(string eventId, string user);
    
    /// <summary>
    /// Получить активные аномалии
    /// </summary>
    IEnumerable<AnomalyEvent> GetActiveAnomalies();
}

/// <summary>
/// Реализация детектора аномалий
/// </summary>
public class AnomalyDetector : IAnomalyDetector
{
    private readonly ConcurrentDictionary<int, AnomalyRule> _rules = new();
    private readonly ConcurrentDictionary<string, AnomalyEvent> _activeAnomalies = new();
    private readonly ConcurrentDictionary<int, ChannelState> _channelStates = new();
    private readonly string _experimentId;
    
    public AnomalyDetector(string experimentId)
    {
        _experimentId = experimentId;
    }
    
    public void LoadRules(IEnumerable<AnomalyRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Enabled)
            {
                _rules[rule.ChannelIndex] = rule;
                _channelStates[rule.ChannelIndex] = new ChannelState();
            }
        }
    }
    
    public IEnumerable<AnomalyEvent> CheckValue(int channelIndex, double value, DateTime timestamp)
    {
        if (!_rules.TryGetValue(channelIndex, out var rule))
            return Array.Empty<AnomalyEvent>();
        
        var events = new List<AnomalyEvent>();
        var state = _channelStates.GetOrAdd(channelIndex, _ => new ChannelState());
        
        // Проверка минимума
        if (rule.MinLimit.HasValue)
        {
            var hysteresis = rule.MinHysteresis ?? 0;
            var effectiveMin = rule.MinLimit.Value - hysteresis;
            
            if (value < effectiveMin)
            {
                state.MinViolationCount++;
                if (state.MinViolationCount >= rule.DebounceCount && !state.HasActiveMinViolation)
                {
                    var evt = CreateEvent(channelIndex, rule.ChannelName, AnomalyType.MinViolation, 
                        value, rule.MinLimit, $"Значение {value:F3} ниже минимума {rule.MinLimit:F3}");
                    events.Add(evt);
                    state.HasActiveMinViolation = true;
                }
            }
            else
            {
                state.MinViolationCount = 0;
                if (state.HasActiveMinViolation)
                {
                    ClearViolation(channelIndex, AnomalyType.MinViolation);
                    state.HasActiveMinViolation = false;
                    // Значение вернулось в допустимые пределы — фиксируем восстановление
                    if (!state.HasActiveMaxViolation)
                    {
                        var restored = CreateEvent(channelIndex, rule.ChannelName, AnomalyType.LimitsRestored,
                            value, rule.MinLimit, $"Значение {value:F3} вернулось в пределы (мин {rule.MinLimit:F3})");
                        events.Add(restored);
                    }
                }
            }
        }

        // Проверка максимума
        if (rule.MaxLimit.HasValue)
        {
            var hysteresis = rule.MaxHysteresis ?? 0;
            var effectiveMax = rule.MaxLimit.Value + hysteresis;

            if (value > effectiveMax)
            {
                state.MaxViolationCount++;
                if (state.MaxViolationCount >= rule.DebounceCount && !state.HasActiveMaxViolation)
                {
                    var evt = CreateEvent(channelIndex, rule.ChannelName, AnomalyType.MaxViolation,
                        value, rule.MaxLimit, $"Значение {value:F3} выше максимума {rule.MaxLimit:F3}");
                    events.Add(evt);
                    state.HasActiveMaxViolation = true;
                }
            }
            else
            {
                state.MaxViolationCount = 0;
                if (state.HasActiveMaxViolation)
                {
                    ClearViolation(channelIndex, AnomalyType.MaxViolation);
                    state.HasActiveMaxViolation = false;
                    // Значение вернулось в допустимые пределы — фиксируем восстановление
                    if (!state.HasActiveMinViolation)
                    {
                        var restored = CreateEvent(channelIndex, rule.ChannelName, AnomalyType.LimitsRestored,
                            value, rule.MaxLimit, $"Значение {value:F3} вернулось в пределы (макс {rule.MaxLimit:F3})");
                        events.Add(restored);
                    }
                }
            }
        }
        
        // Проверка дельты (скачка)
        if (rule.MaxDelta.HasValue && state.LastValue.HasValue)
        {
            var delta = Math.Abs(value - state.LastValue.Value);
            if (delta > rule.MaxDelta.Value)
            {
                var evt = CreateEvent(channelIndex, rule.ChannelName, AnomalyType.DeltaSpike,
                    value, rule.MaxDelta, $"Скачок значения: Δ={delta:F3} (макс. {rule.MaxDelta:F3})",
                    delta);
                events.Add(evt);
            }
        }
        
        state.LastValue = value;
        state.LastCheckTime = timestamp;

        // Немедленно генерируем DataRestored при получении данных после NoData,
        // не дожидаясь следующего вызова CheckTimeouts
        if (state.HasNoDataEvent)
        {
            ClearViolation(channelIndex, AnomalyType.NoData);
            state.HasNoDataEvent = false;
            var recovery = CreateEvent(channelIndex, rule.ChannelName, AnomalyType.DataRestored,
                value, null, $"Данные восстановлены: {rule.ChannelName}");
            events.Add(recovery);
        }

        return events;
    }
    
    public IEnumerable<AnomalyEvent> CheckAggregate(AggregatedValue aggregate)
    {
        if (!_rules.TryGetValue(aggregate.ChannelIndex, out var rule))
            return Array.Empty<AnomalyEvent>();
        
        var events = new List<AnomalyEvent>();
        
        // Проверка качества
        if (aggregate.QualityFlag == 0)
        {
            var evt = CreateEvent(aggregate.ChannelIndex, rule.ChannelName, AnomalyType.QualityDegraded,
                aggregate.Avg, null, 
                $"Ухудшение качества данных: {aggregate.SampleCount} из {aggregate.TotalCount} валидных");
            events.Add(evt);
        }
        else if (aggregate.QualityFlag == -1)
        {
            var evt = CreateEvent(aggregate.ChannelIndex, rule.ChannelName, AnomalyType.QualityBad,
                aggregate.Avg, null,
                $"Плохое качество данных: {aggregate.SampleCount} из {aggregate.TotalCount} валидных");
            events.Add(evt);
        }
        
        return events;
    }
    
    public IEnumerable<AnomalyEvent> CheckTimeouts(DateTime now)
    {
        var events = new List<AnomalyEvent>();
        
        foreach (var kvp in _rules)
        {
            var rule = kvp.Value;
            if (!rule.NoDataTimeoutSec.HasValue)
                continue;
            
            var state = _channelStates.GetOrAdd(rule.ChannelIndex, _ => new ChannelState());
            
            if (state.LastCheckTime.HasValue)
            {
                var elapsed = (now - state.LastCheckTime.Value).TotalSeconds;
                if (elapsed > rule.NoDataTimeoutSec.Value && !state.HasNoDataEvent)
                {
                    var evt = CreateEvent(rule.ChannelIndex, rule.ChannelName, AnomalyType.NoData,
                        null, rule.NoDataTimeoutSec,
                        $"Отсутствие данных более {rule.NoDataTimeoutSec} сек (прошло {elapsed:F0} сек)");
                    events.Add(evt);
                    state.HasNoDataEvent = true;
                }
                else if (elapsed <= rule.NoDataTimeoutSec.Value && state.HasNoDataEvent)
                {
                    ClearViolation(rule.ChannelIndex, AnomalyType.NoData);
                    state.HasNoDataEvent = false;

                    // Канал снова передаёт данные — фиксируем восстановление
                    var recovery = CreateEvent(rule.ChannelIndex, rule.ChannelName, AnomalyType.DataRestored,
                        null, null, $"Данные восстановлены: {rule.ChannelName}");
                    events.Add(recovery);
                }
            }
        }
        
        return events;
    }
    
    public void Acknowledge(string eventId, string user)
    {
        if (_activeAnomalies.TryGetValue(eventId, out var evt))
        {
            evt.IsAcknowledged = true;
            evt.AcknowledgedAt = JsqClock.Now;
            evt.AcknowledgedBy = user;
            evt.EndTime = JsqClock.Now;
        }
    }
    
    public IEnumerable<AnomalyEvent> GetActiveAnomalies()
    {
        return _activeAnomalies.Values.Where(e => e.EndTime == null);
    }
    
    private AnomalyEvent CreateEvent(int channelIndex, string channelName, AnomalyType type,
        double? value, double? threshold, string message, double? delta = null)
    {
        var severity = type switch
        {
            AnomalyType.MinViolation or AnomalyType.MaxViolation => "Warning",  // жёлтый
            AnomalyType.DeltaSpike => "Warning",
            AnomalyType.NoData => "Critical",                                   // красный — отключение канала
            AnomalyType.DataRestored => "Info",                                 // зелёный
            AnomalyType.LimitsRestored => "Info",                               // зелёный — возврат в пределы
            AnomalyType.QualityBad => "Critical",
            _ => "Warning"
        };
        
        var evt = new AnomalyEvent
        {
            ExperimentId = _experimentId,
            ChannelIndex = channelIndex,
            ChannelName = channelName,
            AnomalyType = type,
            Severity = severity,
            Value = value,
            Threshold = threshold,
            Delta = delta,
            Message = message,
            Timestamp = JsqClock.Now
        };
        
        _activeAnomalies[evt.Id] = evt;
        
        return evt;
    }
    
    private void ClearViolation(int channelIndex, AnomalyType type)
    {
        var toClear = _activeAnomalies.Values
            .Where(e => e.ChannelIndex == channelIndex && e.AnomalyType == type && e.EndTime == null)
            .ToList();
        
        foreach (var evt in toClear)
        {
            evt.EndTime = JsqClock.Now;
        }
    }
    
    /// <summary>
    /// Состояние канала для отслеживания нарушений
    /// </summary>
    private class ChannelState
    {
        public double? LastValue { get; set; }
        public DateTime? LastCheckTime { get; set; }
        
        public int MinViolationCount { get; set; }
        public int MaxViolationCount { get; set; }
        
        public bool HasActiveMinViolation { get; set; }
        public bool HasActiveMaxViolation { get; set; }
        public bool HasNoDataEvent { get; set; }
    }
}
