using System;
using JSQ.Core.Models;
using Xunit;

namespace JSQ.Tests;

/// <summary>
/// Тесты модели Experiment
/// </summary>
public class ExperimentTests
{
    [Fact]
    public void Experiment_DefaultState_IsIdle()
    {
        // Arrange
        var experiment = new Experiment();
        
        // Act & Assert
        Assert.Equal(ExperimentState.Idle, experiment.State);
    }
    
    [Fact]
    public void Experiment_Properties_SetAndGet()
    {
        // Arrange
        var experiment = new Experiment();
        
        // Act
        experiment.Id = "EXP001";
        experiment.Name = "Тестовый эксперимент";
        experiment.PartNumber = "345C";
        experiment.Operator = "op3";
        experiment.Refrigerant = "R134A";
        experiment.PostAEnabled = true;
        experiment.PostBEnabled = true;
        experiment.PostCEnabled = false;
        experiment.BatchSize = 500;
        experiment.AggregationIntervalSec = 20;
        experiment.CheckpointIntervalSec = 30;
        
        // Assert
        Assert.Equal("EXP001", experiment.Id);
        Assert.Equal("Тестовый эксперимент", experiment.Name);
        Assert.Equal("345C", experiment.PartNumber);
        Assert.Equal("op3", experiment.Operator);
        Assert.Equal("R134A", experiment.Refrigerant);
        Assert.True(experiment.PostAEnabled);
        Assert.True(experiment.PostBEnabled);
        Assert.False(experiment.PostCEnabled);
        Assert.Equal(500, experiment.BatchSize);
        Assert.Equal(20, experiment.AggregationIntervalSec);
        Assert.Equal(30, experiment.CheckpointIntervalSec);
    }
    
    [Fact]
    public void Experiment_SelectedChannelIndices_InitializedEmpty()
    {
        // Arrange
        var experiment = new Experiment();
        
        // Act & Assert
        Assert.NotNull(experiment.SelectedChannelIndices);
        Assert.Empty(experiment.SelectedChannelIndices);
    }
    
    [Fact]
    public void Experiment_Duration_RunningExperiment_ReturnsElapsed()
    {
        // Arrange
        var experiment = new Experiment
        {
            StartTime = DateTime.Now.AddSeconds(-10)
        };
        
        // Act
        var duration = experiment.Duration;
        
        // Assert
        Assert.True(duration.TotalSeconds >= 10);
    }
}
