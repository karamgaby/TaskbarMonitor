using System.Diagnostics;

namespace TaskbarMonitor.Sensors;

/// <summary>Fallback GPU source when LHM enumerates no NVIDIA hardware (or its sensors go dark).</summary>
public static class NvidiaSmiFallback
{
    private const string ExePath = @"C:\Windows\System32\nvidia-smi.exe";
    private static bool _missingLogged;

    public static (double? Load, double? Temp) Read()
    {
        try
        {
            if (!File.Exists(ExePath))
            {
                if (!_missingLogged) { _missingLogged = true; Log.Warn("nvidia-smi.exe not found; GPU metrics unavailable"); }
                return (null, null);
            }

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return (null, null);

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(1500))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (null, null);
            }

            var parts = output.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2
                && double.TryParse(parts[0], out double load)
                && double.TryParse(parts[1].Split('\n')[0], out double temp))
                return (load, temp);
        }
        catch (Exception ex)
        {
            if (!_missingLogged) { _missingLogged = true; Log.Error("nvidia-smi query failed", ex); }
        }
        return (null, null);
    }
}
