namespace Framex.Core.SetupScore;

public sealed record ScoringContext(
    SetupInventory Inventory,
    bool IncludeStorage,
    IReadOnlyList<BenchmarkDatasetDefinition> Definitions,
    IReadOnlyDictionary<string, DatasetLoadResult> Loads,
    IReadOnlyList<ComponentOverride> Overrides,
    Guid Id,
    DateTimeOffset Now,
    string AppVersion);

/// <summary>
/// Monta a avaliação a partir do inventário local e das bases já carregadas. Função pura e determinística:
/// as mesmas entradas produzem sempre o mesmo resultado (sem rede, relógio ou aleatoriedade).
/// </summary>
public static class SetupScorer
{
    public const string StageIdentify = "Identificação";
    public const string StageQuery = "Consulta às bases";
    public const string StageValidate = "Validação";

    public static string CpuDeviceKey(CpuIdentity cpu) => "cpu:" + HardwareNameNormalizer.Strict(cpu.Name);

    public static BenchmarkDatasetDefinition? CpuDefinition(IReadOnlyList<BenchmarkDatasetDefinition> definitions) =>
        definitions.FirstOrDefault(d => d.Category == SetupCategory.Cpu);

    public static BenchmarkDatasetDefinition? GpuDefinition(IReadOnlyList<BenchmarkDatasetDefinition> definitions, GpuAdapter? gpu) =>
        gpu?.PciVendorId is { } vendor ? definitions.FirstOrDefault(d => d.Category == SetupCategory.Gpu && d.PciVendor == vendor) : null;

    public static GpuSelection SelectGpu(SetupInventory inventory, IReadOnlyList<ComponentOverride> overrides) =>
        GpuSelector.Select(inventory.Gpus, overrides.FirstOrDefault(o => o.Category == SetupCategory.Gpu && o.DeviceKey == ComponentOverride.GpuAdapterSelectionKey)?.RecordName);

    public static SetupEvaluation Build(ScoringContext ctx)
    {
        var issues = new List<EvaluationIssue>();
        var gpuSelection = SelectGpu(ctx.Inventory, ctx.Overrides);
        var cpu = EvaluateCpu(ctx, issues);
        var gpu = EvaluateGpu(ctx, gpuSelection, issues);
        var ram = EvaluateRam(ctx, issues);
        var (storage, aggregation) = EvaluateStorage(ctx, issues);

        var categories = new[] { gpu, cpu, ram, storage };
        var overall = OverallScoreCalculator.Calculate(categories.Select(c => new CategoryInput(c.Category, c.Status, c.Score)).ToList());
        categories = categories.Select(c => c.WithWeight(overall.Weights.First(w => w.Category == c.Category).EffectiveWeight)).ToArray();

        var confidence = overall.Score is null
            ? DataConfidence.None
            : categories.Where(c => c.Status == CategoryStatus.Scored).Min(c => c.Confidence);

        var usedDatasets = ctx.Loads.Values.Where(l => l.Dataset is not null).Select(l => l.Dataset!.Info).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();

        var drives = aggregation.Drives
            .Select(a => new DriveSummary(a.Drive.Model, a.Drive.TypeLabel, a.Drive.BusLabel, a.Drive.SizeBytes, a.Decision, a.Reason))
            .ToList();
        var gpus = ctx.Inventory.Gpus
            .Select(g => new GpuSummary(g.Key, g.Name, g.Vendor ?? GpuAdapter.VendorName(g.PciVendorId) ?? "Desconhecido", g.Kind, g.KindReason, g.DedicatedMemoryBytes, g.Key == gpuSelection.Selected?.Key))
            .ToList();

        return new SetupEvaluation(
            ctx.Id,
            ctx.Now,
            SetupScoreMethodology.Version,
            ctx.AppVersion,
            ctx.IncludeStorage,
            overall,
            confidence,
            categories,
            issues,
            usedDatasets,
            aggregation.IncludedCount > 0 ? aggregation.TotalBytes : null,
            drives,
            gpus);
    }

