using TaskbarMonitor.Rendering;

namespace TaskbarMonitor.Settings;

/// <summary>
/// Validation and normalization the settings window applies between its controls and the draft.
/// Deliberately Form-free (System.Drawing only) so it can be unit-tested — no test in this repo
/// constructs a Form.
///
/// The theme / textColorSource rules mirror <see cref="ThemeWatcher.Resolve"/> exactly, so
/// normalizing an unrecognised value changes the text in settings.json but not one rendered pixel.
/// </summary>
public static class SettingsFieldRules
{
    /// <summary>Combo sentinel for "no adapter override"; maps to null in the settings tree.</summary>
    public const string AutomaticAdapter = "(automatic)";

    /// <summary>Empty means "no override", which is valid. Anything else must parse as a hex color.</summary>
    public static bool IsValidColorField(string? text)
        => string.IsNullOrWhiteSpace(text) || ThemeWatcher.TryParseHex(text, out _);

    /// <summary>Empty/whitespace becomes null, never "" — that is what the defaults and docs specify.</summary>
    public static string? NormalizeColorField(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static string FormatColor(Color c, bool includeAlpha)
        => includeAlpha ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string? NormalizeAdapter(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) || t == AutomaticAdapter ? null : t;
    }

    /// <summary>Unknown values become "auto" — exactly how ThemeWatcher already treats them.</summary>
    public static string NormalizeTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => "auto",
    };

    /// <summary>Unknown values become "accent" — ThemeWatcher treats anything but "theme" as accent.</summary>
    public static string NormalizeTextColorSource(string? value)
        => string.Equals(value?.Trim(), "theme", StringComparison.OrdinalIgnoreCase) ? "theme" : "accent";
}
