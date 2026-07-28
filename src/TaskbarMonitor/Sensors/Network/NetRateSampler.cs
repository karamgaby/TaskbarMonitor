namespace TaskbarMonitor.Sensors.Network;

/// <summary>
/// Turns 64-bit interface octet counters into rates. Rules:
/// - rate = delta / measured elapsed (never an assumed interval)
/// - counter went backwards (reset), adapter changed, or elapsed gap &gt; MaxGapSeconds
///   (sleep/resume) → discard the sample, re-baseline, resume next tick
/// - implausible rates (&gt;100 Gb/s) discarded as garbage
/// </summary>
public sealed class NetRateSampler
{
    public const double MaxGapSeconds = 5.0;
    public const double MinGapSeconds = 0.05;
    private const double MaxPlausibleBps = 12.5e9; // 100 Gb/s in bytes

    private readonly IByteCounterSource _source;
    private int _ifIndex = -1;
    private ulong _in, _out;
    private double _t;
    private bool _hasBaseline;

    public NetRateSampler(IByteCounterSource source) => _source = source;

    /// <summary>Force the next sample to only re-baseline (adapter switch, resume from sleep).</summary>
    public void Rebaseline() => _hasBaseline = false;

    public (double DownBps, double UpBps)? Sample(int ifIndex, double nowSeconds)
    {
        if (ifIndex < 0 || !_source.TryGetCounters(ifIndex, out ulong inOct, out ulong outOct))
        {
            _hasBaseline = false;
            return null;
        }

        double elapsed = nowSeconds - _t;
        bool usable = _hasBaseline
                      && ifIndex == _ifIndex
                      && inOct >= _in && outOct >= _out
                      && elapsed >= MinGapSeconds && elapsed <= MaxGapSeconds;

        double down = usable ? (inOct - _in) / elapsed : 0;
        double up = usable ? (outOct - _out) / elapsed : 0;

        _ifIndex = ifIndex;
        _in = inOct;
        _out = outOct;
        _t = nowSeconds;
        _hasBaseline = true;

        if (!usable || down > MaxPlausibleBps || up > MaxPlausibleBps)
            return null;
        return (down, up);
    }
}
