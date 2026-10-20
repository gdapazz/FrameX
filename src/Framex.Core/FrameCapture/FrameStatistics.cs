using Framex.Core.Domain;

namespace Framex.Core.FrameCapture;

/// <summary>
/// Cálculos estatísticos sobre frametimes. Metodologia (documentada em docs/METRICAS.md):
/// <list type="bullet">
/// <item>FPS médio = 1000 × N / Σ frametime (média harmônica; não é a média de FPS instantâneos).</item>
/// <item>1% low = 1000 / média dos 1% maiores frametimes (mínimo de 100 quadros).</item>
/// <item>0,1% low = 1000 / média dos 0,1% maiores frametimes (mínimo de 1000 quadros).</item>
/// <item>Percentis de frametime por interpolação linear entre postos (Pxx).</item>
/// <item>FPS mín./máx. = extremos entre janelas completas de 1 segundo.</item>
/// <item>Frametimes ≤ 0, não finitos ou acima de <see cref="MaxValidFrameTimeMs"/> são descartados e contados como inválidos.</item>
/// </list>
/// </summary>
public static class FrameStatistics
{
    public const double MaxValidFrameTimeMs = 2000;
    public const int MinFramesFor1Low = 100;
    public const int MinFramesFor01Low = 1000;

    public static bool IsValid(double frameTimeMs) =>
        double.IsFinite(frameTimeMs) && frameTimeMs > 0 && frameTimeMs <= MaxValidFrameTimeMs;

    public static double? AverageFps(IReadOnlyList<double> validFrameTimes)
    {
        if (validFrameTimes.Count == 0)
        {
            return null;
        }

        double sum = 0;
        foreach (var ft in validFrameTimes)
        {
            sum += ft;
        }

        return sum > 0 ? 1000.0 * validFrameTimes.Count / sum : null;
    }

    /// <summary>Percentil (0–100) por interpolação linear sobre um vetor já ordenado crescentemente.</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("Sem amostras.", nameof(sortedAscending));
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = Math.Clamp(percentile, 0, 100) / 100.0 * (sortedAscending.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        var frac = rank - lo;
        return sortedAscending[lo] + ((sortedAscending[hi] - sortedAscending[lo]) * frac);
    }

    /// <summary>FPS equivalente à média da fração mais lenta de quadros (ex.: 0.01 para 1% low).</summary>
    public static double? LowFps(IReadOnlyList<double> sortedAscending, double fraction, int minimumFrames)
    {
        if (sortedAscending.Count < minimumFrames || sortedAscending.Count == 0)
        {
            return null;
        }

        var count = Math.Max(1, (int)Math.Ceiling(sortedAscending.Count * fraction));
        double sum = 0;
        for (var i = sortedAscending.Count - count; i < sortedAscending.Count; i++)
        {
            sum += sortedAscending[i];
        }

        var avg = sum / count;
        return avg > 0 ? 1000.0 / avg : null;
    }

    /// <summary>FPS de cada janela completa de 1 s, acumulando frametimes em ordem.</summary>
    public static List<double> PerSecondFps(IEnumerable<double> orderedValidFrameTimes)
    {
        var result = new List<double>();
        double acc = 0;
        var frames = 0;
        foreach (var ft in orderedValidFrameTimes)
        {
            acc += ft;
            frames++;
            if (acc >= 1000)
            {
                result.Add(1000.0 * frames / acc);
                acc = 0;
                frames = 0;
            }
        }

        return result;
    }

    public static IReadOnlyList<HistogramBin> Histogram(IReadOnlyList<double> validFrameTimes)
    {
        double[] edges = [0, 4, 6, 8.33, 11.11, 16.67, 20, 25, 33.33, 50, 100, MaxValidFrameTimeMs];
        var counts = new long[edges.Length - 1];
        foreach (var ft in validFrameTimes)
        {
            for (var i = 0; i < counts.Length; i++)
            {
                if (ft >= edges[i] && (ft < edges[i + 1] || i == counts.Length - 1))
                {
                    counts[i]++;
                    break;
                }
            }
        }

        return counts.Select((c, i) => new HistogramBin(edges[i], edges[i + 1], c)).ToList();
    }

    public static FrameSummary Summarize(IEnumerable<double> orderedFrameTimes)
    {
        var valid = new List<double>();
        long invalid = 0;
        foreach (var ft in orderedFrameTimes)
        {
            if (IsValid(ft))
            {
                valid.Add(ft);
            }
            else
            {
                invalid++;
            }
        }

        var notes = new List<string>();
        if (valid.Count == 0)
        {
            notes.Add("Nenhum quadro válido foi capturado nesta sessão.");
            return new FrameSummary(0, invalid, null, null, null, null, null, null, null, null, null, null, null, [], notes);
        }

        var perSecond = PerSecondFps(valid);
        var sorted = valid.ToArray();
        Array.Sort(sorted);

        var low1 = LowFps(sorted, 0.01, MinFramesFor1Low);
        var low01 = LowFps(sorted, 0.001, MinFramesFor01Low);
        if (low1 is null)
        {
            notes.Add($"1% low não calculado: são necessários ao menos {MinFramesFor1Low} quadros válidos.");
        }

        if (low01 is null)
        {
            notes.Add($"0,1% low não calculado: são necessários ao menos {MinFramesFor01Low} quadros válidos.");
        }

        if (perSecond.Count == 0)
        {
            notes.Add("FPS mínimo/máximo não calculados: menos de 1 segundo de quadros válidos.");
        }

        if (invalid > 0)
        {
            notes.Add($"{invalid} quadro(s) descartado(s) por frametime inválido (≤ 0 ou > {MaxValidFrameTimeMs:0} ms).");
        }

        double sum = 0;
        foreach (var ft in sorted)
        {
            sum += ft;
        }

        return new FrameSummary(
            valid.Count,
            invalid,
            AverageFps(valid),
            perSecond.Count > 0 ? perSecond.Min() : null,
            perSecond.Count > 0 ? perSecond.Max() : null,
            low1,
            low01,
            sum / sorted.Length,
            Percentile(sorted, 50),
            Percentile(sorted, 95),
            Percentile(sorted, 99),
            Percentile(sorted, 99.9),
            sorted[^1],
            Histogram(sorted),
            notes);
    }
}
