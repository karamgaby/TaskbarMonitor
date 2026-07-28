using System.Runtime.InteropServices;
using TaskbarMonitor.Interop;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor;

/// <summary>
/// One borderless strip per taskbar. WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST:
/// never takes focus, no Alt+Tab entry, no taskbar button. Right-click opens the menu;
/// left-clicks are swallowed.
/// </summary>
public sealed class OverlayWindow : Form
{
    private static readonly uint TaskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

    public event Action? ExplorerRestarted;
    public event Action? DisplayOrSettingsChanged;
    public event Action? ExitRequested;
    public event Action? ReloadRequested;
    public event Action? OpenSettingsRequested;

    private readonly ContextMenuStrip _menu;
    private string _line = "";
    private Font _font = null!;
    private Color _bg, _text;

    public Size ContentSize { get; private set; }

    public OverlayWindow(AppSettings settings)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1, 1);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open settings file", null, (_, _) => OpenSettingsRequested?.Invoke());
        _menu.Items.Add("Reload settings", null, (_, _) => ReloadRequested?.Invoke());
        _menu.Items.Add(new ToolStripSeparator());
        if (!ElevationInfo.IsElevated)
            _menu.Items.Add(new ToolStripMenuItem("CPU temp needs admin (run via installed task)") { Enabled = false });
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        ApplyAppearance(settings);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // UIPI blocks the TaskbarCreated broadcast from medium-IL explorer to this elevated process
        NativeMethods.ChangeWindowMessageFilterEx(Handle, TaskbarCreatedMsg, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
        int pref = 3; // DWMWCP_ROUNDSMALL — subtle pill look to match Win11
        _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    public void ApplyAppearance(AppSettings settings)
    {
        _font?.Dispose();
        float px = settings.Appearance.FontSizePt * DeviceDpi / 72f;
        Font? font = null;
        try { font = new Font(settings.Appearance.FontName, px, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { }
        if (font is null || !string.Equals(font.Name, settings.Appearance.FontName, StringComparison.OrdinalIgnoreCase))
            font ??= new Font(FontFamily.GenericMonospace, px, FontStyle.Regular, GraphicsUnit.Pixel);
        _font = font;

        (_bg, _text, _) = ThemeWatcher.Resolve(settings.Appearance);
        BackColor = _bg;

        int padX = Math.Max(6, 8 * DeviceDpi / 96);
        var textSize = TextRenderer.MeasureText(LineComposer.WidestSample(settings), _font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        ContentSize = new Size(textSize.Width + 2 * padX, textSize.Height + Math.Max(4, 6 * DeviceDpi / 96));
        Invalidate();
    }

    public void UpdateLine(string line)
    {
        if (line == _line) return;
        _line = line;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_bg);
        TextRenderer.DrawText(e.Graphics, _line, _font, ClientRectangle, _text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
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
        else if (m.Msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_SETTINGCHANGE)
        {
            DisplayOrSettingsChanged?.Invoke();
        }
        else if (m.Msg == NativeMethods.WM_RBUTTONUP)
        {
            // Non-activating windows can't dismiss menus on outside clicks without this
            NativeMethods.SetForegroundWindow(Handle);
            _menu.Show(Cursor.Position);
            return;
        }
        else if (m.Msg == NativeMethods.WM_LBUTTONDOWN)
        {
            return; // swallow
        }
        base.WndProc(ref m);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        DisplayOrSettingsChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
            _font?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
