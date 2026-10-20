using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Framex.Core.SetupScore;

/// <summary>
/// Serialização da avaliação (histórico e exportação) e relatório HTML autocontido.
/// Não contém números de série, nome de usuário, nome do computador nem caminhos locais.
/// </summary>
public static class SetupReportExporter
{
    public const string Format = "framex-setup-score";
    public const int FormatVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    private static readonly JsonSerializerOptions CompactOptions = new(JsonOptions) { WriteIndented = false };

    private sealed record Envelope(string Format, int FormatVersion, SetupEvaluation Evaluation);

    public static string Serialize(SetupEvaluation evaluation, bool indented = false) =>
        JsonSerializer.Serialize(new Envelope(Format, FormatVersion, evaluation), indented ? JsonOptions : CompactOptions);

    /// <summary>Nulo para conteúdo de formato desconhecido ou inválido.</summary>
    public static SetupEvaluation? Deserialize(string json)
    {
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(json, JsonOptions);
            return env is { Format: Format, FormatVersion: FormatVersion, Evaluation: not null } ? env.Evaluation : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static string SuggestFileName(SetupEvaluation e, string extension) => $"FRAMEX_NotaDoSetup_{e.CreatedAt.ToLocalTime():yyyyMMdd_HHmmss}{extension}";

    public static string ToHtml(SetupEvaluation e)
    {
        var pt = SetupScoreText.Pt;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>FRAMEX — Nota do Setup</title><style>");
        sb.Append("""
            :root{--bg:#070b14;--sf:#0f1626;--ln:#22304d;--tx:#e7eefc;--mt:#8a9bbd;--c1:#00e5ff;--wr:#ffb020;--er:#ff4d5e;--ok:#19f5b5}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--tx);font-family:Bahnschrift,'Segoe UI',sans-serif;padding:32px;max-width:1100px}
            h1{margin:0;font-size:28px}h2{font-size:14px;letter-spacing:.16em;text-transform:uppercase;color:var(--mt);margin:28px 0 10px}
            .brand{color:var(--c1);font-weight:700;letter-spacing:.3em;font-size:12px}.sub{color:var(--mt);margin-top:6px}
            .score{font-size:64px;font-weight:700;color:var(--c1)}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:12px}
            .card{background:var(--sf);border:1px solid var(--ln);border-radius:12px;padding:14px 16px}.k{color:var(--mt);font-size:12px}
            table{width:100%;border-collapse:collapse;font-size:13px}td,th{padding:6px 8px;border-bottom:1px solid var(--ln);text-align:left;vertical-align:top}th{color:var(--mt);font-weight:500}
            .warn{color:var(--wr)}.err{color:var(--er)}.ok{color:var(--ok)}code{font-family:Consolas,monospace;font-size:12px}footer{margin-top:32px;color:var(--mt);font-size:12px}
            """);
        sb.Append("</style></head><body><div class=\"brand\">FRAMEX · NOTA DO SETUP</div>");
        sb.Append("<h1>").Append(e.Overall.Score is { } s ? s.ToString(pt) + " / 1000" : "Sem nota geral").Append("</h1>");
        sb.Append("<div class=\"sub\">").Append(Enc(e.Overall.Tier ?? SetupScoreText.Completeness(e.Overall.Completeness))).Append(" · ")
          .Append(Enc(SetupScoreText.Completeness(e.Overall.Completeness))).Append(" · Cobertura ").Append(SetupScoreText.Percent(e.Overall.Coverage))
          .Append(" · Confiabilidade ").Append(Enc(SetupScoreText.Confidence(e.Confidence))).Append("<br>")
          .Append(e.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", pt)).Append(" · Metodologia ").Append(Enc(e.MethodologyVersion))
          .Append(" · Armazenamento ").Append(e.IncludeStorage ? "considerado" : "desativado").Append("</div>");

        sb.Append("<h2>Componentes</h2><div class=\"grid\">");
        foreach (var c in e.Categories)
        {
            sb.Append("<div class=\"card\"><div class=\"k\">").Append(Enc(SetupScoreText.Category(c.Category))).Append(" · peso ")
              .Append(SetupScoreText.Percent(c.BaseWeight)).Append(c.Status == CategoryStatus.Scored ? " (efetivo " + SetupScoreText.Percent(c.EffectiveWeight) + ")" : "").Append("</div>");
            sb.Append("<div style=\"font-size:16px;margin:6px 0\">").Append(Enc(c.ComponentName)).Append("</div>");
            sb.Append("<div style=\"font-size:28px;font-weight:600\">").Append(c.Score is { } cs ? cs.ToString(pt) : "<span class=\"warn\" style=\"font-size:16px\">" + Enc(SetupScoreText.Status(c.Status)) + "</span>").Append("</div>");
            sb.Append("<table>");
            Row(sb, "Correspondência", SetupScoreText.Match(c.Match));
            Row(sb, "Confiabilidade", SetupScoreText.Confidence(c.Confidence));
            foreach (var f in c.Facts)
            {
                Row(sb, f.Label, f.Value);
            }

            if (c.UnavailableReason is { } why)
            {
                Row(sb, "Motivo", why);
            }

            sb.Append("</table></div>");
        }

        sb.Append("</div>");

        if (e.Drives.Count > 0)
        {
            sb.Append("<h2>Unidades de armazenamento</h2><div class=\"card\"><table><tr><th>Modelo</th><th>Tipo</th><th>Interface</th><th>Capacidade</th><th>Situação</th></tr>");
            foreach (var d in e.Drives)
            {
                sb.Append("<tr><td>").Append(Enc(d.Model)).Append("</td><td>").Append(Enc(d.Type)).Append("</td><td>").Append(Enc(d.Bus)).Append("</td><td>")
                  .Append(d.SizeBytes is { } b ? Enc(StorageCapacityAggregator.FormatNominal(b)) : "—").Append("</td><td>").Append(Enc(d.Reason)).Append("</td></tr>");
            }

            sb.Append("</table></div>");
        }

        sb.Append("<h2>Como chegamos a essa nota</h2><div class=\"card\">");
        sb.Append("<p><code>S = round( Σ wᵢ·sᵢ / Σ wᵢ )</code>, somando apenas as categorias avaliadas.</p><table><tr><th>Categoria</th><th>Nota</th><th>Peso original</th><th>Peso efetivo</th><th>Contribuição</th></tr>");
        foreach (var c in e.Categories)
        {
            sb.Append("<tr><td>").Append(Enc(SetupScoreText.Category(c.Category))).Append("</td><td>").Append(c.Score?.ToString(pt) ?? Enc(SetupScoreText.Status(c.Status)))
              .Append("</td><td>").Append(SetupScoreText.Percent(c.BaseWeight)).Append("</td><td>").Append(SetupScoreText.Percent(c.EffectiveWeight)).Append("</td><td>")
              .Append(c.Score is { } v ? SetupScoreText.Number(v * c.EffectiveWeight, "0.0") : "—").Append("</td></tr>");
        }

        sb.Append("</table><ul>");
        foreach (var n in e.Overall.Notes)
        {
            sb.Append("<li>").Append(Enc(n)).Append("</li>");
        }

        foreach (var c in e.Categories.Where(c => c.Basis != ScoreBasis.None))
        {
            sb.Append("<li><b>").Append(Enc(SetupScoreText.Category(c.Category))).Append(":</b> ").Append(Enc(c.BasisDescription)).Append("</li>");
        }

        sb.Append("</ul></div>");

        if (e.Issues.Count > 0)
        {
            sb.Append("<h2>Problemas na identificação do hardware</h2>");
            foreach (var i in e.Issues)
            {
                var cls = i.Severity switch { IssueSeverity.Error => "err", IssueSeverity.Warning => "warn", _ => "" };
                sb.Append("<div class=\"card\" style=\"margin-bottom:10px\"><div class=\"").Append(cls).Append("\">").Append(Enc(SetupScoreText.Category(i.Category)))
                  .Append(" · ").Append(Enc(i.State)).Append("</div><table>");
                Row(sb, "Nome detectado", i.DetectedName);
                Row(sb, "Fonte", i.Source);
                Row(sb, "Motivo", i.Reason);
                Row(sb, "Consequência", i.Consequence);
                if (i.Action is { } a)
                {
                    Row(sb, "Ação sugerida", a);
                }

                Row(sb, "Etapa · código", $"{i.Stage} · {i.ErrorCode}");
                if (i.Candidates.Count > 0)
                {
                    Row(sb, "Candidatos", string.Join(" · ", i.Candidates));
                }

                sb.Append("</table></div>");
            }
        }

        sb.Append("<h2>Fontes</h2><div class=\"card\"><table><tr><th>Base</th><th>Licença</th><th>Obtida em</th><th>Registros</th><th>SHA-256</th></tr>");
        foreach (var d in e.Datasets)
        {
            sb.Append("<tr><td>").Append(Enc(d.Title)).Append("<br><code>").Append(Enc(d.SourceUrl)).Append("</code></td><td>").Append(Enc(d.License)).Append("</td><td>")
              .Append(d.RetrievedAt.ToString("dd/MM/yyyy", pt)).Append(" (").Append(Enc(d.Origin)).Append(")</td><td>").Append(d.RecordCount).Append("</td><td><code>")
              .Append(Enc(d.Sha256[..Math.Min(16, d.Sha256.Length)])).Append("…</code></td></tr>");
        }

        sb.Append("</table></div><footer>Gerado pelo FRAMEX ").Append(Enc(e.AppVersion))
          .Append(". As notas de CPU e GPU usam medianas públicas do Blender Benchmark (renderização), não testes de jogos, e nenhum teste foi executado neste computador. ")
          .Append("Componentes não confirmados nunca recebem nota zero.</footer></body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string v) => sb.Append("<tr><th>").Append(Enc(k)).Append("</th><td>").Append(Enc(v)).Append("</td></tr>");

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
