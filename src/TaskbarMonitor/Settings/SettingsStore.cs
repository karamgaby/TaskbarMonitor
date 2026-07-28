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
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);
            if (loaded is null)
            {
                warn?.Invoke($"settings.json deserialized to null; using defaults (file left untouched)");
                return new AppSettings();
            }
            return loaded;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Failed to load settings ({ex.GetType().Name}: {ex.Message}); using defaults (file left untouched)");
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
        => File.WriteAllText(path, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
}
