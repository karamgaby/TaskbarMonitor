using System.Security.Principal;
using Microsoft.Win32;

namespace TaskbarMonitor;

public static class ElevationInfo
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>PawnIO is the signed kernel driver LHM uses for MSR reads (CPU package temp).</summary>
    public static string PawnIoState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is null ? "not installed" : "installed";
        }
        catch { return "unknown"; }
    }
}
