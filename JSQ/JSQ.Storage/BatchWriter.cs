using System.Diagnostics;
using Dapper;
using JSQ.Core.Models;

namespace JSQ.Storage;

/// <summary>
/// Сервис пакетной записи данных
/// </summary>
public interface IBatchWriter : IDisposable
{
    /// <summary>
    /// Добавить измерения для записи
    /// </summary>
    void AddSamples(string experimentId, IEnumerable<Sample> samples);
    
    /// <summary>
    /// Добавить пакет измерений
    /// </summary>
    void AddBatch(string experimentId, SampleBatch batch);
    
    /// <summary>
    /// Принудительно записать все данные
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Статистика записи
    /// </summary>
    BatchWriterStatistics GetStatistics();
}

/// <summary>
/// Статистика пакетной записи
/// </summary>
public class BatchWriterStatistics
{
    public ulong TotalSamplesWritten { get; set; }
    public ulong TotalBatchesWritten { get; set; }
    public ulong DroppedSamples { get; set; }
    public DateTime LastWriteTime { get; set; }
    public TimeSpan AvgWriteDuration { get; set; }
}

/// <summary>
/// Реализация пакетной записи
/// </summary>
public class BatchWriter : IBatchWriter
{
    private readonly IDatabaseService _dbService;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    
    private readonly List<SampleToWrite> _buffer = new();
    private readonly object _lock = new();
    private DateTime _lastFlushTime = DateTime.MinValue;
    private readonly BatchWriterStatistics _stats = new();
    private readonly Stopwatch _writeTimer = new();
    
    public BatchWriter(IDatabaseService dbService, int batchSize = 500, int flushIntervalSec = 1)
    {
        _dbService = dbService;
        _batchSize = batchSize;
        _flushInterval = TimeSpan.FromSeconds(flushIntervalSec);
    }
    
    public void AddSamples(string experimentId, IEnumerable<Sample> samples)
    {
        var timestamp = DateTime.Now.ToString("O");
        
        lock (_lock)
        {
            foreach (var sample in samples)
            {
                if (_buffer.Count >= _batchSize * 10) // Max buffer protection
                {
                    _stats.DroppedSamples++;
                    continue;
                }
                
                _buffer.Add(new SampleToWrite
                {
                    ExperimentId = experimentId,
                    Timestamp = timestamp,
                    ChannelIndex = sample.ChannelIndex,
                    Value = sample.Value,
                    IsValid = sample.IsValid
                });
            }
            
            // Проверяем необходимость flush
            if (_buffer.Count >= _batchSize || 
                (DateTime.Now - _lastFlushTime) >= _flushInterval)
            {
                FlushInternal();
            }
        }
    }
    
    public void AddBatch(string experimentId, SampleBatch batch)
    {
        var timestamp = batch.Timestamp.ToString("O");
        
        lock (_lock)
        {
            foreach (var sample in batch.Samples)
            {
                if (_buffer.Count >= _batchSize * 10)
                {
                    _stats.DroppedSamples++;
                    continue;
                }
                
                _buffer.Add(new SampleToWrite
                {
                    ExperimentId = experimentId,
                    Timestamp = timestamp,
                    ChannelIndex = sample.ChannelIndex,
                    Value = sample.Value,
                    IsValid = sample.IsValid
                });
            }
            
            if (_buffer.Count >= _batchSize || 
                (DateTime.Now - _lastFlushTime) >= _flushInterval)
            {
                FlushInternal();
            }
        }
    }
    
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                FlushInternal();
            }
        }, ct);
    }
    
    private void FlushInternal()
    {
        if (_buffer.Count == 0)
            return;
        
        try
        {
            _writeTimer.Restart();
            
            using var conn = _dbService.GetConnection();
            using var transaction = conn.BeginTransaction();
            
            const string sql = @"
                INSERT INTO raw_samples (experiment_id, timestamp, channel_index, value, is_valid)
                VALUES (@ExperimentId, @Timestamp, @ChannelIndex, @Value, @IsValid);
            ";
            
            conn.Execute(sql, _buffer, transaction);
            transaction.Commit();
            
            _writeTimer.Stop();
            
            // Обновляем статистику
            _stats.TotalSamplesWritten += (ulong)_buffer.Count;
            _stats.TotalBatchesWritten++;
            _stats.LastWriteTime = DateTime.Now;
            
            // Скользящее среднее длительности записи
            var currentAvg = _stats.AvgWriteDuration.TotalMilliseconds;
            var newDuration = _writeTimer.Elapsed;
            _stats.AvgWriteDuration = TimeSpan.FromMilliseconds(
                (currentAvg * 0.9) + (newDuration.TotalMilliseconds * 0.1)
            );
            
            _buffer.Clear();
            _lastFlushTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            // Логгируем ошибку но не выбрасываем чтобы не прерывать поток
            System.Diagnostics.Debug.WriteLine($"BatchWriter flush error: {ex.Message}");
        }
    }
    
    public BatchWriterStatistics GetStatistics()
    {
        lock (_lock)
        {
            return new BatchWriterStatistics
            {
                TotalSamplesWritten = _stats.TotalSamplesWritten,
                TotalBatchesWritten = _stats.TotalBatchesWritten,
                DroppedSamples = _stats.DroppedSamples,
                LastWriteTime = _stats.LastWriteTime,
                AvgWriteDuration = _stats.AvgWriteDuration
            };
        }
    }
    
    public void Dispose()
    {
        // Последний flush при уничтожении
        try
        {
            FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        
        GC.SuppressFinalize(this);
    }
    
    private class SampleToWrite
    {
        public string ExperimentId { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public int ChannelIndex { get; set; }
        public double Value { get; set; }
        public bool IsValid { get; set; }
    }
}
