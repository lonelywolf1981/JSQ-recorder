using System.Collections.Concurrent;

namespace JSQ.Capture.Pipeline;

/// <summary>
/// Базовый элемент конвейера обработки
/// </summary>
public class PipelineItem<T>
{
    public T Data { get; set; } = default!;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public int RetryCount { get; set; }
    public bool IsProcessed { get; set; }
}

/// <summary>
/// Очередь приема данных (Ingest Queue)
/// Буферизует сырые TCP пакеты перед обработкой
/// </summary>
public class IngestQueue
{
    private readonly ConcurrentQueue<PipelineItem<byte[]>> _queue = new();
    private readonly int _maxSize;
    private ulong _droppedCount;
    
    public IngestQueue(int maxSize = 10000)
    {
        _maxSize = maxSize;
    }
    
    public int Count => _queue.Count;
    public ulong DroppedCount => _droppedCount;
    
    public bool Enqueue(byte[] data)
    {
        if (_queue.Count >= _maxSize)
        {
            _droppedCount++;
            return false;
        }
        
        _queue.Enqueue(new PipelineItem<byte[]> { Data = data });
        return true;
    }
    
    public bool TryDequeue(out PipelineItem<byte[]> item)
    {
        return _queue.TryDequeue(out item);
    }
    
    public void Clear()
    {
        while (!_queue.IsEmpty)
            _queue.TryDequeue(out _);
    }
}
