using Framex.Core.SetupScore;

namespace Framex.Tests.SetupScore;

public class ScoringTests
{
    private static CategoryInput In(SetupCategory c, int? score, CategoryStatus status = CategoryStatus.Scored) => new(c, status, score);

    private static OverallResult All(int gpu, int cpu, int ram, int? storage, CategoryStatus storageStatus = CategoryStatus.Scored) =>
        OverallScoreCalculator.Calculate([In(SetupCategory.Gpu, gpu), In(SetupCategory.Cpu, cpu), In(SetupCategory.Ram, ram), In(SetupCategory.Storage, storage, storageStatus)]);

    [Fact]
    public void Weights_SumToOne_AndStorageIsAtMost8Percent()
    {
        Assert.Equal(1.0, SetupScoreMethodology.BaseWeights.Sum(w => w.Weight), 12);
        Assert.True(SetupScoreMethodology.WeightOf(SetupCategory.Storage) <= 0.08);
        Assert.Equal(0.45, SetupScoreMethodology.WeightOf(SetupCategory.Gpu));
        Assert.Equal(0.35, SetupScoreMethodology.WeightOf(SetupCategory.Cpu));
        Assert.Equal(0.12, SetupScoreMethodology.WeightOf(SetupCategory.Ram));
    }

    [Fact]
    public void MinimumAndMaximum()
    {
        Assert.Equal(0, All(0, 0, 0, 0).Score);
        Assert.Equal(1000, All(1000, 1000, 1000, 1000).Score);
    }

    [Fact]
    public void WeightedFormula_MatchesHandCalculation()
    {
        // 0,45·380 + 0,35·82 + 0,12·750 + 0,08·489 = 328,82 → 329
        var r = All(380, 82, 750, 489);
        Assert.Equal(329, r.Score);
        Assert.Equal(EvaluationCompleteness.Complete, r.Completeness);
        Assert.Equal("Básico", r.Tier);
        Assert.Equal(1.0, r.Coverage);
        Assert.False(r.Renormalized);
    }

    [Fact]
    public void StorageDisabled_RenormalizesToDocumentedWeights()
    {
        var r = All(380, 82, 750, null, CategoryStatus.ExcludedByUser);
        var w = r.Weights.ToDictionary(x => x.Category, x => x.EffectiveWeight);
        Assert.Equal(0.4891, w[SetupCategory.Gpu], 4);
        Assert.Equal(0.3804, w[SetupCategory.Cpu], 4);
        Assert.Equal(0.1304, w[SetupCategory.Ram], 4);
        Assert.Equal(0, w[SetupCategory.Storage]);
        Assert.Equal(1.0, w.Values.Sum(), 12);
        // (0,45·380 + 0,35·82 + 0,12·750) / 0,92 = 289,7 / 0,92 = 314,89 → 315
        Assert.Equal(315, r.Score);
        Assert.Equal(EvaluationCompleteness.Complete, r.Completeness);
        Assert.Equal(1.0, r.Coverage);
        Assert.True(r.Renormalized);
    }

    [Fact]
    public void StorageEnabledButUnavailable_IsPartial_NotZero()
    {
        var partial = All(380, 82, 750, null, CategoryStatus.Unavailable);
        var zero = All(380, 82, 750, 0);
        Assert.Equal(EvaluationCompleteness.Partial, partial.Completeness);
        Assert.Equal(315, partial.Score);
        Assert.NotEqual(zero.Score, partial.Score);
        Assert.Equal(0.92, partial.Coverage, 10);
    }

    [Theory]
    [InlineData(SetupCategory.Gpu)]
    [InlineData(SetupCategory.Cpu)]
    [InlineData(SetupCategory.Ram)]
    public void MissingRequiredCategory_IsIncomplete_WithoutScore(SetupCategory missing)
    {
        var inputs = new[] { SetupCategory.Gpu, SetupCategory.Cpu, SetupCategory.Ram, SetupCategory.Storage }
            .Select(c => c == missing ? In(c, null, CategoryStatus.Unavailable) : In(c, 700)).ToList();
        var r = OverallScoreCalculator.Calculate(inputs);
        Assert.Null(r.Score);
        Assert.Null(r.Tier);
        Assert.Equal(EvaluationCompleteness.Incomplete, r.Completeness);
        Assert.True(r.Coverage < 1);
    }