    private static CategoryEvaluation EvaluateCpu(ScoringContext ctx, List<EvaluationIssue> issues)
    {
        var cpu = ctx.Inventory.Cpus.FirstOrDefault();
        if (cpu is null)
        {
            issues.Add(Issue(ctx, SetupCategory.Cpu, IssueKind.InsufficientLocalData, "—", "Não identificado", "Windows (WMI)", StageIdentify, "Nenhum processador foi reportado."));
            return Unavailable(SetupCategory.Cpu, "Processador não identificado", "O Windows não reportou o processador.", []);
        }

        if (ctx.Inventory.Cpus.Select(c => HardwareNameNormalizer.Strict(c.Name)).Distinct().Count() > 1)
        {
            issues.Add(Issue(ctx, SetupCategory.Cpu, IssueKind.MultipleProcessors, cpu.Name, "Informativo", "Windows (WMI)", StageIdentify,
                $"Foram reportados {ctx.Inventory.Cpus.Count} processadores com modelos diferentes.", severity: IssueSeverity.Info));
        }

        var facts = new List<Fact>
        {
            new("Fabricante", cpu.Manufacturer ?? "indisponível"),
            new("Núcleos físicos", cpu.PhysicalCores?.ToString(SetupScoreText.Pt) ?? "indisponível"),
            new("Threads", cpu.LogicalProcessors?.ToString(SetupScoreText.Pt) ?? "indisponível"),
            new("Clock informado", cpu.ReportedClockMhz is { } mhz ? SetupScoreText.Number(mhz / 1000.0, "0.00") + " GHz" : "indisponível"),
            new("Arquitetura", cpu.Architecture ?? "indisponível"),
        };

        var def = CpuDefinition(ctx.Definitions);
        return EvaluateBenchmark(ctx, SetupCategory.Cpu, cpu.Name, CpuDeviceKey(cpu), def, SetupScoreMethodology.CpuScale, facts,
            "Mediana medida do Blender Benchmark (renderização Cycles na CPU), publicada no Blender Open Data. É um teste de renderização, não de jogos, e não foi executado neste computador.",
            issues);
    }

    private static CategoryEvaluation EvaluateGpu(ScoringContext ctx, GpuSelection selection, List<EvaluationIssue> issues)
    {
        var gpu = selection.Selected;
        if (gpu is null)
        {
            if (selection.Ambiguous)
            {
                issues.Add(Issue(ctx, SetupCategory.Gpu, IssueKind.GpuSelectionAmbiguous, string.Join(" · ", selection.Eligible.Select(g => g.Name)), "Escolha necessária",
                    "Windows (WMI)", StageIdentify, selection.Reason, selection.Eligible.Select(g => g.Name).ToList()));
            }
            else
            {
                issues.Add(Issue(ctx, SetupCategory.Gpu, IssueKind.InsufficientLocalData, ctx.Inventory.Gpus.Count > 0 ? string.Join(" · ", ctx.Inventory.Gpus.Select(g => g.Name)) : "—",
                    "Não identificado", "Windows (WMI)", StageIdentify, selection.Reason));
            }

            return Unavailable(SetupCategory.Gpu, selection.Ambiguous ? "GPU a confirmar" : "GPU não identificada", selection.Reason,
                [], selection.Eligible.Select(g => g.Name).ToList());
        }

        var def = GpuDefinition(ctx.Definitions, gpu);
        var facts = new List<Fact>
        {
            new("Fabricante", gpu.Vendor ?? GpuAdapter.VendorName(gpu.PciVendorId) ?? "indisponível"),
            new("Tipo", SetupScoreText.GpuKindLabel(gpu.Kind)),
            new("Memória dedicada", gpu.DedicatedMemoryBytes is { } vram ? SetupScoreText.Number(vram / 1073741824.0, "0.#") + " GiB" : "indisponível"),
            new("ID PCI", gpu.PciVendorId is { } v ? $"{v:X4}:{gpu.PciDeviceId?.ToString("X4", SetupScoreText.Pt) ?? "????"}" : "indisponível"),
            new("Seleção", selection.Reason),
        };
        if (def is not null)
        {
            facts.Add(new Fact("Base de comparação", def.Title));
        }

        if (def is null)
        {
            var vendor = gpu.Vendor ?? GpuAdapter.VendorName(gpu.PciVendorId) ?? "desconhecido";
            issues.Add(Issue(ctx, SetupCategory.Gpu, IssueKind.MissingPerformanceData, gpu.Name, "Sem base comparável", "Blender Open Data", StageQuery,
                $"Esta metodologia só possui bases para NVIDIA (OptiX), AMD (HIP) e Intel (oneAPI); fabricante detectado: {vendor}."));
            return Unavailable(SetupCategory.Gpu, gpu.Name, "Sem base de desempenho comparável para este fabricante.", facts);
        }

        return EvaluateBenchmark(ctx, SetupCategory.Gpu, gpu.Name, gpu.Key, def, SetupScoreMethodology.GpuScale, facts,
            $"Mediana medida do Blender Benchmark (renderização Cycles na GPU via {def.Backend}), publicada no Blender Open Data. É um teste de renderização, não de jogos, e não foi executado neste computador. A memória de vídeo não entra na nota.",
            issues);
    }

