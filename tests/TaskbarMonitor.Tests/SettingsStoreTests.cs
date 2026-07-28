using TaskbarMonitor.Settings;
using Xunit;

namespace TaskbarMonitor.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TaskbarMonitorTests_" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void MissingFile_WritesDefaults()
    {
        var s = SettingsStore.Load(SettingsPath);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(1000, s.PollIntervalMs);
        Assert.True(s.Metrics.Gpu.Enabled);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var s = new AppSettings { PollIntervalMs = 2000 };
        s.Metrics.Cpu.ShowTemp = false;
        s.Network.AdapterOverride = "Ethernet";
        s.Positioning.TrayReservePx = 300;
        SettingsStore.Save(SettingsPath, s);

        var loaded = SettingsStore.Load(SettingsPath);
        Assert.Equal(2000, loaded.PollIntervalMs);
        Assert.False(loaded.Metrics.Cpu.ShowTemp);
        Assert.Equal("Ethernet", loaded.Network.AdapterOverride);
        Assert.Equal(300, loaded.Positioning.TrayReservePx);
    }

    [Fact]
    public void CorruptFile_YieldsDefaults_AndDoesNotOverwrite()
    {
        File.WriteAllText(SettingsPath, "{ not valid json !!!");
        string? warning = null;
        var s = SettingsStore.Load(SettingsPath, w => warning = w);
        Assert.Equal(1000, s.PollIntervalMs);
        Assert.NotNull(warning);
        Assert.Equal("{ not valid json !!!", File.ReadAllText(SettingsPath)); // user's file untouched
    }

    [Fact]
    public void CommentsAndTrailingCommas_Tolerated()
    {
        File.WriteAllText(SettingsPath, """
            {
              // user-edited
              "pollIntervalMs": 1500,
            }
            """);
        Assert.Equal(1500, SettingsStore.Load(SettingsPath).PollIntervalMs);
    }
}
