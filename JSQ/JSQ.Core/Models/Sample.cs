using System;

namespace JSQ.Core.Models;

/// <summary>
/// Сырое измерение от канала
/// </summary>
public class Sample
{
    public DateTime Timestamp { get; set; }
    public int ChannelIndex { get; set; }
    public double Value { get; set; }
    public bool IsValid => Value > -90; // -99 означает нет данных
    
    public Sample() { }
    
    public Sample(int channelIndex, double value, DateTime timestamp)
    {
        ChannelIndex = channelIndex;
        Value = value;
        Timestamp = timestamp;
    }
}
