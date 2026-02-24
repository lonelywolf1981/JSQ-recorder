using System;
using System.Linq;
using JSQ.Core.Models;
using JSQ.Rules;
using Xunit;

namespace JSQ.Tests;

public class AnomalyDetectorTests
{
    [Fact]
    public void CheckTimeouts_NoDataThenDataRestored_GeneratesRecoveryEvent()
    {
        var detector = new AnomalyDetector("exp-1");
        detector.LoadRules(new[]
        {
            new AnomalyRule
            {
                ChannelIndex = 1,
                ChannelName = "A-T1",
                NoDataTimeoutSec = 10,
                DebounceCount = 1,
                Enabled = true
            }
        });

        var t0 = DateTime.Now;
        _ = detector.CheckValue(1, 25.0, t0).ToList();

        var noDataEvents = detector.CheckTimeouts(t0.AddSeconds(11)).ToList();
        var noData = Assert.Single(noDataEvents);
        Assert.Equal(AnomalyType.NoData, noData.AnomalyType);
        Assert.Equal("Critical", noData.Severity);

        // DataRestored генерируется немедленно в CheckValue (не ждём следующего CheckTimeouts)
        var restoredEvents = detector.CheckValue(1, 25.1, t0.AddSeconds(12)).ToList();
        var restored = Assert.Single(restoredEvents);
        Assert.Equal(AnomalyType.DataRestored, restored.AnomalyType);
        Assert.Equal("Info", restored.Severity);

        // CheckTimeouts не должен генерировать дубль DataRestored
        var duplicates = detector.CheckTimeouts(t0.AddSeconds(12)).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void CheckValue_MinViolationThenBackInRange_GeneratesLimitsRestored()
    {
        var detector = new AnomalyDetector("exp-2");
        detector.LoadRules(new[]
        {
            new AnomalyRule
            {
                ChannelIndex = 2,
                ChannelName = "A-Pe",
                MinLimit = 0,
                MaxLimit = 10,
                DebounceCount = 1,
                Enabled = true
            }
        });

        var violationEvents = detector.CheckValue(2, -1, DateTime.Now).ToList();
        var violation = Assert.Single(violationEvents);
        Assert.Equal(AnomalyType.MinViolation, violation.AnomalyType);
        Assert.Equal("Warning", violation.Severity);

        var restoredEvents = detector.CheckValue(2, 5, DateTime.Now).ToList();
        var restored = Assert.Single(restoredEvents);
        Assert.Equal(AnomalyType.LimitsRestored, restored.AnomalyType);
        Assert.Equal("Info", restored.Severity);
    }

    [Fact]
    public void CheckValue_MaxViolationThenBackInRange_GeneratesLimitsRestored()
    {
        var detector = new AnomalyDetector("exp-3");
        detector.LoadRules(new[]
        {
            new AnomalyRule
            {
                ChannelIndex = 3,
                ChannelName = "A-Pc",
                MinLimit = 0,
                MaxLimit = 10,
                DebounceCount = 1,
                Enabled = true
            }
        });

        var violationEvents = detector.CheckValue(3, 12, DateTime.Now).ToList();
        var violation = Assert.Single(violationEvents);
        Assert.Equal(AnomalyType.MaxViolation, violation.AnomalyType);
        Assert.Equal("Warning", violation.Severity);

        var restoredEvents = detector.CheckValue(3, 7, DateTime.Now).ToList();
        var restored = Assert.Single(restoredEvents);
        Assert.Equal(AnomalyType.LimitsRestored, restored.AnomalyType);
        Assert.Equal("Info", restored.Severity);
    }
}
