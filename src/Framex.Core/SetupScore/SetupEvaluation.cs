namespace Framex.Core.SetupScore;

public enum CategoryStatus
{
    /// <summary>Pontuação calculada com dados confirmados.</summary>
    Scored,

    /// <summary>Excluída intencionalmente pelo usuário (ex.: armazenamento desmarcado).</summary>
    ExcludedByUser,

    /// <summary>Não pôde ser avaliada (identificação, fonte ou dados insuficientes). Nunca equivale a nota zero.</summary>
    Unavailable,
}

public enum DataConfidence
{
    None,
    Low,
    Medium,
    High,
}

public enum EvaluationCompleteness
{
    /// <summary>CPU, GPU e RAM avaliadas e, se habilitado, o armazenamento também.</summary>
    Complete,

    /// <summary>CPU, GPU e RAM avaliadas, mas o armazenamento (habilitado) não pôde ser avaliado: pesos renormalizados.</summary>
    Partial,

    /// <summary>Falta CPU, GPU ou RAM: nenhuma nota geral é emitida.</summary>
    Incomplete,
}

public enum ScoreBasis
{
    None,
    MeasuredBenchmark,
    CapacityCurve,
}

public sealed record Fact(string Label, string Value);

public sealed record DriveSummary(string Model, string Type, string Bus, long? SizeBytes, DriveDecision Decision, string Reason);

public sealed record GpuSummary(string Key, string Name, string Vendor, GpuKind Kind, string KindReason, long? DedicatedMemoryBytes, bool Selected);

public sealed record CategoryEvaluation(
    SetupCategory Category,
    CategoryStatus Status,
    int? Score,
    double BaseWeight,
    double EffectiveWeight,
    string ComponentName,
    ScoreBasis Basis,
    string BasisDescription,
    MatchState? Match,
    string? MatchedRecord,
    /// <summary>Valor de entrada da fórmula: mediana do benchmark (CPU/GPU), GiB instalados (RAM) ou GB nominais (armazenamento).</summary>
    double? InputValue,
    int? BenchmarkSamples,
    string? DatasetId,
    DataConfidence Confidence,
    string? UnavailableReason,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<string> Candidates)
{
    public CategoryEvaluation WithWeight(double effective) => this with { EffectiveWeight = effective };
}

public sealed record WeightShare(SetupCategory Category, double BaseWeight, double EffectiveWeight, CategoryStatus Status);

public sealed record OverallResult(
    int? Score,
    EvaluationCompleteness Completeness,
    IReadOnlyList<WeightShare> Weights,
    bool Renormalized,
    double Coverage,
    string? Tier,
    IReadOnlyList<string> Notes);

public sealed record CategoryInput(SetupCategory Category, CategoryStatus Status, int? Score);

/// <summary>
/// Nota geral: <c>S = round(Σ aᵢ·wᵢ·sᵢ / Σ aᵢ·wᵢ)</c> sobre as categorias pontuadas (aᵢ = 1), com arredondamento
/// "meio para longe de zero". Exclusão pelo usuário e indisponibilidade são estados diferentes:
/// sem CPU, GPU ou RAM não há nota geral; sem armazenamento habilitado a nota é marcada como parcial.
/// </summary>
public static class OverallScoreCalculator
{
    public static OverallResult Calculate(IReadOnlyList<CategoryInput> inputs)
    {
        var notes = new List<string>();
        var byCategory = SetupScoreMethodology.BaseWeights.ToDictionary(
            w => w.Category,
            w => inputs.FirstOrDefault(i => i.Category == w.Category) ?? new CategoryInput(w.Category, CategoryStatus.Unavailable, null));

        foreach (var input in byCategory.Values)
        {
            if (input.Status == CategoryStatus.Scored && input.Score is not (>= 0 and <= SetupScoreMethodology.MaxScore))
            {
                throw new ArgumentException($"Pontuação de {input.Category} fora de 0–1000.", nameof(inputs));
            }
        }

        var scored = SetupScoreMethodology.BaseWeights.Where(w => byCategory[w.Category].Status == CategoryStatus.Scored).ToList();
        var active = SetupScoreMethodology.BaseWeights.Where(w => byCategory[w.Category].Status != CategoryStatus.ExcludedByUser).ToList();
        var scoredWeight = scored.Sum(w => w.Weight);
        var activeWeight = active.Sum(w => w.Weight);
        var coverage = activeWeight > 0 ? scoredWeight / activeWeight : 0;

        var weights = SetupScoreMethodology.BaseWeights
            .Select(w => new WeightShare(w.Category, w.Weight, byCategory[w.Category].Status == CategoryStatus.Scored && scoredWeight > 0 ? w.Weight / scoredWeight : 0, byCategory[w.Category].Status))
            .ToList();
        var renormalized = Math.Abs(scoredWeight - 1) > 1e-12;

        var missingRequired = SetupScoreMethodology.RequiredCategories.Where(c => byCategory[c].Status != CategoryStatus.Scored).ToList();
        if (missingRequired.Count > 0)
        {
            notes.Add($"Sem avaliação válida de {string.Join(", ", missingRequired.Select(Label))}: a nota geral não foi calculada.");
            return new OverallResult(null, EvaluationCompleteness.Incomplete, weights, renormalized, coverage, null, notes);
        }

        var total = scored.Sum(w => w.Weight * byCategory[w.Category].Score!.Value) / scoredWeight;
        var score = SetupScoreMethodology.Round(total);
        var storage = byCategory[SetupCategory.Storage].Status;
        var completeness = storage == CategoryStatus.Unavailable ? EvaluationCompleteness.Partial : EvaluationCompleteness.Complete;

        if (storage == CategoryStatus.ExcludedByUser)
        {
            notes.Add("Armazenamento desativado pelo usuário: os pesos de GPU, CPU e RAM foram renormalizados para somar 100%.");
        }
        else if (storage == CategoryStatus.Unavailable)
        {
            notes.Add("Armazenamento habilitado, mas não avaliado: nota parcial, com os pesos de GPU, CPU e RAM renormalizados. A ausência não foi tratada como zero.");
        }

        return new OverallResult(score, completeness, weights, renormalized, coverage, SetupScoreMethodology.TierFor(score).Name, notes);
    }

