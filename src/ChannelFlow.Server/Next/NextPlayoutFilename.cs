using System.Globalization;

namespace FinTv.Next;

/// <summary>
/// Builds the compact ISO 8601 file names ErsatzTV next uses to locate the playout
/// window for the current time, e.g.
/// <c>20260413T000000.000000000-0500_20260414T002131.620000000-0500.json</c>.
/// </summary>
public static class NextPlayoutFilename
{
    public static string ForWindow(DateTimeOffset start, DateTimeOffset finish)
        => $"{ToCompactIso8601(start)}_{ToCompactIso8601(finish)}.json";

    private static string ToCompactIso8601(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var hours = Math.Abs(offset.Hours).ToString("00", CultureInfo.InvariantCulture);
        var minutes = Math.Abs(offset.Minutes).ToString("00", CultureInfo.InvariantCulture);

        // Nine-digit fractional seconds (1 tick = 100 ns).
        var nano = (value.Ticks % TimeSpan.TicksPerSecond) * 100;

        return value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
            + "."
            + nano.ToString("000000000", CultureInfo.InvariantCulture)
            + sign + hours + minutes;
    }
}