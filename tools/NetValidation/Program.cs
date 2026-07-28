using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using TaskbarMonitor.Sensors.Network;

// Network-accuracy harness: samples the app's own measurement path (DefaultRouteResolver +
// NetRateSampler over GetIfEntry2) side by side with the OS reference ("Network Interface \
// Bytes Received/sec" — the same counter Task Manager charts) during a sustained download.
// PASS = steady-state means agree within 10%.
//
// Usage: NetValidation [--seconds 30] [--bytes 100000000] [--mode internet|loopback]
//                      [--adapter <alias>] [--url <download url>]
// Bandwidth note: --bytes is a hard cap on downloaded data (default 100 MB).

int seconds = GetIntArg("--seconds", 30);
long byteCap = GetLongArg("--bytes", 100_000_000);
string mode = GetStrArg("--mode", "internet");
string? adapterOverride = GetStrArg("--adapter", null);
// Cloudflare's __down endpoint 403s non-browser TLS fingerprints; OVH serves plain files.
// --bytes caps consumption regardless of file size (the stream is abandoned at the cap).
string url = GetStrArg("--url", "https://proof.ovh.net/files/100Mb.dat")!;

return mode.Equals("loopback", StringComparison.OrdinalIgnoreCase)
    ? RunLoopback(seconds, byteCap)
    : RunInternet(seconds, byteCap, adapterOverride, url);

int RunInternet(int secs, long cap, string? adapterAlias, string dlUrl)
{
    using var resolver = new DefaultRouteResolver(adapterAlias);
    resolver.RefreshIfNeeded(true);
    if (resolver.CurrentIfIndex < 0)
    {
        Console.Error.WriteLine("FAIL: could not resolve a default-route adapter");
        return 1;
    }
    Console.WriteLine($"App-path adapter: ifIndex={resolver.CurrentIfIndex} alias='{resolver.CurrentAlias}'");

    var nic = NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => IfEntry2CounterSource.TryGetIndex(n, out int i) && i == resolver.CurrentIfIndex);
    if (nic is null)
    {
        Console.Error.WriteLine("FAIL: adapter not found in NetworkInterface list");
        return 1;
    }

    string? instance = MatchPdhInstance(nic.Description);
    if (instance is null)
    {
        Console.Error.WriteLine($"FAIL: no 'Network Interface' counter instance matches '{nic.Description}'");
        return 1;
    }
    Console.WriteLine($"PDH reference instance: '{instance}' (Task Manager's source for this adapter)");

    using var pdhDown = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance);
    using var pdhUp = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance);
    pdhDown.NextValue();
    pdhUp.NextValue();

    using var cts = new CancellationTokenSource();
    long downloaded = 0;
    var downloadDone = false;
    var downloadTask = Task.Run(async () =>
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetValidation/1.0");
            using var resp = await http.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Console.WriteLine($"download: HTTP {(int)resp.StatusCode}, Content-Length={resp.Content.Headers.ContentLength?.ToString() ?? "?"}");
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                Interlocked.Add(ref downloaded, n);
                if (Interlocked.Read(ref downloaded) >= cap) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"download error: {ex.Message}"); }
        finally { Volatile.Write(ref downloadDone, true); }
    });

    var sampler = new NetRateSampler(new IfEntry2CounterSource());
    var sw = Stopwatch.StartNew();
    sampler.Sample(resolver.CurrentIfIndex, sw.Elapsed.TotalSeconds); // baseline

    var rows = new List<(double t, double? app, double? appUp, double pdh, double pdhUp, bool active)>();
    var csv = new StringBuilder("elapsedSec,appDownBps,appUpBps,pdhDownBps,pdhUpBps,downloadActive\n");

    for (int i = 0; i < secs; i++)
    {
        Thread.Sleep(1000);
        resolver.RefreshIfNeeded(false); // event-driven only; mid-run switch shows up as one skipped sample
        double t = sw.Elapsed.TotalSeconds;
        var app = sampler.Sample(resolver.CurrentIfIndex, t);
        double pd = pdhDown.NextValue(), pu = pdhUp.NextValue();
        bool active = !Volatile.Read(ref downloadDone);
        rows.Add((t, app?.DownBps, app?.UpBps, pd, pu, active));
        csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{t:F2},{app?.DownBps ?? -1:F0},{app?.UpBps ?? -1:F0},{pd:F0},{pu:F0},{active}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"t={t,6:F1}s  app={FormatMbps(app?.DownBps),10}  pdh={FormatMbps(pd),10}  active={active}"));
        if (!active && i >= 10) break; // cap reached; no point sampling idle air
    }

    cts.Cancel();
    try { downloadTask.Wait(3000); } catch { }

    string csvPath = Path.Combine(Environment.CurrentDirectory, $"netvalidation_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    File.WriteAllText(csvPath, csv.ToString());
    Console.WriteLine($"\nCSV: {csvPath}   (downloaded {downloaded / 1_000_000.0:F1} MB)");

    // Steady window: drop ramp-up and tail
    var active_ = rows.Where(r => r.active && r.app is not null).ToList();
    var steady = active_.Skip(5).SkipLast(2).ToList();
    if (steady.Count < 8)
    {
        Console.Error.WriteLine($"FAIL: only {steady.Count} steady samples — increase --bytes or --seconds (link may be faster than the cap allows)");
        return 1;
    }

    double meanApp = steady.Average(r => r.app!.Value);
    double meanPdh = steady.Average(r => r.pdh);
    double deviation = Math.Abs(meanApp - meanPdh) / Math.Max(1, meanPdh);
    Console.WriteLine($"steady samples: {steady.Count}");
    Console.WriteLine($"mean app  : {meanApp,12:F0} B/s  ({FormatMbps(meanApp)})");
    Console.WriteLine($"mean pdh  : {meanPdh,12:F0} B/s  ({FormatMbps(meanPdh)})");
    Console.WriteLine($"deviation : {deviation:P2}  (assert ≤ 10%)");
    Console.WriteLine(deviation <= 0.10 ? "RESULT: PASS" : "RESULT: FAIL");
    return deviation <= 0.10 ? 0 : 1;
}