    private static CategoryEvaluation EvaluateBenchmark(
        ScoringContext ctx,
        SetupCategory category,
        string name,
        string deviceKey,
        BenchmarkDatasetDefinition? def,
        LogisticScale scale,
        List<Fact> facts,
        string basis,
        List<EvaluationIssue> issues)
    {
        var load = def is not null ? ctx.Loads.GetValueOrDefault(def.Id) : null;
        var dataset = load?.Dataset;
        var sourceLabel = dataset is not null ? $"{dataset.Definition.Title} · obtida em {dataset.Info.RetrievedAt:dd/MM/yyyy}" : def?.Title ?? "Blender Open Data";
        var userChoice = def is null ? null : ctx.Overrides.FirstOrDefault(o => o.Category == category && o.DeviceKey == deviceKey && o.DatasetId == def.Id)?.RecordName;
        var match = ComponentMatcher.Match(name, category, dataset, userChoice);
        var candidates = match.Candidates.Select(c => c.DeviceName).ToList();

        if (!match.IsConfirmed || match.Record is null)
        {
            var (kind, extra) = match.State switch
            {
                MatchState.IncompleteName => (IssueKind.IncompleteLocalName, (string?)null),
                MatchState.SourceUnavailable when load?.Failure == DatasetFailure.Invalid => (IssueKind.InvalidSource, load.Error),
                MatchState.SourceUnavailable => (IssueKind.SourceUnavailable, load?.Error),
                MatchState.AmbiguousMatch => (IssueKind.AmbiguousRecords, null),
                MatchState.ProbableMatch => (IssueKind.ProbableNeedsConfirmation, null),
                MatchState.InvalidRecord => (IssueKind.InvalidRecord, match.Detail),
                _ => (IssueKind.NotInDatabase, null),
            };
            var stage = match.State is MatchState.SourceUnavailable ? StageQuery : StageValidate;
            if (match.State == MatchState.IncompleteName && dataset is not null)
            {
                candidates = ComponentMatcher.Candidates(HardwareNameNormalizer.Extended(name, category), category, dataset).Select(c => c.DeviceName).ToList();
            }

            issues.Add(Issue(ctx, category, kind, name, SetupScoreText.Match(match.State), sourceLabel, stage, extra, candidates,
                errorCode: load?.Failure is { } f and not DatasetFailure.None ? f.ToString() : null));
            return new CategoryEvaluation(category, CategoryStatus.Unavailable, null, SetupScoreMethodology.WeightOf(category), 0, name, ScoreBasis.None,
                basis, match.State, null, null, null, def?.Id, DataConfidence.None, match.Detail, facts, candidates);
        }

        var record = match.Record;
        var confidence = record.Samples >= SetupScoreMethodology.HighConfidenceSamples ? DataConfidence.High
            : record.Samples >= SetupScoreMethodology.MediumConfidenceSamples ? DataConfidence.Medium
            : DataConfidence.Low;
        if (match.State == MatchState.UserSelected && confidence == DataConfidence.High)
        {
            confidence = DataConfidence.Medium;
        }

        if (record.Samples < SetupScoreMethodology.MediumConfidenceSamples)
        {
            issues.Add(Issue(ctx, category, IssueKind.LowSampleCount, name, SetupScoreText.Match(match.State), sourceLabel, StageValidate,
                $"Envios na base: {record.Samples}.", severity: IssueSeverity.Warning));
        }

        facts.Add(new Fact("Registro na base", record.DeviceName));
        facts.Add(new Fact("Mediana na base", SetupScoreText.Number(record.MedianScore, "0.0") + $" ({record.Samples} envios)"));
        var score = SetupScoreMethodology.Round(scale.Score(record.MedianScore));
        return new CategoryEvaluation(category, CategoryStatus.Scored, score, SetupScoreMethodology.WeightOf(category), 0, name, ScoreBasis.MeasuredBenchmark,
            basis, match.State, record.DeviceName, record.MedianScore, record.Samples, def!.Id, confidence, null, facts, candidates);
    }

