using Microsoft.Win32;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.Rendering;

/// <summary>Resolves strip colors from settings + the system light/dark taskbar theme.</summary>
public static class ThemeWatcher
{
    public static (Color Background, Color Text, Color Separator) Resolve(AppearanceSettings appearance)
    {
        bool light = appearance.Theme.ToLowerInvariant() switch
        {
            "light" => true,
            "dark" => false,
            _ => SystemUsesLightTheme(),
        };

        var bg = light ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x20, 0x20, 0x20);
        var text = light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.FromArgb(0xE8, 0xE8, 0xE8);
        var sep = light ? Color.FromArgb(0xA0, 0xA0, 0xA0) : Color.FromArgb(0x60, 0x60, 0x60);

        if (TryParseHex(appearance.BackgroundOverride, out var bgOverride)) bg = bgOverride;
        if (TryParseHex(appearance.TextOverride, out var textOverride)) text = textOverride;
        return (bg, text, sep);
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

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        color = Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        return true;
    }
}
