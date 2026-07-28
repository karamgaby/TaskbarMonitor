using System.Globalization;

namespace TaskbarMonitor.Formatting;

/// <summary>
/// Fixed-width formatters: every method returns a constant-length string for any input,
/// so the rendered line never changes width between refreshes.
/// </summary>
public static class UnitFormatter
{
    /// <summary>Always 10 chars: "999.9 KB/s", " --.- KB/s". Binary divisors, one decimal, KB/s floor.</summary>
    public static string FormatRate(double? bps)
    {
        if (bps is null || bps.Value < 0 || double.IsNaN(bps.Value))
            return " --.- KB/s";

        double v = bps.Value;
        double scaled;
        string unit;
        if (v < 999.95 * 1024) { scaled = v / 1024.0; unit = "KB/s"; }
        else if (v < 999.95 * 1024 * 1024) { scaled = v / (1024.0 * 1024.0); unit = "MB/s"; }
        else { scaled = Math.Min(v / (1024.0 * 1024.0 * 1024.0), 999.9); unit = "GB/s"; }
        return string.Create(CultureInfo.InvariantCulture, $"{scaled,5:F1} {unit}");
    }

    /// <summary>Always 3 chars, clamped 0-100: " 34", "100", " --".</summary>
    public static string FormatPercent(double? percent)
    {
        if (percent is null || double.IsNaN(percent.Value)) return " --";
        double p = Math.Clamp(percent.Value, 0, 100);
        return string.Create(CultureInfo.InvariantCulture, $"{p,3:F0}");
    }

    /// <summary>Always 5 chars: " 58°C", "100°C", " --°C".</summary>
    public static string FormatTemp(double? celsius)
    {
        if (celsius is null || double.IsNaN(celsius.Value)) return " --°C";
        double t = Math.Clamp(celsius.Value, -99, 999);
        return string.Create(CultureInfo.InvariantCulture, $"{t,3:F0}°C");
    }
}