// Loopback mode: validates the delta/timing math without touching the internet. A local HTTP
// server streams data to a local client; ground truth = server-side served-byte count. Note:
// loopback traffic never crosses the physical NIC, so this does NOT validate adapter
// attribution — internet mode remains the primary assertion.
int RunLoopback(int secs, long cap)
{
    var loopbackNic = NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
    if (loopbackNic is null || !IfEntry2CounterSource.TryGetIndex(loopbackNic, out int loIndex))
    {
        Console.Error.WriteLine("FAIL: loopback interface not found");
        return 2;
    }
    Console.WriteLine($"Loopback ifIndex={loIndex}");

    const string prefix = "http://127.0.0.1:18877/";
    using var listener = new HttpListener();
    listener.Prefixes.Add(prefix);
    listener.Start();

    long served = 0;
    using var cts = new CancellationTokenSource();
    var serverTask = Task.Run(async () =>
    {
        var block = new byte[1 << 20];
        Random.Shared.NextBytes(block);
        while (!cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { break; }
            _ = Task.Run(async () =>
            {
                try
                {
                    ctx.Response.ContentLength64 = cap;
                    long sent = 0;
                    while (sent < cap && !cts.IsCancellationRequested)
                    {
                        int n = (int)Math.Min(block.Length, cap - sent);
                        await ctx.Response.OutputStream.WriteAsync(block.AsMemory(0, n), cts.Token);
                        sent += n;
                        Interlocked.Add(ref served, n);
                    }
                    ctx.Response.Close();
                }
                catch { }
            });
        }
    });

    var clientTask = Task.Run(async () =>
    {
        try
        {
            using var http = new HttpClient();
            await using var stream = await http.GetStreamAsync(prefix, cts.Token);
            var buf = new byte[81920];
            while (await stream.ReadAsync(buf, cts.Token) > 0) { }
        }
        catch { }
    });

    var sampler = new NetRateSampler(new IfEntry2CounterSource());
    var sw = Stopwatch.StartNew();
    sampler.Sample(loIndex, sw.Elapsed.TotalSeconds);
    long prevServed = 0;
    double prevT = sw.Elapsed.TotalSeconds;

    var pairs = new List<(double app, double truth)>();
    for (int i = 0; i < secs; i++)
    {
        Thread.Sleep(1000);
        double t = sw.Elapsed.TotalSeconds;
        long s = Interlocked.Read(ref served);
        var app = sampler.Sample(loIndex, t);
        double truth = (s - prevServed) / (t - prevT);
        prevServed = s; prevT = t;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"t={t,6:F1}s  app={FormatMbps(app?.DownBps),10}  truth={FormatMbps(truth),10}"));
        if (app?.DownBps > 0 && truth > 0) pairs.Add((app.Value.DownBps, truth));
        if (s >= cap) break;
    }
    cts.Cancel();
    listener.Stop();
    try { Task.WaitAll(new[] { serverTask, clientTask }, 3000); } catch { }

    if (pairs.Count < 5)
    {
        Console.Error.WriteLine("INCONCLUSIVE: loopback MIB counters not updating on this system — use internet mode");
        return 2;
    }
    var steady = pairs.Skip(2).SkipLast(1).ToList();
    double meanApp = steady.Average(p => p.app);
    double meanTruth = steady.Average(p => p.truth);
    double deviation = Math.Abs(meanApp - meanTruth) / Math.Max(1, meanTruth);
    Console.WriteLine($"mean app: {FormatMbps(meanApp)}  mean truth: {FormatMbps(meanTruth)}  deviation: {deviation:P2}");
    Console.WriteLine(deviation <= 0.10 ? "RESULT: PASS (delta/timing math)" : "RESULT: FAIL");
    return deviation <= 0.10 ? 0 : 1;
}

static string? MatchPdhInstance(string description)
{
    string mangled = description.Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('/', '_').Replace('\\', '_');
    var instances = new PerformanceCounterCategory("Network Interface").GetInstanceNames();
    return instances.FirstOrDefault(i => i.Equals(mangled, StringComparison.OrdinalIgnoreCase))
        ?? instances.FirstOrDefault(i => i.Contains(mangled, StringComparison.OrdinalIgnoreCase)
                                      || mangled.Contains(i, StringComparison.OrdinalIgnoreCase));
}

static string FormatMbps(double? bps)
    => bps is null ? "--" : string.Create(CultureInfo.InvariantCulture, $"{bps * 8 / 1_000_000.0:F1} Mbps");

int GetIntArg(string name, int def) => int.TryParse(GetStrArg(name, null), out int v) ? v : def;
long GetLongArg(string name, long def) => long.TryParse(GetStrArg(name, null), out long v) ? v : def;
string? GetStrArg(string name, string? def)
{
    var a = Environment.GetCommandLineArgs();
    for (int i = 0; i < a.Length - 1; i++)
        if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return a[i + 1];
    return def;
}
