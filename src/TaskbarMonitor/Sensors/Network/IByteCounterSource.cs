namespace TaskbarMonitor.Sensors.Network;

/// <summary>Seam over GetIfEntry2 so the delta math is unit-testable with fake counters.</summary>
public interface IByteCounterSource
{
    bool TryGetCounters(int ifIndex, out ulong inOctets, out ulong outOctets);
}
