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
        Assert.True(ThemeWatcher.RelativeLuminance(adjusted) <= 0.15);
    }

    /// <summary>
    /// The thresholds are asymmetric because the backgrounds are. Against Win11's light taskbar
    /// (#F3F3F3, L ≈ 0.876) the old symmetric 0.35 ceiling allowed ≈2.3:1 — well under WCAG AA.
    /// Bright accents are the case that actually reached it; ramp index 5 handles the rest.
    /// </summary>
    [Theory]
    [InlineData(0xFF, 0xF1, 0x00)] // yellow
    [InlineData(0xB0, 0xFF, 0x00)] // lime
    [InlineData(0x00, 0xFF, 0xFF)] // cyan
    [InlineData(0xFF, 0xFF, 0xFF)] // white
    public void EnsureReadable_ClearsWcagAaAgainstTheLightTaskbar(int r, int g, int b)
    {
        var adjusted = ThemeWatcher.EnsureReadable(Color.FromArgb(r, g, b), darkTheme: false);
        Assert.True(ContrastRatio(adjusted, Color.FromArgb(0xF3, 0xF3, 0xF3)) >= 4.5,
            $"#{r:X2}{g:X2}{b:X2} corrected to #{adjusted.R:X2}{adjusted.G:X2}{adjusted.B:X2}, "
            + $"contrast {ContrastRatio(adjusted, Color.FromArgb(0xF3, 0xF3, 0xF3)):F2}:1");
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00)] // black
    [InlineData(0x10, 0x10, 0x40)] // deep navy
    [InlineData(0x6B, 0x00, 0x00)] // dark red
    public void EnsureReadable_ClearsWcagAaAgainstTheDarkTaskbar(int r, int g, int b)
    {
        var adjusted = ThemeWatcher.EnsureReadable(Color.FromArgb(r, g, b), darkTheme: true);
        Assert.True(ContrastRatio(adjusted, Color.FromArgb(0x20, 0x20, 0x20)) >= 4.5,
            $"#{r:X2}{g:X2}{b:X2} corrected to #{adjusted.R:X2}{adjusted.G:X2}{adjusted.B:X2}, "
            + $"contrast {ContrastRatio(adjusted, Color.FromArgb(0x20, 0x20, 0x20)):F2}:1");
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = ThemeWatcher.RelativeLuminance(a);
        double lb = ThemeWatcher.RelativeLuminance(b);
        (double hi, double lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
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

    /// <summary>
    /// The override supplies colour; backgroundAlpha supplies opacity. Previously the override
    /// replaced the background wholesale including its alpha, which made the alpha slider a silent
    /// no-op whenever one was set.
    /// </summary>
    [Fact]
    public void Resolve_BackgroundOverrideSuppliesRgbAndAlphaComesFromSetting()
    {
        var palette = ThemeWatcher.Resolve(Dark(a =>
        {
            a.BackgroundOverride = "#204060";
            a.BackgroundAlpha = 0x40;
        }));
        Assert.Equal(Color.FromArgb(0x40, 0x20, 0x40, 0x60), palette.Background);
    }

    /// <summary>An alpha byte on a hand-edited #AARRGGBB override is ignored, not an error.</summary>
    [Fact]
    public void Resolve_BackgroundOverrideAlphaByteIsIgnored()
    {
        var palette = ThemeWatcher.Resolve(Dark(a =>
        {
            a.BackgroundOverride = "#FF204060";
            a.BackgroundAlpha = 0x20;
        }));
        Assert.Equal(Color.FromArgb(0x20, 0x20, 0x40, 0x60), palette.Background);
    }

    /// <summary>The slider still works when an override is set — the whole point of the change.</summary>
    [Fact]
    public void Resolve_BackgroundOverrideStillRespondsToAlphaChanges()
    {
        var low = ThemeWatcher.Resolve(Dark(a => { a.BackgroundOverride = "#204060"; a.BackgroundAlpha = 10; }));
        var high = ThemeWatcher.Resolve(Dark(a => { a.BackgroundOverride = "#204060"; a.BackgroundAlpha = 250; }));
        Assert.Equal(10, low.Background.A);
        Assert.Equal(250, high.Background.A);
        Assert.Equal(low.Background.ToArgb() & 0x00FFFFFF, high.Background.ToArgb() & 0x00FFFFFF);
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
