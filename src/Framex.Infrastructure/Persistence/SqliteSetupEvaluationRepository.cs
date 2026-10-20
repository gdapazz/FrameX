using Framex.Core.SetupScore;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Persistence;

/// <summary>
/// Histórico da Nota do Setup. Cada avaliação é gravada em uma transação com o payload completo e seu SHA-256;
/// na leitura, um payload que não confere é tratado como corrompido e não é exibido como válido.
/// </summary>
public sealed class SqliteSetupEvaluationRepository : ISetupEvaluationRepository
{
    private readonly SqliteDatabase _db;
    private readonly ILogger<SqliteSetupEvaluationRepository> _logger;

    public SqliteSetupEvaluationRepository(SqliteDatabase db, ILogger<SqliteSetupEvaluationRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Save(SetupEvaluation evaluation)
    {
        var payload = SetupReportExporter.Serialize(evaluation);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.Command("""
            INSERT OR REPLACE INTO setup_evaluations(id, created_at, methodology, include_storage, completeness, overall, coverage, payload_json, payload_sha256)
            VALUES($id, $t, $m, $s, $c, $o, $cov, $p, $h)
            """, tx))
        {
            cmd.With("$id", evaluation.Id.ToString()).With("$t", evaluation.CreatedAt.ToUnixMs()).With("$m", evaluation.MethodologyVersion)
               .With("$s", evaluation.IncludeStorage ? 1 : 0).With("$c", (int)evaluation.Overall.Completeness).With("$o", evaluation.Overall.Score)
               .With("$cov", evaluation.Overall.Coverage).With("$p", payload).With("$h", BlenderOpenDataParser.Sha256(payload));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<SetupEvaluationSummary> List(int limit = 50)
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT id, created_at, methodology, include_storage, completeness, overall, coverage FROM setup_evaluations ORDER BY created_at DESC LIMIT $n")
            .With("$n", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        var list = new List<SetupEvaluationSummary>();
        while (r.Read())
        {
            if (!Guid.TryParse(r.GetString(0), out var id))
            {
                continue;
            }

            list.Add(new SetupEvaluationSummary(id, Db.FromUnixMs(r.GetInt64(1)), r.GetString(2), r.GetInt64(3) != 0,
                Enum.IsDefined(typeof(EvaluationCompleteness), r.GetInt32(4)) ? (EvaluationCompleteness)r.GetInt32(4) : EvaluationCompleteness.Incomplete,
                r.IsDBNull(5) ? null : r.GetInt32(5), r.GetDouble(6)));
        }

        return list;
    }

    public SetupEvaluation? Get(Guid id)
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT payload_json, payload_sha256 FROM setup_evaluations WHERE id=$id").With("$id", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        var payload = r.GetString(0);
        if (!string.Equals(BlenderOpenDataParser.Sha256(payload), r.GetString(1), StringComparison.Ordinal))
        {
            _logger.LogWarning("Avaliação {Id} com falha de integridade (SHA-256 não confere)", id);
            return null;
        }

        var evaluation = SetupReportExporter.Deserialize(payload);
        if (evaluation is null)
        {
            _logger.LogWarning("Avaliação {Id} com formato desconhecido", id);
        }

        return evaluation;
    }

    public void Delete(Guid id)
    {
        using var c = _db.Open();
        using var cmd = c.Command("DELETE FROM setup_evaluations WHERE id=$id").With("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ComponentOverride> GetOverrides()
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT category, device_key, record_name, dataset_id, created_at FROM setup_overrides");
        using var r = cmd.ExecuteReader();
        var list = new List<ComponentOverride>();
        while (r.Read())
        {
            var cat = r.GetInt32(0);
            if (Enum.IsDefined(typeof(SetupCategory), cat))
            {
                list.Add(new ComponentOverride((SetupCategory)cat, r.GetString(1), r.GetString(2), r.GetString(3), Db.FromUnixMs(r.GetInt64(4))));
            }
        }

        return list;
    }

    public void SaveOverride(ComponentOverride value)
    {
        using var c = _db.Open();
        using var cmd = c.Command("""
            INSERT INTO setup_overrides(category, device_key, record_name, dataset_id, created_at) VALUES($c, $k, $r, $d, $t)
            ON CONFLICT(category, device_key) DO UPDATE SET record_name=excluded.record_name, dataset_id=excluded.dataset_id, created_at=excluded.created_at
            """).With("$c", (int)value.Category).With("$k", value.DeviceKey).With("$r", value.RecordName).With("$d", value.DatasetId).With("$t", value.CreatedAt.ToUnixMs());
        cmd.ExecuteNonQuery();
    }

    public void RemoveOverride(SetupCategory category, string deviceKey)
    {
        using var c = _db.Open();
        using var cmd = c.Command("DELETE FROM setup_overrides WHERE category=$c AND device_key=$k").With("$c", (int)category).With("$k", deviceKey);
        cmd.ExecuteNonQuery();
    }
}
