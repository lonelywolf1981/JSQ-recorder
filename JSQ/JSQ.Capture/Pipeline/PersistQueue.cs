using System.Collections.Concurrent;
using JSQ.Core.Models;

namespace JSQ.Capture.Pipeline;

/// <summary>
/// Очередь сохранения (Persist Queue)
/// Содержит декодированные данные готовые к записи в БД
/// </summary>
public class PersistQueue
{
    private readonly ConcurrentQueue<PipelineItem<SampleBatch>> _queue = new();
    private readonly int _maxSize;
    private readonly int _batchSize;
    private ulong _droppedCount;
    
    public PersistQueue(int maxSize = 1000, int batchSize = 500)
    {
        _maxSize = maxSize;
        _batchSize = batchSize;
    }
    
    public int Count => _queue.Count;
    public ulong DroppedCount => _droppedCount;
    public int BatchSize => _batchSize;
    
    public bool Enqueue(SampleBatch batch)
    {
        if (_queue.Count >= _maxSize)
        {
            _droppedCount++;
            return false;
        }
        
        _queue.Enqueue(new PipelineItem<SampleBatch> { Data = batch });
        return true;
    }
    
    public bool TryDequeue(out PipelineItem<SampleBatch> item)
    {
        return _queue.TryDequeue(out item);
    }
    
    public bool TryGetBatch(out List<SampleBatch> batch)
    {
        batch = new List<SampleBatch>();
        
        while (batch.Count < _batchSize && TryDequeue(out var item))
        {
            batch.Add(item.Data);
        }
        
        return batch.Count > 0;
    }
    
    public void Clear()
    {
        while (!_queue.IsEmpty)
            _queue.TryDequeue(out _);
    }
}
