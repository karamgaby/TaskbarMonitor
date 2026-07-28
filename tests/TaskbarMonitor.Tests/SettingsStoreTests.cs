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
        // Network + CPU only: the four-metric line is ~640 px at the default font and overlaps the
        // centred taskbar buttons on a 1920-wide bar. RAM and GPU are opt-in.
        Assert.True(s.Metrics.Network.Enabled);
        Assert.True(s.Metrics.Cpu.Enabled);
        Assert.False(s.Metrics.Ram.Enabled);
        Assert.False(s.Metrics.Gpu.Enabled);
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

    [Fact]
    public void Parse_ReturnsNullOnCorruptInput()
    {
        string? warning = null;
        Assert.Null(SettingsStore.Parse("{ not valid json !!!", w => warning = w));
        Assert.NotNull(warning);
    }

    [Fact]
    public void Parse_ReturnsSettingsOnValidInput()
        => Assert.Equal(1500, SettingsStore.Parse("""{ "pollIntervalMs": 1500 }""")!.PollIntervalMs);

    [Fact]
    public void Clone_ProducesIndependentDeepCopy()
    {
        var source = new AppSettings();
        var clone = SettingsStore.Clone(source);

        clone.Appearance.FontName = "Cascadia Mono";
        clone.Metrics.Cpu.ShowTemp = false;
        clone.Network.AdapterOverride = "Wi-Fi";

        Assert.NotSame(source.Appearance, clone.Appearance);
        Assert.NotSame(source.Metrics.Cpu, clone.Metrics.Cpu);
        Assert.Equal("Consolas", source.Appearance.FontName);
        Assert.True(source.Metrics.Cpu.ShowTemp);
        Assert.Null(source.Network.AdapterOverride);
    }

    [Fact]
    public void Clone_RoundTripsEveryField()
    {
        var source = new AppSettings
        {
            PollIntervalMs = 1234,
            SensorIntervalMs = 4321,
        };
        source.Appearance.FontSizePt = 13.5f;   // fractional: shortest-round-trippable float
        source.Appearance.TextOverride = "#80FF00FF";
        source.Appearance.BackgroundOverride = null;
        source.Appearance.Theme = "dark";
        source.Behavior.MultiMonitor = false;
        source.Network.AdapterOverride = "Ethernet 2";
        source.Positioning.VerticalOffsetPx = -7;
        source.Logging.MaxSizeKb = 64;

        Assert.Equal(SettingsStore.Serialize(source), SettingsStore.Serialize(SettingsStore.Clone(source)));
    }

    [Fact]
    public void Clone_PreservesNullOverrides()
    {
        var clone = SettingsStore.Clone(new AppSettings());
        Assert.Null(clone.Appearance.BackgroundOverride);
        Assert.Null(clone.Appearance.TextOverride);
        Assert.Null(clone.Network.AdapterOverride);
    }

    /// <summary>
    /// Pins the self-write suppression contract: PositionController recognises its own write by
    /// comparing the returned text against File.ReadAllText. A BOM or newline translation here
    /// would make the app silently double-reload after every save.
    /// </summary>
    [Fact]
    public void SaveAndCapture_ReturnsExactlyWhatIsOnDisk()
    {
        var s = new AppSettings { PollIntervalMs = 777 };
        string written = SettingsStore.SaveAndCapture(SettingsPath, s);
        Assert.Equal(File.ReadAllText(SettingsPath), written);
    }
}
