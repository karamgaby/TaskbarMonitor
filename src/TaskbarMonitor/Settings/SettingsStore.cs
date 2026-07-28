using System.Text.Json;

namespace TaskbarMonitor.Settings;

public static class SettingsStore
{
    /// <summary>Missing file → write defaults. Corrupt file → defaults in memory, user's file untouched.</summary>
    public static AppSettings Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new AppSettings();
                Save(path, defaults);
                return defaults;
            }
            return Parse(File.ReadAllText(path), warn) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Failed to load settings ({ex.GetType().Name}: {ex.Message}); using defaults (file left untouched)");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Null when the text is unparseable, so the caller can choose between "use defaults" (first
    /// load) and "keep what we already have" (hot reload — applying defaults to live strips over a
    /// transient read error would look like the settings were wiped).
    /// </summary>
    public static AppSettings? Parse(string text, Action<string>? warn = null)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings);
            if (loaded is null)
                warn?.Invoke("settings.json deserialized to null; using defaults (file left untouched)");
            return loaded;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Failed to load settings ({ex.GetType().Name}: {ex.Message}); using defaults (file left untouched)");
            return null;
        }
    }

    /// <summary>The exact text <see cref="Save"/> writes. Also the canonical form for change detection.</summary>
    public static string Serialize(AppSettings settings)
        => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    /// <summary>
    /// Deep copy through the source-generated serializer: no reflection, and structurally identical
    /// to a save/load round-trip, so a clone can never drift from what lands on disk. Every member
    /// is a primitive or nullable string, so the round-trip is lossless (nulls stay null, and float
    /// uses shortest-round-trippable formatting).
    /// </summary>
    public static AppSettings Clone(AppSettings settings)
        => JsonSerializer.Deserialize(Serialize(settings), SettingsJsonContext.Default.AppSettings)
           ?? new AppSettings();

    /// <summary>
    /// Writes and returns the exact text written, so the caller can recognise its own write when the
    /// FileSystemWatcher reports it. File.WriteAllText/ReadAllText round-trip byte-for-byte (UTF-8,
    /// no BOM, no newline translation), which is what makes that comparison sound.
    /// </summary>
    public static string SaveAndCapture(string path, AppSettings settings)
    {
        string json = Serialize(settings);
        File.WriteAllText(path, json);
        return json;
    }

    public static void Save(string path, AppSettings settings) => SaveAndCapture(path, settings);
}
