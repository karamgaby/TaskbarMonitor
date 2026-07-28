using System.Runtime.InteropServices;
using TaskbarMonitor.Interop;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor;

/// <summary>
/// One borderless strip per taskbar. WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST:
/// never takes focus, no Alt+Tab entry, no taskbar button. Right-click opens the menu;
/// left-clicks are swallowed.
///
/// The strip is a per-pixel-alpha layered window: it has no panel of its own, so the taskbar's
/// acrylic shows through and the text looks painted directly onto it. All painting goes through
/// <see cref="LayeredSurface"/> / UpdateLayeredWindow — WM_PAINT is unused.
///
/// Never set Opacity, TransparencyKey or AllowTransparency on this form: WinForms would then drive
/// the layer via SetLayeredWindowAttributes, which cannot be mixed with UpdateLayeredWindow, and
/// the strip would go blank.
/// </summary>
public sealed class OverlayWindow : Form
{
    private static readonly uint TaskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

    public event Action? ExplorerRestarted;
    public event Action? DisplayOrSettingsChanged;
    public event Action? AppearanceInvalidated;
    public event Action? ExitRequested;
    public event Action? ReloadRequested;
    public event Action? OpenSettingsRequested;
    public event Action? SettingsWindowRequested;

    private readonly ContextMenuStrip _menu;
    private readonly LayeredSurface _surface = new();
    private string _line = "";
    private Font _font = null!;
    private StripPalette _palette;
    private SizeF _textSize;
    private bool _shadow;

    public Size ContentSize { get; private set; }

