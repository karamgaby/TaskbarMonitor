using System.Drawing;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Settings;
using Xunit;

namespace TaskbarMonitor.Tests;

public class ThemeWatcherTests
{
    // Explicit dark/light avoids the registry, so these run the same everywhere
    private static AppearanceSettings Dark(Action<AppearanceSettings>? tweak = null)
    {
        var a = new AppearanceSettings { Theme = "dark" };
        tweak?.Invoke(a);
        return a;
    }

    [Theory]
    [InlineData("#0078D4", 255, 0x00, 0x78, 0xD4)]
    [InlineData("0078D4", 255, 0x00, 0x78, 0xD4)]
    [InlineData("#800078D4", 0x80, 0x00, 0x78, 0xD4)]
    [InlineData("800078D4", 0x80, 0x00, 0x78, 0xD4)]
    public void TryParseHex_AcceptsRgbAndArgb(string input, int a, int r, int g, int b)
    {
        Assert.True(ThemeWatcher.TryParseHex(input, out var c));
        Assert.Equal(Color.FromArgb(a, r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]
    [InlineData("#0078D")]
    [InlineData("#0078D4FF00")]
    [InlineData("#ZZZZZZ")]
    public void TryParseHex_RejectsMalformed(string? input)
        => Assert.False(ThemeWatcher.TryParseHex(input, out _));

    [Fact]
    public void EnsureReadable_LightensTooDarkColorOnDarkTheme()
    {
        var adjusted = ThemeWatcher.EnsureReadable(Color.FromArgb(0x0A, 0x0A, 0x18), darkTheme: true);
        Assert.True(ThemeWatcher.RelativeLuminance(adjusted) >= 0.35);
    }

    [Fact]
    public void EnsureReadable_DarkensTooLightColorOnLightTheme()
    {
        var adjusted = ThemeWatcher.EnsureReadable(Color.FromArgb(0xFA, 0xFA, 0xE0), darkTheme: false);
        Assert.True(ThemeWatcher.RelativeLuminance(adjusted) <= 0.35);
    }

    [Fact]
    public void EnsureReadable_LeavesAlreadyReadableColorAlone()
    {
        var ok = Color.FromArgb(0x9C, 0xD8, 0xFF);
        Assert.Equal(ok, ThemeWatcher.EnsureReadable(ok, darkTheme: true));
    }

    [Fact]
    public void Resolve_DefaultBackgroundIsFullyTransparent()
        => Assert.Equal(0, ThemeWatcher.Resolve(Dark()).Background.A);

    [Fact]
    public void Resolve_BackgroundAlphaIsHonoured()
        => Assert.Equal(120, ThemeWatcher.Resolve(Dark(a => a.BackgroundAlpha = 120)).Background.A);

    [Fact]
    public void Resolve_TextOverrideWinsOverAccent()
    {
        var palette = ThemeWatcher.Resolve(Dark(a => a.TextOverride = "#FF00FF"));
        Assert.Equal(Color.FromArgb(255, 0xFF, 0x00, 0xFF), palette.Text);
    }

    [Fact]
    public void Resolve_BackgroundOverrideCarriesAlpha()
    {
        var palette = ThemeWatcher.Resolve(Dark(a => a.BackgroundOverride = "#40202020"));
        Assert.Equal(Color.FromArgb(0x40, 0x20, 0x20, 0x20), palette.Background);
    }

    [Fact]
    public void Resolve_ThemeSourceUsesNeutralTextNotAccent()
    {
        var palette = ThemeWatcher.Resolve(Dark(a => a.TextColorSource = "theme"));
        Assert.Equal(Color.FromArgb(0xE8, 0xE8, 0xE8), palette.Text);
    }

    [Fact]
    public void Resolve_AccentTextIsReadableOnBothThemes()
    {
        var dark = ThemeWatcher.Resolve(new AppearanceSettings { Theme = "dark" });
        var light = ThemeWatcher.Resolve(new AppearanceSettings { Theme = "light" });
        Assert.True(ThemeWatcher.RelativeLuminance(dark.Text) >= 0.35);
        Assert.True(ThemeWatcher.RelativeLuminance(light.Text) <= 0.35);
    }

    [Fact]
    public void Resolve_ShadowOpposesTheme()
    {
        Assert.Equal(0, ThemeWatcher.Resolve(new AppearanceSettings { Theme = "dark" }).Shadow.R);
        Assert.Equal(255, ThemeWatcher.Resolve(new AppearanceSettings { Theme = "light" }).Shadow.R);
    }
}
