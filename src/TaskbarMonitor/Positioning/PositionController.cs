using System.Diagnostics;
using Microsoft.Win32;
using TaskbarMonitor.Interop;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Sensors;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.Positioning;

/// <summary>
/// Owns the strip windows (one per taskbar), the 1 s UI refresh, and the 2 s watchdog that
/// re-locates taskbars, re-asserts TOPMOST, and handles auto-hide / fullscreen / explorer
/// restarts. Event-driven redocks are debounced through the watchdog.
/// </summary>
public sealed class PositionController : ApplicationContext
{
    private readonly Dictionary<IntPtr, OverlayWindow> _strips = new();
    private readonly SensorEngine _engine;
    private readonly string _settingsPath;
    private AppSettings _settings;

    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly System.Windows.Forms.Timer _watchdog = new();
    private readonly System.Windows.Forms.Timer _redockDelay = new(); // explorer restart settles async
    private readonly System.Windows.Forms.Timer _reloadDebounce = new();
    private readonly FileSystemWatcher? _settingsWatcher;

    public PositionController(AppSettings settings, SensorEngine engine, string settingsPath)
    {
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
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadSettings(); };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        try
        {
            var dir = Path.GetDirectoryName(settingsPath)!;
            _settingsWatcher = new FileSystemWatcher(dir, Path.GetFileName(settingsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
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
        foreach (var strip in _strips.Values) strip.ApplyAppearance(_settings);
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
                if (!strip.IsHandleCreated) strip.Show();

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
        strip.ExitRequested += ExitApp;
        strip.ReloadRequested += ReloadSettings;
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

    private void ReloadSettings()
    {
        var fresh = SettingsStore.Load(_settingsPath, Log.Warn);
        _settings = fresh;
        _engine.ApplySettings(fresh);
        _uiTimer.Interval = Math.Max(250, fresh.PollIntervalMs);
        _watchdog.Interval = Math.Max(1000, fresh.Behavior.WatchdogIntervalMs);
        foreach (var strip in _strips.Values) strip.ApplyAppearance(fresh);
        SyncStrips();
        Log.Info("Settings reloaded");
    }

    private void ExitApp()
    {
        Log.Info("Exit requested");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _uiTimer.Dispose();
            _watchdog.Dispose();
            _redockDelay.Dispose();
            _reloadDebounce.Dispose();
            _settingsWatcher?.Dispose();
            foreach (var strip in _strips.Values) strip.Dispose();
            _strips.Clear();
        }
        base.Dispose(disposing);
    }
}
