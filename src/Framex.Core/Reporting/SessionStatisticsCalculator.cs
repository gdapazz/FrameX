using Framex.Core.Domain;
using Framex.Core.FrameCapture;

namespace Framex.Core.Reporting;

public sealed record SessionAnalysis(SessionSummary Summary, IReadOnlyList<SessionEvent> DetectedEvents);

/// <summary>Calcula o resumo de uma sessão a partir das amostras persistidas.</summary>
public static class SessionStatisticsCalculator
{
    /// <summary>Métricas cuja ausência total é informada como limitação do relatório.</summary>
    private static readonly MetricKind[] KeyMetrics =
    [
        MetricKind.CpuLoad, MetricKind.CpuTemperaturePackage, MetricKind.GpuLoad, MetricKind.GpuTemperature,
        MetricKind.RamUsed, MetricKind.VramUsed,
    ];

    private static readonly HashSet<MetricKind> FrameMetrics = [MetricKind.Fps, MetricKind.FrameTimeAvg, MetricKind.FrameTimeMax, MetricKind.FrameCount];

    public static SessionAnalysis Analyze(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<double> orderedFrameTimes,
        TimeSpan hardwareInterval,
        DateTimeOffset now)
    {
        var duration = end > start ? end - start : TimeSpan.Zero;
        var limitations = new List<string>();
        var stats = new List<MetricStatistic>();

        foreach (var group in samples.GroupBy(s => s.Metric).OrderBy(g => (int)g.Key))
        {
            if (group.Key == MetricKind.FrameCount)
            {
                continue;
            }

            var values = group.Select(s => s.Value).Where(double.IsFinite).ToList();
            if (values.Count == 0)
            {
                continue;
            }

            var isFrame = FrameMetrics.Contains(group.Key);
            var interval = isFrame ? TimeSpan.FromSeconds(1) : hardwareInterval;
            var coverage = duration.TotalSeconds > 0
                ? Math.Min(100, values.Count * interval.TotalSeconds / duration.TotalSeconds * 100)
                : 0;
            var method = isFrame
                ? "Média, mínimo e máximo entre janelas de 1 s com quadros (não ponderado por quadro)."
                : $"Média aritmética das amostras (intervalo nominal de {interval.TotalSeconds:0.#} s).";

            stats.Add(new MetricStatistic(group.Key, values.Average(), values.Min(), values.Max(), values.Count, coverage, method));
        }

        foreach (var key in KeyMetrics)
        {
            if (stats.All(s => s.Metric != key))
            {
                limitations.Add($"{MetricCatalog.Get(key).Label}: sem leituras válidas nesta sessão (sensor indisponível ou não suportado).");
            }
        }

        FrameSummary? frames = null;
        if (orderedFrameTimes.Count > 0)
        {
            frames = FrameStatistics.Summarize(orderedFrameTimes);
        }
        else
        {
            limitations.Add("Sem dados de quadros: FPS e frametime não foram capturados nesta sessão.");
        }

        if (duration < TimeSpan.FromSeconds(30))
        {
            limitations.Add("Sessão curta (< 30 s): as estatísticas têm baixa representatividade.");
        }

        var gaps = DetectGaps(start, end, samples, hardwareInterval);
        var events = new List<SessionEvent>();
        events.AddRange(PerformanceDropDetector.Detect(samples.Where(s => s.Metric == MetricKind.Fps).OrderBy(s => s.Timestamp).ToList()));

        var summary = new SessionSummary(duration, frames, stats, gaps, limitations, now);
        return new SessionAnalysis(summary, events);
    }

    /// <summary>
    /// Períodos sem amostras de hardware maiores que 3× o intervalo nominal (mínimo 5 s),
    /// e períodos sem quadros maiores que 5 s entre segundos com captura.
    /// </summary>
    public static IReadOnlyList<DataGap> DetectGaps(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<MetricSample> samples, TimeSpan hardwareInterval)
    {
        var gaps = new List<DataGap>();
        var threshold = TimeSpan.FromTicks(Math.Max(hardwareInterval.Ticks * 3, TimeSpan.FromSeconds(5).Ticks));

        var hwTimes = samples.Where(s => !FrameMetrics.Contains(s.Metric)).Select(s => s.Timestamp).Distinct().Order().ToList();
        AddGaps(gaps, hwTimes, start, end, threshold, "Sensores de hardware");

        var frameTimes = samples.Where(s => s.Metric == MetricKind.Fps).Select(s => s.Timestamp).Distinct().Order().ToList();
        if (frameTimes.Count > 0)
        {
            AddGaps(gaps, frameTimes, frameTimes[0], frameTimes[^1], TimeSpan.FromSeconds(5), "Quadros");
        }

        return gaps.OrderBy(g => g.Start).ToList();
    }

    private static void AddGaps(List<DataGap> gaps, List<DateTimeOffset> times, DateTimeOffset start, DateTimeOffset end, TimeSpan threshold, string scope)
    {
        if (times.Count == 0)
        {
            if (end - start > threshold)
            {
                gaps.Add(new DataGap(start, end, scope));
            }

            return;
        }

        var previous = start;
        foreach (var t in times)
        {
            if (t - previous > threshold)
            {
                gaps.Add(new DataGap(previous, t, scope));
            }

            previous = t;
        }

        if (end - previous > threshold)
        {
            gaps.Add(new DataGap(previous, end, scope));
        }
    }
}

/// <summary>
/// Detecção estatística de quedas: trechos de ≥ 3 s consecutivos com FPS abaixo de 60% da mediana da sessão.
/// Não atribui causa — apenas sinaliza o intervalo para investigação.
/// </summary>
public static class PerformanceDropDetector
{
    public const double DropRatio = 0.6;
    public const int MinConsecutiveSeconds = 3;

    public static IReadOnlyList<SessionEvent> Detect(IReadOnlyList<MetricSample> orderedFpsSamples)
    {
        var events = new List<SessionEvent>();
        if (orderedFpsSamples.Count < 30)
        {
            return events;
        }

        var sorted = orderedFpsSamples.Select(s => s.Value).Order().ToList();
        var median = FrameStatistics.Percentile(sorted, 50);
        if (median < 20)
        {
            return events;
        }

        var limit = median * DropRatio;
        var n = orderedFpsSamples.Count;
        var i = 0;
        while (i < n)
        {
            if (orderedFpsSamples[i].Value >= limit)
            {
                i++;
                continue;
            }

            var j = i;
            while (j + 1 < n && orderedFpsSamples[j + 1].Value < limit
                   && orderedFpsSamples[j + 1].Timestamp - orderedFpsSamples[j].Timestamp <= TimeSpan.FromSeconds(2))
            {
                j++;
            }

            var length = j - i + 1;
            if (length >= MinConsecutiveSeconds)
            {
                var min = orderedFpsSamples.Skip(i).Take(length).Min(s => s.Value);
                events.Add(new SessionEvent(orderedFpsSamples[i].Timestamp, SessionEventType.PerformanceDrop, EventSeverity.Warning,
                    $"Queda de desempenho por {length} s: FPS mínimo {min:0} (mediana da sessão {median:0}). Causa não determinada."));
            }

            i = j + 1;
        }

        return events;
    }
}
