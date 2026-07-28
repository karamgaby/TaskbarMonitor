using System.Runtime.InteropServices;
using TaskbarMonitor.Positioning;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Sensors;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        string settingsPath = Path.Combine(baseDir, "settings.json");

        // Log first, with defaults for its own settings: Load's warning is the only account of a
        // corrupt settings.json, and initialising the log afterwards discarded it — the strip
        // silently reverted to stock with nothing on disk explaining why. Re-init below once the
        // real logging settings are known; a corrupt file leaves the defaults in place, which is
        // what we want for reporting the corruption.
        string logPath = Path.Combine(baseDir, "monitor.log");
        var logDefaults = new LoggingSettings();
        Log.Init(logPath, logDefaults.Enabled, logDefaults.MaxSizeKb);
        var settings = SettingsStore.Load(settingsPath, Log.Warn);
        Log.Init(logPath, settings.Logging.Enabled, settings.Logging.MaxSizeKb);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Log.Info($"TaskbarMonitor v{typeof(Program).Assembly.GetName().Version} starting. " +
                 $"Elevated={ElevationInfo.IsElevated}, PawnIO={ElevationInfo.PawnIoState()}, " +
                 $"args=[{string.Join(' ', args)}]");

        if (args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase)))
            return RunConsole(args, settings);

        using var mutex = new Mutex(true, @"Global\TaskbarMonitor_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log.Info("Another instance is already running; exiting");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var engine = new SensorEngine(settings);
        engine.Start();
        using var controller = new PositionController(settings, engine, settingsPath);
        Application.Run(controller);
        Log.Info("TaskbarMonitor exited cleanly");
        return 0;
    }

    /// <summary>Diagnostic mode: prints the composed line to stdout once per poll interval.
    /// Usage: TaskbarMonitor.exe --console [seconds]</summary>
    private static int RunConsole(string[] args, AppSettings settings)
    {
        AttachConsole(unchecked((uint)-1)); // best effort; redirected stdout works regardless

        int seconds = 0;
        foreach (var a in args)
            if (int.TryParse(a, out int n)) seconds = n;

        using var engine = new SensorEngine(settings);
        engine.Start();
        Console.WriteLine($"# TaskbarMonitor --console  Elevated={ElevationInfo.IsElevated}  PawnIO={ElevationInfo.PawnIoState()}");

        var end = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(settings.PollIntervalMs);
            var snap = engine.Latest;
            Console.WriteLine($"{LineComposer.Compose(snap, settings)}   [adapter: {snap.AdapterAlias ?? "?"}]");
        }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);
}
