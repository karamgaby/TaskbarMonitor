namespace TaskbarMonitor.Settings;

/// <summary>
/// Everything the settings window needs from the running app. Implemented by PositionController;
/// the window deliberately does not see the strips or the timers.
/// </summary>
public interface ISettingsHost
{
    /// <summary>The settings currently driving the strips. Treat as read-only — clone before editing.</summary>
    AppSettings Current { get; }

    string SettingsPath { get; }

    /// <summary>Apply to the live strips without touching disk. Takes its own copy.</summary>
    void ApplyLive(AppSettings settings);

    /// <summary>Write to disk and apply, suppressing the watcher-driven reload of our own write.</summary>
    bool TrySave(AppSettings settings, out string? error);
}
