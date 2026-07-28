using System.Drawing;
using TaskbarMonitor.Rendering;
using TaskbarMonitor.Settings;
using Xunit;

namespace TaskbarMonitor.Tests;

/// <summary>
/// The strip is measured with GDI+ but the fixed-width contract is a property of the *font*:
/// if any glyph in the line falls back to a proportional family the advance stops being the
/// monospace cell and the strip jitters. These guard that.
/// </summary>
public class StripRendererTests
{
    private static (Graphics G, Bitmap B) Surface()
    {
        var b = new Bitmap(1, 1);
        return (Graphics.FromImage(b), b);
    }

    [Fact]
    public void MeasureLine_ConsolasLineIsExactlyCharCountTimesCellWidth()
    {
        var (g, b) = Surface();
        using (b)
        using (g)
        using (var font = new Font("Consolas", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            string sample = LineComposer.WidestSample(new AppSettings());
            float cell = StripRenderer.MeasureLine(g, "0", font).Width;
            float actual = StripRenderer.MeasureLine(g, sample, font).Width;

            // Within one cell: any glyph falling back to a proportional face would blow this out
            Assert.InRange(actual, sample.Length * cell - cell, sample.Length * cell + cell);
        }
    }

    [Theory]
    [InlineData("│")]   // LineComposer.Separator
    [InlineData("↑")]
    [InlineData("↓")]
    [InlineData("°")]
    public void MeasureLine_NonAsciiGlyphsShareTheMonospaceCell(string glyph)
    {
        var (g, b) = Surface();
        using (b)
        using (g)
        using (var font = new Font("Consolas", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            float cell = StripRenderer.MeasureLine(g, "0", font).Width;
            float actual = StripRenderer.MeasureLine(g, glyph, font).Width;
            Assert.Equal(cell, actual, 1.0);
        }
    }

    [Fact]
    public void MeasureLine_WidthIsStableAcrossDifferentValues()
    {
        var (g, b) = Surface();
        using (b)
        using (g)
        using (var font = new Font("Consolas", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            var settings = new AppSettings();
            float widest = StripRenderer.MeasureLine(g, LineComposer.WidestSample(settings), font).Width;

            foreach (var snapshot in new[]
            {
                new TaskbarMonitor.Sensors.SensorSnapshot(),
                new TaskbarMonitor.Sensors.SensorSnapshot { DownBps = 0, UpBps = 0, CpuPercent = 5, CpuTempC = 40, RamPercent = 7, GpuPercent = 0, GpuTempC = 9 },
                new TaskbarMonitor.Sensors.SensorSnapshot { DownBps = 1024, UpBps = 1024 * 1024, CpuPercent = 100, CpuTempC = 100, RamPercent = 100, GpuPercent = 100, GpuTempC = 100 },
            })
            {
                float w = StripRenderer.MeasureLine(g, LineComposer.Compose(snapshot, settings), font).Width;
                Assert.Equal(widest, w, 1.0);
            }
        }
    }
}
