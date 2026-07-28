using System.Net.NetworkInformation;
using TaskbarMonitor.Interop;

namespace TaskbarMonitor.Sensors.Network;

/// <summary>
/// Resolves the interface that owns the default route via GetBestInterfaceEx (pure route-table
/// lookup toward 8.8.8.8 — no packet sent). This inherently skips WSL2-mirrored / Docker
/// vEthernet adapters. Re-resolves when NotifyRouteChange2 fires or on the periodic refresh.
/// </summary>
public sealed class DefaultRouteResolver : IDisposable
{
    private static readonly byte[] Dest = NativeMethods.MakeSockaddrIn(8, 8, 8, 8);

    private readonly string? _adapterOverride;
    private readonly NativeMethods.RouteChangeCallback _callback; // rooted: prevents GC of the native thunk
    private IntPtr _notifyHandle;
    private volatile bool _dirty = true;

    public int CurrentIfIndex { get; private set; } = -1;
    public string? CurrentAlias { get; private set; }

    public DefaultRouteResolver(string? adapterOverride)
    {
        _adapterOverride = adapterOverride;
        _callback = (_, _, _) => _dirty = true;
        uint err = NativeMethods.NotifyRouteChange2(NativeMethods.AF_UNSPEC, _callback, IntPtr.Zero, false, out _notifyHandle);
        if (err != 0)
        {
            _notifyHandle = IntPtr.Zero;
            Log.Warn($"NotifyRouteChange2 registration failed ({err}); relying on periodic re-resolve only");
        }
    }

    public void MarkDirty() => _dirty = true;

    /// <summary>Returns true if the active interface changed (caller should re-baseline the sampler).</summary>
    public bool RefreshIfNeeded(bool periodicDue)
    {
        if (!_dirty && !periodicDue) return false;
        _dirty = false;

        int newIndex = -1;
        string? newAlias = null;

        if (!string.IsNullOrWhiteSpace(_adapterOverride))
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, _adapterOverride, StringComparison.OrdinalIgnoreCase)) continue;
                if (IfEntry2CounterSource.TryGetIndex(nic, out int idx)) { newIndex = idx; newAlias = nic.Name; }
                break;
            }
            if (newIndex < 0)
                Log.Warn($"adapterOverride '{_adapterOverride}' not found; falling back to default-route resolution");
        }

        if (newIndex < 0 && NativeMethods.GetBestInterfaceEx(Dest, out uint best) == 0)
        {
            newIndex = (int)best;
            newAlias = AliasOf(newIndex);
        }

        bool changed = newIndex != CurrentIfIndex;
        if (changed)
        {
            Log.Info($"Active adapter: ifIndex={newIndex} alias='{newAlias ?? "?"}' (was {CurrentIfIndex})");
            CurrentIfIndex = newIndex;
            CurrentAlias = newAlias;
        }
        return changed;
    }

    private static string? AliasOf(int ifIndex)
    {
        try
        {
            var row = new NativeMethods.MIB_IF_ROW2 { InterfaceIndex = (uint)ifIndex };
            if (NativeMethods.GetIfEntry2(ref row) == 0) return row.Alias;
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_notifyHandle != IntPtr.Zero)
        {
            NativeMethods.CancelMibChangeNotify2(_notifyHandle);
            _notifyHandle = IntPtr.Zero;
        }
    }
}