    public static string Label(SetupCategory category) => category switch
    {
        SetupCategory.Gpu => "GPU",
        SetupCategory.Cpu => "CPU",
        SetupCategory.Ram => "RAM",
        _ => "Armazenamento",
    };
}

public sealed record GpuSelection(GpuAdapter? Selected, bool Ambiguous, string Reason, IReadOnlyList<GpuAdapter> Eligible);

/// <summary>
/// Escolhe a GPU pontuada. Nunca soma GPUs. Adaptadores virtuais (ex.: Parsec, Microsoft Basic Display) são ignorados.
/// Com uma única GPU dedicada, ela é escolhida; sem dedicadas, uma única integrada; nos demais casos, a escolha é do usuário.
/// </summary>
public static class GpuSelector
{
    public static GpuSelection Select(IReadOnlyList<GpuAdapter> gpus, string? userSelectedKey)
    {
        var eligible = gpus.Where(g => g.Kind != GpuKind.Virtual).ToList();
        if (userSelectedKey is not null && eligible.FirstOrDefault(g => g.Key == userSelectedKey) is { } chosen)
        {
            return new GpuSelection(chosen, false, "GPU escolhida manualmente pelo usuário.", eligible);
        }

        if (eligible.Count == 0)
        {
            return new GpuSelection(null, false, gpus.Count == 0
                ? "Nenhum adaptador gráfico foi reportado pelo Windows."
                : "Somente adaptadores virtuais foram encontrados.", eligible);
        }

        if (eligible.Count == 1)
        {
            return new GpuSelection(eligible[0], false, "Único adaptador gráfico físico encontrado.", eligible);
        }

        var dedicated = eligible.Where(g => g.Kind == GpuKind.Dedicated).ToList();
        var unknown = eligible.Count(g => g.Kind == GpuKind.Unknown);
        if (dedicated.Count == 1 && unknown == 0)
        {
            return new GpuSelection(dedicated[0], false, "Única GPU dedicada; as demais são integradas ou virtuais.", eligible);
        }

        if (dedicated.Count == 0 && unknown == 0 && eligible.Count(g => g.Kind == GpuKind.Integrated) == 1)
        {
            return new GpuSelection(eligible.First(g => g.Kind == GpuKind.Integrated), false, "Única GPU integrada.", eligible);
        }

        return new GpuSelection(null, true, dedicated.Count > 1
            ? "Há mais de uma GPU dedicada e não é possível saber qual é usada nos jogos."
            : "Não foi possível classificar com segurança qual adaptador é dedicado.", eligible);
    }
}

public enum IssueKind
{
    NotInDatabase,
    IncompleteLocalName,
    SourceUnavailable,
    InvalidSource,
    AmbiguousRecords,
    ProbableNeedsConfirmation,
    MissingPerformanceData,
    InvalidRecord,
    InsufficientLocalData,
    LowSampleCount,
    GpuSelectionAmbiguous,
    StorageNoEligibleDrive,
    StorageDriveUncertain,
    MemorySourcesDisagree,
    MultipleProcessors,
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Ocorrência explicada na seção "Problemas na identificação do hardware".</summary>
public sealed record EvaluationIssue(
    SetupCategory Category,
    IssueKind Kind,
    IssueSeverity Severity,
    string DetectedName,
    string State,
    string Source,
    string Reason,
    string Consequence,
    string? Action,
    string Stage,
    DateTimeOffset At,
    string? ErrorCode,
    IReadOnlyList<string> Candidates);

public sealed record SetupEvaluation(
    Guid Id,
    DateTimeOffset CreatedAt,
    string MethodologyVersion,
    string AppVersion,
    bool IncludeStorage,
    OverallResult Overall,
    DataConfidence Confidence,
    IReadOnlyList<CategoryEvaluation> Categories,
    IReadOnlyList<EvaluationIssue> Issues,
    IReadOnlyList<BenchmarkDatasetInfo> Datasets,
    long? StorageTotalBytes,
    IReadOnlyList<DriveSummary> Drives,
    IReadOnlyList<GpuSummary> Gpus)
{
    public CategoryEvaluation Get(SetupCategory category) => Categories.First(c => c.Category == category);
}

public sealed record SetupEvaluationSummary(Guid Id, DateTimeOffset CreatedAt, string MethodologyVersion, bool IncludeStorage, EvaluationCompleteness Completeness, int? Score, double Coverage);

public sealed record ComponentOverride(SetupCategory Category, string DeviceKey, string RecordName, string DatasetId, DateTimeOffset CreatedAt)
{
    /// <summary>Chave reservada para a escolha manual do adaptador gráfico a ser avaliado (RecordName = chave do adaptador).</summary>
    public const string GpuAdapterSelectionKey = "@adaptador-selecionado";
}

public interface ISetupEvaluationRepository
{
    void Save(SetupEvaluation evaluation);

    IReadOnlyList<SetupEvaluationSummary> List(int limit = 50);

    /// <summary>Nulo se não existir ou se o registro estiver corrompido (falha de integridade).</summary>
    SetupEvaluation? Get(Guid id);

    void Delete(Guid id);

    IReadOnlyList<ComponentOverride> GetOverrides();

    void SaveOverride(ComponentOverride value);

    void RemoveOverride(SetupCategory category, string deviceKey);
}
