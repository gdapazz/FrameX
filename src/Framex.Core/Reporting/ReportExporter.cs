using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Framex.Core.Domain;
using Framex.Core.Monitoring;

namespace Framex.Core.Reporting;

public sealed record SessionReport(Session Session, SessionSummary? Summary, IReadOnlyList<MetricSample> Samples, IReadOnlyList<SessionEvent> Events);

public enum ExportFormat
{
    Csv,
    Json,
    Html,
}

/// <summary>
/// Exporta relatórios compartilháveis. Não inclui caminhos de arquivos, nome de usuário nem nome do computador.
/// </summary>
public static class ReportExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Json => ".json",
        _ => ".html",
    };

    public static string SuggestFileName(SessionReport report, ExportFormat format)
    {
        var safe = new string(report.Session.GameName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (safe.Length == 0)
        {
            safe = "sessao";
        }

        return $"FRAMEX_{safe}_{report.Session.StartedAt.ToLocalTime():yyyyMMdd_HHmmss}{Extension(format)}";
    }

    public static string Export(SessionReport report, ExportFormat format) => format switch
    {
        ExportFormat.Csv => ToCsv(report),
        ExportFormat.Json => ToJson(report),
        _ => ToHtml(report),
    };

    /// <summary>CSV largo: uma linha por instante, uma coluna por métrica. Separador vírgula, decimais com ponto.</summary>
    public static string ToCsv(SessionReport report)
    {
        var metrics = report.Samples.Select(s => s.Metric).Distinct().OrderBy(m => (int)m).ToList();
        var rows = report.Samples.GroupBy(s => s.Timestamp.ToUnixTimeMilliseconds()).OrderBy(g => g.Key);
        var sb = new StringBuilder();
        sb.Append("timestamp_utc,elapsed_s");
        foreach (var m in metrics)
        {
            sb.Append(',').Append(ColumnName(m));
        }

        sb.AppendLine();
        var start = report.Session.StartedAt.ToUnixTimeMilliseconds();
        foreach (var row in rows)
        {
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(row.Key);
            sb.Append(ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv)).Append(',');
            sb.Append(((row.Key - start) / 1000.0).ToString("0.###", Inv));
            var byMetric = row.ToDictionary(s => s.Metric, s => s.Value);
            foreach (var m in metrics)
            {
                sb.Append(',');
                if (byMetric.TryGetValue(m, out var v))
                {
                    sb.Append(v.ToString("0.###", Inv));
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ColumnName(MetricKind kind)
    {
        var d = MetricCatalog.Get(kind);
        var unit = d.Unit switch
        {
            MetricUnit.Percent => "pct",
            MetricUnit.Celsius => "c",
            MetricUnit.Megahertz => "mhz",
            MetricUnit.Watt => "w",
            MetricUnit.Rpm => "rpm",
            MetricUnit.Megabyte => "mib",
            MetricUnit.Milliseconds => "ms",
            _ => "",
        };
        var name = ToSnake(kind.ToString());
        return unit.Length > 0 ? $"{name}_{unit}" : name;
    }

    private static string ToSnake(string pascal)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(pascal[i]));
        }

        return sb.ToString();
    }

    public static string ToJson(SessionReport report)
    {
        var s = report.Session;
        var payload = new
        {
            format = "framex-session",
            formatVersion = 1,
            app = "FRAMEX",
            appVersion = s.AppVersion,
            session = new
            {
                id = s.Id,
                game = s.GameName,
                executable = s.ExecutableName,
                gameConfirmed = s.GameConfirmed,
                startedAtUtc = s.StartedAt.UtcDateTime,
                endedAtUtc = s.EndedAt?.UtcDateTime,
                state = s.State,
                hardware = s.HardwareFingerprint,
                resolution = s.Resolution,
                monitoringProfile = s.MonitoringProfile,
                initialCaptureState = s.InitialCaptureState,
            },
            summary = report.Summary,
            events = report.Events.Select(e => new { timestampUtc = e.Timestamp.UtcDateTime, type = e.Type, severity = e.Severity, message = e.Message }),
            samples = report.Samples
                .GroupBy(x => x.Metric)
                .ToDictionary(g => ColumnName(g.Key), g => g.OrderBy(x => x.Timestamp).Select(x => new[] { (x.Timestamp - s.StartedAt).TotalSeconds, x.Value })),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string ToHtml(SessionReport report)
    {
        var s = report.Session;
        var sum = report.Summary;
        var f = sum?.Frames;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>FRAMEX — ").Append(Enc(s.GameName)).Append("</title><style>");
        sb.Append("""
            :root{--bg:#070b14;--sf:#0f1626;--sf2:#151e33;--ln:#22304d;--tx:#e7eefc;--mt:#8a9bbd;--c1:#00e5ff;--c2:#19f5b5;--c3:#a259ff;--c4:#ff3d9a;--wr:#ffb020;--er:#ff4d5e}
            *{box-sizing:border-box}body{margin:0;background:radial-gradient(1200px 600px at 80% -10%,#122347 0,transparent 60%),var(--bg);color:var(--tx);font-family:Bahnschrift,'Segoe UI',sans-serif;padding:32px}
            h1{margin:0;font-size:30px;letter-spacing:.04em}h2{font-size:15px;letter-spacing:.18em;text-transform:uppercase;color:var(--mt);margin:28px 0 12px}
            .brand{color:var(--c1);font-weight:700;letter-spacing:.3em;font-size:13px}.sub{color:var(--mt);margin-top:6px}
            .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:12px}
            .card{background:linear-gradient(160deg,var(--sf2),var(--sf));border:1px solid var(--ln);border-radius:14px;padding:14px 16px}
            .k{color:var(--mt);font-size:12px;text-transform:uppercase;letter-spacing:.12em}.v{font-size:28px;font-weight:600;margin-top:4px}.u{font-size:13px;color:var(--mt);margin-left:4px}
            .na{color:var(--mt);font-size:16px}table{width:100%;border-collapse:collapse;font-size:13px}td,th{padding:8px 10px;border-bottom:1px solid var(--ln);text-align:left}th{color:var(--mt);font-weight:500}
            .chart{background:var(--sf);border:1px solid var(--ln);border-radius:14px;padding:12px 14px;margin-bottom:12px}.chart .t{font-size:13px;color:var(--mt);margin-bottom:6px}
            .warn{color:var(--wr)}.err{color:var(--er)}ul{margin:0;padding-left:18px;color:var(--mt)}footer{margin-top:36px;color:var(--mt);font-size:12px}
            .badge{display:inline-block;padding:3px 10px;border-radius:99px;border:1px solid var(--ln);font-size:12px;margin-left:8px;color:var(--mt)}
            """);
        sb.Append("</style></head><body>");
        sb.Append("<div class=\"brand\">FRAMEX · RELATÓRIO DE SESSÃO</div>");
        sb.Append("<h1>").Append(Enc(s.GameName)).Append("</h1>");
        sb.Append("<div class=\"sub\">").Append(s.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Pt));
        sb.Append(" · ").Append(UnitFormatter.FormatDuration(sum?.Duration ?? s.Duration));
        if (s.State == SessionState.Incomplete)
        {
            sb.Append("<span class=\"badge warn\">Sessão incompleta (recuperada)</span>");
        }

        if (!s.GameConfirmed)
        {
            sb.Append("<span class=\"badge\">Jogo não confirmado</span>");
        }

        sb.Append("</div>");

        sb.Append("<h2>Desempenho</h2><div class=\"grid\">");
        Card(sb, "FPS médio", f?.AverageFps, "", "0");
        Card(sb, "1% low", f?.Low1PercentFps, "", "0");
        Card(sb, "0,1% low", f?.Low01PercentFps, "", "0");
        Card(sb, "FPS mín. (1 s)", f?.MinFps1s, "", "0");
        Card(sb, "FPS máx. (1 s)", f?.MaxFps1s, "", "0");
        Card(sb, "Frametime médio", f?.AverageFrameTimeMs, "ms", "0.00");
        Card(sb, "Frametime P99", f?.P99FrameTimeMs, "ms", "0.00");
        Card(sb, "Quadros válidos", f?.FrameCount, "", "0");
        sb.Append("</div>");

        sb.Append("<h2>Hardware</h2><div class=\"grid\">");
        CardStat(sb, "CPU uso médio", sum?.Get(MetricKind.CpuLoad)?.Average, "%");
        CardStat(sb, "CPU temp. máx.", sum?.Get(MetricKind.CpuTemperaturePackage)?.Maximum, "°C");
        CardStat(sb, "GPU uso médio", sum?.Get(MetricKind.GpuLoad)?.Average, "%");
        CardStat(sb, "GPU temp. máx.", sum?.Get(MetricKind.GpuTemperature)?.Maximum, "°C");
        CardStat(sb, "RAM máx.", sum?.Get(MetricKind.RamUsed)?.Maximum / 1024, "GiB", "0.0");
        CardStat(sb, "VRAM máx.", sum?.Get(MetricKind.VramUsed)?.Maximum / 1024, "GiB", "0.0");
        sb.Append("</div>");

        sb.Append("<h2>Gráficos</h2>");
        Chart(sb, report, MetricKind.Fps, "FPS (janelas de 1 s)", "#00e5ff");
        Chart(sb, report, MetricKind.FrameTimeMax, "Frametime máximo por segundo (ms)", "#ff3d9a");
        Chart(sb, report, MetricKind.CpuLoad, "Uso da CPU (%)", "#19f5b5");
        Chart(sb, report, MetricKind.GpuLoad, "Uso da GPU (%)", "#a259ff");
        Chart(sb, report, MetricKind.CpuTemperaturePackage, "Temperatura da CPU (°C)", "#ffb020");
        Chart(sb, report, MetricKind.GpuTemperature, "Temperatura da GPU (°C)", "#ff6b3d");
        Chart(sb, report, MetricKind.VramUsed, "VRAM utilizada (MiB)", "#4d7cff");
        Chart(sb, report, MetricKind.RamUsed, "RAM em uso (MiB)", "#7fdcff");

        if (sum is not null)
        {
            sb.Append("<h2>Estatísticas detalhadas</h2><div class=\"card\"><table><tr><th>Métrica</th><th>Média</th><th>Mín.</th><th>Máx.</th><th>Amostras</th><th>Cobertura</th><th>Metodologia</th></tr>");
            foreach (var m in sum.Metrics)
            {
                var d = MetricCatalog.Get(m.Metric);
                sb.Append("<tr><td>").Append(Enc(d.Label)).Append("</td><td>").Append(Num(m.Average)).Append("</td><td>").Append(Num(m.Minimum))
                  .Append("</td><td>").Append(Num(m.Maximum)).Append("</td><td>").Append(m.SampleCount).Append("</td><td>")
                  .Append(m.CoveragePercent.ToString("0", Pt)).Append("%</td><td>").Append(Enc(m.Method)).Append("</td></tr>");
            }

            sb.Append("</table></div>");

            if (sum.Limitations.Count > 0 || (f?.Notes.Count ?? 0) > 0)
            {
                sb.Append("<h2>Limitações e dados ausentes</h2><div class=\"card\"><ul>");
                foreach (var l in sum.Limitations.Concat(f?.Notes ?? []))
                {
                    sb.Append("<li>").Append(Enc(l)).Append("</li>");
                }

                foreach (var g in sum.Gaps)
                {
                    sb.Append("<li>Sem dados (").Append(Enc(g.Scope)).Append("): ")
                      .Append(g.Start.ToLocalTime().ToString("HH:mm:ss", Pt)).Append(" – ").Append(g.End.ToLocalTime().ToString("HH:mm:ss", Pt))
                      .Append(" (").Append(g.Duration.TotalSeconds.ToString("0", Pt)).Append(" s)</li>");
                }

                sb.Append("</ul></div>");
            }
        }

        if (report.Events.Count > 0)
        {
            sb.Append("<h2>Linha do tempo</h2><div class=\"card\"><table><tr><th>Horário</th><th>Evento</th></tr>");
            foreach (var e in report.Events.OrderBy(e => e.Timestamp))
            {
                var cls = e.Severity switch { EventSeverity.Error => "err", EventSeverity.Warning => "warn", _ => "" };
                sb.Append("<tr><td>").Append(e.Timestamp.ToLocalTime().ToString("HH:mm:ss", Pt)).Append("</td><td class=\"").Append(cls).Append("\">")
                  .Append(Enc(e.Message)).Append("</td></tr>");
            }

            sb.Append("</table></div>");
        }

        sb.Append("<h2>Condições</h2><div class=\"card\"><table>");
        Row(sb, "Hardware", s.HardwareFingerprint);
        Row(sb, "Resolução principal", s.Resolution ?? "—");
        Row(sb, "Perfil de monitoramento", s.MonitoringProfile);
        Row(sb, "Executável", s.ExecutableName);
        Row(sb, "Versão do FRAMEX", s.AppVersion);
        sb.Append("</table></div>");
        sb.Append("<footer>Gerado pelo FRAMEX em ").Append(DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm", Pt))
          .Append(". Métricas indisponíveis são exibidas como “—” e nunca estimadas.</footer></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string label, double? value, string unit, string fmt)
    {
        sb.Append("<div class=\"card\"><div class=\"k\">").Append(Enc(label)).Append("</div><div class=\"v\">");
        if (value is { } v)
        {
            sb.Append(v.ToString(fmt, Pt)).Append("<span class=\"u\">").Append(unit).Append("</span>");
        }
        else
        {
            sb.Append("<span class=\"na\">indisponível</span>");
        }

        sb.Append("</div></div>");
    }

    private static void Card(StringBuilder sb, string label, long? value, string unit, string fmt) => Card(sb, label, value.HasValue ? (double?)value.Value : null, unit, fmt);

    private static void CardStat(StringBuilder sb, string label, double? value, string unit, string fmt = "0") => Card(sb, label, value, unit, fmt);

    private static void Row(StringBuilder sb, string k, string v) => sb.Append("<tr><th>").Append(Enc(k)).Append("</th><td>").Append(Enc(v)).Append("</td></tr>");

    private static string Num(double? v) => v is { } x ? x.ToString("0.#", Pt) : "—";

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static void Chart(StringBuilder sb, SessionReport report, MetricKind kind, string title, string color)
    {
        var points = report.Samples.Where(x => x.Metric == kind).OrderBy(x => x.Timestamp).ToList();
        if (points.Count < 2)
        {
            return;
        }

        const int w = 1000;
        const int h = 160;
        var t0 = points[0].Timestamp;
        var span = Math.Max(1, (points[^1].Timestamp - t0).TotalSeconds);
        var min = Math.Min(0, points.Min(p => p.Value));
        var max = points.Max(p => p.Value);
        if (max - min < 1e-6)
        {
            max = min + 1;
        }

        // Decimação min/max por coluna de pixel para manter o arquivo leve em sessões longas.
        var columns = new Dictionary<int, (double Min, double Max)>();
        foreach (var p in points)
        {
            var x = (int)((p.Timestamp - t0).TotalSeconds / span * w);
            columns[x] = columns.TryGetValue(x, out var c) ? (Math.Min(c.Min, p.Value), Math.Max(c.Max, p.Value)) : (p.Value, p.Value);
        }

        var path = new StringBuilder();
        foreach (var (x, c) in columns.OrderBy(k => k.Key))
        {
            var y1 = h - ((c.Max - min) / (max - min) * (h - 8)) - 4;
            var y2 = h - ((c.Min - min) / (max - min) * (h - 8)) - 4;
            path.Append(path.Length == 0 ? 'M' : 'L').Append(x.ToString(Inv)).Append(',').Append(y1.ToString("0.#", Inv));
            if (Math.Abs(y2 - y1) > 0.5)
            {
                path.Append('L').Append(x.ToString(Inv)).Append(',').Append(y2.ToString("0.#", Inv));
            }
        }

        var gid = "g" + (int)kind;
        sb.Append("<div class=\"chart\"><div class=\"t\">").Append(Enc(title)).Append(" · máx. ").Append(max.ToString("0.#", Pt)).Append("</div>");
        sb.Append("<svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" width=\"100%\" height=\"").Append(h).Append("\" preserveAspectRatio=\"none\">");
        sb.Append("<defs><linearGradient id=\"").Append(gid).Append("\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"").Append(color)
          .Append("\" stop-opacity=\".35\"/><stop offset=\"1\" stop-color=\"").Append(color).Append("\" stop-opacity=\"0\"/></linearGradient></defs>");
        sb.Append("<path d=\"").Append(path).Append(" L").Append(w).Append(',').Append(h).Append(" L0,").Append(h).Append(" Z\" fill=\"url(#").Append(gid).Append(")\"/>");
        sb.Append("<path d=\"").Append(path).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"1.6\" vector-effect=\"non-scaling-stroke\"/>");
        sb.Append("</svg></div>");
    }
}
