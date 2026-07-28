using TaskbarMonitor.Interop;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.Positioning;

public sealed record TaskbarInfo(IntPtr Hwnd, Rectangle Bounds, Rectangle? NotifyBounds, bool IsPrimary);

/// <summary>
/// Finds every taskbar. Primary: Shell_TrayWnd + TrayNotifyWnd child (verified present on build
/// 26200). Secondary monitors: Shell_SecondaryTrayWnd. Fallbacks: SHAppBarMessage, then the
/// primary monitor bounds-minus-workarea strip. Never SetParent — overlay only.
/// </summary>
public static class TaskbarLocator
{
    public static List<TaskbarInfo> LocateAll(bool multiMonitor)
    {
        var bars = new List<TaskbarInfo>();

        IntPtr hTray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (hTray != IntPtr.Zero
            && NativeMethods.GetWindowRect(hTray, out var trayRect)
            && IsPlausibleBar(trayRect.ToRectangle()))
        {
            Rectangle? notify = null;
            IntPtr hNotify = NativeMethods.FindWindowExW(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (hNotify != IntPtr.Zero && NativeMethods.GetWindowRect(hNotify, out var notifyRect))
            {
                var nr = notifyRect.ToRectangle();
                if (nr.Width > 0 && nr.Height > 0 && trayRect.ToRectangle().IntersectsWith(nr))
                    notify = nr;
            }
            bars.Add(new TaskbarInfo(hTray, trayRect.ToRectangle(), notify, IsPrimary: true));
        }
        else if (TryAppBarFallback(out var fallbackRect) || TryWorkAreaFallback(out fallbackRect))
        {
            bars.Add(new TaskbarInfo(IntPtr.Zero, fallbackRect, null, IsPrimary: true));
        }

        if (multiMonitor)
        {
            IntPtr h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowExW(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(h)
                    && NativeMethods.GetWindowRect(h, out var r)
                    && IsPlausibleBar(r.ToRectangle()))
                    bars.Add(new TaskbarInfo(h, r.ToRectangle(), null, IsPrimary: false));
            }
        }

        return bars;
    }

    public static Rectangle ComputeStripRect(TaskbarInfo bar, Size content, PositioningSettings pos)
    {
        int right = bar.NotifyBounds is { } n
            ? n.Left - pos.RightMarginPx
            : bar.Bounds.Right - (bar.IsPrimary ? pos.TrayReservePx : pos.SecondaryTrayReservePx);
        right -= pos.ExtraLeftMarginPx;

        // Full taskbar height; text stays vertically centered by the renderer
        int height = Math.Max(16, bar.Bounds.Height);
        int top = bar.Bounds.Top + pos.VerticalOffsetPx;
        return new Rectangle(right - content.Width, top, content.Width, height);
    }

    public static bool IsAutoHideEnabled()
    {
        var abd = new NativeMethods.APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        return ((long)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd) & NativeMethods.ABS_AUTOHIDE) != 0;
    }

    /// <summary>With auto-hide, the bar slides off-screen leaving a sliver; ≤8 px visible = hidden.</summary>
    public static bool IsBarSlidAway(TaskbarInfo bar)
    {
        if (bar.Hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(bar.Hwnd, out var r)) return false;
        var rect = r.ToRectangle();
        var screen = Screen.FromRectangle(rect).Bounds;
        var visible = Rectangle.Intersect(rect, screen);
        return Math.Min(visible.Height, visible.Width) <= 8;
    }

    private static bool IsPlausibleBar(Rectangle r)
        => r.Width > 0 && r.Height > 0
           && (r.Height is >= 20 and <= 160 || r.Width is >= 20 and <= 160); // horizontal or vertical bar

    private static bool TryAppBarFallback(out Rectangle rect)
    {
        rect = default;
        var abd = new NativeMethods.APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        if ((long)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref abd) == 0) return false;
        rect = abd.rc.ToRectangle();
        return IsPlausibleBar(rect);
    }

    private static bool TryWorkAreaFallback(out Rectangle rect)
    {
        rect = default;
        var screen = Screen.PrimaryScreen;
        if (screen is null) return false;
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        if (work.Bottom < bounds.Bottom) // taskbar at bottom
        {
            rect = Rectangle.FromLTRB(bounds.Left, work.Bottom, bounds.Right, bounds.Bottom);
            return true;
        }
        return false;
    }
}