    private static CategoryEvaluation EvaluateRam(ScoringContext ctx, List<EvaluationIssue> issues)
    {
        var mem = ctx.Inventory.Memory;
        var bytes = mem.InstalledBytes ?? mem.ModulesTotalBytes;
        var facts = new List<Fact>
        {
            new("Tipo", mem.CommonType ?? "indisponível"),
            new("Velocidade", mem.EffectiveSpeedMts is { } mts ? $"{mts} MT/s" : "indisponível"),
            new("Módulos", mem.Modules.Count > 0 ? mem.Modules.Count.ToString(SetupScoreText.Pt) : "indisponível"),
            new("Fabricante", Distinct(mem.Modules.Select(m => m.Manufacturer)) ?? "indisponível"),
            new("Modelo", Distinct(mem.Modules.Select(m => m.PartNumber)) ?? "indisponível"),
        };

        if (bytes is not > 0)
        {
            issues.Add(Issue(ctx, SetupCategory.Ram, IssueKind.InsufficientLocalData, "—", "Não identificado", "Windows (firmware/WMI)", StageIdentify, "A capacidade instalada não foi informada."));
            return Unavailable(SetupCategory.Ram, "Memória não identificada", "Capacidade instalada indisponível.", facts);
        }

        var confidence = DataConfidence.Medium;
        if (mem.InstalledBytes is { } installed && mem.ModulesTotalBytes is { } modules)
        {
            if (Math.Abs(installed - modules) <= installed * 0.01)
            {
                confidence = DataConfidence.High;
            }
            else
            {
                issues.Add(Issue(ctx, SetupCategory.Ram, IssueKind.MemorySourcesDisagree, FormatGiB(installed), "Divergência", "Windows (firmware/WMI)", StageValidate,
                    $"Firmware: {FormatGiB(installed)}; soma dos módulos: {FormatGiB(modules)}.", severity: IssueSeverity.Warning));
            }
        }

        var gib = bytes.Value / 1073741824.0;
        facts.Insert(0, new Fact("Capacidade instalada", FormatGiB(bytes.Value) + (mem.InstalledSource is { } src ? $" ({src})" : "")));
        return new CategoryEvaluation(SetupCategory.Ram, CategoryStatus.Scored, SetupScoreMethodology.Round(SetupScoreMethodology.RamCurve.Score(gib)),
            SetupScoreMethodology.WeightOf(SetupCategory.Ram), 0, $"{FormatGiB(bytes.Value)} {mem.CommonType ?? ""}".Trim(), ScoreBasis.CapacityCurve,
            "Estimativa técnica: curva de capacidade instalada com retornos decrescentes. Tipo e frequência são exibidos, mas não alteram a nota, pois não há base medida que relacione esses atributos ao desempenho em jogos.",
            null, null, gib, null, null, confidence, null, facts, []);
    }

