namespace TaskbarMonitor.Settings;

/// <summary>
/// The settings window's editing model: a mutable draft plus the immutable as-of-open snapshot that
/// Cancel restores. Deliberately Form-free so it can be tested directly.
/// </summary>
public sealed class SettingsDraft
{
    private string _snapshotJson;

    public SettingsDraft(AppSettings current)
    {
        Snapshot = SettingsStore.Clone(current);
        Current = SettingsStore.Clone(current);
        _snapshotJson = SettingsStore.Serialize(Snapshot);
    }

    /// <summary>State as of open (or the last reseed). Never mutated.</summary>
    public AppSettings Snapshot { get; private set; }

    /// <summary>The tree the editors mutate.</summary>
    public AppSettings Current { get; private set; }

    public bool IsDirty => SettingsStore.Serialize(Current) != _snapshotJson;

    /// <summary>
    /// Resets only the groups the window exposes. pollIntervalMs, sensorIntervalMs and the whole
    /// logging group are file-only: resetting keys the user cannot see would be silent data loss.
    /// </summary>
    public void ResetExposedGroupsToDefaults()
    {
        var defaults = new AppSettings();
        var next = SettingsStore.Clone(Current);
        next.Metrics = defaults.Metrics;
        next.Positioning = defaults.Positioning;
        next.Appearance = defaults.Appearance;
        next.Behavior = defaults.Behavior;
        next.Network = defaults.Network;
        Current = next;
    }

    /// <summary>Explicit "Reload settings": both draft and snapshot become the on-disk state.</summary>
    public void Reseed(AppSettings fromDisk)
    {
        Snapshot = SettingsStore.Clone(fromDisk);
        Current = SettingsStore.Clone(fromDisk);
        _snapshotJson = SettingsStore.Serialize(Snapshot);
    }
}
