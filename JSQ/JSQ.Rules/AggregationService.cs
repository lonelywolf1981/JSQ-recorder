using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using JSQ.Core.Models;

namespace JSQ.Rules;

/// <summary>
/// Сервис 20-секундной агрегации данных
/// </summary>
public interface IAggregationService
{
    /// <summary>
    /// Добавить измерение для агрегации
    /// </summary>
    void AddSample(Sample sample);
    
    /// <summary>
    /// Получить готовые агрегированные данные
    /// </summary>
    IEnumerable<AggregatedValue> GetReadyAggregates();
    
    /// <summary>
    /// Принудительно завершить текущие окна
    /// </summary>
    IEnumerable<AggregatedValue> Flush();
    
    /// <summary>
    /// Статистика агрегации
    /// </summary>
    AggregationStatistics GetStatistics();
}

/// <summary>
/// Статистика агрегации
/// </summary>
public class AggregationStatistics
{
    public int ActiveWindows { get; set; }
    public int TotalWindowsCompleted { get; set; }
    public int TotalSamplesProcessed { get; set; }
    public DateTime LastAggregationTime { get; set; }
}

/// <summary>
/// Реализация сервиса агрегации
/// </summary>
public class AggregationService : IAggregationService
{
    private readonly int _intervalSeconds;
    private readonly int _highPrecisionIntervalSeconds;
    private readonly HashSet<int> _highPrecisionChannels;
    private readonly ConcurrentDictionary<int, WindowState> _windows = new();
    private readonly object _lock = new();
    private int _completedWindows;
    private int _totalSamples;
    private DateTime _lastAggregationTime = DateTime.MinValue;
    
    public AggregationService(
        int intervalSeconds = 20,
        IEnumerable<int>? highPrecisionChannels = null,
        int highPrecisionIntervalSeconds = 10)
    {
        _intervalSeconds = intervalSeconds;
        _highPrecisionIntervalSeconds = highPrecisionIntervalSeconds;
        _highPrecisionChannels = new HashSet<int>(highPrecisionChannels ?? Array.Empty<int>());
    }
    
    public void AddSample(Sample sample)
    {
        if (!sample.IsValid)
            return;
        
        // Вычисляем окно времени (округление вниз до интервала)
        var intervalSeconds = GetChannelIntervalSeconds(sample.ChannelIndex);
        var windowStart = GetWindowStart(sample.Timestamp, intervalSeconds);
        
        var window = _windows.GetOrAdd(sample.ChannelIndex, _ => new WindowState())
            .GetOrCreateWindow(windowStart, intervalSeconds);
        
        lock (_lock)
        {
            window.AddValue(sample.Value);
            _totalSamples++;
        }
    }
    
    public IEnumerable<AggregatedValue> GetReadyAggregates()
    {
        var now = JsqClock.Now;
        var ready = new List<AggregatedValue>();
        
        foreach (var kvp in _windows)
        {
            var channelIndex = kvp.Key;
            var windowState = kvp.Value;
            
            foreach (var window in windowState.GetCompletedWindows(now))
            {
                var aggregate = window.ToAggregatedValue(channelIndex);
                if (aggregate != null)
                {
                    ready.Add(aggregate);
                    _completedWindows++;
                }
            }
        }
        
        if (ready.Count > 0)
            _lastAggregationTime = JsqClock.Now;
        
        return ready;
    }
    
    public IEnumerable<AggregatedValue> Flush()
    {
        var all = new List<AggregatedValue>();
        
        foreach (var kvp in _windows)
        {
            var channelIndex = kvp.Key;
            var windowState = kvp.Value;
            
            foreach (var window in windowState.GetAllWindows())
            {
                var aggregate = window.ToAggregatedValue(channelIndex);
                if (aggregate != null)
                {
                    all.Add(aggregate);
                    _completedWindows++;
                }
            }
            
            windowState.Clear();
        }
        
        return all;
    }
    
    public AggregationStatistics GetStatistics()
    {
        return new AggregationStatistics
        {
            ActiveWindows = _windows.Sum(kvp => kvp.Value.GetWindowCount()),
            TotalWindowsCompleted = _completedWindows,
            TotalSamplesProcessed = _totalSamples,
            LastAggregationTime = _lastAggregationTime
        };
    }
    
