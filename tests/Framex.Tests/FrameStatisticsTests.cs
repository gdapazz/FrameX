using Framex.Core.Domain;
using Framex.Core.FrameCapture;

namespace Framex.Tests;

public class FrameStatisticsTests
{
    [Fact]
    public void AverageFps_IsHarmonic_NotMeanOfInstantFps()
    {
        // 1 quadro de 10 ms (100 FPS) + 1 de 30 ms (33 FPS): média de FPS instantâneos seria 66,7; correto é 2 / 40 ms = 50.
        var fps = FrameStatistics.AverageFps([10, 30]);
        Assert.Equal(50, fps!.Value, 6);
    }

    [Fact]
    public void AverageFps_EmptyReturnsNull() => Assert.Null(FrameStatistics.AverageFps([]));

    [Fact]
    public void Percentile_InterpolatesLinearly()
    {
        double[] sorted = [10, 20, 30, 40, 50];
        Assert.Equal(30, FrameStatistics.Percentile(sorted, 50));
        Assert.Equal(48, FrameStatistics.Percentile(sorted, 95), 6);
        Assert.Equal(10, FrameStatistics.Percentile(sorted, 0));
        Assert.Equal(50, FrameStatistics.Percentile(sorted, 100));
    }

    [Fact]
    public void Low1Percent_AveragesSlowestOnePercent()
    {
        // 990 quadros de 10 ms + 10 quadros de 50 ms → os 1% piores são exatamente os de 50 ms → 20 FPS.
        var frames = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToArray();
        Array.Sort(frames);
        Assert.Equal(20, FrameStatistics.LowFps(frames, 0.01, FrameStatistics.MinFramesFor1Low)!.Value, 6);
        // 0,1% de 1000 = 1 quadro: o pior (50 ms) → 20 FPS.
        Assert.Equal(20, FrameStatistics.LowFps(frames, 0.001, FrameStatistics.MinFramesFor01Low)!.Value, 6);
    }

    [Fact]
    public void LowMetrics_RequireMinimumSamples()
    {
        var frames = Enumerable.Repeat(16.6, 99).ToArray();
        Assert.Null(FrameStatistics.LowFps(frames, 0.01, FrameStatistics.MinFramesFor1Low));
        var summary = FrameStatistics.Summarize(frames);
        Assert.Null(summary.Low1PercentFps);
        Assert.Null(summary.Low01PercentFps);
        Assert.Contains(summary.Notes, n => n.Contains("1% low", StringComparison.Ordinal));
    }

    [Fact]
    public void Summarize_DiscardsInvalidFrames()
    {
        double[] frames = [10, 10, -1, double.NaN, 0, 5000, 10];
        var s = FrameStatistics.Summarize(frames);
        Assert.Equal(3, s.FrameCount);
        Assert.Equal(4, s.InvalidFrameCount);
        Assert.Equal(100, s.AverageFps!.Value, 6);
    }

    [Fact]
    public void Summarize_NoValidFrames_ReportsLimitation()
    {
        var s = FrameStatistics.Summarize([0, -5]);
        Assert.Null(s.AverageFps);
        Assert.NotEmpty(s.Notes);
    }

    [Fact]
    public void PerSecondFps_UsesCompleteWindows()
    {
        // 100 quadros de 10 ms = 1 s a 100 FPS; depois 50 de 20 ms = 1 s a 50 FPS; resto incompleto ignorado.
        var frames = Enumerable.Repeat(10.0, 100).Concat(Enumerable.Repeat(20.0, 50)).Concat([5.0]);
        var windows = FrameStatistics.PerSecondFps(frames);
        Assert.Equal(2, windows.Count);
        Assert.Equal(100, windows[0], 6);
        Assert.Equal(50, windows[1], 6);
    }

    [Fact]
    public void Histogram_CountsEveryValidFrame()
    {
        double[] frames = [3, 7, 16, 16.7, 40, 120];
        var bins = FrameStatistics.Histogram(frames);
        Assert.Equal(frames.Length, bins.Sum(b => b.Count));
    }

    [Fact]
    public void SecondBucketAggregator_EmitsPerSecondMetrics()
    {
        var agg = new SecondBucketAggregator();
        var output = new List<MetricSample>();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        for (var i = 0; i < 60; i++)
        {
            agg.Add(new FrameSample(t0.AddMilliseconds(i * 16), 16), output);
        }

        Assert.Empty(output); // ainda no primeiro segundo
        agg.Add(new FrameSample(t0.AddSeconds(1.01), 20), output);
        Assert.Equal(62.5, output.Single(s => s.Metric == MetricKind.Fps).Value, 6);
        Assert.Equal(60, output.Single(s => s.Metric == MetricKind.FrameCount).Value);

        output.Clear();
        agg.Flush(output);
        Assert.Equal(50, output.Single(s => s.Metric == MetricKind.Fps).Value, 6);
    }

    [Fact]
    public void LiveAggregator_ReportsNullWhenIdle()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = new LiveFrameAggregator(time, 1000);
        var frames = Enumerable.Range(0, 120).Select(i => new FrameSample(time.Now.AddMilliseconds(-1000 + (i * 8.33)), 8.33)).ToList();
        live.Add(frames);
        var m = live.Compute();
        Assert.InRange(m.Fps!.Value, 119, 121);
        Assert.Equal(8.33, m.FrameTimeMs!.Value, 3);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(live.Compute().Fps);
    }

    [Fact]
    public void LiveAggregator_UsesFixedMemory()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = new LiveFrameAggregator(time, 100);
        for (var batch = 0; batch < 50; batch++)
        {
            live.Add(Enumerable.Range(0, 100).Select(i => new FrameSample(time.Now, 10)).ToList());
        }

        Assert.Equal(100, live.Compute().WindowFrames);
    }
}
