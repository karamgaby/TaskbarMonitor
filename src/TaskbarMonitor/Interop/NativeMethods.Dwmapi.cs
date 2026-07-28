using System.Runtime.InteropServices;

namespace TaskbarMonitor.Interop;

internal static partial class NativeMethods
{
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUNDSMALL = 3;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Current DWM colorization (accent) as 0xAARRGGBB. Fallback when AccentPalette is unreadable.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetColorizationColor(out uint pcrColorization,
        [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);
}