    private int GetChannelIntervalSeconds(int channelIndex)
    {
        return _highPrecisionChannels.Contains(channelIndex)
            ? _highPrecisionIntervalSeconds
            : _intervalSeconds;
    }

    private static DateTime GetWindowStart(DateTime timestamp, int intervalSeconds)
    {
        // Округление вниз до ближайшего интервала
        var ticks = timestamp.Ticks;
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        var windowTicks = (ticks / intervalTicks) * intervalTicks;
        return new DateTime(windowTicks, DateTimeKind.Local);
    }
    
    /// <summary>
    /// Состояние окна агрегации
    /// </summary>
    private class WindowState
    {
        private readonly SortedDictionary<DateTime, AggregationWindow> _windows = new();
        private readonly object _windowsLock = new();

        public AggregationWindow GetOrCreateWindow(DateTime windowStart, int intervalSeconds)
        {
            lock (_windowsLock)
            {
                if (!_windows.TryGetValue(windowStart, out var window))
                {
                    window = new AggregationWindow(windowStart, intervalSeconds);
                    _windows[windowStart] = window;
                }
                return window;
            }
        }

        public IEnumerable<AggregationWindow> GetCompletedWindows(DateTime now)
        {
            var completed = new List<AggregationWindow>();

            lock (_windowsLock)
            {
                foreach (var kvp in _windows.ToList())
                {
                    if (kvp.Value.IsCompleted(now))
                    {
                        completed.Add(kvp.Value);
                        _windows.Remove(kvp.Key);
                    }
                }
            }

            return completed;
        }

        public IEnumerable<AggregationWindow> GetAllWindows()
        {
            lock (_windowsLock)
            {
                var all = _windows.Values.ToList();
                _windows.Clear();
                return all;
            }
        }

        public int GetWindowCount()
        {
            lock (_windowsLock) return _windows.Count;
        }

        public void Clear()
        {
            lock (_windowsLock) _windows.Clear();
        }
    }
    
    /// <summary>
    /// Окно агрегации для одного канала
    /// </summary>
    private class AggregationWindow
    {
        private readonly List<double> _values = new();
        private double _sum;
        private double _sumSquares;
        
        public DateTime WindowStart { get; }
        public int IntervalSeconds { get; }
        public int InvalidCount { get; set; }
        
        public AggregationWindow(DateTime windowStart, int intervalSeconds)
        {
            WindowStart = windowStart;
            IntervalSeconds = intervalSeconds;
        }
        
        public void AddValue(double value)
        {
            // Проверка на invalid value (-99)
            if (value <= -90)
            {
                InvalidCount++;
                return;
            }
            
            _values.Add(value);
            _sum += value;
            _sumSquares += value * value;
        }
        
        public bool IsCompleted(DateTime now)
        {
            // Окно завершено если прошло больше интервала + буфер 2 секунды
            var windowEnd = WindowStart.AddSeconds(IntervalSeconds);
            return now > windowEnd.AddSeconds(2);
        }
        
        public AggregatedValue? ToAggregatedValue(int channelIndex)
        {
            if (_values.Count == 0)
                return null;
            
            var avg = _sum / _values.Count;
            var variance = (_sumSquares / _values.Count) - (avg * avg);
            var stddev = variance > 0 ? Math.Sqrt(variance) : (double?)null;
            
            return new AggregatedValue
            {
                ChannelIndex = channelIndex,
                WindowSeconds = IntervalSeconds,
                WindowStart = WindowStart,
                WindowEnd = WindowStart.AddSeconds(IntervalSeconds),
                Min = _values.Min(),
                Max = _values.Max(),
                Avg = avg,
                First = _values.First(),
                Last = _values.Last(),
                SampleCount = _values.Count,
                InvalidCount = InvalidCount,
                TotalCount = _values.Count + InvalidCount,
                QualityFlag = CalculateQualityFlag(),
                StdDev = stddev
            };
        }
        
        private int CalculateQualityFlag()
        {
            // Качество: 1 = OK, 0 = degraded (>10% invalid), -1 = bad (>50% invalid)
            var total = _values.Count + InvalidCount;
            if (total == 0)
                return -1;
            
            var invalidRatio = (double)InvalidCount / total;
            
            if (invalidRatio > 0.5)
                return -1;
            if (invalidRatio > 0.1)
                return 0;
            
            return 1;
        }
    }
}
