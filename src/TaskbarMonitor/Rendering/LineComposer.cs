using System.Text;
using TaskbarMonitor.Formatting;
using TaskbarMonitor.Sensors;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.Rendering;

/// <summary>
/// Builds the display line. With a monospace font and fixed-width formatters the string length is
/// constant for a given metric configuration, so the strip never jitters.
/// </summary>
public static class LineComposer
{
    public const string Separator = " │ "; // │

    public static string Compose(SensorSnapshot s, AppSettings settings)
    {
        var m = settings.Metrics;
        var parts = new List<string>(4);

        if (m.Network.Enabled)
        {
            var sb = new StringBuilder();
            sb.Append('↓').Append(' ').Append(UnitFormatter.FormatRate(s.DownBps)); // ↓
            if (m.Network.ShowUpload)
                sb.Append("  ↑ ").Append(UnitFormatter.FormatRate(s.UpBps));        // ↑
            parts.Add(sb.ToString());
        }
        if (m.Cpu.Enabled)
            parts.Add($"CPU {UnitFormatter.FormatPercent(s.CpuPercent)}%{(m.Cpu.ShowTemp ? " " + UnitFormatter.FormatTemp(s.CpuTempC) : "")}");
        if (m.Ram.Enabled)
            parts.Add($"RAM {UnitFormatter.FormatPercent(s.RamPercent)}%");
        if (m.Gpu.Enabled)
            parts.Add($"GPU {UnitFormatter.FormatPercent(s.GpuPercent)}%{(m.Gpu.ShowTemp ? " " + UnitFormatter.FormatTemp(s.GpuTempC) : "")}");

        return string.Join(Separator, parts);
    }

    /// <summary>Widest possible line for the current config — used once per (font, DPI) to size the strip.</summary>
    public static string WidestSample(AppSettings settings)
        => Compose(new SensorSnapshot
        {
            DownBps = 999.9 * 1024 * 1024,
            UpBps = 999.9 * 1024 * 1024,
            CpuPercent = 100, CpuTempC = 100,
            RamPercent = 100,
            GpuPercent = 100, GpuTempC = 100,
        }, settings);
}
