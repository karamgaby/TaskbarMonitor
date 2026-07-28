using TaskbarMonitor.Formatting;
using Xunit;

namespace TaskbarMonitor.Tests;

public class UnitFormatterTests
{
    [Theory]
    [InlineData(null, " --.- KB/s")]
    [InlineData(-1.0, " --.- KB/s")]
    [InlineData(0.0, "  0.0 KB/s")]
    [InlineData(120 * 1024.0, "120.0 KB/s")]
    [InlineData(4.2 * 1024 * 1024, "  4.2 MB/s")]
    [InlineData(2.5 * 1024 * 1024 * 1024, "  2.5 GB/s")]
    public void FormatRate_KnownValues(double? bps, string expected)
        => Assert.Equal(expected, UnitFormatter.FormatRate(bps));

    [Fact]
    public void FormatRate_UnitRollover_At999_95()
    {
        Assert.EndsWith("KB/s", UnitFormatter.FormatRate(999.9 * 1024));
        Assert.Equal("  1.0 MB/s", UnitFormatter.FormatRate(999.96 * 1024));
        Assert.EndsWith("MB/s", UnitFormatter.FormatRate(999.9 * 1024 * 1024));
        Assert.Equal("  1.0 GB/s", UnitFormatter.FormatRate(999.96 * 1024 * 1024));
    }

    [Fact]
    public void FormatRate_ConstantWidth_AcrossMagnitudes()
    {
        double[] samples = [0, 1, 999, 1024, 5e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1.2e12];
        foreach (var s in samples)
            Assert.Equal(10, UnitFormatter.FormatRate(s).Length);
        Assert.Equal(10, UnitFormatter.FormatRate(null).Length);
    }

    [Theory]
    [InlineData(null, " --")]
    [InlineData(0.0, "  0")]
    [InlineData(33.6, " 34")]
    [InlineData(100.0, "100")]
    [InlineData(250.0, "100")] // clamped
    public void FormatPercent_FixedWidth3(double? v, string expected)
        => Assert.Equal(expected, UnitFormatter.FormatPercent(v));

    [Theory]
    [InlineData(null, " --°C")]
    [InlineData(58.4, " 58°C")]
    [InlineData(100.0, "100°C")]
    [InlineData(5.0, "  5°C")]
    public void FormatTemp_FixedWidth5(double? v, string expected)
        => Assert.Equal(expected, UnitFormatter.FormatTemp(v));
}
