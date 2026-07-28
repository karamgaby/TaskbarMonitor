using System.Runtime.InteropServices;

namespace TaskbarMonitor.Interop;

internal static partial class NativeMethods
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    /// <summary>English counter path — locale-proof, unlike localized PerformanceCounter names.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr hQuery);
}
