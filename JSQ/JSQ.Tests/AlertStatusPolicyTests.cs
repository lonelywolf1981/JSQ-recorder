using System;
using JSQ.Core.Models;
using Xunit;

namespace JSQ.Tests;

public class AlertStatusPolicyTests
{
    [Fact]
    public void ResolveAfterStop_NoValue_ReturnsNoData()
    {
        var status = AlertStatusPolicy.ResolveAfterStop(
            DateTime.Now,
            null,
            DateTime.Now,
            TimeSpan.FromSeconds(5));

        Assert.Equal(HealthStatus.NoData, status);
    }

    [Fact]
    public void ResolveAfterStop_StaleValue_ReturnsNoData()
    {
        var now = DateTime.Now;
        var status = AlertStatusPolicy.ResolveAfterStop(
            now.AddSeconds(-10),
            12.3,
            now,
            TimeSpan.FromSeconds(5));

        Assert.Equal(HealthStatus.NoData, status);
    }

    [Fact]
    public void ResolveAfterStop_FreshValue_ReturnsOk()
    {
        var now = DateTime.Now;
        var status = AlertStatusPolicy.ResolveAfterStop(
            now.AddSeconds(-1),
            12.3,
            now,
            TimeSpan.FromSeconds(5));

        Assert.Equal(HealthStatus.OK, status);
    }
}
