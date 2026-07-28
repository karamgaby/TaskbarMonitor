using TaskbarMonitor.Interop;

namespace TaskbarMonitor.Sensors;

/// <summary>
/// CPU % from PDH "\Processor Information(_Total)\% Processor Utility" — the counter Task Manager
/// shows. It is frequency-weighted, which matters on a boosting/parking 14900KF where
/// "% Processor Time" diverges by 20+ points. Falls back to % Processor Time if unavailable.
/// </summary>
public sealed class CpuUtilizationPdh : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    private bool _primed;
    public bool UsingUtilityCounter { get; private set; }

    public CpuUtilizationPdh()
    {
        if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            Log.Error("PdhOpenQuery failed; CPU% unavailable");
            return;
        }

        if (NativeMethods.PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _counter) == 0)
        {
            UsingUtilityCounter = true;
        }
        else if (NativeMethods.PdhAddEnglishCounterW(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _counter) == 0)
        {
            UsingUtilityCounter = false;
            Log.Warn("% Processor Utility unavailable; using % Processor Time (will read lower than Task Manager). Remedy for corrupt counters: elevated 'lodctr /r'");
        }
        else
        {
            Log.Error("No CPU PDH counter could be added; CPU% unavailable");
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }
        Log.Info($"CPU%% source: {(UsingUtilityCounter ? "% Processor Utility" : "% Processor Time")}");
    }

    public double? Sample()
    {
        if (_query == IntPtr.Zero) return null;
        if (NativeMethods.PdhCollectQueryData(_query) != 0) return null;
        if (!_primed)
        {
            _primed = true; // rate counters need two collections
            return null;
        }
        if (NativeMethods.PdhGetFormattedCounterValue(_counter, NativeMethods.PDH_FMT_DOUBLE, IntPtr.Zero, out var value) != 0)
            return null;
        if (value.CStatus != NativeMethods.PDH_CSTATUS_VALID_DATA && value.CStatus != NativeMethods.PDH_CSTATUS_NEW_DATA)
            return null;
        return Math.Clamp(value.doubleValue, 0, 100); // Utility can exceed 100 above nominal clock
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
