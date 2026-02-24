using System;

namespace JSQ.Core.Models;

public static class AlertStatusPolicy
{
    public static bool IsStale(DateTime lastUpdateTime, DateTime now, TimeSpan staleThreshold)
    {
        if (lastUpdateTime == default)
        {
            return true;
        }

        return (now - lastUpdateTime) > staleThreshold;
    }

    public static HealthStatus ResolveAfterStop(
        DateTime lastUpdateTime,
        double? currentValue,
        DateTime now,
        TimeSpan staleThreshold)
    {
        if (!currentValue.HasValue || IsStale(lastUpdateTime, now, staleThreshold))
        {
            return HealthStatus.NoData;
        }

        return HealthStatus.OK;
    }
}