    [Fact]
    public void ScoredOutOfRange_IsRejected() =>
        Assert.Throws<ArgumentException>(() => All(1001, 0, 0, 0));

    [Fact]
    public void Rounding_IsAwayFromZero_AndClamped()
    {
        Assert.Equal(501, SetupScoreMethodology.Round(500.5));
        Assert.Equal(500, SetupScoreMethodology.Round(500.4999));
        Assert.Equal(0, SetupScoreMethodology.Round(-3));
        Assert.Equal(1000, SetupScoreMethodology.Round(1000.6));
    }

    [Fact]
    public void LogisticScale_HitsAnchors_AndPreservesOrder()
    {
        var s = SetupScoreMethodology.CpuScale;
        Assert.Equal(50, s.Score(25), 6);
        Assert.Equal(950, s.Score(775), 6);
        Assert.Equal(500, s.Score(Math.Sqrt(25 * 775)), 6);
        var values = new[] { 1.0, 5, 25, 34.1, 113.7, 130.3, 500, 775, 3000, 1e5 };
        var scores = values.Select(s.Score).ToList();
        for (var i = 1; i < scores.Count; i++)
        {
            Assert.True(scores[i] > scores[i - 1]);
        }

        Assert.All(scores, x => Assert.InRange(x, 0, 1000));
    }

    [Fact]
    public void DoublingBenchmark_AddsBoundedIncrement_NotProportional()
    {
        // Na escala logarítmica, dobrar o benchmark soma no máximo 1000·k·ln2/4 pontos (inclinação máxima da logística).
        var g = SetupScoreMethodology.GpuScale;
        var maxStep = 1000 * g.Steepness * Math.Log(2) / 4;
        foreach (var x in new[] { 10.0, 120, 500, 1500, 4000, 9700, 50000 })
        {
            Assert.True(g.Score(2 * x) - g.Score(x) <= maxStep + 1e-9);
        }

        foreach (var x in new[] { 1500.0, 4000, 9700 })
        {
            Assert.True(g.Score(2 * x) < 2 * g.Score(x));
        }

        // Outliers não deslocam a escala: as âncoras são fixas na versão.
        Assert.True(g.Score(1e6) <= 1000);
    }

