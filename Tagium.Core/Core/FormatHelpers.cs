using System.Globalization;

namespace Tagium.Core;

public static class FormatHelpers
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats a byte count as a short human-readable string, e.g. "1.5 MB".
    /// Uses the invariant culture so output is stable across machines.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {SizeUnits[unit]}";
    }

    /// <summary>
    /// Formats a unix-seconds timestamp as "yyyy-MM-dd HH:mm" in local time.
    /// </summary>
    public static string FormatUnix(long unixSeconds)
    {
        DateTime local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
