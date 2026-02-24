using System;
using System.Globalization;

namespace JSQ.Core.Models;

public static class JsqClock
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(5);

    public static DateTime Now => DateTime.UtcNow + Offset;

    public static string NowIso() => Now.ToString("O", CultureInfo.InvariantCulture);
}
