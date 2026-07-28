using TaskbarMonitor.Settings;
using Xunit;

namespace TaskbarMonitor.Tests;

public class SettingsDraftTests
{
    [Fact]
    public void NewDraft_IsNotDirty() => Assert.False(new SettingsDraft(new AppSettings()).IsDirty);

    [Fact]
    public void MutatingCurrent_MakesItDirty()
    {
        var draft = new SettingsDraft(new AppSettings());
        draft.Current.Appearance.FontSizePt = 14f;
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void Snapshot_IsUnaffectedByDraftEdits()
    {
        var source = new AppSettings();
        var draft = new SettingsDraft(source);

        draft.Current.Appearance.FontName = "Cascadia Mono";
        draft.Current.Metrics.Gpu.Enabled = false;

        Assert.Equal("Consolas", draft.Snapshot.Appearance.FontName);
        Assert.True(draft.Snapshot.Metrics.Gpu.Enabled);
        Assert.Equal("Consolas", source.Appearance.FontName); // the caller's tree too
    }

    [Fact]
    public void ResetExposedGroups_RestoresDefaults()
    {
        var draft = new SettingsDraft(new AppSettings());
        draft.Current.Appearance.FontName = "Arial";
        draft.Current.Appearance.BackgroundAlpha = 200;
        draft.Current.Positioning.RightMarginPx = 99;
        draft.Current.Metrics.Ram.Enabled = false;
        draft.Current.Behavior.HideOnFullscreen = false;
        draft.Current.Network.AdapterOverride = "Ethernet";

        draft.ResetExposedGroupsToDefaults();

        Assert.Equal("Consolas", draft.Current.Appearance.FontName);
        Assert.Equal(0, draft.Current.Appearance.BackgroundAlpha);
        Assert.Equal(8, draft.Current.Positioning.RightMarginPx);
        Assert.True(draft.Current.Metrics.Ram.Enabled);
        Assert.True(draft.Current.Behavior.HideOnFullscreen);
        Assert.Null(draft.Current.Network.AdapterOverride);
    }

    /// <summary>
    /// The window never shows these, so resetting them would be data loss the user cannot see.
    /// </summary>
    [Fact]
    public void ResetExposedGroups_PreservesFileOnlyKeys()
    {
        var source = new AppSettings { PollIntervalMs = 4321, SensorIntervalMs = 9999 };
        source.Logging.MaxSizeKb = 42;
        source.Logging.Enabled = false;

        var draft = new SettingsDraft(source);
        draft.ResetExposedGroupsToDefaults();

        Assert.Equal(4321, draft.Current.PollIntervalMs);
        Assert.Equal(9999, draft.Current.SensorIntervalMs);
        Assert.Equal(42, draft.Current.Logging.MaxSizeKb);
        Assert.False(draft.Current.Logging.Enabled);
    }

    [Fact]
    public void Reseed_ClearsDirtyAndReplacesBoth()
    {
        var draft = new SettingsDraft(new AppSettings());
        draft.Current.Appearance.FontSizePt = 20f;
        Assert.True(draft.IsDirty);

        var fromDisk = new AppSettings();
        fromDisk.Appearance.FontSizePt = 11f;
        draft.Reseed(fromDisk);

        Assert.False(draft.IsDirty);
        Assert.Equal(11f, draft.Current.Appearance.FontSizePt);
        Assert.Equal(11f, draft.Snapshot.Appearance.FontSizePt);
        Assert.NotSame(fromDisk, draft.Current);
    }
}
