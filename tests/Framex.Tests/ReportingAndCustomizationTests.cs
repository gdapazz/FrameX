using System.Text.Json;
using Framex.Core.Customization;
using Framex.Core.Domain;
using Framex.Core.Reporting;
using Framex.Infrastructure.FrameCapture;

namespace Framex.Tests;

public class ReportingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Fact]
    public void Gaps_AreDetectedForHardwareAndFrames()
    {
        var samples = new List<MetricSample>();
        for (var i = 0; i < 10; i++)
        {
            samples.Add(new MetricSample(T0.AddSeconds(i), MetricKind.CpuLoad, 10));
        }

        for (var i = 30; i < 40; i++)
        {
            samples.Add(new MetricSample(T0.AddSeconds(i), MetricKind.CpuLoad, 10));
            samples.Add(new MetricSample(T0.AddSeconds(i), MetricKind.Fps, 60));
        }

        samples.Add(new MetricSample(T0.AddSeconds(5), MetricKind.Fps, 60));
        var gaps = SessionStatisticsCalculator.DetectGaps(T0, T0.AddSeconds(40), samples, TimeSpan.FromSeconds(1));
        Assert.Contains(gaps, g => g.Scope == "Sensores de hardware" && g.Duration == TimeSpan.FromSeconds(21));
        Assert.Contains(gaps, g => g.Scope == "Quadros" && g.Start == T0.AddSeconds(5));
    }

    [Fact]
    public void Analyze_ReportsCoverage_AndMissingMetrics()
    {
        var samples = Enumerable.Range(0, 30).Select(i => new MetricSample(T0.AddSeconds(i * 2), MetricKind.GpuLoad, 50)).ToList();
        var a = SessionStatisticsCalculator.Analyze(T0, T0.AddSeconds(60), samples, [], TimeSpan.FromSeconds(1), T0);
        var gpu = a.Summary.Get(MetricKind.GpuLoad)!;
        Assert.Equal(50, gpu.CoveragePercent, 3);
        Assert.Null(a.Summary.Frames);
        Assert.Contains(a.Summary.Limitations, l => l.Contains("Sem dados de quadros", StringComparison.Ordinal));
        Assert.Contains(a.Summary.Limitations, l => l.Contains("Uso da CPU", StringComparison.Ordinal));
    }

    [Fact]
    public void PerformanceDrop_IsDetected_WithoutClaimingCause()
    {
        var samples = Enumerable.Range(0, 60).Select(i => new MetricSample(T0.AddSeconds(i), MetricKind.Fps, i is >= 20 and < 25 ? 30 : 120)).ToList();
        var drop = Assert.Single(PerformanceDropDetector.Detect(samples));
        Assert.Equal(T0.AddSeconds(20), drop.Timestamp);
        Assert.Contains("5 s", drop.Message, StringComparison.Ordinal);
        Assert.Contains("Causa não determinada", drop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortDips_AreNotFlagged()
    {
        var samples = Enumerable.Range(0, 60).Select(i => new MetricSample(T0.AddSeconds(i), MetricKind.Fps, i is 10 or 11 ? 20 : 120)).ToList();
        Assert.Empty(PerformanceDropDetector.Detect(samples));
    }

    private static SessionReport Report()
    {
        var s = new Session
        {
            GameName = "Meu <Jogo>", ExecutableName = "jogo.exe", StartedAt = T0, EndedAt = T0.AddMinutes(2), State = SessionState.Completed,
            AppVersion = "0.1.0", HardwareFingerprint = "CPU | GPU | 16 GiB", MonitoringProfile = "Equilibrado",
        };
        var samples = Enumerable.Range(0, 120).SelectMany(i => new[]
        {
            new MetricSample(T0.AddSeconds(i), MetricKind.Fps, 100 + (i % 7)),
            new MetricSample(T0.AddSeconds(i), MetricKind.CpuLoad, 40),
        }).ToList();
        var a = SessionStatisticsCalculator.Analyze(T0, T0.AddMinutes(2), samples, Enumerable.Repeat(10.0, 12000).ToList(), TimeSpan.FromSeconds(1), T0);
        return new SessionReport(s, a.Summary, samples, [new SessionEvent(T0, SessionEventType.SessionStarted, EventSeverity.Info, "início")]);
    }

    [Fact]
    public void Csv_HasHeaderAndOneRowPerInstant()
    {
        var csv = ReportExporter.ToCsv(Report()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("timestamp_utc,elapsed_s,cpu_load_pct,fps", csv[0], StringComparison.Ordinal);
        Assert.Equal(121, csv.Length);
    }

    [Fact]
    public void Json_IsValid_AndVersioned()
    {
        using var doc = JsonDocument.Parse(ReportExporter.ToJson(Report()));
        Assert.Equal("framex-session", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(120, doc.RootElement.GetProperty("samples").GetProperty("fps").GetArrayLength());
    }

    [Fact]
    public void Html_IsSelfContained_EscapesText_AndHasNoPersonalPaths()
    {
        var html = ReportExporter.ToHtml(Report());
        Assert.Contains("Meu &lt;Jogo&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_WarnsWhenConditionsDiffer()
    {
        var a = Report().Session;
        var b = new Session { GameName = a.GameName, GameId = a.GameId, HardwareFingerprint = "outro", StartedAt = T0, State = SessionState.Completed, MonitoringProfile = a.MonitoringProfile };
        var cmp = SessionComparer.Compare(a, b);
        Assert.True(cmp.ConditionsDiffer);
        Assert.Contains(cmp.Warnings, w => w.Contains("hardware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonRow_ComputesDeltas()
    {
        var row = new ComparisonRow("FPS", 100, 120, "FPS", true);
        Assert.Equal(20, row.Delta);
        Assert.Equal(20, row.DeltaPercent!.Value, 6);
        Assert.Null(new ComparisonRow("x", null, 1, "", true).Delta);
    }
}

public class CustomizationTests
{
    [Fact]
    public void Theme_ExportImport_RoundTrip()
    {
        var theme = BuiltInThemes.Get("purple-haze").Clone();
        theme.Name = "Meu tema";
        var imported = ConfigPackageSerializer.ImportTheme(ConfigPackageSerializer.Export(theme));
        Assert.True(imported.Success, string.Join("; ", imported.Errors));
        Assert.Equal("#B07CFF", imported.Value!.Primary);
        Assert.False(imported.Value.BuiltIn);
        Assert.NotEqual(theme.Id, imported.Value.Id);
    }

    [Fact]
    public void Theme_InvalidColor_IsRejected()
    {
        var json = ConfigPackageSerializer.Export(BuiltInThemes.All[0]).Replace("#00E5FF", "javascript:alert(1)", StringComparison.Ordinal);
        var r = ConfigPackageSerializer.ImportTheme(json);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("Cor inválida", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"format\":\"outro\",\"version\":1,\"kind\":\"Theme\"}")]
    [InlineData("{\"format\":\"framex-config\",\"version\":99,\"kind\":\"Theme\"}")]
    [InlineData("{\"format\":\"framex-config\",\"version\":1,\"kind\":\"OverlayLayout\"}")]
    public void InvalidPackages_AreRejected(string json) => Assert.False(ConfigPackageSerializer.ImportTheme(json).Success);

    [Fact]
    public void OversizedPackage_IsRejected()
    {
        var huge = new string(' ', ConfigPackage.MaxFileBytes + 1);
        Assert.Contains(ConfigPackageSerializer.ImportTheme(huge).Errors, e => e.Contains("grande", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlay_ImportClampsValues()
    {
        var layout = OverlayPresets.Create(OverlayStyle.Detailed);
        layout.Scale = 50;
        layout.RefreshMs = 1;
        var r = ConfigPackageSerializer.ImportOverlay(ConfigPackageSerializer.Export(layout));
        Assert.True(r.Success);
        Assert.Equal(3, r.Value!.Scale);
        Assert.Equal(100, r.Value.RefreshMs);
        Assert.Equal(layout.Items.Count, r.Value.Items.Count);
    }

    [Fact]
    public void Dashboard_NormalizeRestoresMissingWidgets_AndRemovesDuplicates()
    {
        var d = new DashboardLayout { Widgets = [new() { Widget = DashboardWidget.CpuGauge }, new() { Widget = DashboardWidget.CpuGauge }] };
        d.Normalize();
        Assert.Equal(Enum.GetValues<DashboardWidget>().Length, d.Widgets.Count);
        Assert.Single(d.Widgets, w => w.Widget == DashboardWidget.CpuGauge);
    }

    [Fact]
    public void AllBuiltInThemes_AreValid()
    {
        foreach (var t in BuiltInThemes.All)
        {
            Assert.Empty(t.Clone().Validate());
        }
    }

    [Fact]
    public void BuiltInThemes_HaveReadableTextContrast()
    {
        foreach (var t in BuiltInThemes.All)
        {
            Assert.True(Contrast(t.Text, t.Background) >= 7, $"{t.Name}: texto principal");
            Assert.True(Contrast(t.TextMuted, t.Surface) >= 4.5, $"{t.Name}: texto secundário");
        }
    }

    private static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            double C(int i)
            {
                var v = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * C(1)) + (0.7152 * C(3)) + (0.0722 * C(5));
        }

        var (l1, l2) = (L(a), L(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}

public class PresentMonParserTests
{
    private const string HeaderV2 = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartQPCTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,DisplayedTime";
    private const string HeaderV1 = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,MsBetweenPresents";

    [Fact]
    public void ParsesFrameTime_AndConvertsQpcTimestamp()
    {
        var p = new PresentMonCsvParser { Anchor = (DateTimeOffset.FromUnixTimeSeconds(1000), 50_000) };
        Assert.True(p.TrySetHeader(HeaderV2));
        Assert.Equal("MsBetweenPresents", p.FrameTimeColumn);
        Assert.True(p.TryParse("game.exe,1234,0x1,DXGI,1,0,0,Composed: Flip,51000.5,NA,16.667,16.6,0.2,5.1,12.3,16.6", 1234, DateTimeOffset.UtcNow, out var f));
        Assert.Equal(16.667, f.FrameTimeMs, 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000).AddMilliseconds(1000.5), f.Timestamp);
        Assert.True(f.Displayed);
    }

    [Fact]
    public void RejectsOtherProcesses_AndMalformedLines()
    {
        var p = new PresentMonCsvParser();
        p.TrySetHeader(HeaderV2);
        Assert.False(p.TryParse("other.exe,999,0x1,DXGI,1,0,0,x,1,NA,16,16,0,0,0,16", 1234, DateTimeOffset.UtcNow, out _));
        Assert.False(p.TryParse("lixo", 1234, DateTimeOffset.UtcNow, out _));
        Assert.False(p.TryParse("game.exe,1234,0x1,DXGI,1,0,0,x,1,NA,abc,16,0,0,0,16", 1234, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void NotDisplayedFrames_AreFlagged()
    {
        var p = new PresentMonCsvParser();
        p.TrySetHeader(HeaderV2);
        Assert.True(p.TryParse("g.exe,1,0x1,DXGI,1,0,0,x,1,NA,8,NA,0,0,NA,NA", 1, DateTimeOffset.UtcNow, out var f));
        Assert.False(f.Displayed);
    }

    [Fact]
    public void SupportsV1Columns()
    {
        var p = new PresentMonCsvParser();
        Assert.True(p.TrySetHeader(HeaderV1));
        Assert.True(p.TryParse("g.exe,1,0x1,DXGI,1,0,1,0.5,0.1,10,10", 1, DateTimeOffset.UtcNow, out var f));
        Assert.False(f.Displayed);
    }

    [Fact]
    public void NonHeaderLines_AreIgnored() => Assert.False(new PresentMonCsvParser().TrySetHeader("warning: something"));
}
