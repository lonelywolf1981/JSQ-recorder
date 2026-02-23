using System.Collections.Concurrent;
using JSQ.Core.Models;

namespace JSQ.Capture.Pipeline;

/// <summary>
/// Очередь декодирования (Decode Queue)
/// Содержит разобранные TCP сегменты готовые к обработке
/// </summary>
public class DecodeQueue
{
    private readonly ConcurrentQueue<PipelineItem<TcpSegment>> _queue = new();
    private readonly int _maxSize;
    private ulong _droppedCount;
    
    public DecodeQueue(int maxSize = 5000)
    {
        _maxSize = maxSize;
    }
    
    public int Count => _queue.Count;
    public ulong DroppedCount => _droppedCount;
    
    public bool Enqueue(TcpSegment segment)
    {
        if (_queue.Count >= _maxSize)
        {
            _droppedCount++;
            return false;
        }
        
        _queue.Enqueue(new PipelineItem<TcpSegment> { Data = segment });
        return true;
    }
    
    public bool TryDequeue(out PipelineItem<TcpSegment> item)
    {
        return _queue.TryDequeue(out item);
    }
    
    public void Clear()
    {
        while (!_queue.IsEmpty)
            _queue.TryDequeue(out _);
    }
}

/// <summary>
/// TCP сегмент после реассембли
/// </summary>
public class TcpSegment
{
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; }
    public ulong SequenceNumber { get; set; }
    public bool IsRetransmit { get; set; }
    public bool HasGap { get; set; }
    public int GapSize { get; set; }
}