    public OverlayWindow(AppSettings settings)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1, 1);
        // The controller owns our geometry; PerMonitorV2 auto-scaling would fight SyncStrips
        AutoScaleMode = AutoScaleMode.None;
        // No double buffer: nothing is ever shown from the WM_PAINT back buffer
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        // Stock chrome on purpose. A custom ToolStripRenderer + ShowImageMargin=false was tried here
        // to make the menu follow dark mode (see StripMenuRenderer), and the menu stopped responding
        // to mouse clicks on its items while keyboard navigation kept working — i.e. mouse input was
        // not reaching the drop-down at all. Not diagnosed yet, and this menu is the app's only UI,
        // so looking right is not worth it being unusable. Do not re-wire without verifying clicks.
        _menu = new ContextMenuStrip();
        _menu.Items.Add("&Settings…", null, (_, _) => SettingsWindowRequested?.Invoke());

        // The file items still matter — the window deliberately does not expose pollIntervalMs,
        // sensorIntervalMs or logging, and the docs point here for those — but a real settings UI
        // outranks them, so they move one level down instead of competing at the top.
        var advanced = new ToolStripMenuItem("&Advanced");
        advanced.DropDownItems.Add("&Open settings file", null, (_, _) => OpenSettingsRequested?.Invoke());
        advanced.DropDownItems.Add("&Reload settings", null, (_, _) => ReloadRequested?.Invoke());
        advanced.DropDownItems.Add(new ToolStripSeparator());
        advanced.DropDownItems.Add(new ToolStripMenuItem(
            $"TaskbarMonitor {typeof(OverlayWindow).Assembly.GetName().Version}") { Enabled = false });
        _menu.Items.Add(advanced);

        // Shown whenever the CPU package temp cannot be read, naming the actual cause: elevation and
        // the PawnIO driver are independent, and "needs admin" is simply wrong on an elevated machine
        // with no driver. ElevationInfo already computes both; only the log ever saw the difference.
        string? tempHint = CpuTempUnavailableHint();
        if (tempHint is not null)
        {
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem(tempHint) { Enabled = false });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("E&xit", null, (_, _) => ExitRequested?.Invoke());

        ApplyAppearance(settings);
    }

    /// <summary>
    /// Null when the CPU package temperature should be readable. Elevation and the PawnIO driver are
    /// separate prerequisites and either can be the one that is missing.
    /// </summary>
    private static string? CpuTempUnavailableHint()
    {
        bool elevated = ElevationInfo.IsElevated;
        bool driver = ElevationInfo.PawnIoState() == "installed";
        return (elevated, driver) switch
        {
            (true, true) => null,
            (false, true) => "CPU temp needs admin — run via the installed task",
            (true, false) => "CPU temp needs the PawnIO driver — pawnio.eu",
            (false, false) => "CPU temp needs admin and the PawnIO driver — pawnio.eu",
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                        | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // UIPI blocks the TaskbarCreated broadcast from medium-IL explorer to this elevated process
        NativeMethods.ChangeWindowMessageFilterEx(Handle, TaskbarCreatedMsg, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);

        // Rounding would trace a faint DWM frame line around an otherwise invisible window
        int pref = NativeMethods.DWMWCP_DONOTROUND;
        _ = NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        int borderColor = unchecked((int)NativeMethods.DWMWA_COLOR_NONE);
        _ = NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

        Render(); // define the surface before the window can be shown
    }

    public void ApplyAppearance(AppSettings settings)
    {
        _font?.Dispose();
        float px = settings.Appearance.FontSizePt * DeviceDpi / 72f;
        // GraphicsUnit.Pixel is load-bearing: the render Graphics comes from a 96-DPI bitmap, so a
        // Point-sized font would silently ignore DeviceDpi.
        Font? font = null;
        try { font = new Font(settings.Appearance.FontName, px, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { }
        if (font is null || !string.Equals(font.Name, settings.Appearance.FontName, StringComparison.OrdinalIgnoreCase))
        {
            font?.Dispose();
            font = new Font(FontFamily.GenericMonospace, px, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        _font = font;

        _palette = ThemeWatcher.Resolve(settings.Appearance);
        _shadow = settings.Appearance.TextShadow;

        // Measure with the same engine and hints that Draw uses, or the text drifts inside the strip
        _textSize = MeasureWidestSample(settings);

        int padX = Math.Max(6, 8 * DeviceDpi / 96);
        ContentSize = new Size(
            (int)Math.Ceiling(_textSize.Width) + 2 * padX,
            (int)Math.Ceiling(_textSize.Height) + Math.Max(4, 6 * DeviceDpi / 96));
        Render();
    }

    private SizeF MeasureWidestSample(AppSettings settings)
    {
        string sample = LineComposer.WidestSample(settings);
        if (_surface.Graphics is not null)
            return StripRenderer.MeasureLine(_surface.Graphics, sample, _font);

        // Before the surface exists (constructor path), measure on a throwaway 96-DPI surface
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        return StripRenderer.MeasureLine(g, sample, _font);
    }

    public void UpdateLine(string line)
    {
        if (line == _line) return;
        _line = line;
        Render();
    }

    /// <summary>
    /// Sizes the layered surface to the live window rect — not ContentSize, which only constrains
    /// the width (TaskbarLocator stretches the strip to the full taskbar height).
    /// </summary>
    private void Render()
    {
        if (!IsHandleCreated || _font is null) return;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (!_surface.Resize(size.Width, size.Height)) return;

        StripRenderer.Draw(_surface.Graphics!, _line, _font, size, _textSize, _palette, _shadow);
        _surface.Commit(Handle);
    }

    // Painting goes through UpdateLayeredWindow; anything drawn here would be discarded.
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Render();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Render();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }
        if (m.Msg == TaskbarCreatedMsg && TaskbarCreatedMsg != 0)
        {
            ExplorerRestarted?.Invoke();
        }
        else if (m.Msg == NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            AppearanceInvalidated?.Invoke();
        }
        else if (m.Msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_SETTINGCHANGE)
        {
            // "ImmersiveColorSet" is the shell's accent/light-dark signal; it needs an immediate
            // recolor, not the 2 s redock debounce the geometry path uses.
            if (m.Msg == NativeMethods.WM_SETTINGCHANGE && IsImmersiveColorSet(m.LParam))
                AppearanceInvalidated?.Invoke();
            else
                DisplayOrSettingsChanged?.Invoke();
        }
        else if (m.Msg == NativeMethods.WM_RBUTTONUP)
        {
            // Reverted to right-click-only. Opening the menu from WM_LBUTTONUP as well (for
            // discoverability — the strip has no other affordance) left the menu unresponsive to
            // mouse clicks on its items while keyboard navigation still worked. A WM_SETCURSOR
            // handler returning IDC_HAND went in at the same time. Neither is obviously capable of
            // that, so the cause is not yet known; this is the configuration known to work.
            // See MenuDiagnostics below before trying again.
            ShowMenu();
            return;
        }
        else if (m.Msg == NativeMethods.WM_LBUTTONDOWN)
        {
            return; // swallow: never take focus from the taskbar
        }
        base.WndProc(ref m);
    }

    private void ShowMenu()
    {
        // Non-activating windows can't dismiss menus on outside clicks without this
        NativeMethods.SetForegroundWindow(Handle);
        _menu.Show(Cursor.Position);
    }

    private static bool IsImmersiveColorSet(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;
        try { return Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"; }
        catch { return false; }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        DisplayOrSettingsChanged?.Invoke();
    }

    /// <summary>
    /// The font is built manually in pixel units, so WinForms scaling won't resize it — a DPI change
    /// has to rebuild it. (OnDpiChangedAfterParent never fires for a top-level form.)
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        AppearanceInvalidated?.Invoke();
        DisplayOrSettingsChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
            _font?.Dispose();
            _surface.Dispose();
        }
        base.Dispose(disposing);
    }
}
