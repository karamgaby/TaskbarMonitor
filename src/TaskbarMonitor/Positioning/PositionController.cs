using System.Diagnostics;
using Microsoft.Win32;
using TaskbarMonitor.Interop;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Sensors;
using TaskbarMonitor.Settings;
using TaskbarMonitor.UI;

namespace TaskbarMonitor.Positioning;

/// <summary>
/// Owns the strip windows (one per taskbar), the 1 s UI refresh, and the 2 s watchdog that
/// re-locates taskbars, re-asserts TOPMOST, and handles auto-hide / fullscreen / explorer
/// restarts. Event-driven redocks are debounced through the watchdog.
/// </summary>
public sealed class PositionController : ApplicationContext, ISettingsHost
{
    private readonly Dictionary<IntPtr, OverlayWindow> _strips = new();
    private readonly SensorEngine _engine;
    private readonly string _settingsPath;
    private AppSettings _settings;

    /// <summary>
    /// The settings window, or null when it is closed. Only ever touched on the UI thread — every
    /// path that reaches it is either a WinForms timer tick, a menu handler, or posted through
    /// <see cref="_sync"/> — so it needs no locking.
    /// </summary>
    private SettingsForm? _settingsForm;

    /// <summary>
    /// The exact text of our last write, valid only while we believe the file still matches
    /// in-memory state. Non-null lets the watcher recognise (and skip) the reload our own save
    /// triggers; cleared the moment we load anything from disk.
    /// </summary>
    private string? _lastWrittenJson;
    private int _reloadRetries;

    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly System.Windows.Forms.Timer _watchdog = new();
    private readonly System.Windows.Forms.Timer _redockDelay = new(); // explorer restart settles async
    private readonly System.Windows.Forms.Timer _reloadDebounce = new();
    private readonly FileSystemWatcher? _settingsWatcher;

    /// <summary>
    /// Handle-bearing control created on the UI thread, used to marshal FileSystemWatcher and
    /// SystemEvents callbacks. Both fire on background threads, and a System.Windows.Forms.Timer
    /// started from one never ticks — its WM_TIMER has no message loop to be pumped on.
    /// </summary>
    private readonly Control _sync = new();

    public PositionController(AppSettings settings, SensorEngine engine, string settingsPath)
    {
        _sync.CreateControl();
        _ = _sync.Handle; // force handle creation now, on the UI thread
        _settings = settings;
        _engine = engine;
        _settingsPath = settingsPath;

        _uiTimer.Interval = Math.Max(250, settings.PollIntervalMs);
        _uiTimer.Tick += (_, _) => RefreshLines();
        _watchdog.Interval = Math.Max(1000, settings.Behavior.WatchdogIntervalMs);
        _watchdog.Tick += (_, _) => SyncStrips();
        _redockDelay.Interval = 2000;
        _redockDelay.Tick += (_, _) => { _redockDelay.Stop(); SyncStrips(); };
        _reloadDebounce.Interval = 500;
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadSettings(force: false); };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        try
        {
            var dir = Path.GetDirectoryName(settingsPath)!;
            _settingsWatcher = new FileSystemWatcher(dir, Path.GetFileName(settingsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                SynchronizingObject = _sync,   // without this the debounce timer never ticks
                EnableRaisingEvents = true,
            };
            _settingsWatcher.Changed += (_, _) => { _reloadDebounce.Stop(); _reloadDebounce.Start(); };
            _settingsWatcher.Created += (_, _) => { _reloadDebounce.Stop(); _reloadDebounce.Start(); };
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings watcher unavailable: {ex.Message}");
        }

        SyncStrips();
        _uiTimer.Start();
        _watchdog.Start();
        Log.Info($"PositionController up: {_strips.Count} strip(s)");
    }

