using System.Text;

namespace TaskbarMonitor;

/// <summary>
/// Minimal rolling file log. Diagnoses "the strip didn't appear after logon": startup state,
/// sensor-source selection, adapter re-resolves, redocks, exceptions.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static long _maxBytes;
    private static bool _enabled;

    public static void Init(string path, bool enabled, int maxSizeKb)
    {
        _path = path;
        _enabled = enabled;
        _maxBytes = Math.Max(64, maxSizeKb) * 1024L;
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex is null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        if (!_enabled || _path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > _maxBytes)
                {
                    var old = _path + ".old";
                    File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { /* logging must never take the app down */ }
        }
    }
}
