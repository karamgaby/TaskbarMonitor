using System.Net.NetworkInformation;
using TaskbarMonitor.Interop;

namespace TaskbarMonitor.Sensors.Network;

/// <summary>Live counters: GetIfEntry2 (64-bit InOctets/OutOctets) with a managed fallback
/// (NetworkInterface.GetIPStatistics, itself GetIfEntry2-backed) if the struct marshaling misbehaves.</summary>
public sealed class IfEntry2CounterSource : IByteCounterSource
{
    private bool _nativeBroken;

    public bool TryGetCounters(int ifIndex, out ulong inOctets, out ulong outOctets)
    {
        inOctets = outOctets = 0;
        if (!_nativeBroken)
        {
            try
            {
                var row = new NativeMethods.MIB_IF_ROW2 { InterfaceIndex = (uint)ifIndex };
                if (NativeMethods.GetIfEntry2(ref row) == 0)
                {
                    inOctets = row.InOctets;
                    outOctets = row.OutOctets;
                    return true;
                }
                return false; // interface gone; not a marshaling problem
            }
            catch (Exception ex)
            {
                _nativeBroken = true;
                Log.Error("GetIfEntry2 marshaling failed; switching to managed counter fallback", ex);
            }
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!TryGetIndex(nic, out int idx) || idx != ifIndex) continue;
                var stats = nic.GetIPStatistics();
                inOctets = (ulong)stats.BytesReceived;
                outOctets = (ulong)stats.BytesSent;
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool TryGetIndex(NetworkInterface nic, out int index)
    {
        index = -1;
        try
        {
            var props = nic.GetIPProperties();
            try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { }
            if (index < 0)
                try { index = props.GetIPv6Properties()?.Index ?? -1; } catch { }
            return index >= 0;
        }
        catch { return false; }
    }
}
