using TaskbarMonitor.Sensors.Network;
using Xunit;

namespace TaskbarMonitor.Tests;

public class NetRateSamplerTests
{
    private sealed class FakeCounters : IByteCounterSource
    {
        public Dictionary<int, (ulong In, ulong Out)> Table = new();
        public bool Fail;

        public bool TryGetCounters(int ifIndex, out ulong inOctets, out ulong outOctets)
        {
            inOctets = outOctets = 0;
            if (Fail || !Table.TryGetValue(ifIndex, out var v)) return false;
            (inOctets, outOctets) = v;
            return true;
        }
    }

    [Fact]
    public void FirstSample_IsBaselineOnly()
    {
        var fake = new FakeCounters { Table = { [5] = (1000, 500) } };
        var s = new NetRateSampler(fake);
        Assert.Null(s.Sample(5, 0.0));
    }

    [Fact]
    public void SteadyFlow_ComputesRateFromMeasuredElapsed()
    {
        var fake = new FakeCounters { Table = { [5] = (0, 0) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);

        fake.Table[5] = (1_000_000, 100_000);
        var r = s.Sample(5, 1.0);
        Assert.NotNull(r);
        Assert.Equal(1_000_000, r!.Value.DownBps, 3);
        Assert.Equal(100_000, r.Value.UpBps, 3);

        // Irregular tick: 0.7 s then 1.4 s — rate uses actual elapsed, not assumed 1 s
        fake.Table[5] = (1_700_000, 170_000);
        r = s.Sample(5, 1.7);
        Assert.Equal(1_000_000, r!.Value.DownBps, 3);

        fake.Table[5] = (3_100_000, 310_000);
        r = s.Sample(5, 3.1);
        Assert.Equal(1_000_000, r!.Value.DownBps, 3);
    }

    [Fact]
    public void CounterReset_SkipsOneSample_ThenResumes()
    {
        var fake = new FakeCounters { Table = { [5] = (5_000_000, 500) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);

        fake.Table[5] = (1_000, 100); // adapter driver reset: counters went backwards
        Assert.Null(s.Sample(5, 1.0));

        fake.Table[5] = (2_001_000, 200_100);
        var r = s.Sample(5, 2.0);
        Assert.NotNull(r);
        Assert.Equal(2_000_000, r!.Value.DownBps, 3);
    }

    [Fact]
    public void AdapterChange_SkipsOneSample_ThenResumes()
    {
        var fake = new FakeCounters { Table = { [5] = (1_000_000, 0), [9] = (777, 0) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);

        Assert.Null(s.Sample(9, 1.0)); // ethernet → wifi mid-session

        fake.Table[9] = (500_777, 0);
        var r = s.Sample(9, 2.0);
        Assert.Equal(500_000, r!.Value.DownBps, 3);
    }

    [Fact]
    public void LongGap_SleepResume_DiscardsAndRebaselines()
    {
        var fake = new FakeCounters { Table = { [5] = (0, 0) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);

        // 8 hours asleep: delta is real but the rate would be garbage
        fake.Table[5] = (30_000_000_000, 1_000_000_000);
        Assert.Null(s.Sample(5, 8 * 3600.0));

        fake.Table[5] = (30_000_400_000, 1_000_000_000);
        var r = s.Sample(5, 8 * 3600.0 + 1.0);
        Assert.Equal(400_000, r!.Value.DownBps, 3);
    }

    [Fact]
    public void ExplicitRebaseline_SkipsNextSample()
    {
        var fake = new FakeCounters { Table = { [5] = (0, 0) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);
        s.Rebaseline();
        fake.Table[5] = (1000, 0);
        Assert.Null(s.Sample(5, 1.0));
    }

    [Fact]
    public void SourceFailure_ReturnsNull_AndRebaselines()
    {
        var fake = new FakeCounters { Table = { [5] = (1000, 0) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);

        fake.Fail = true;
        Assert.Null(s.Sample(5, 1.0));

        fake.Fail = false;
        fake.Table[5] = (999_999_000, 0);
        Assert.Null(s.Sample(5, 2.0)); // first sample after recovery is baseline only
        fake.Table[5] = (1_000_000_000, 0);
        Assert.NotNull(s.Sample(5, 3.0));
    }

    [Fact]
    public void InvalidIfIndex_ReturnsNull()
    {
        var s = new NetRateSampler(new FakeCounters());
        Assert.Null(s.Sample(-1, 0.0));
    }

    [Fact]
    public void ImplausibleRate_Discarded()
    {
        var fake = new FakeCounters { Table = { [5] = (0, 0) } };
        var s = new NetRateSampler(fake);
        s.Sample(5, 0.0);
        fake.Table[5] = (20_000_000_000_000, 0); // 20 TB in 1 s — garbage
        Assert.Null(s.Sample(5, 1.0));
    }
}