    private void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _engine.OnPowerResume();
            _redockDelay.Stop();
            _redockDelay.Start();
        }
    }

    private void OnDisplaySettingsChanged(object? s, EventArgs e) { _redockDelay.Stop(); _redockDelay.Start(); }

    private void OnUserPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Color or UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Window)) return;
        ReapplyAppearance();
    }

    /// <summary>
    /// SystemEvents raises on its own thread, so this must marshal: the strips' GDI surfaces have
    /// thread affinity and the 1 s timer is already rendering them on the UI thread.
    /// </summary>
    private void ReapplyAppearance()
    {
        if (_sync.InvokeRequired)
        {
            try { _sync.BeginInvoke(ReapplyAppearance); }
            catch (Exception ex) { Log.Warn($"Appearance marshal failed: {ex.Message}"); }
            return;
        }

        foreach (var s in _strips.Values) s.ApplyAppearance(_settings);
        SyncStrips();
    }

    private void RefreshLines()
    {
        string line = LineComposer.Compose(_engine.Latest, _settings);
        foreach (var strip in _strips.Values) strip.UpdateLine(line);
        UpdateVisibility();
    }

    private void SyncStrips()
    {
        try
        {
            var bars = TaskbarLocator.LocateAll(_settings.Behavior.MultiMonitor);

            // Drop strips whose taskbar is gone (explorer restart recreates hwnds)
            var live = new HashSet<IntPtr>(bars.Select(b => b.Hwnd));
            foreach (var (hwnd, strip) in _strips.Where(kv => !live.Contains(kv.Key)).ToList())
            {
                Log.Info($"Taskbar {hwnd:X} gone; disposing its strip");
                strip.Dispose();
                _strips.Remove(hwnd);
            }

            foreach (var bar in bars)
            {
                if (!_strips.TryGetValue(bar.Hwnd, out var strip))
                {
                    strip = CreateStrip();
                    _strips[bar.Hwnd] = strip;
                    Log.Info($"Strip created for taskbar {bar.Hwnd:X} ({(bar.IsPrimary ? "primary" : "secondary")}, bounds {bar.Bounds})");
                }

                var target = TaskbarLocator.ComputeStripRect(bar, strip.ContentSize, _settings.Positioning);
                // Seed the bounds before the handle exists, or the strip is briefly shown at the
                // default 300x300 near the origin — an invisible hit-trap now that it has no panel.
                if (!strip.IsHandleCreated) { strip.Bounds = target; strip.Show(); }

                // Re-assert top-of-topmost every tick: the taskbar is TOPMOST too and can raise
                // itself above us within the band without touching our exstyle bit.
                bool drifted = strip.Bounds != target;
                if (drifted)
                    NativeMethods.SetWindowPos(strip.Handle, NativeMethods.HWND_TOPMOST,
                        target.X, target.Y, target.Width, target.Height,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                else
                    NativeMethods.SetWindowPos(strip.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
            }

            UpdateVisibility(bars);
        }
        catch (Exception ex)
        {
            Log.Error("SyncStrips failed", ex);
        }
    }

    private void UpdateVisibility(List<TaskbarInfo>? bars = null)
    {
        try
        {
            bars ??= TaskbarLocator.LocateAll(_settings.Behavior.MultiMonitor);
            bool autoHide = TaskbarLocator.IsAutoHideEnabled();

            IntPtr fullscreenMonitor = IntPtr.Zero;
            if (_settings.Behavior.HideOnFullscreen
                && NativeMethods.SHQueryUserNotificationState(out var quns) == 0
                && quns is NativeMethods.QueryUserNotificationState.QUNS_BUSY
                    or NativeMethods.QueryUserNotificationState.QUNS_RUNNING_D3D_FULL_SCREEN
                    or NativeMethods.QueryUserNotificationState.QUNS_PRESENTATION_MODE)
            {
                fullscreenMonitor = NativeMethods.MonitorFromWindow(NativeMethods.GetForegroundWindow(), 2 /* NEAREST */);
            }

            foreach (var bar in bars)
            {
                if (!_strips.TryGetValue(bar.Hwnd, out var strip)) continue;
                bool hide = (autoHide && TaskbarLocator.IsBarSlidAway(bar))
                            || (fullscreenMonitor != IntPtr.Zero
                                && bar.Hwnd != IntPtr.Zero
                                && NativeMethods.MonitorFromWindow(bar.Hwnd, 2) == fullscreenMonitor);
                if (strip.Visible == hide) strip.Visible = !hide;
            }
        }
        catch (Exception ex)
        {
            Log.Error("UpdateVisibility failed", ex);
        }
    }

    private OverlayWindow CreateStrip()
    {
        var strip = new OverlayWindow(_settings);
        strip.ExplorerRestarted += () =>
        {
            Log.Info("TaskbarCreated received (explorer restarted); redocking in 2 s");
            _redockDelay.Stop();
            _redockDelay.Start();
        };
        strip.DisplayOrSettingsChanged += () => { _redockDelay.Stop(); _redockDelay.Start(); };
        strip.AppearanceInvalidated += ReapplyAppearance;
        strip.ExitRequested += ExitApp;
        strip.ReloadRequested += () => ReloadSettings(force: true);
        strip.SettingsWindowRequested += () =>
        {
            try { _sync.BeginInvoke(ShowSettingsWindow); }
            catch (Exception ex) { Log.Warn($"Settings window marshal failed: {ex.Message}"); }
        };
        strip.OpenSettingsRequested += () =>
        {
            try
            {
                if (!File.Exists(_settingsPath)) SettingsStore.Save(_settingsPath, _settings);
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_settingsPath}\""));
            }
            catch (Exception ex) { Log.Error("Open settings failed", ex); }
        };
        return strip;
    }

    public AppSettings Current => _settings;

    public string SettingsPath => _settingsPath;

    public bool PrimaryTrayLocated
    {
        get
        {
            try { return TaskbarLocator.LocateAll(multiMonitor: false).Any(b => b.NotifyBounds is not null); }
            catch (Exception ex) { Log.Warn($"Tray probe failed: {ex.Message}"); return true; }
        }
    }

    /// <summary>
    /// Applies settings to the running app without touching disk — the settings window's live
    /// preview. Takes its own deep copy, because the window keeps mutating its draft afterwards and
    /// the sensor thread reads the tree we hand to <see cref="SensorEngine.ApplySettings"/>.
    /// </summary>
    public void ApplyLive(AppSettings settings)
    {
        _settings = SettingsStore.Clone(settings);
        _engine.ApplySettings(_settings);

        // Assign only on a real change. Timer.Interval is KillTimer+SetTimer even when the value is
        // identical, so an unconditional write restarted both timers on every preview — with the
        // settings window's throttle applying on the leading edge of a drag, that would reset the
        // 2 s watchdog every 100 ms and it would never tick for the length of the drag.
        int uiInterval = Math.Max(250, _settings.PollIntervalMs);
        if (_uiTimer.Interval != uiInterval) _uiTimer.Interval = uiInterval;

        int watchdogInterval = Math.Max(1000, _settings.Behavior.WatchdogIntervalMs);
        if (_watchdog.Interval != watchdogInterval) _watchdog.Interval = watchdogInterval;

        // Geometry and text have to change together: ApplyAppearance recomputes ContentSize and
        // SyncStrips resizes to it immediately, so without this the old line renders at the new
        // width until the next UI tick.
        string line = LineComposer.Compose(_engine.Latest, _settings);
        foreach (var strip in _strips.Values)
        {
            strip.ApplyAppearance(_settings);
            strip.UpdateLine(line);
        }
        SyncStrips();
    }

    public bool TrySave(AppSettings settings, out string? error)
    {
        error = null;
        try
        {
            var snapshot = SettingsStore.Clone(settings);
            _lastWrittenJson = SettingsStore.SaveAndCapture(_settingsPath, snapshot);
            ApplyLive(snapshot);
            Log.Info("Settings saved from the settings window");
            return true;
        }
        catch (Exception ex)
        {
            _lastWrittenJson = null; // unknown disk state — never suppress a reload after a failed write
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("Saving settings failed", ex);
            return false;
        }
    }

    /// <param name="force">
    /// True for the explicit "Reload settings" menu item, which overrides the open-window rule.
    /// False for the FileSystemWatcher, which must not clobber an in-progress edit.
    /// </param>
    private void ReloadSettings(bool force)
    {
        if (!force && _settingsForm is { IsDisposed: false })
        {
            // The window's draft is already live on the strips; reloading behind its back would
            // leave its controls and the strips showing different things, with no visible cause.
            Log.Info("settings.json changed on disk; ignored while the settings window is open");
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(_settingsPath);
        }
        catch (IOException ex)
        {
            // Notepad (or another writer) still holds the file. Retrying beats falling through to
            // SettingsStore.Load, which would hand back defaults and wipe the live settings.
            if (_reloadRetries++ < 3)
            {
                Log.Warn($"settings.json unreadable ({ex.Message}); retrying in 500 ms");
                _reloadDebounce.Start();
                return;
            }
            _reloadRetries = 0;
            Log.Warn("settings.json still unreadable after 3 retries; keeping current settings");
            return;
        }
        _reloadRetries = 0;

        if (!force && text == _lastWrittenJson) return; // our own write; in-memory already matches
        _lastWrittenJson = null;                        // state now tracks the file, not our write

        var parsed = SettingsStore.Parse(text, Log.Warn);
        if (parsed is null) return;                     // corrupt — keep what we have, file untouched

        ApplyLive(parsed);
        Log.Info("Settings reloaded");
        if (force) _settingsForm?.ReseedFromDisk(parsed);
    }

    /// <summary>
    /// Posted through <see cref="_sync"/> rather than called from the menu handler: inside the
    /// ContextMenuStrip click the dropdown still holds capture, and an activatable window shown
    /// from there loses the activation race and comes up behind the taskbar.
    /// </summary>
    private void ShowSettingsWindow()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            NativeMethods.SetForegroundWindow(_settingsForm.Handle);
            return;
        }

        try
        {
            // Ownerless on purpose: SyncStrips disposes strips whose taskbar hwnd disappears, so an
            // owning strip could be destroyed out from under the window on any explorer restart.
            var form = new SettingsForm(this);
            form.FormClosed += (_, _) => { if (ReferenceEquals(_settingsForm, form)) _settingsForm = null; };
            _settingsForm = form;
            form.Show();
            form.Activate();
            // The strip called SetForegroundWindow on itself to make its menu dismissable, but it is
            // WS_EX_NOACTIVATE — nothing in this process actually holds activation. Claim it.
            NativeMethods.SetForegroundWindow(form.Handle);
        }
        catch (Exception ex)
        {
            _settingsForm = null;
            Log.Error("Opening the settings window failed", ex);
        }
    }

    private void ExitApp()
    {
        // One unconfirmed click otherwise ends the app until next sign-in, and getting it back means
        // knowing a scheduled task exists — knowledge that only lives in docs\INSTALL.md. The item
        // also sits directly under a disabled one, which is exactly where a mis-click lands.
        //
        // Owned by a throwaway TopMost form: the only windows this process has are the strips, which
        // are WS_EX_NOACTIVATE, so an ownerless modal can come up behind the taskbar — invisible and
        // blocking the UI thread, which would read as a hang rather than a prompt.
        using var anchor = new Form { TopMost = true, ShowInTaskbar = false, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen };
        if (MessageBox.Show(anchor,
                "Stop TaskbarMonitor until you next sign in?\n\n" +
                "To start it again sooner, run the \"TaskbarMonitor\" task in Task Scheduler.",
                "TaskbarMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        Log.Info("Exit requested");
        // Close first: the window's preview debounce would otherwise tick into a controller that is
        // already tearing down, and Application.Run should not be left with an open form.
        if (_settingsForm is { IsDisposed: false }) _settingsForm.Close();
        _settingsForm = null;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_settingsForm is { IsDisposed: false }) _settingsForm.Close();
            _settingsForm = null;
            _uiTimer.Dispose();
            _watchdog.Dispose();
            _redockDelay.Dispose();
            _reloadDebounce.Dispose();
            _settingsWatcher?.Dispose();
            foreach (var strip in _strips.Values) strip.Dispose();
            _strips.Clear();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
