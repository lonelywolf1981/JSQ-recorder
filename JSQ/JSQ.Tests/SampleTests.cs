using System;
using JSQ.Core.Models;
using Xunit;

namespace JSQ.Tests;

/// <summary>
/// Тесты модели Sample
/// </summary>
public class SampleTests
{
    [Fact]
    public void Sample_ValidValue_IsValidReturnsTrue()
    {
        // Arrange
        var sample = new Sample(0, 25.5, DateTime.Now);
        
        // Act & Assert
        Assert.True(sample.IsValid);
        Assert.Equal(0, sample.ChannelIndex);
        Assert.Equal(25.5, sample.Value);
    }
    
    [Theory]
    [InlineData(-99)]
    [InlineData(-99.0)]
    [InlineData(-95)]
    public void Sample_InvalidValue_IsValidReturnsFalse(double value)
    {
        // Arrange
        var sample = new Sample(0, value, DateTime.Now);
        
        // Act & Assert
        Assert.False(sample.IsValid);
    }
    
    [Fact]
    public void Sample_DefaultConstructor_CreatesValidObject()
    {
        // Arrange & Act
        var sample = new Sample();
        
        // Assert
        Assert.NotNull(sample);
    }
}
