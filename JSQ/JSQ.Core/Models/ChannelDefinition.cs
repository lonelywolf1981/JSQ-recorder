namespace JSQ.Core.Models;

/// <summary>
/// Тип канала по группе
/// </summary>
public enum ChannelGroup
{
    PostA,
    PostB,
    PostC,
    Common,
    System
}

/// <summary>
/// Тип канала по физическому параметру
/// </summary>
public enum ChannelType
{
    Pressure,
    Temperature,
    Electrical,
    Flow,
    Humidity,
    CurrentLoop,
    System
}

/// <summary>
/// Определение канала измерения
/// </summary>
public class ChannelDefinition
{
    public int Index { get; set; }
    public string RawCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ChannelGroup Group { get; set; }
    public ChannelType Type { get; set; }
    
    // Настройки аномалий
    public double? MinLimit { get; set; }
    public double? MaxLimit { get; set; }
    public bool Enabled { get; set; } = true;
    public bool HighPrecision { get; set; } = false; // true -> 10s, false -> 20s
    
    public override string ToString() => $"{Name} ({Unit})";
}