    private static (CategoryEvaluation Category, StorageCapacityResult Aggregation) EvaluateStorage(ScoringContext ctx, List<EvaluationIssue> issues)
    {
        var agg = StorageCapacityAggregator.Aggregate(ctx.Inventory.Drives);
        var name = agg.IncludedCount > 0
            ? $"{StorageCapacityAggregator.FormatNominal(agg.TotalBytes)} nominais em {agg.IncludedCount} {(agg.IncludedCount == 1 ? "unidade" : "unidades")}"
            : "Nenhuma unidade elegível";
        var facts = new List<Fact>();
        if (agg.IncludedCount > 0)
        {
            facts.Add(new Fact("Total nominal (GB decimais)", StorageCapacityAggregator.FormatNominal(agg.TotalBytes)));
            facts.Add(new Fact("Equivalente binário (GiB)", StorageCapacityAggregator.FormatBinary(agg.TotalBytes)));
        }

        facts.Add(new Fact("Unidades consideradas", agg.IncludedCount.ToString(SetupScoreText.Pt)));
        facts.Add(new Fact("Unidades ignoradas", (agg.Drives.Count - agg.IncludedCount).ToString(SetupScoreText.Pt)));
        const string basis = "Estimativa técnica: curva da capacidade total nominal das unidades físicas internas, com retornos decrescentes. O tipo e a interface (HDD, SATA, NVMe) são descritivos e não alteram a nota.";

        if (!ctx.IncludeStorage)
        {
            return (new CategoryEvaluation(SetupCategory.Storage, CategoryStatus.ExcludedByUser, null, SetupScoreMethodology.WeightOf(SetupCategory.Storage), 0, name,
                ScoreBasis.None, basis, null, null, null, null, null, DataConfidence.None, "Desativado pelo usuário.", facts, []), agg);
        }

        foreach (var d in agg.Drives.Where(d => d.Decision is DriveDecision.ExcludedUnclassified or DriveDecision.ExcludedInvalidCapacity))
        {
            issues.Add(Issue(ctx, SetupCategory.Storage, IssueKind.StorageDriveUncertain, d.Drive.Model, "Não somada", d.Drive.Source, StageValidate, d.Reason, severity: IssueSeverity.Warning));
        }

        if (agg.IncludedCount == 0)
        {
            issues.Add(Issue(ctx, SetupCategory.Storage, IssueKind.StorageNoEligibleDrive, ctx.Inventory.Drives.Count > 0 ? string.Join(" · ", ctx.Inventory.Drives.Select(d => d.Model)) : "—",
                "Não avaliado", "Windows (Storage/WMI)", StageIdentify, agg.Drives.Count == 0 ? "Nenhuma unidade física foi reportada." : null));
            return (new CategoryEvaluation(SetupCategory.Storage, CategoryStatus.Unavailable, null, SetupScoreMethodology.WeightOf(SetupCategory.Storage), 0, name,
                ScoreBasis.None, basis, null, null, null, null, null, DataConfidence.None, "Nenhuma unidade interna elegível com capacidade confirmada.", facts, []), agg);
        }

        var score = SetupScoreMethodology.Round(SetupScoreMethodology.StorageCurve.Score(agg.TotalBytes / 1e9));
        return (new CategoryEvaluation(SetupCategory.Storage, CategoryStatus.Scored, score, SetupScoreMethodology.WeightOf(SetupCategory.Storage), 0, name,
            ScoreBasis.CapacityCurve, basis, null, null, agg.TotalBytes / 1e9, null, null, agg.UncertainCount == 0 ? DataConfidence.High : DataConfidence.Medium,
            null, facts, []), agg);
    }

    private static CategoryEvaluation Unavailable(SetupCategory category, string name, string reason, IReadOnlyList<Fact> facts, IReadOnlyList<string>? candidates = null) =>
        new(category, CategoryStatus.Unavailable, null, SetupScoreMethodology.WeightOf(category), 0, name, ScoreBasis.None, "", null, null, null, null, null,
            DataConfidence.None, reason, facts, candidates ?? []);

    private static EvaluationIssue Issue(
        ScoringContext ctx,
        SetupCategory category,
        IssueKind kind,
        string detected,
        string state,
        string source,
        string stage,
        string? extra,
        IReadOnlyList<string>? candidates = null,
        string? errorCode = null,
        IssueSeverity? severity = null) =>
        new(category, kind, severity ?? (category == SetupCategory.Storage ? IssueSeverity.Warning : IssueSeverity.Error), detected, state, source,
            SetupScoreText.Reason(kind, extra), SetupScoreText.Consequence(kind, category), SetupScoreText.Action(kind), stage, ctx.Now, errorCode ?? kind.ToString(),
            candidates ?? []);

    private static string FormatGiB(long bytes) => SetupScoreText.Number(bytes / 1073741824.0, "0.#") + " GiB";

    private static string? Distinct(IEnumerable<string?> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }
}
