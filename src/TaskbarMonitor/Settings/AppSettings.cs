using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarMonitor.Settings;

public sealed class AppSettings
{
    public int PollIntervalMs { get; set; } = 1000;       // network / CPU% / RAM cadence
    public int SensorIntervalMs { get; set; } = 2500;     // LHM (MSR temps) + GPU cadence
    public MetricsSettings Metrics { get; set; } = new();
    public PositioningSettings Positioning { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}

public sealed class MetricsSettings
{
    public NetworkMetric Network { get; set; } = new();
    public CpuMetric Cpu { get; set; } = new();
    public RamMetric Ram { get; set; } = new();
    public GpuMetric Gpu { get; set; } = new();

    public sealed class NetworkMetric { public bool Enabled { get; set; } = true; public bool ShowUpload { get; set; } = true; }
    public sealed class CpuMetric { public bool Enabled { get; set; } = true; public bool ShowTemp { get; set; } = true; }
    public sealed class RamMetric { public bool Enabled { get; set; } = true; }
    public sealed class GpuMetric { public bool Enabled { get; set; } = true; public bool ShowTemp { get; set; } = true; }
}

public sealed class PositioningSettings
{
    public int RightMarginPx { get; set; } = 8;           // gap between strip and TrayNotifyWnd
    public int TrayReservePx { get; set; } = 260;         // primary fallback when TrayNotifyWnd not found
    public int SecondaryTrayReservePx { get; set; } = 220; // clock area on secondary taskbars
    public int VerticalOffsetPx { get; set; } = 0;
    public int ExtraLeftMarginPx { get; set; } = 0;       // shove the strip further left of the tray
}

public sealed class AppearanceSettings
{
    public string FontName { get; set; } = "Consolas";
    public float FontSizePt { get; set; } = 12.0f;
    public string Theme { get; set; } = "auto";           // auto | dark | light
    public string? BackgroundOverride { get; set; }       // "#RRGGBB"
    public string? TextOverride { get; set; }
}

public sealed class BehaviorSettings
{
    public bool HideOnFullscreen { get; set; } = true;
    public int WatchdogIntervalMs { get; set; } = 2000;
    public bool MultiMonitor { get; set; } = true;        // one strip per taskbar
}

public sealed class NetworkSettings
{
    public string? AdapterOverride { get; set; }          // interface alias, e.g. "Ethernet"
    public int AdapterRefreshSec { get; set; } = 5;
}

public sealed class LoggingSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxSizeKb { get; set; } = 512;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
