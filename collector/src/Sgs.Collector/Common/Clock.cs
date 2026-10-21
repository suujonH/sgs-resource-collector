using System.Globalization;

namespace Sgs.Collector.Common;

internal static class Clock
{
    public static string NowIso() => FormatIso(DateTimeOffset.UtcNow);

    public static string FormatIso(DateTimeOffset v)
    {
        return v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset ParseIso(string s)
    {
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
