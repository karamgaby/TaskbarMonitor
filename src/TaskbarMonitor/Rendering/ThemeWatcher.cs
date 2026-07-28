using Microsoft.Win32;
using TaskbarMonitor.Interop;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.Rendering;

/// <summary>Colors for one strip. Background carries alpha; the strip is transparent by default.</summary>
public readonly record struct StripPalette(Color Background, Color Text, Color Shadow);

/// <summary>
/// Resolves strip colors from settings, the system light/dark theme and the Windows accent color.
/// The strip paints onto the taskbar with no panel behind it, so text contrast is enforced here
/// rather than being guaranteed by a known background.
/// </summary>
public static class ThemeWatcher
{
    /// <summary>
    /// Relative-luminance the text must clear on a dark taskbar. Win11's dark bar is around #202020
    /// (L ≈ 0.014), so 0.35 lands near 5.6:1 — comfortably past WCAG AA.
    /// </summary>
    private const double DarkLuminanceFloor = 0.35;

    /// <summary>
    /// Ceiling on a light taskbar. The two themes are not symmetric: Win11's light bar is around
    /// #F3F3F3 (L ≈ 0.876), where L = 0.35 is only ≈2.3:1 — well under AA. Clearing 4.5:1 against
    /// that background needs L ≤ ≈0.156, so the ceiling is much lower than the dark floor.
    /// Ramp index 5 already lands most accents in range; this backstops bright ones (yellow, lime).
    /// </summary>
    private const double LightLuminanceCeiling = 0.15;

    public static StripPalette Resolve(AppearanceSettings appearance)
    {
        bool light = appearance.Theme.ToLowerInvariant() switch
        {
            "light" => true,
            "dark" => false,
            _ => SystemUsesLightTheme(),
        };

        // Neutral fallback, also used when textColorSource is "theme"
        var text = light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.FromArgb(0xE8, 0xE8, 0xE8);

        if (!string.Equals(appearance.TextColorSource, "theme", StringComparison.OrdinalIgnoreCase)
            && TryGetAccentColor(light, out var accent))
            text = EnsureReadable(accent, darkTheme: !light);

        int alpha = Math.Clamp(appearance.BackgroundAlpha, 0, 255);
        var bgRgb = light ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x20, 0x20, 0x20);

        // The override supplies RGB only; alpha always comes from backgroundAlpha. Taking alpha from
        // the override too made the slider a silent no-op whenever one was set, which then needed a
        // disabled state and a hint to explain, and forced #AARRGGBB to exist so alpha could be typed
        // (ColorDialog cannot pick it). One rule instead: colour here, opacity there.
        if (TryParseHex(appearance.BackgroundOverride, out var bgOverride)) bgRgb = bgOverride;
        var bg = Color.FromArgb(alpha, bgRgb);

        if (TryParseHex(appearance.TextOverride, out var textOverride)) text = textOverride;

        // Shadow opposes the text so it reads on either theme
        var shadow = light ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0);
        return new StripPalette(bg, text, shadow);
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// AccentPalette is 8 four-byte RGBA entries. Indices 0-6 are a lightest-to-darkest ramp of the
    /// user's accent (index 3 is the base, matching AccentColorMenu); index 7 is an unrelated
    /// complement color and must never be used. Verified against a #0078D4 accent: index 3 reads
    /// 00 78 D4, AccentColorMenu reads 0xFFD47800 (AABBGGRR) and DWM colorization 0xC40078D4.
    /// <see cref="EnsureReadable"/> still backstops the choice for unusual accents.
    /// </summary>
    public static bool TryGetAccentColor(bool lightTheme, out Color color)
    {
        color = default;
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        try
        {
            if (Registry.GetValue(key, "AccentPalette", null) is byte[] palette && palette.Length >= 28)
            {
                int index = lightTheme ? 5 : 1;   // darker shade on light theme, lighter on dark
                int o = index * 4;
                color = Color.FromArgb(palette[o], palette[o + 1], palette[o + 2]); // RGBA
                return true;
            }
        }
        catch { }

        try
        {
            // 0xAABBGGRR
            if (Registry.GetValue(key, "AccentColorMenu", null) is int abgr)
            {
                color = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                return true;
            }
        }
        catch { }

        try
        {
            // 0xAARRGGBB, post-blend so it can drift from the picked accent — fallback only
            if (NativeMethods.DwmGetColorizationColor(out uint argb, out _) == 0)
            {
                color = Color.FromArgb((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>Lerps toward white (dark theme) or black (light theme) until the color clears the luminance bar.</summary>
    public static Color EnsureReadable(Color c, bool darkTheme)
    {
        var target = darkTheme ? Color.White : Color.Black;
        for (int i = 0; i < 24; i++)
        {
            double l = RelativeLuminance(c);
            if (darkTheme ? l >= DarkLuminanceFloor : l <= LightLuminanceCeiling) return c;
            c = Lerp(c, target, 0.12);
        }
        return c;
    }

    public static double RelativeLuminance(Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Lerp(Color from, Color to, double t) => Color.FromArgb(
        from.A,
        (int)Math.Round(from.R + (to.R - from.R) * t),
        (int)Math.Round(from.G + (to.G - from.G) * t),
        (int)Math.Round(from.B + (to.B - from.B) * t));

    /// <summary>Accepts "#RRGGBB" / "RRGGBB" (opaque) and "#AARRGGBB" / "AARRGGBB".</summary>
    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length is not (6 or 8)) return false;
        if (!uint.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint v)) return false;
        int a = h.Length == 8 ? (int)((v >> 24) & 0xFF) : 255;
        color = Color.FromArgb(a, (int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
        return true;
    }
}
