using System.Diagnostics;
using TaskbarMonitor.Sensors.Network;
using TaskbarMonitor.Settings;

namespace TaskbarMonitor.Sensors;

/// <summary>
/// Background sampling loop. Fast lane (pollIntervalMs): network rates, CPU%, RAM.
/// Slow lane (sensorIntervalMs): LHM MSR temps + GPU — a 24-core Raptor Lake Update() reads a
/// pile of MSRs, so it does not run at 1 Hz. The UI thread only ever reads Latest.
/// </summary>
public sealed class SensorEngine : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private volatile AppSettings _settings;
    private volatile SensorSnapshot _latest = SensorSnapshot.Empty;
    private DefaultRouteResolver _resolver;
    private NetRateSampler _sampler;
    private readonly CpuUtilizationPdh _cpu = new();
    private readonly LhmSource _lhm = new();

    private enum GpuMode { Lhm, NvidiaSmi }
    private GpuMode _gpuMode = GpuMode.Lhm;
    private int _lhmGpuNullStreak;

    public SensorSnapshot Latest => _latest;

    public SensorEngine(AppSettings settings)
    {
        _settings = settings;
        _resolver = new DefaultRouteResolver(settings.Network.AdapterOverride);
        _sampler = new NetRateSampler(new IfEntry2CounterSource());
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SensorEngine", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Resume from sleep: force re-resolve + re-baseline (the gap guard also catches this).</summary>
    public void OnPowerResume()
    {
        _resolver.MarkDirty();
        _sampler.Rebaseline();
        Log.Info("Power resume: adapter re-resolve + counter re-baseline forced");
    }

    public void ApplySettings(AppSettings settings)
    {
        var old = _settings;
        _settings = settings;
        if (!string.Equals(old.Network.AdapterOverride, settings.Network.AdapterOverride, StringComparison.OrdinalIgnoreCase))
        {
            var newResolver = new DefaultRouteResolver(settings.Network.AdapterOverride);
            var oldResolver = Interlocked.Exchange(ref _resolver, newResolver);
            oldResolver.Dispose();
            _sampler.Rebaseline();
        }
    }

    private void Loop()
    {
        _lhm.Open();
        if (!_lhm.GpuAvailable)
        {
            _gpuMode = GpuMode.NvidiaSmi;
            Log.Info("No NVIDIA GPU via LHM; using nvidia-smi fallback");
        }

        double lastRouteResolve = -1e9, lastSensorRead = -1e9;
        double? cpuTemp = null, gpuLoad = null, gpuTemp = null;

        while (!_cts.IsCancellationRequested)
        {
            var settings = _settings;
            double now = _clock.Elapsed.TotalSeconds;

            try
            {
                // Adapter resolution (event-dirty or periodic)
                bool periodicDue = now - lastRouteResolve >= Math.Max(1, settings.Network.AdapterRefreshSec);
                if (_resolver.RefreshIfNeeded(periodicDue))
                    _sampler.Rebaseline();
                if (periodicDue) lastRouteResolve = now;

                // Fast lane
                var net = _sampler.Sample(_resolver.CurrentIfIndex, now);
                double? cpuPct = _cpu.Sample();
                double? ramPct = MemoryStatus.Percent();

                // Slow lane
                if (now - lastSensorRead >= settings.SensorIntervalMs / 1000.0)
                {
                    lastSensorRead = now;
                    if (_gpuMode == GpuMode.Lhm)
                    {
                        var (ct, gl, gt) = _lhm.Read();
                        cpuTemp = ct; gpuLoad = gl; gpuTemp = gt;
                        if (gl is null && gt is null)
                        {
                            if (++_lhmGpuNullStreak >= 5)
                            {
                                _gpuMode = GpuMode.NvidiaSmi;
                                Log.Warn("LHM GPU sensors returned null 5 consecutive reads; switching permanently to nvidia-smi");
                            }
                        }
                        else _lhmGpuNullStreak = 0;
                    }
                    if (_gpuMode == GpuMode.NvidiaSmi)
                    {
                        var (gl, gt) = NvidiaSmiFallback.Read();
                        gpuLoad = gl; gpuTemp = gt;
                        if (cpuTemp is null || _lhmGpuNullStreak >= 5)
                            cpuTemp = _lhm.Read().CpuTemp; // CPU temp still comes from LHM
                    }
                }

                _latest = new SensorSnapshot
                {
                    DownBps = net?.DownBps,
                    UpBps = net?.UpBps,
                    CpuPercent = cpuPct,
                    CpuTempC = cpuTemp,
                    RamPercent = ramPct,
                    GpuPercent = gpuLoad,
                    GpuTempC = gpuTemp,
                    AdapterAlias = _resolver.CurrentAlias,
                };
            }
            catch (Exception ex)
            {
                Log.Error("Sensor loop tick failed", ex);
            }

            double spent = _clock.Elapsed.TotalSeconds - now;
            int sleep = Math.Max(100, _settings.PollIntervalMs - (int)(spent * 1000));
            if (_cts.Token.WaitHandle.WaitOne(sleep)) break;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(3000);
        _resolver.Dispose();
        _cpu.Dispose();
        _lhm.Dispose();
        _cts.Dispose();
    }
}
