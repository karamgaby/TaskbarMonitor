using TaskbarMonitor.Rendering;
using TaskbarMonitor.Settings;
using Xunit;

namespace TaskbarMonitor.Tests;

public class SettingsFieldRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("#0078D4", true)]
    [InlineData("0078D4", true)]
    [InlineData("#800078D4", true)]
    [InlineData("#FFF", false)]
    [InlineData("#0078D", false)]
    [InlineData("nope", false)]
    public void IsValidColorField_MatchesTheRenderersParser(string? text, bool expected)
        => Assert.Equal(expected, SettingsFieldRules.IsValidColorField(text));

    [Theory]
    [InlineData("  #FF0000 ", "#FF0000")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeColorField_TrimsAndMapsEmptyToNull(string? text, string? expected)
        => Assert.Equal(expected, SettingsFieldRules.NormalizeColorField(text));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FormatColor_RoundTripsThroughTryParseHex(bool includeAlpha)
    {
        var source = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
        string text = SettingsFieldRules.FormatColor(source, includeAlpha);

        Assert.True(ThemeWatcher.TryParseHex(text, out var parsed));
        Assert.Equal(source.R, parsed.R);
        Assert.Equal(source.G, parsed.G);
        Assert.Equal(source.B, parsed.B);
        Assert.Equal(includeAlpha ? source.A : 255, parsed.A);
    }

    [Theory]
    [InlineData("(automatic)", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData(" Ethernet ", "Ethernet")]
    public void NormalizeAdapter_MapsSentinelAndEmptyToNull(string? text, string? expected)
        => Assert.Equal(expected, SettingsFieldRules.NormalizeAdapter(text));

    [Theory]
    [InlineData("DARK", "dark")]
    [InlineData(" light ", "light")]
    [InlineData("auto", "auto")]
    [InlineData("purple", "auto")]
    [InlineData(null, "auto")]
    public void NormalizeTheme_UnknownBecomesAuto(string? value, string expected)
        => Assert.Equal(expected, SettingsFieldRules.NormalizeTheme(value));

    [Theory]
    [InlineData("Theme", "theme")]
    [InlineData(" theme ", "theme")]
    [InlineData("accent", "accent")]
    [InlineData("anything", "accent")]
    [InlineData(null, "accent")]
    public void NormalizeTextColorSource_AnythingButThemeIsAccent(string? value, string expected)
        => Assert.Equal(expected, SettingsFieldRules.NormalizeTextColorSource(value));
}
