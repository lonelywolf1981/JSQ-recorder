using System;
using System.Collections.Generic;

namespace JSQ.Core.Models;

/// <summary>
/// Пакет измерений для записи в БД
/// </summary>
public class SampleBatch
{
    public DateTime Timestamp { get; set; }
    public List<Sample> Samples { get; set; } = new();
    public int ChannelCount => Samples.Count;
    
    public SampleBatch() { }
    
    public SampleBatch(DateTime timestamp, List<Sample> samples)
    {
        Timestamp = timestamp;
        Samples = samples;
    }
}
