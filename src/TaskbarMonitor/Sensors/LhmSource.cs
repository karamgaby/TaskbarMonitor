using LibreHardwareMonitor.Hardware;

namespace TaskbarMonitor.Sensors;

/// <summary>
/// LibreHardwareMonitorLib wrapper: CPU package temp (PawnIO/MSR — needs elevation) and
/// NVIDIA GPU core load + temp. Every call is guarded; failure yields nulls, never a crash.
/// </summary>
public sealed class LhmSource : IDisposable
{
    private Computer? _computer;
    private IHardware? _cpu;
    private IHardware? _gpu;

    public bool GpuAvailable => _gpu is not null;

    public void Open()
    {
        try
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu) _cpu ??= hw;
                if (hw.HardwareType == HardwareType.GpuNvidia) _gpu ??= hw;
            }
            Log.Info($"LHM opened. CPU hw: {_cpu?.Name ?? "none"}; NVIDIA GPU hw: {_gpu?.Name ?? "none"}");
        }
        catch (Exception ex)
        {
            Log.Error("LHM Computer.Open failed; CPU temp/GPU via LHM unavailable", ex);
            _computer = null;
        }
    }

    public (double? CpuTemp, double? GpuLoad, double? GpuTemp) Read()
    {
        double? cpuTemp = null, gpuLoad = null, gpuTemp = null;

        if (_cpu is not null)
        {
            try
            {
                _cpu.Update();
                cpuTemp = FindTemp(_cpu, "CPU Package") ?? MaxCoreTemp(_cpu);
            }
            catch (Exception ex) { LogOnce(ref _cpuErrorLogged, "LHM CPU update failed", ex); }
        }

        if (_gpu is not null)
        {
            try
            {
                _gpu.Update();
                foreach (var s in _gpu.Sensors)
                {
                    if (s.SensorType == SensorType.Load && s.Name == "GPU Core") gpuLoad = s.Value;
                    else if (s.SensorType == SensorType.Temperature && s.Name == "GPU Core") gpuTemp = s.Value;
                }
            }
            catch (Exception ex) { LogOnce(ref _gpuErrorLogged, "LHM GPU update failed", ex); }
        }

        return (cpuTemp, gpuLoad, gpuTemp);
    }

    private bool _cpuErrorLogged, _gpuErrorLogged;
    private static void LogOnce(ref bool flag, string msg, Exception ex)
    {
        if (flag) return;
        flag = true;
        Log.Error(msg, ex);
    }

    private static double? FindTemp(IHardware hw, string name)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Temperature && s.Name == name && s.Value is { } v && !float.IsNaN(v))
                return v;
        return null;
    }

    private static double? MaxCoreTemp(IHardware hw)
    {
        double? max = null;
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Temperature && s.Value is { } v && !float.IsNaN(v))
                max = max is null ? v : Math.Max(max.Value, v);
        return max;
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
