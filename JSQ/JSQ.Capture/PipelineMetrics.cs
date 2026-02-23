namespace JSQ.Capture;

/// <summary>
/// Метрики производительности pipeline
/// </summary>
public class PipelineMetrics
{
    // Размеры очередей
    public int IngestQueueSize { get; set; }
    public int DecodeQueueSize { get; set; }
    public int PersistQueueSize { get; set; }
    
    // Пропускная способность
    public double ThroughputSamplesPerSec { get; set; }
    public double ThroughputBytesPerSec { get; set; }
    
    // Ошибки
    public ulong TotalDropped { get; set; }
    public uint RetransmitCount { get; set; }
    public uint GapCount { get; set; }
    
    // Задержки
    public TimeSpan IngestLatency { get; set; }
    public TimeSpan DecodeLatency { get; set; }
    public TimeSpan PersistLatency { get; set; }
    
    public override string ToString() => 
        $"Q: {IngestQueueSize}/{DecodeQueueSize}/{PersistQueueSize}, " +
        $"T: {ThroughputSamplesPerSec:F1} samp/s, " +
        $"Dropped: {TotalDropped}";
}
