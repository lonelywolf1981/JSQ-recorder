using JSQ.Core.Models;
using Xunit;

namespace JSQ.Tests;

/// <summary>
/// Тесты модели ChannelDefinition
/// </summary>
public class ChannelDefinitionTests
{
    [Fact]
    public void ChannelDefinition_Properties_SetAndGet()
    {
        // Arrange
        var channel = new ChannelDefinition();
        
        // Act
        channel.Index = 0;
        channel.RawCode = "v000";
        channel.Name = "A-Pc";
        channel.Unit = "bara";
        channel.Description = "Discharge Pressure";
        channel.Group = ChannelGroup.PostA;
        channel.Type = ChannelType.Pressure;
        channel.MinLimit = 0;
        channel.MaxLimit = 30;
        channel.Enabled = true;
        
        // Assert
        Assert.Equal(0, channel.Index);
        Assert.Equal("v000", channel.RawCode);
        Assert.Equal("A-Pc", channel.Name);
        Assert.Equal("bara", channel.Unit);
        Assert.Equal("Discharge Pressure", channel.Description);
        Assert.Equal(ChannelGroup.PostA, channel.Group);
        Assert.Equal(ChannelType.Pressure, channel.Type);
        Assert.Equal(0, channel.MinLimit);
        Assert.Equal(30, channel.MaxLimit);
        Assert.True(channel.Enabled);
    }
    
    [Fact]
    public void ChannelDefinition_ToString_ReturnsNameAndUnit()
    {
        // Arrange
        var channel = new ChannelDefinition { Name = "A-Pc", Unit = "bara" };
        
        // Act
        var result = channel.ToString();
        
        // Assert
        Assert.Equal("A-Pc (bara)", result);
    }
}
