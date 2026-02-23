using System;
using System.Linq;
using JSQ.Capture;
using JSQ.Core.Models;
using Xunit;

namespace JSQ.Tests;

/// <summary>
/// Тесты TCP Capture сервиса
/// </summary>
public class TcpCaptureServiceTests : IDisposable
{
    private readonly TcpCaptureService _service;
    
    public TcpCaptureServiceTests()
    {
        _service = new TcpCaptureService();
    }
    
    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        // Act & Assert
        Assert.NotNull(_service);
        Assert.Equal(ConnectionStatus.Disconnected, _service.Status);
        Assert.NotNull(_service.AvailableInterfaces);
    }
    
    [Fact]
    public void AvailableInterfaces_NotEmpty_OnSystemWithNetworkInterfaces()
    {
        // Act
        var interfaces = _service.AvailableInterfaces;
        
        // Assert
        Assert.NotEmpty(interfaces);
        Assert.All(interfaces, i =>
        {
            Assert.False(string.IsNullOrEmpty(i.Id));
            Assert.False(string.IsNullOrEmpty(i.Name));
        });
    }
    
    [Fact]
    public void GetInterfacesAsync_ReturnsSameAsProperty()
    {
        // Act
        var propertyInterfaces = _service.AvailableInterfaces;
        var methodInterfaces = _service.GetInterfacesAsync().Result;
        
        // Assert
        Assert.Equal(propertyInterfaces.Count, methodInterfaces.Count);
    }
    
    [Fact]
    public void Statistics_InitialState_HasZeroValues()
    {
        // Act
        var stats = _service.Statistics;
        
        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0UL, stats.TotalBytesReceived);
        Assert.Equal(0UL, stats.TotalPacketsReceived);
        Assert.Equal(ConnectionStatus.Disconnected, stats.Status);
    }
    
    [Fact]
    public void Disconnect_WhenNotConnected_DoesNotThrow()
    {
        // Act & Assert
        var exception = Record.Exception(() => _service.DisconnectAsync().Wait());
        Assert.Null(exception);
    }
    
    public void Dispose()
    {
        _service.Dispose();
    }
}
