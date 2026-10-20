using Framex.Core.Domain;

namespace Framex.Core.Reporting;

public sealed record ComparisonRow(string Label, double? A, double? B, string Unit, bool HigherIsBetter)
{
    public double? Delta => A is { } a && B is { } b ? b - a : null;

    public double? DeltaPercent => A is { } a && B is { } b && Math.Abs(a) > 1e-9 ? (b - a) / a * 100 : null;
}

public sealed record SessionComparison(Session A, Session B, IReadOnlyList<ComparisonRow> Rows, IReadOnlyList<string> Warnings)
{
    public bool ConditionsDiffer => Warnings.Count > 0;
}

public static class SessionComparer
{
    public static SessionComparison Compare(Session a, Session b)
    {
        var warnings = new List<string>();
        if (a.GameId != b.GameId || !string.Equals(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Jogos diferentes: os números não são diretamente comparáveis.");
        }

        if (!string.Equals(a.HardwareFingerprint, b.HardwareFingerprint, StringComparison.Ordinal))
        {
            warnings.Add("O hardware detectado mudou entre as sessões.");
        }

        if (!string.Equals(a.Resolution, b.Resolution, StringComparison.Ordinal))
        {
            warnings.Add($"Resolução do monitor principal diferente ({a.Resolution ?? "?"} × {b.Resolution ?? "?"}).");
        }

        if (!string.Equals(a.MonitoringProfile, b.MonitoringProfile, StringComparison.Ordinal))
        {
            warnings.Add("Perfis de monitoramento diferentes (frequência de coleta distinta).");
        }

        if (a.State != SessionState.Completed || b.State != SessionState.Completed)
        {
            warnings.Add("Ao menos uma sessão está incompleta (interrompida).");
        }

        warnings.Add("Configurações gráficas e versão do jogo não são conhecidas pelo FRAMEX e podem ter mudado.");

        var sa = a.Summary;
        var sb = b.Summary;
        var rows = new List<ComparisonRow>
        {
            new("Duração (min)", sa?.Duration.TotalMinutes, sb?.Duration.TotalMinutes, "min", true),
            new("FPS médio", sa?.Frames?.AverageFps, sb?.Frames?.AverageFps, "FPS", true),
            new("1% low", sa?.Frames?.Low1PercentFps, sb?.Frames?.Low1PercentFps, "FPS", true),
            new("0,1% low", sa?.Frames?.Low01PercentFps, sb?.Frames?.Low01PercentFps, "FPS", true),
            new("Frametime médio", sa?.Frames?.AverageFrameTimeMs, sb?.Frames?.AverageFrameTimeMs, "ms", false),
            new("Frametime P99", sa?.Frames?.P99FrameTimeMs, sb?.Frames?.P99FrameTimeMs, "ms", false),
        };

        void AddMetric(string label, MetricKind kind, bool useMax, string unit, bool higherBetter)
        {
            var ma = sa?.Get(kind);
            var mb = sb?.Get(kind);
            rows.Add(new ComparisonRow(label, useMax ? ma?.Maximum : ma?.Average, useMax ? mb?.Maximum : mb?.Average, unit, higherBetter));
        }

        AddMetric("Uso médio da CPU", MetricKind.CpuLoad, false, "%", false);
        AddMetric("Uso médio da GPU", MetricKind.GpuLoad, false, "%", true);
        AddMetric("Temp. máx. da CPU", MetricKind.CpuTemperaturePackage, true, "°C", false);
        AddMetric("Temp. máx. da GPU", MetricKind.GpuTemperature, true, "°C", false);
        AddMetric("RAM máx.", MetricKind.RamUsed, true, "MiB", false);
        AddMetric("VRAM máx.", MetricKind.VramUsed, true, "MiB", false);

        return new SessionComparison(a, b, rows, warnings);
    }
}