    [Fact]
    public void LogisticScale_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupScoreMethodology.GpuScale.Score(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupScoreMethodology.GpuScale.Score(double.NaN));
    }

    [Fact]
    public void Ram_HasDiminishingReturns()
    {
        var c = SetupScoreMethodology.RamCurve;
        Assert.Equal(450, c.Score(8), 6);
        Assert.Equal(750, c.Score(16), 6);
        Assert.Equal(1000, c.Score(128), 6);
        var gains = new[] { 4.0, 8, 16, 32, 64 }.Select(g => c.Score(g * 2) - c.Score(g)).ToList();
        Assert.True(gains[1] >= gains[2] && gains[2] > gains[3] && gains[3] > gains[4]);
        Assert.True(c.Score(12) > c.Score(8) && c.Score(12) < c.Score(16));
    }

    [Fact]
    public void Storage_HasDiminishingReturns_AndCaps()
    {
        var c = SetupScoreMethodology.StorageCurve;
        Assert.Equal(489, SetupScoreMethodology.Round(c.Score(376.094638080)));
        Assert.Equal(1000, c.Score(16000));
        Assert.Equal(0, c.Score(0));
        var gains = new[] { 256.0, 512, 1000, 2000 }.Select(g => c.Score(g * 2) - c.Score(g)).ToList();
        for (var i = 1; i < gains.Count; i++)
        {
            Assert.True(gains[i] < gains[i - 1]);
        }
    }

    [Fact]
    public void StorageWeight_LimitsItsInfluence()
    {
        // Diferença máxima possível causada pelo armazenamento: 8% de 1000 = 80 pontos.
        Assert.Equal(80, All(600, 600, 600, 1000).Score - All(600, 600, 600, 0).Score);
    }

    [Theory]
    [InlineData(0, "Entrada")]
    [InlineData(199, "Entrada")]
    [InlineData(200, "Básico")]
    [InlineData(399, "Básico")]
    [InlineData(400, "Intermediário")]
    [InlineData(549, "Intermediário")]
    [InlineData(550, "Avançado")]
    [InlineData(700, "Alto desempenho")]
    [InlineData(849, "Alto desempenho")]
    [InlineData(850, "Entusiasta")]
    [InlineData(1000, "Entusiasta")]
    public void Tiers_AreContiguous_AndDocumented(int score, string tier) =>
        Assert.Equal(tier, SetupScoreMethodology.TierFor(score).Name);

    [Fact]
    public void Tiers_CoverWholeRangeWithoutGaps()
    {
        var tiers = SetupScoreMethodology.Tiers;
        Assert.Equal(0, tiers[0].Min);
        Assert.Equal(1000, tiers[^1].Max);
        for (var i = 1; i < tiers.Count; i++)
        {
            Assert.Equal(tiers[i - 1].Max + 1, tiers[i].Min);
        }
    }

    [Fact]
    public void FullBuild_ReferenceMachine()
    {
        var inv = Synthetic.Inventory(cpus: [Synthetic.Cpu("Intel(R) Xeon(R) CPU E3-1230 V2 @ 3.30GHz")]);
        var e = Synthetic.Build(inv);
        Assert.Equal(380, e.Get(SetupCategory.Gpu).Score);
        Assert.Equal(82, e.Get(SetupCategory.Cpu).Score);
        Assert.Equal(750, e.Get(SetupCategory.Ram).Score);
        Assert.Equal(489, e.Get(SetupCategory.Storage).Score);
        Assert.Equal(329, e.Overall.Score);
        Assert.Equal(DataConfidence.Low, e.Get(SetupCategory.Cpu).Confidence);
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.LowSampleCount && i.Category == SetupCategory.Cpu);
        Assert.Equal(DataConfidence.Low, e.Confidence);
        Assert.Equal(SetupScoreMethodology.Version, e.MethodologyVersion);
    }

    [Fact]
    public void Build_IsRepeatable()
    {
        var a = SetupReportExporter.Serialize(Synthetic.Build());
        var b = SetupReportExporter.Serialize(Synthetic.Build());
        Assert.Equal(a, b);
    }

    [Fact]
    public void UnknownCpu_IsUnavailable_NotZero_AndBlocksOverall()
    {
        var e = Synthetic.Build(Synthetic.Inventory(cpus: [Synthetic.Cpu("Processador Futurista X9 9999")]));
        var cpu = e.Get(SetupCategory.Cpu);
        Assert.Equal(CategoryStatus.Unavailable, cpu.Status);
        Assert.Null(cpu.Score);
        Assert.Null(e.Overall.Score);
        Assert.Equal(EvaluationCompleteness.Incomplete, e.Overall.Completeness);
        var issue = Assert.Single(e.Issues, i => i.Category == SetupCategory.Cpu);
        Assert.Equal(IssueKind.NotInDatabase, issue.Kind);
        Assert.False(string.IsNullOrWhiteSpace(issue.Consequence));
        Assert.NotNull(issue.Action);
    }

    [Fact]
    public void StorageOption_Unchecked_MarksNotParticipated()
    {
        var e = Synthetic.Build(includeStorage: false);
        var st = e.Get(SetupCategory.Storage);
        Assert.Equal(CategoryStatus.ExcludedByUser, st.Status);
        Assert.Null(st.Score);
        Assert.Equal(0, st.EffectiveWeight);
        Assert.Equal(EvaluationCompleteness.Complete, e.Overall.Completeness);
        Assert.DoesNotContain(e.Issues, i => i.Category == SetupCategory.Storage);
    }

    [Fact]
    public void NoEligibleDrive_IsPartial_WithIssue()
    {
        var e = Synthetic.Build(Synthetic.Inventory(drives: [Synthetic.Drive("usb", 64_000_000_000, bus: DriveBus.Usb)]));
        Assert.Equal(EvaluationCompleteness.Partial, e.Overall.Completeness);
        Assert.NotNull(e.Overall.Score);
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.StorageNoEligibleDrive);
    }

    [Fact]
    public void RamUnavailable_IsIncomplete()
    {
        var e = Synthetic.Build(Synthetic.Inventory(ram: MemoryInventory.Empty));
        Assert.Equal(CategoryStatus.Unavailable, e.Get(SetupCategory.Ram).Status);
        Assert.Equal(EvaluationCompleteness.Incomplete, e.Overall.Completeness);
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.InsufficientLocalData && i.Category == SetupCategory.Ram);
    }

    [Fact]
    public void Ram_UsesInstalledCapacity_NotAvailable_AndFlagsDisagreement()
    {
        var mem = new MemoryInventory(16L << 30, "firmware", 8L << 30, [new MemoryModule(8L << 30, 3200, "DDR4", null, null)]);
        var e = Synthetic.Build(Synthetic.Inventory(ram: mem));
        Assert.Equal(750, e.Get(SetupCategory.Ram).Score);
        Assert.Equal(DataConfidence.Medium, e.Get(SetupCategory.Ram).Confidence);
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.MemorySourcesDisagree);
    }

    [Fact]
    public void Ram_TypeAndSpeed_DoNotChangeScore()
    {
        MemoryInventory M(string type, int speed) => new(16L << 30, "f", 16L << 30, [new MemoryModule(16L << 30, speed, type, null, null)]);
        var ddr3 = Synthetic.Build(Synthetic.Inventory(ram: M("DDR3", 1333)));
        var ddr5 = Synthetic.Build(Synthetic.Inventory(ram: M("DDR5", 6000)));
        Assert.Equal(ddr3.Get(SetupCategory.Ram).Score, ddr5.Get(SetupCategory.Ram).Score);
    }

    [Fact]
    public void Gpu_VramAlone_DoesNotChangeScore()
    {
        GpuAdapter G(long vram) => Synthetic.Gpu("NVIDIA GeForce GTX 1660 SUPER", 0x10DE, GpuKind.Dedicated) with { DedicatedMemoryBytes = vram };
        Assert.Equal(Synthetic.Build(Synthetic.Inventory(gpus: [G(4L << 30)])).Get(SetupCategory.Gpu).Score,
            Synthetic.Build(Synthetic.Inventory(gpus: [G(24L << 30)])).Get(SetupCategory.Gpu).Score);
    }

    [Fact]
    public void SourceUnavailable_ProducesIssue_AndNoFakeScore()
    {
        var loads = Synthetic.AllLoaded();
        loads[Synthetic.NvidiaDef.Id] = new DatasetLoadResult(Synthetic.NvidiaDef, null, DatasetFailure.Network, "sem conexão");
        var e = Synthetic.Build(loads: loads);
        Assert.Null(e.Get(SetupCategory.Gpu).Score);
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.SourceUnavailable && i.ErrorCode == "Network");
        Assert.Null(e.Overall.Score);
    }

    [Fact]
    public void UnsupportedGpuVendor_HasNoComparableData()
    {
        var e = Synthetic.Build(Synthetic.Inventory(gpus: [Synthetic.Gpu("Moore Threads MTT S80", 0x1ED5, GpuKind.Dedicated)]));
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.MissingPerformanceData);
        Assert.Equal(CategoryStatus.Unavailable, e.Get(SetupCategory.Gpu).Status);
    }

    [Fact]
    public void ManualOverride_IsApplied_WithMediumConfidenceCap()
    {
        var gpu = Synthetic.Gpu("NVIDIA GeForce RTX 3060 Max-Q", 0x10DE, GpuKind.Dedicated, key: "PCI\\VEN_10DE&DEV_2520");
        var inv = Synthetic.Inventory(gpus: [gpu]);
        Assert.Null(Synthetic.Build(inv).Get(SetupCategory.Gpu).Score);

        var o = new ComponentOverride(SetupCategory.Gpu, gpu.Key, "NVIDIA GeForce RTX 3060 Laptop GPU", Synthetic.NvidiaDef.Id, Synthetic.Now);
        var e = Synthetic.Build(inv, overrides: [o]);
        var g = e.Get(SetupCategory.Gpu);
        Assert.Equal(MatchState.UserSelected, g.Match);
        Assert.NotNull(g.Score);
        Assert.Equal(DataConfidence.Medium, g.Confidence);
    }

    [Fact]
    public void MethodologyVersion_IsRecorded_AndOldResultsAreNotRecomputed()
    {
        var e = Synthetic.Build() with { MethodologyVersion = "0.9" };
        var restored = SetupReportExporter.Deserialize(SetupReportExporter.Serialize(e))!;
        Assert.Equal("0.9", restored.MethodologyVersion);
        Assert.Equal(e.Overall.Score, restored.Overall.Score);
    }
}
