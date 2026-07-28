using TaskbarMonitor.Interop;

namespace TaskbarMonitor.Sensors;

public static class MemoryStatus
{
    /// <summary>Task Manager's "In use" percentage (dwMemoryLoad).</summary>
    public static double? Percent()
    {
        var status = NativeMethods.MEMORYSTATUSEX.Create();
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : null;
    }
}
