namespace TaskbarMonitor.Sensors;

/// <summary>Immutable view of all displayed values; null = unavailable → rendered as fixed-width placeholder.</summary>
public sealed record SensorSnapshot
{
    public double? DownBps { get; init; }
    public double? UpBps { get; init; }
    public double? CpuPercent { get; init; }
    public double? CpuTempC { get; init; }
    public double? RamPercent { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuTempC { get; init; }
    public string? AdapterAlias { get; init; }

    public static readonly SensorSnapshot Empty = new();
}
