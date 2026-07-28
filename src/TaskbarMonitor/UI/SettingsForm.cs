using System.Drawing.Drawing2D;
using System.Media;
using System.Net.NetworkInformation;
using TaskbarMonitor.Interop;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.UI;

/// <summary>
/// Field-by-field editor for settings.json, opened from the strip's context menu.
///
/// Every edit previews live on the running strips through <see cref="ISettingsHost.ApplyLive"/>;
/// nothing reaches disk until Save. Cancel, Esc, the X button and Alt+F4 all revert the strips to
/// the as-of-open snapshot.
///
/// Hand-coded with no designer or .resx, matching OverlayWindow. Anything worth unit-testing lives
/// in <see cref="SettingsDraft"/> / <see cref="SettingsFieldRules"/> instead of here — no test in
/// this repo constructs a Form.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ISettingsHost _host;
    private readonly SettingsDraft _draft;

    /// <summary>
    /// Floor on how often ApplyLive runs. ApplyLive is not free: it restarts the WinForms timers
    /// through Timer.Interval (KillTimer+SetTimer, which starves the watchdog for the length of a
    /// drag) and re-registers NotifyRouteChange2 whenever adapterOverride differs.
    /// </summary>
    private const int PreviewIntervalMs = 100;

    private readonly System.Windows.Forms.Timer _previewDebounce = new() { Interval = PreviewIntervalMs };
    private readonly ErrorProvider _errors = new();

    /// <summary>Environment.TickCount of the last ApplyLive, for the leading edge of the throttle.</summary>
    private int _lastPreviewTick = Environment.TickCount - PreviewIntervalMs;

    /// <summary>Set while seeding controls, so the ~25 change events that fires don't write back.</summary>
    private bool _seeding;
    private bool _syncingAlpha;
    private bool _fontsLoaded;

    /// <summary>File text as of the last seed, for the overwrite-conflict check at Save.</summary>
    private string? _fileTextAsOfSeed;

    // Metrics. Both temperature boxes read "Show temperature" on screen, which is right next to their
    // parent — but a screen reader announces them out of that context, so each carries an
    // AccessibleName that names the metric.
    private readonly CheckBox _netEnabled = Check("&Network");
    private readonly CheckBox _netUpload = Check("Show &upload rate", indent: true);
    private readonly CheckBox _cpuEnabled = Check("&CPU");
    private readonly CheckBox _cpuTemp = Check("Show &temperature", indent: true, accessibleName: "Show CPU temperature");
    private readonly CheckBox _ramEnabled = Check("&RAM");
    private readonly CheckBox _gpuEnabled = Check("&GPU");
    private readonly CheckBox _gpuTemp = Check("Show t&emperature", indent: true, accessibleName: "Show GPU temperature");
    private readonly Label _metricsPinHint = ReservedHint(SystemColors.GrayText);

    // Appearance
    private readonly ComboBox _fontName = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 200 };
    private readonly CheckBox _monoOnly = new() { Text = "Monospace only", AutoSize = true, Checked = true };
    // MenuHighlight rather than Firebrick: a fixed red is unreadable on a dark or high-contrast
    // theme, and this is the one warning colour in the window that was not a SystemColor.
    private readonly Label _fontWarning = ReservedHint(SystemColors.MenuHighlight);
    private readonly NumericUpDown _fontSize = Num(6m, 32m, 0.5m, decimals: 1);
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _textColorSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly TrackBar _alphaBar = new() { Minimum = 0, Maximum = 255, TickFrequency = 32, LargeChange = 16, Width = 220, AutoSize = false, Height = 32 };
    private readonly NumericUpDown _alphaNum = Num(0m, 255m, 1m);
    private readonly CheckBox _textShadow = new() { Text = "Text s&hadow", AutoSize = true };
    private readonly TextBox _bgOverride = new() { Width = 110 };
    private readonly TextBox _textOverride = new() { Width = 110 };
    private readonly Panel _bgSwatch = new() { Width = 28, Height = 20 };
    private readonly Panel _textSwatch = new() { Width = 28, Height = 20 };

    // Positioning
    private readonly NumericUpDown _rightMargin = Num(0m, 200m, 1m);
    private readonly NumericUpDown _trayReserve = Num(0m, 1000m, 10m);
    private readonly NumericUpDown _secondaryTrayReserve = Num(0m, 1000m, 10m);
    private readonly NumericUpDown _verticalOffset = Num(-200m, 200m, 1m);
    private readonly NumericUpDown _extraLeftMargin = Num(0m, 2000m, 10m);
    private readonly Label _fallbackHeader = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "Fallback — only used if the tray cannot be located", Margin = new Padding(3, 10, 3, 2) };
    private readonly Label _fallbackHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(460, 0), Margin = new Padding(3, 4, 3, 3) };

    // Behaviour
    private readonly CheckBox _hideOnFullscreen = new() { Text = "Hide while a &fullscreen app is running", AutoSize = true };
    private readonly CheckBox _multiMonitor = new() { Text = "One strip per task&bar", AutoSize = true };
    private readonly NumericUpDown _watchdogInterval = Num(1000m, 30000m, 500m);

    // Network
    private readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 220 };
    private readonly NumericUpDown _adapterRefresh = Num(1m, 300m, 1m);

    private readonly Button _reset = new() { Text = "&Reset to defaults", AutoSize = true, Margin = new Padding(3, 3, 12, 3) };
    private readonly Button _save = new() { Text = "&Save", AutoSize = true, MinimumSize = new Size(88, 0) };
    private readonly Button _cancel = new() { Text = "Close", AutoSize = true, MinimumSize = new Size(88, 0) };

    public SettingsForm(ISettingsHost host)
    {
        _host = host;
        _draft = new SettingsDraft(host.Current);

        Text = "TaskbarMonitor settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        ShowInTaskbar = true;   // the strip has no taskbar button; without this the window is unfindable
        TopMost = false;        // a window the user may leave open must not float over everything
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 780);
        MinimumSize = new Size(520, 420);
        // Deliberately the opposite of OverlayWindow's AutoScaleMode.None, which exists only because
        // SyncStrips owns the strip's geometry. AutoScaleDimensions is the Segoe UI 9 pt @96 dpi
        // baseline, so the explicit pixel sizes above scale on high-DPI displays.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = _cancel;
        // No AcceptButton on purpose: Enter inside a NumericUpDown would otherwise save and close.

        _errors.ContainerControl = this;
        // A glyph that blinks every time a partially-typed hex value is momentarily invalid is
        // motion for its own sake; the icon alone already marks the field.
        _errors.BlinkStyle = ErrorBlinkStyle.NeverBlink;

        // Inside Pair() these follow a TrackBar and a TextBox rather than a Label, so WinForms'
        // "name from the preceding label" heuristic has nothing to find.
        _alphaBar.AccessibleName = "Background alpha";
        _alphaNum.AccessibleName = "Background alpha value";
        _monoOnly.AccessibleName = "Show monospace fonts only";
        _fontName.AccessibleName = "Font name";
        _previewDebounce.Tick += (_, _) => FlushPreview();

        BuildLayout();
        WireEvents();
        SeedFileText();
        SeedControls();
        ApplyFallbackAvailability();
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        void AddGroup(string title, Control inner)
        {
            var box = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 10),
                Margin = new Padding(4, 4, 4, 10),
            };
            box.Controls.Add(inner);
            int r = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(box, 0, r);
        }

        // Metrics
        var metrics = Grid();
        FullRow(metrics, _netEnabled);
        FullRow(metrics, _netUpload);
        FullRow(metrics, _cpuEnabled);
        FullRow(metrics, ElevationInfo.IsElevated
            ? _cpuTemp
            : Pair(_cpuTemp, new Label { Text = "needs admin", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 4, 3, 3) }));
        FullRow(metrics, _ramEnabled);
        FullRow(metrics, _gpuEnabled);
        FullRow(metrics, _gpuTemp);
        FullRow(metrics, _metricsPinHint);
        AddGroup("&Metrics", metrics);

        // Appearance. Text settings are kept together and background settings kept together —
        // previously the alpha row sat between "Text color from" and "Text shadow", splitting the
        // two text rows apart and separating alpha from the background override it pairs with.
        var appearance = Grid();
        Row(appearance, "Font", Pair(_fontName, _monoOnly));
        FullRow(appearance, _fontWarning);
        Row(appearance, "Size (pt)", _fontSize);
        Row(appearance, "Theme", _theme);
        Row(appearance, "Text color from", _textColorSource);
        Row(appearance, "Text override", ColorField(_textOverride, _textSwatch, "Text"));
        FullRow(appearance, _textShadow);
        Row(appearance, "Background color", ColorField(_bgOverride, _bgSwatch, "Background"));
        Row(appearance, "Background alpha", Pair(_alphaBar, _alphaNum));
        FullRow(appearance, Hint("Overrides are #RRGGBB. Leave empty to follow the accent/theme. "
                                 + "Background alpha sets the opacity either way — 0 is fully transparent."));
        AddGroup("&Appearance", appearance);

        // Positioning. These are not peers, and presenting them as five identical spinners meant a
        // user could drag one that does nothing on their machine. ComputeStripRect picks exactly one
        // horizontal anchor per taskbar: the primary bar uses "Gap from tray" when TrayNotifyWnd is
        // found, and secondary bars always fall to their reserve (LocateAll builds them with a null
        // NotifyBounds). "Tray reserve" is the genuinely rare one — primary-only, fallback-only.
        var positioning = Grid();
        Row(positioning, "Gap from tray (px)", _rightMargin);
        Row(positioning, "Secondary bar reserve (px)", _secondaryTrayReserve);
        Row(positioning, "Extra left margin (px)", _extraLeftMargin);
        Row(positioning, "Vertical offset (px)", _verticalOffset);
        FullRow(positioning, Hint("The strip docks left of the tray on the primary taskbar, and left of "
                                  + "the clock on secondary ones. Extra left margin shoves it further "
                                  + "left; vertical offset nudges it up or down."));
        FullRow(positioning, _fallbackHeader);
        Row(positioning, "Tray reserve (px)", _trayReserve);
        FullRow(positioning, _fallbackHint);
        AddGroup("&Positioning", positioning);

        // Behaviour
        var behavior = Grid();
        FullRow(behavior, _hideOnFullscreen);
        FullRow(behavior, _multiMonitor);
        Row(behavior, "Watchdog interval (ms)", _watchdogInterval);
        AddGroup("Behaviour", behavior);

        // Network
        var network = Grid();
        Row(network, "Adapter", _adapter);
        Row(network, "Adapter refresh (s)", _adapterRefresh);
        FullRow(network, Hint("Leave on (automatic) to follow the default route. WSL2 and Docker vEthernet adapters are excluded either way."));
        AddGroup("Network", network);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        scroll.Controls.Add(stack);

        // RightToLeft flow puts the first-added control rightmost, so Close is added first to lay the
        // pair out as [Save] [Close] — Windows order, primary on the left. Add order (not visual
        // order) drives tabbing, though, which made the bar tab Reset → Close → Save: the primary
        // action reached last, after the one that discards. TabIndex restores Save-then-Close, so
        // tab order now matches the visual left-to-right reading.
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        right.Controls.Add(_cancel);
        right.Controls.Add(_save);
        _save.TabIndex = 0;
        _cancel.TabIndex = 1;

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 4, 8, 8),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttons.Controls.Add(_reset, 0, 0);
        buttons.Controls.Add(right, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(scroll, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
    }

    private static CheckBox Check(string text, bool indent = false, string? accessibleName = null)
        => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(indent ? 22 : 3, 3, 3, 3),
            AccessibleName = accessibleName,
        };

    private static NumericUpDown Num(decimal min, decimal max, decimal step, int decimals = 0)
        => new() { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = 80 };

    private static Label Hint(string text)
        => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(460, 0), Margin = new Padding(3, 6, 3, 3) };

    /// <summary>
    /// A hint whose text comes and goes. Toggling Visible on a row of an AutoSize table reflows
    /// everything below it — mid-typing, that yanks the field the caret is in. These keep their row
    /// and blank their text instead, so showing a warning never moves anything.
    /// </summary>
    private static Label ReservedHint(Color foreColor) => new()
    {
        Text = "",
        AutoSize = true,
        ForeColor = foreColor,
        MaximumSize = new Size(460, 0),
        MinimumSize = new Size(0, 15),
        Margin = new Padding(3, 6, 3, 3),
    };

    /// <summary>
    /// Width of the label column, shared by every group. With SizeType.AutoSize each group sized its
    /// own column independently, so the editors started at a different x in each one and visibly
    /// stepped left and right as you scrolled. One absolute width lines them all up. Sized to the
    /// longest label in the window ("Background override") with room to spare.
    /// </summary>
    private const int LabelColumnWidth = 148;

    private static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return t;
    }

    private static void Row(TableLayoutPanel t, string label, Control editor)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) }, 0, r);
        editor.Anchor = AnchorStyles.Left;
        t.Controls.Add(editor, 1, r);
    }

    private static void FullRow(TableLayoutPanel t, Control c)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Anchor = AnchorStyles.Left;
        t.Controls.Add(c, 0, r);
        t.SetColumnSpan(c, 2);
    }

    private static FlowLayoutPanel Pair(params Control[] children)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        foreach (var c in children)
        {
            c.Anchor = AnchorStyles.Left;
            p.Controls.Add(c);
        }
        return p;
    }

    /// <summary>
    /// Hex box, swatch, Pick…, Clear. Colour only — alpha comes from the background-alpha slider, so
    /// the swatch is always opaque and there is no #AARRGGBB form to explain.
    /// </summary>
    private Control ColorField(TextBox box, Panel swatch, string what)
    {
        var pick = new Button { Text = "&Pick…", AutoSize = true, AccessibleName = $"Pick {what} color" };
        var clear = new Button { Text = "Clear", AutoSize = true, AccessibleName = $"Clear {what} color" };
        pick.Click += (_, _) => PickColor(box);
        clear.Click += (_, _) => box.Text = "";

        box.AccessibleName = $"{what} color, hex";
        // ErrorProvider draws its icon just right of the control, and a FlowLayoutPanel reserves no
        // space for it — without this the glyph lands on top of the swatch.
        box.Margin = new Padding(3, 3, 20, 3);

        swatch.Margin = new Padding(6, 5, 6, 3);
        swatch.Cursor = Cursors.Hand;
        swatch.Click += (_, _) => PickColor(box);
        swatch.AccessibleName = $"{what} color preview";
        // BackColor does not alpha-blend, so paint the color over a checkerboard instead.
        swatch.Paint += (_, e) =>
        {
            using (var checks = new HatchBrush(HatchStyle.LargeCheckerBoard, SystemColors.Window, SystemColors.ControlLight))
                e.Graphics.FillRectangle(checks, swatch.ClientRectangle);
            if (ThemeWatcher.TryParseHex(box.Text, out var c))
                using (var b = new SolidBrush(Color.FromArgb(255, c)))
                    e.Graphics.FillRectangle(b, swatch.ClientRectangle);
            using var border = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawRectangle(border, 0, 0, swatch.Width - 1, swatch.Height - 1);
        };
        return Pair(box, swatch, pick, clear);
    }

    // ---------------------------------------------------------------- wiring

    private void WireEvents()
    {
        _netEnabled.CheckedChanged += (_, _) => Commit(s => s.Metrics.Network.Enabled = _netEnabled.Checked);
        _netUpload.CheckedChanged += (_, _) => Commit(s => s.Metrics.Network.ShowUpload = _netUpload.Checked);
        _cpuEnabled.CheckedChanged += (_, _) => Commit(s => s.Metrics.Cpu.Enabled = _cpuEnabled.Checked);
        _cpuTemp.CheckedChanged += (_, _) => Commit(s => s.Metrics.Cpu.ShowTemp = _cpuTemp.Checked);
        _ramEnabled.CheckedChanged += (_, _) => Commit(s => s.Metrics.Ram.Enabled = _ramEnabled.Checked);
        _gpuEnabled.CheckedChanged += (_, _) => Commit(s => s.Metrics.Gpu.Enabled = _gpuEnabled.Checked);
        _gpuTemp.CheckedChanged += (_, _) => Commit(s => s.Metrics.Gpu.ShowTemp = _gpuTemp.Checked);

        _fontName.DropDown += (_, _) => LoadFontFamilies();
        _monoOnly.CheckedChanged += (_, _) => { _fontsLoaded = false; if (_fontName.DroppedDown) LoadFontFamilies(); };
        // Not TextChanged, for the same reason as the adapter combo below: every keystroke of
        // "Cascadia Mono" is its own font name, and each partial one misses and pushes the generic
        // monospace fallback onto the live strip with the red warning flashing alongside it.
        _fontName.SelectionChangeCommitted += (_, _) => CommitFont();
        _fontName.Leave += (_, _) => CommitFont();
        _fontSize.ValueChanged += (_, _) => Commit(s => s.Appearance.FontSizePt = (float)_fontSize.Value);
        _theme.SelectedIndexChanged += (_, _) => Commit(s => s.Appearance.Theme = (string)_theme.SelectedItem!);
        _textColorSource.SelectedIndexChanged += (_, _) => Commit(s => s.Appearance.TextColorSource = (string)_textColorSource.SelectedItem!);
        _textShadow.CheckedChanged += (_, _) => Commit(s => s.Appearance.TextShadow = _textShadow.Checked);

        // Each control programmatically sets the other, so both need the re-entrancy guard.
        _alphaBar.ValueChanged += (_, _) =>
        {
            if (_syncingAlpha) return;
            _syncingAlpha = true; _alphaNum.Value = _alphaBar.Value; _syncingAlpha = false;
            Commit(s => s.Appearance.BackgroundAlpha = _alphaBar.Value);
        };
        _alphaNum.ValueChanged += (_, _) =>
        {
            if (_syncingAlpha) return;
            _syncingAlpha = true; _alphaBar.Value = (int)_alphaNum.Value; _syncingAlpha = false;
            Commit(s => s.Appearance.BackgroundAlpha = (int)_alphaNum.Value);
        };

        HookColorField(_bgOverride, _bgSwatch, v => _draft.Current.Appearance.BackgroundOverride = v);
        HookColorField(_textOverride, _textSwatch, v => _draft.Current.Appearance.TextOverride = v);

        _rightMargin.ValueChanged += (_, _) => Commit(s => s.Positioning.RightMarginPx = (int)_rightMargin.Value);
        _trayReserve.ValueChanged += (_, _) => Commit(s => s.Positioning.TrayReservePx = (int)_trayReserve.Value);
        _secondaryTrayReserve.ValueChanged += (_, _) => Commit(s => s.Positioning.SecondaryTrayReservePx = (int)_secondaryTrayReserve.Value);
        _verticalOffset.ValueChanged += (_, _) => Commit(s => s.Positioning.VerticalOffsetPx = (int)_verticalOffset.Value);
        _extraLeftMargin.ValueChanged += (_, _) => Commit(s => s.Positioning.ExtraLeftMarginPx = (int)_extraLeftMargin.Value);

        _hideOnFullscreen.CheckedChanged += (_, _) => Commit(s => s.Behavior.HideOnFullscreen = _hideOnFullscreen.Checked);
        _multiMonitor.CheckedChanged += (_, _) => Commit(s => s.Behavior.MultiMonitor = _multiMonitor.Checked);
        _watchdogInterval.ValueChanged += (_, _) => Commit(s => s.Behavior.WatchdogIntervalMs = (int)_watchdogInterval.Value);

        // Not TextChanged: every distinct value tears down and re-registers the route-change
        // notification in SensorEngine.ApplySettings and re-baselines the sampler.
        _adapter.SelectionChangeCommitted += (_, _) => CommitAdapter();
        _adapter.Leave += (_, _) => CommitAdapter();
        _adapterRefresh.ValueChanged += (_, _) => Commit(s => s.Network.AdapterRefreshSec = (int)_adapterRefresh.Value);

        _reset.Click += (_, _) =>
        {
            _draft.ResetExposedGroupsToDefaults();
            SeedControls();
            FlushPreview();
        };
        _cancel.Click += (_, _) => Close();
        _save.Click += (_, _) => Save();
    }

    /// <summary>Writes one field into the draft and schedules a preview, unless we are seeding.</summary>
    private void Commit(Action<AppSettings> mutate)
    {
        if (_seeding) return;
        mutate(_draft.Current);
        UpdateDependentEnabled();
        UpdateDirtyIndicator();
        SchedulePreview();
    }

    private const string BaseTitle = "TaskbarMonitor settings";

    /// <summary>
    /// Live preview means the strip can differ from what is on disk with nothing saying so — the
    /// window is modeless, so it can sit in that state indefinitely. The asterisk is the only cue
    /// that Save still has something to do, and it is what makes the close prompt feel expected.
    /// </summary>
    private void UpdateDirtyIndicator()
    {
        string title = _draft.IsDirty ? BaseTitle + " *" : BaseTitle;
        if (Text != title) Text = title;
        _save.Enabled = _draft.IsDirty;
    }

    private void HookColorField(TextBox box, Panel swatch, Action<string?> write)
    {
        box.TextChanged += (_, _) =>
        {
            swatch.Invalidate();
            bool ok = SettingsFieldRules.IsValidColorField(box.Text);
            // Recolour the text, not the background: a hard-coded pale pink keeps SystemColors
            // WindowText on top of it, which is white-on-pink under a dark or high-contrast theme.
            box.ForeColor = ok ? SystemColors.WindowText : SystemColors.MenuHighlight;
            _errors.SetError(box, ok ? "" : "Expected #RRGGBB, or empty for none.");
            if (_seeding || !ok) return; // an invalid value never reaches the draft, so never the preview
            write(SettingsFieldRules.NormalizeColorField(box.Text));
            UpdateDependentEnabled();
            UpdateDirtyIndicator();
            SchedulePreview();
        };
    }

    private void CommitFont()
    {
        if (_seeding) return;
        UpdateFontWarning();
        string value = _fontName.Text.Trim();
        if (string.Equals(value, _draft.Current.Appearance.FontName, StringComparison.Ordinal)) return;
        _draft.Current.Appearance.FontName = value;
        UpdateDirtyIndicator();
        SchedulePreview();
    }

    private void CommitAdapter()
    {
        if (_seeding) return;
        var value = SettingsFieldRules.NormalizeAdapter(_adapter.Text);
        if (string.Equals(value, _draft.Current.Network.AdapterOverride, StringComparison.Ordinal)) return;
        _draft.Current.Network.AdapterOverride = value;
        UpdateDirtyIndicator();
        SchedulePreview();
    }

    /// <summary>
    /// ColorDialog cannot pick alpha, which used to matter — the background override carried its own.
    /// Now alpha lives on the slider and this only ever deals in RGB, so the dialog's limitation is
    /// no longer a limitation.
    /// </summary>
    private void PickColor(TextBox box)
    {
        using var dlg = new ColorDialog { FullOpen = true, AnyColor = true };
        if (ThemeWatcher.TryParseHex(box.Text, out var previous))
            dlg.Color = Color.FromArgb(previous.R, previous.G, previous.B);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        box.Text = SettingsFieldRules.FormatColor(dlg.Color, includeAlpha: false);
    }

    /// <summary>
    /// Leading-plus-trailing throttle, not a plain trailing debounce. A pure debounce restarted its
    /// window on every ValueChanged, and a TrackBar raises those continuously — so dragging the
    /// alpha slider updated the strip only once the user stopped, which reads as lag on the one
    /// control you would drag precisely to watch it change. Applying on the leading edge keeps the
    /// preview live during a drag; the trailing tick still coalesces the tail, so ApplyLive is never
    /// hit faster than <see cref="PreviewIntervalMs"/> and the concerns behind the original debounce
    /// (Timer.Interval churn starving the watchdog, NotifyRouteChange2 re-registration) still hold.
    /// </summary>
    private void SchedulePreview()
    {
        int sinceLast = Environment.TickCount - _lastPreviewTick;
        if (sinceLast >= PreviewIntervalMs)
        {
            FlushPreview();
            return;
        }
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    /// <summary>Apply immediately and drop any pending tick — for Reset, Reseed and close.</summary>
    private void FlushPreview()
    {
        _previewDebounce.Stop();
        _lastPreviewTick = Environment.TickCount;
        _host.ApplyLive(_draft.Current);
    }

    // ---------------------------------------------------------------- seeding

    private void SeedFileText()
    {
        try { _fileTextAsOfSeed = File.Exists(_host.SettingsPath) ? File.ReadAllText(_host.SettingsPath) : null; }
        catch (IOException) { _fileTextAsOfSeed = null; }
    }

    private void SeedControls()
    {
        _seeding = true;
        try
        {
            var s = _draft.Current;

            _netEnabled.Checked = s.Metrics.Network.Enabled;
            _netUpload.Checked = s.Metrics.Network.ShowUpload;
            _cpuEnabled.Checked = s.Metrics.Cpu.Enabled;
            _cpuTemp.Checked = s.Metrics.Cpu.ShowTemp;
            _ramEnabled.Checked = s.Metrics.Ram.Enabled;
            _gpuEnabled.Checked = s.Metrics.Gpu.Enabled;
            _gpuTemp.Checked = s.Metrics.Gpu.ShowTemp;

            _fontName.Text = s.Appearance.FontName;
            Seed(_fontSize, (decimal)s.Appearance.FontSizePt);
            SeedCombo(_theme, new[] { "auto", "dark", "light" }, SettingsFieldRules.NormalizeTheme(s.Appearance.Theme));
            SeedCombo(_textColorSource, new[] { "accent", "theme" }, SettingsFieldRules.NormalizeTextColorSource(s.Appearance.TextColorSource));
            int alpha = Math.Clamp(s.Appearance.BackgroundAlpha, 0, 255);
            _alphaBar.Value = alpha;
            _alphaNum.Value = alpha;
            _textShadow.Checked = s.Appearance.TextShadow;
            _bgOverride.Text = s.Appearance.BackgroundOverride ?? "";
            _textOverride.Text = s.Appearance.TextOverride ?? "";

            Seed(_rightMargin, s.Positioning.RightMarginPx);
            Seed(_trayReserve, s.Positioning.TrayReservePx);
            Seed(_secondaryTrayReserve, s.Positioning.SecondaryTrayReservePx);
            Seed(_verticalOffset, s.Positioning.VerticalOffsetPx);
            Seed(_extraLeftMargin, s.Positioning.ExtraLeftMarginPx);

            _hideOnFullscreen.Checked = s.Behavior.HideOnFullscreen;
            _multiMonitor.Checked = s.Behavior.MultiMonitor;
            Seed(_watchdogInterval, s.Behavior.WatchdogIntervalMs);

            SeedAdapters(s.Network.AdapterOverride);
            Seed(_adapterRefresh, s.Network.AdapterRefreshSec);
        }
        finally
        {
            _seeding = false;
        }

        UpdateFontWarning();
        UpdateDependentEnabled();
        UpdateDirtyIndicator();
    }

    /// <summary>
    /// trayReservePx only participates when the primary taskbar's TrayNotifyWnd cannot be found.
    /// Leaving it live on a normal machine meant a spinner that moved nothing, which reads as a bug
    /// rather than as a fallback.
    /// </summary>
    private void ApplyFallbackAvailability()
    {
        bool located;
        try { located = _host.PrimaryTrayLocated; }
        catch { located = true; }

        _trayReserve.Enabled = !located;
        _fallbackHint.Text = located
            ? "The tray was found normally, so this is unused. It positions the primary strip only if that ever fails."
            : "In use now — the tray could not be located, so the primary strip is placed this far from the right edge.";
    }

    /// <summary>
    /// settings.json is hand-editable, so a seed value can be anything. NumericUpDown.Value throws
    /// outside [Minimum, Maximum] — every seed goes through here. Saving then normalizes.
    /// </summary>
    private static void Seed(NumericUpDown n, decimal value) => n.Value = Math.Clamp(value, n.Minimum, n.Maximum);

    private static void SeedCombo(ComboBox combo, string[] items, string value)
    {
        if (combo.Items.Count == 0) combo.Items.AddRange(items);
        combo.SelectedItem = value;
    }

    private void SeedAdapters(string? current)
    {
        _adapter.Items.Clear();
        _adapter.Items.Add(SettingsFieldRules.AutomaticAdapter);
        try
        {
            // Matched against NetworkInterface.Name in DefaultRouteResolver — not Description, and
            // not the MIB_IF_ROW2 alias used for logging.
            foreach (var name in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                         .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(n => n.Name))
                _adapter.Items.Add(name);
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"Adapter enumeration failed: {ex.Message}");
        }

        // Editable, so a disconnected or renamed adapter name from the file survives untouched.
        _adapter.Text = string.IsNullOrWhiteSpace(current) ? SettingsFieldRules.AutomaticAdapter : current;
    }

    private void LoadFontFamilies()
    {
        if (_fontsLoaded) return;
        _fontsLoaded = true;

        string keep = _fontName.Text;
        try
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var names = FontFamily.Families
                .Where(f => f.IsStyleAvailable(FontStyle.Regular))
                .Where(f => !_monoOnly.Checked || IsMonospace(f, g))
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _seeding = true;
            try
            {
                _fontName.Items.Clear();
                _fontName.Items.AddRange(names);
                _fontName.Text = keep;
            }
            finally { _seeding = false; }
        }
        catch (Exception ex)
        {
            Log.Warn($"Font enumeration failed: {ex.Message}");
        }
    }

    private static bool IsMonospace(FontFamily family, Graphics g)
    {
        try
        {
            using var font = new Font(family, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            var fmt = StringFormat.GenericTypographic; // the format StripRenderer measures with
            float narrow = g.MeasureString("iiii", font, PointF.Empty, fmt).Width;
            float wide = g.MeasureString("WWWW", font, PointF.Empty, fmt).Width;
            return Math.Abs(narrow - wide) < 0.5f;
        }
        catch { return false; }
    }

    /// <summary>
    /// Mirrors OverlayWindow.ApplyAppearance's substitution check exactly, so the warning can never
    /// disagree with what actually renders. The value is never rewritten — the file keeps the name.
    /// </summary>
    private void UpdateFontWarning()
    {
        string name = _fontName.Text.Trim();

        // An empty box is "no font chosen", not "that font is missing" — the old code left
        // installed=false for it and showed the not-installed warning, which named a problem the
        // user did not have.
        if (name.Length == 0)
        {
            _fontWarning.Text = "Enter a font name — the strip falls back to a generic monospace font.";
            return;
        }

        bool installed;
        try
        {
            using var f = new Font(name, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            installed = string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
        }
        catch { installed = false; }

        _fontWarning.Text = installed
            ? ""
            : "Not installed — the strip falls back to a generic monospace font.";
    }

    private void UpdateDependentEnabled()
    {
        _netUpload.Enabled = _netEnabled.Checked;
        _cpuTemp.Enabled = _cpuEnabled.Checked;
        _gpuTemp.Enabled = _gpuEnabled.Checked;

        // With every metric off the composed line is empty, the strip collapses to a few pixels, and
        // the context menu — the only UI — becomes unreachable. Pin the last one that is checked.
        var boxes = new[] { _netEnabled, _cpuEnabled, _ramEnabled, _gpuEnabled };
        int checkedCount = boxes.Count(b => b.Checked);
        foreach (var b in boxes) b.Enabled = checkedCount != 1 || !b.Checked;

        // Only shown at the moment it applies. As a permanent fixture it was noise on every open and
        // easy to miss on the one interaction it explains.
        _metricsPinHint.Text = checkedCount == 1
            ? "Keep at least one metric on — the strip needs some width to stay clickable."
            : "";

        // The alpha slider is never disabled any more: ThemeWatcher takes RGB from the override and
        // alpha from here, so both always do something.
    }

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Called by the controller for the explicit "Reload settings" menu item, which outranks the
    /// window. Watcher-driven reloads never get here — they are skipped while this window is open.
    /// </summary>
    public void ReseedFromDisk(AppSettings fromDisk)
    {
        if (_draft.IsDirty &&
            MessageBox.Show(this,
                "Discard your unsaved changes and reload settings.json?",
                "TaskbarMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            FlushPreview(); // the controller already applied the file; put the draft back
            return;
        }

        _draft.Reseed(fromDisk);
        SeedFileText();
        SeedControls();
        FlushPreview();
    }

    /// <summary>
    /// Writes to disk and stays open. Closing on Save fought the workflow live preview invites —
    /// tune, save, keep tuning — by making the user reopen the window each round. Closing is now
    /// exactly one thing (the Close button, Esc, or X), and Save is exactly one thing.
    /// </summary>
    /// <returns>True when the settings reached disk.</returns>
    private bool Save()
    {
        // Both editable combos commit on Leave, which has not fired if one still has focus
        CommitFont();
        CommitAdapter();

        if (!SettingsFieldRules.IsValidColorField(_bgOverride.Text)) return Reject(_bgOverride);
        if (!SettingsFieldRules.IsValidColorField(_textOverride.Text)) return Reject(_textOverride);

        if (FileChangedSinceSeed() &&
            MessageBox.Show(this,
                "settings.json has been changed outside this window since it was opened.\n" +
                "Saving will overwrite those changes. Continue?",
                "TaskbarMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        if (!_host.TrySave(_draft.Current, out var error))
        {
            // Stay open: the user's edits must survive a failed write.
            MessageBox.Show(this, $"Could not write settings.json.\n\n{error}",
                "TaskbarMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Saved state is the new baseline, so the window stops reporting itself as dirty and a
        // subsequent close has nothing to prompt about.
        _draft.Reseed(_draft.Current);   // Reseed clones, so this is a genuine new baseline
        SeedFileText();
        UpdateDirtyIndicator();
        return true;
    }

    /// <summary>
    /// A beep alone left the user with a button that visibly did nothing. The field is already
    /// flagged; this says which one and why, in the same place the eye goes after pressing Save.
    /// </summary>
    private bool Reject(Control c)
    {
        SystemSounds.Beep.Play();
        c.Focus();
        MessageBox.Show(this,
            "That color isn't a valid hex value.\n\nUse #RRGGBB, or leave the box empty to follow the accent/theme.",
            "TaskbarMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private bool FileChangedSinceSeed()
    {
        try
        {
            if (!File.Exists(_host.SettingsPath)) return _fileTextAsOfSeed is not null;
            return File.ReadAllText(_host.SettingsPath) != _fileTextAsOfSeed;
        }
        catch (IOException)
        {
            return false; // unreadable: let TrySave surface the real error
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        // "Nothing was written, so there is nothing to lose" is true of the file and false of the
        // user's work: this window is modeless and can hold several minutes of tuning, and Esc is
        // easy to hit by accident. ReseedFromDisk already prompts on IsDirty for exactly this loss,
        // so not prompting here was also inconsistent with the rest of the window.
        // App shutdown is exempt — ExitApp has already confirmed, and blocking it would strand
        // Application.Run with an open form.
        if (_draft.IsDirty && e.CloseReason != CloseReason.ApplicationExitCall)
        {
            switch (MessageBox.Show(this,
                        "Save your changes to settings.json before closing?",
                        "TaskbarMonitor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning))
            {
                case DialogResult.Yes when !Save():
                    e.Cancel = true;   // invalid field or a failed write — stay open with the edits
                    return;
                case DialogResult.Cancel:
                    e.Cancel = true;
                    return;
            }
        }

        _previewDebounce.Stop();
        // Discarding still means "stop previewing": revert the strips to the as-of-open snapshot.
        // A successful Save has already reseeded, so IsDirty is false and nothing is reverted.
        if (_draft.IsDirty) _host.ApplyLive(_draft.Snapshot);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewDebounce.Dispose();
            _errors.Dispose();
        }
        base.Dispose(disposing);
    }
}
