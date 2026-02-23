using System;

namespace JSQ.Core.Models;

/// <summary>
/// Информация о сетевом интерфейсе для захвата
/// </summary>
public class NetworkInterfaceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public bool IsUp { get; set; }
    public bool SupportsMulticast { get; set; }
    
    public override string ToString() => $"{Name} ({IpAddress})";
}

/// <summary>
/// Статус подключения к передатчику
/// </summary>
public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

/// <summary>
/// Статистика TCP захвата
/// </summary>
public class CaptureStatistics
{
    public ulong TotalBytesReceived { get; set; }
    public ulong TotalPacketsReceived { get; set; }
    public ulong BytesPerSecond { get; set; }
    public ulong PacketsPerSecond { get; set; }
    
    public uint RetransmitCount { get; set; }
    public uint GapCount { get; set; }
    public uint OutOfOrderCount { get; set; }
    
    public DateTime LastPacketTime { get; set; }
    public TimeSpan LastLatency { get; set; }
    
    public ConnectionStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}
