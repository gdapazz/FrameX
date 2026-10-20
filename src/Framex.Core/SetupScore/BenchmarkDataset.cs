using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Framex.Core.SetupScore;

/// <summary>Resultado mediano de um dispositivo em uma base de benchmark.</summary>
public sealed record BenchmarkRecord(string DeviceName, double MedianScore, int Samples);

public sealed record RejectedRecord(string? DeviceName, string Reason);

/// <summary>Definição fixa de um conjunto de dados (o que baixar, de onde e para qual categoria).</summary>
public sealed record BenchmarkDatasetDefinition(
    string Id,
    SetupCategory Category,
    string Backend,
    string Title,
    string BlenderVersion,
    string SourceUrl,
    ushort? PciVendor)
{
    public const string License = "CC0 1.0 (domínio público) — Blender Open Data";
}

/// <summary>Metadados de proveniência registrados junto com cada avaliação.</summary>
public sealed record BenchmarkDatasetInfo(
    string Id,
    string Title,
    string SourceUrl,
    string License,
    string BlenderVersion,
    DateTimeOffset RetrievedAt,
    string Sha256,
    int RecordCount,
    int RejectedCount,
    string Origin);

public sealed class BenchmarkDataset
{
    private readonly Dictionary<string, List<BenchmarkRecord>> _strict = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BenchmarkRecord>> _extended = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RejectedRecord> _rejectedByStrict = new(StringComparer.Ordinal);

    public BenchmarkDataset(BenchmarkDatasetDefinition definition, BenchmarkDatasetInfo info, IReadOnlyList<BenchmarkRecord> records, IReadOnlyList<RejectedRecord> rejected)
    {
        Definition = definition;
        Info = info;
        Records = records;
        Rejected = rejected;
        foreach (var r in records)
        {
            Add(_strict, HardwareNameNormalizer.Strict(r.DeviceName), r);
            Add(_extended, HardwareNameNormalizer.Extended(r.DeviceName, definition.Category), r);
        }

        foreach (var r in rejected.Where(r => !string.IsNullOrWhiteSpace(r.DeviceName)))
        {
            _rejectedByStrict.TryAdd(HardwareNameNormalizer.Strict(r.DeviceName), r);
        }
    }

    public BenchmarkDatasetDefinition Definition { get; }

    public BenchmarkDatasetInfo Info { get; }

    public IReadOnlyList<BenchmarkRecord> Records { get; }

    public IReadOnlyList<RejectedRecord> Rejected { get; }

    public IReadOnlyList<BenchmarkRecord> ByStrict(string key) => _strict.TryGetValue(key, out var l) ? l : [];

    public IReadOnlyList<BenchmarkRecord> ByExtended(string key) => _extended.TryGetValue(key, out var l) ? l : [];

    public RejectedRecord? RejectedByStrict(string key) => _rejectedByStrict.GetValueOrDefault(key);

    private static void Add(Dictionary<string, List<BenchmarkRecord>> map, string key, BenchmarkRecord r)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(r);
    }
}

public sealed record DatasetParseResult(bool Success, IReadOnlyList<BenchmarkRecord> Records, IReadOnlyList<RejectedRecord> Rejected, string? Error);

/// <summary>
/// Leitor estrito da resposta de consulta do Blender Open Data
/// (<c>/benchmarks/query/?group_by=device_name&amp;response_type=datatables</c>).
/// Rejeita o arquivo inteiro se o esquema mudar; rejeita linhas individuais inválidas mantendo o motivo.
/// </summary>
public static class BlenderOpenDataParser
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MinValidRecords = 15;
    public const int MaxNameLength = 160;
    public const double MaxPlausibleScore = 1_000_000;

    private static readonly string[] ExpectedColumns = ["Device Name", "Median Score", "Number of Benchmarks"];

    public static string Sha256(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static DatasetParseResult Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Fail("Conteúdo vazio.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return Fail($"Arquivo maior que o limite de {MaxBytes / 1024 / 1024} MB.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            return Fail("JSON inválido: " + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return Fail("Esquema inesperado: faltam as listas \"columns\" e \"rows\".");
            }

            var names = new List<string>();
            foreach (var c in columns.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("display_name", out var dn) || dn.ValueKind != JsonValueKind.String)
                {
                    return Fail("Esquema inesperado: coluna sem \"display_name\".");
                }

                names.Add(dn.GetString()!);
            }

            if (!names.SequenceEqual(ExpectedColumns))
            {
                return Fail($"Esquema inesperado: colunas [{string.Join(", ", names)}]; esperado [{string.Join(", ", ExpectedColumns)}].");
            }

            var records = new List<BenchmarkRecord>();
            var rejected = new List<RejectedRecord>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3)
                {
                    rejected.Add(new RejectedRecord(null, "Linha com formato inesperado."));
                    continue;
                }

                var nameEl = row[0];
                var name = nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
                {
                    rejected.Add(new RejectedRecord(name, "Nome de dispositivo ausente ou inválido."));
                    continue;
                }

                if (row[1].ValueKind != JsonValueKind.Number || !row[1].TryGetDouble(out var score) || !double.IsFinite(score) || score <= 0 || score > MaxPlausibleScore)
                {
                    rejected.Add(new RejectedRecord(name, "Pontuação mediana ausente, não numérica ou fora da faixa plausível."));
                    continue;
                }

                if (row[2].ValueKind != JsonValueKind.Number || !row[2].TryGetInt32(out var samples) || samples < 1)
                {
                    rejected.Add(new RejectedRecord(name, "Número de envios ausente ou inválido."));
                    continue;
                }

                records.Add(new BenchmarkRecord(name, score, samples));
            }

            if (records.Count < MinValidRecords)
            {
                return new DatasetParseResult(false, records, rejected, $"Apenas {records.Count} registros válidos (mínimo {MinValidRecords}); a base parece incompleta.");
            }

            return new DatasetParseResult(true, records, rejected, null);
        }
    }

    private static DatasetParseResult Fail(string error) => new(false, [], [], error);
}

public enum DatasetFailure
{
    None,
    NotAvailable,
    Invalid,
    Network,
    Timeout,
    Cancelled,
}

public sealed record DatasetLoadResult(BenchmarkDatasetDefinition Definition, BenchmarkDataset? Dataset, DatasetFailure Failure, string? Error)
{
    public bool Success => Dataset is not null;
}

public sealed record DatasetUpdateResult(string DatasetId, bool Updated, bool Unchanged, DatasetFailure Failure, string Message);

/// <summary>Fonte de bases de benchmark (cache local versionado, cópia embutida e atualização explícita).</summary>
public interface IBenchmarkDatasetSource
{
    IReadOnlyList<BenchmarkDatasetDefinition> Definitions { get; }

    Task<DatasetLoadResult> LoadAsync(BenchmarkDatasetDefinition definition, CancellationToken cancellationToken);

    Task<IReadOnlyList<DatasetUpdateResult>> UpdateAsync(CancellationToken cancellationToken);
}
