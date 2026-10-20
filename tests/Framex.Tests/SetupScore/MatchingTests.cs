using Framex.Core.SetupScore;

namespace Framex.Tests.SetupScore;

public class MatchingTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", "intel core i5 10400f cpu @ 2.90ghz")]
    [InlineData("NVIDIA GeForce RTX™ 3060 Ti", "nvidia geforce rtx 3060 ti")]
    [InlineData("  AMD   Radeon(TM)  RX 6600 XT ", "amd radeon rx 6600 xt")]
    public void Strict_RemovesOnlyTypography(string raw, string expected) =>
        Assert.Equal(expected, HardwareNameNormalizer.Strict(raw));

    [Theory]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", "amd ryzen 5 3600")]
    [InlineData("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", "intel core i5 10400f")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "amd ryzen 7 5800x3d")]
    public void Extended_RemovesDocumentedCpuAnnotations(string raw, string expected) =>
        Assert.Equal(expected, HardwareNameNormalizer.Extended(raw, SetupCategory.Cpu));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060 Ti", "NVIDIA GeForce RTX 3060")]
    [InlineData("AMD Radeon RX 6600 XT", "AMD Radeon RX 6600")]
    [InlineData("Intel Core i7-12700KF", "Intel Core i7-12700K")]
    [InlineData("AMD Ryzen 7 5800X3D", "AMD Ryzen 7 5800X")]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", "NVIDIA GeForce RTX 3060")]
    public void DistinguishingSuffixes_AreNeverRemoved(string a, string b)
    {
        Assert.NotEqual(HardwareNameNormalizer.Extended(a, SetupCategory.Gpu), HardwareNameNormalizer.Extended(b, SetupCategory.Gpu));
        Assert.NotEqual(HardwareNameNormalizer.Extended(a, SetupCategory.Cpu), HardwareNameNormalizer.Extended(b, SetupCategory.Cpu));
    }

    [Fact]
    public void ExactCpu_IsExactMatch()
    {
        var r = ComponentMatcher.Match("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", SetupCategory.Cpu, Synthetic.Cpus());
        Assert.Equal(MatchState.ExactMatch, r.State);
        Assert.Equal("Intel Core i5-10400F CPU @ 2.90GHz", r.Record!.DeviceName);
        Assert.True(r.IsConfirmed);
    }

    [Fact]
    public void ExactGpu_IsExactMatch_AndKeepsSuper()
    {
        var r = ComponentMatcher.Match("NVIDIA GeForce GTX 1660 SUPER", SetupCategory.Gpu, Synthetic.Nvidia());
        Assert.Equal(MatchState.ExactMatch, r.State);
        Assert.Equal(748.0, r.Record!.MedianScore);
    }

    [Fact]
    public void CpuWithoutCoreSuffix_IsVerifiedAlias()
    {
        var r = ComponentMatcher.Match("AMD Ryzen 5 3600", SetupCategory.Cpu, Synthetic.Cpus());
        Assert.Equal(MatchState.VerifiedAlias, r.State);
        Assert.Equal("AMD Ryzen 5 3600 6-Core Processor", r.Record!.DeviceName);
    }

    [Fact]
    public void SimilarName_IsNeverScoredAutomatically()
    {
        // "RTX 3060 Ti" ausente: não pode herdar a nota da RTX 3060.
        var ds = Synthetic.Dataset(Synthetic.NvidiaDef, [("NVIDIA GeForce RTX 3060", 3300, 500), ("NVIDIA GeForce RTX 3060 Laptop GPU", 2600, 200)]);
        var r = ComponentMatcher.Match("NVIDIA GeForce RTX 3060 Ti", SetupCategory.Gpu, ds);
        Assert.False(r.IsConfirmed);
        Assert.Null(r.Record);
    }

    [Fact]
    public void MultipleCandidates_AreAmbiguous()
    {
        var r = ComponentMatcher.Match("NVIDIA GeForce RTX 3060 Max-Q", SetupCategory.Gpu, Synthetic.Nvidia());
        Assert.Equal(MatchState.AmbiguousMatch, r.State);
        Assert.True(r.Candidates.Count >= 2);
        Assert.Null(r.Record);
    }

    [Fact]
    public void SingleCandidate_IsProbable_NotConfirmed()
    {
        var r = ComponentMatcher.Match("NVIDIA GeForce RTX 4090 D", SetupCategory.Gpu, Synthetic.Nvidia());
        Assert.Equal(MatchState.ProbableMatch, r.State);
        Assert.Single(r.Candidates);
        Assert.False(r.IsConfirmed);
    }

    [Fact]
    public void Absent_IsNotFound()
    {
        var r = ComponentMatcher.Match("NVIDIA GeForce GT 710", SetupCategory.Gpu, Synthetic.Nvidia());
        Assert.Equal(MatchState.NotFound, r.State);
        Assert.Empty(r.Candidates);
    }

    [Fact]
    public void SourceUnavailable_WhenDatasetMissing()
    {
        var r = ComponentMatcher.Match("NVIDIA GeForce GTX 1660 SUPER", SetupCategory.Gpu, null);
        Assert.Equal(MatchState.SourceUnavailable, r.State);
    }

    [Fact]
    public void RejectedRecord_IsInvalidRecord()
    {
        var ds = Synthetic.Cpus([new RejectedRecord("Intel Core i9-13900K", "Pontuação fora da faixa.")]);
        var r = ComponentMatcher.Match("Intel(R) Core(TM) i9-13900K", SetupCategory.Cpu, ds);
        Assert.Equal(MatchState.InvalidRecord, r.State);
        Assert.False(r.IsConfirmed);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("Intel(R) UHD Graphics")]
    [InlineData("")]
    public void NameWithoutModelNumber_IsIncomplete(string name)
    {
        var r = ComponentMatcher.Match(name, SetupCategory.Gpu, Synthetic.Amd());
        Assert.Equal(MatchState.IncompleteName, r.State);
    }

    [Fact]
    public void UserSelection_IsUsed_OnlyIfRecordExists()
    {
        var ok = ComponentMatcher.Match("NVIDIA GeForce RTX 3060 Max-Q", SetupCategory.Gpu, Synthetic.Nvidia(), "NVIDIA GeForce RTX 3060 Laptop GPU");
        Assert.Equal(MatchState.UserSelected, ok.State);
        Assert.Equal(2600, ok.Record!.MedianScore);

        var gone = ComponentMatcher.Match("NVIDIA GeForce RTX 3060 Max-Q", SetupCategory.Gpu, Synthetic.Nvidia(), "Registro removido");
        Assert.Equal(MatchState.AmbiguousMatch, gone.State);
    }

    [Fact]
    public void DuplicateRecords_UseMostSamples_Deterministically()
    {
        var ds = Synthetic.Dataset(Synthetic.NvidiaDef, [("NVIDIA GeForce RTX 3060", 3000, 5), ("NVIDIA GeForce RTX™ 3060", 3300, 500)]);
        var r = ComponentMatcher.Match("NVIDIA GeForce RTX 3060", SetupCategory.Gpu, ds);
        Assert.Equal(MatchState.ExactMatch, r.State);
        Assert.Equal(500, r.Record!.Samples);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Theory]
    [InlineData("Parsec Virtual Display Adapter", "ROOT\\DISPLAY\\0000", (ushort)0, GpuKind.Virtual)]
    [InlineData("Microsoft Basic Display Adapter", "PCI\\VEN_1414&DEV_008C", (ushort)0x1414, GpuKind.Virtual)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "PCI\\VEN_10DE&DEV_21C4&SUBSYS_13D310DE&REV_A1\\4&1", (ushort)0x10DE, GpuKind.Dedicated)]
    [InlineData("AMD Radeon RX 6600", "PCI\\VEN_1002&DEV_73FF", (ushort)0x1002, GpuKind.Dedicated)]
    [InlineData("AMD Radeon(TM) Graphics", "PCI\\VEN_1002&DEV_1638", (ushort)0x1002, GpuKind.Integrated)]
    [InlineData("AMD Radeon RX Vega 11 Graphics", "PCI\\VEN_1002&DEV_15DD", (ushort)0x1002, GpuKind.Integrated)]
    [InlineData("AMD Radeon 780M", "PCI\\VEN_1002&DEV_15BF", (ushort)0x1002, GpuKind.Integrated)]
    [InlineData("Intel(R) UHD Graphics 630", "PCI\\VEN_8086&DEV_3E92", (ushort)0x8086, GpuKind.Integrated)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "PCI\\VEN_8086&DEV_56A0", (ushort)0x8086, GpuKind.Dedicated)]
    [InlineData("Intel(R) Arc(TM) Graphics", "PCI\\VEN_8086&DEV_7D55", (ushort)0x8086, GpuKind.Integrated)]
    [InlineData("Matrox G200eR2", "PCI\\VEN_102B&DEV_0534", (ushort)0x102B, GpuKind.Unknown)]
    public void GpuKind_UsesExplicitEvidenceOnly(string name, string pnp, ushort vendor, GpuKind expected) =>
        Assert.Equal(expected, HardwareClassifiers.ClassifyGpu(name, pnp, vendor == 0 ? null : vendor).Kind);

    [Fact]
    public void PciId_IsParsed_WithoutRevisionOrInstance()
    {
        var p = HardwareClassifiers.ParsePci("PCI\\VEN_10DE&DEV_21C4&SUBSYS_13D310DE&REV_A1\\4&2A0B3C&0&0008")!;
        Assert.Equal(0x10DE, p.VendorId);
        Assert.Equal(0x21C4, p.DeviceId);
        Assert.Equal("PCI\\VEN_10DE&DEV_21C4&SUBSYS_13D310DE", p.ModelKey);
        Assert.Null(HardwareClassifiers.ParsePci("ROOT\\DISPLAY\\0000"));
    }

    [Fact]
    public void MultipleGpus_PicksSingleDedicated_IgnoringVirtualAndIntegrated()
    {
        var gpus = new[]
        {
            Synthetic.Gpu("Parsec Virtual Display Adapter", 0, GpuKind.Virtual, key: "v"),
            Synthetic.Gpu("Intel(R) UHD Graphics 630", 0x8086, GpuKind.Integrated, key: "i"),
            Synthetic.Gpu("NVIDIA GeForce GTX 1660 SUPER", 0x10DE, GpuKind.Dedicated, key: "n"),
        };
        var s = GpuSelector.Select(gpus, null);
        Assert.Equal("n", s.Selected!.Key);
        Assert.False(s.Ambiguous);
    }

    [Fact]
    public void TwoDedicatedGpus_AreAmbiguous_AndNeverSummed()
    {
        var gpus = new[] { Synthetic.Gpu("NVIDIA GeForce RTX 3060", 0x10DE, GpuKind.Dedicated, key: "a"), Synthetic.Gpu("AMD Radeon RX 6600", 0x1002, GpuKind.Dedicated, key: "b") };
        var s = GpuSelector.Select(gpus, null);
        Assert.Null(s.Selected);
        Assert.True(s.Ambiguous);

        var chosen = GpuSelector.Select(gpus, "b");
        Assert.Equal("b", chosen.Selected!.Key);

        var e = Synthetic.Build(Synthetic.Inventory(gpus: gpus));
        Assert.Equal(CategoryStatus.Unavailable, e.Get(SetupCategory.Gpu).Status);
        Assert.Contains(e.Issues, i => i.Kind == IssueKind.GpuSelectionAmbiguous && i.Candidates.Count == 2);
        Assert.Null(e.Overall.Score);
    }

    [Fact]
    public void UnknownKindWithDedicated_RequiresChoice()
    {
        var gpus = new[] { Synthetic.Gpu("NVIDIA GeForce RTX 3060", 0x10DE, GpuKind.Dedicated, key: "a"), Synthetic.Gpu("Matrox G200", 0x102B, GpuKind.Unknown, key: "m") };
        Assert.True(GpuSelector.Select(gpus, null).Ambiguous);
    }
}
