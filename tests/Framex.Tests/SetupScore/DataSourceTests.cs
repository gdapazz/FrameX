using System.Net;
using System.Text;
using Framex.Core.SetupScore;
using Framex.Infrastructure.Persistence;
using Framex.Infrastructure.SetupScore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests.SetupScore;

public class DataSourceTests
{
    private const string Header = """{"columns":[{"display_name":"Device Name"},{"display_name":"Median Score"},{"display_name":"Number of Benchmarks"}],"rows":[""";

    private static string Json(int rows, string extra = "") =>
        Header + string.Join(",", Enumerable.Range(1, rows).Select(i => FormattableString.Invariant($"[\"Device {i}000\",{i * 10.5},{i}]"))) + extra + "]}";

    [Fact]
    public void Parser_AcceptsValidSchema()
    {
        var r = BlenderOpenDataParser.Parse(Json(20));
        Assert.True(r.Success);
        Assert.Equal(20, r.Records.Count);
        Assert.Empty(r.Rejected);
    }

    [Fact]
    public void Parser_RejectsInvalidRows_KeepingReason()
    {
        var bad = """,["",1,1],["Nan Card",-5,3],["Huge",1e9,3],["NoCount",10,0],[1,2,3],["short"]""";
        var r = BlenderOpenDataParser.Parse(Json(20, bad));
        Assert.True(r.Success);
        Assert.Equal(20, r.Records.Count);
        Assert.Equal(6, r.Rejected.Count);
        Assert.All(r.Rejected, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"rows":[]}""")]
    [InlineData("""{"columns":[{"display_name":"Name"},{"display_name":"Score"}],"rows":[]}""")]
    [InlineData("[1,2,3]")]
    public void Parser_RejectsUnexpectedSchema(string json) =>
        Assert.False(BlenderOpenDataParser.Parse(json).Success);

    [Fact]
    public void Parser_RejectsTooFewRecords() => Assert.False(BlenderOpenDataParser.Parse(Json(5)).Success);

    [Fact]
    public void Parser_RejectsOversizedContent() =>
        Assert.False(BlenderOpenDataParser.Parse(new string(' ', BlenderOpenDataParser.MaxBytes + 1)).Success);

    [Fact]
    public async Task BundledDatasets_LoadAndPassIntegrity()
    {
        using var tmp = new TempDir();
        using var store = Store(tmp.Path, new StubHandler(_ => throw new HttpRequestException("offline")));
        foreach (var d in store.Definitions)
        {
            var r = await store.LoadAsync(d, CancellationToken.None);
            Assert.True(r.Success, r.Error);
            Assert.Equal("cópia incluída no FRAMEX", r.Dataset!.Info.Origin);
            Assert.True(r.Dataset.Records.Count >= BlenderOpenDataParser.MinValidRecords);
        }
    }

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU E3-1230 V2 @ 3.30GHz", SetupCategory.Cpu, "blender-4.5.0-cpu", MatchState.ExactMatch)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", SetupCategory.Cpu, "blender-4.5.0-cpu", MatchState.ExactMatch)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", SetupCategory.Gpu, "blender-4.5.0-optix", MatchState.ExactMatch)]
    [InlineData("AMD Radeon RX 6600", SetupCategory.Gpu, "blender-4.5.0-hip", MatchState.ExactMatch)]
    public async Task BundledDatasets_ResolveKnownDevices(string name, SetupCategory category, string id, MatchState expected)
    {
        using var tmp = new TempDir();
        using var store = Store(tmp.Path, new StubHandler(_ => throw new HttpRequestException("offline")));
        var ds = (await store.LoadAsync(store.Definitions.First(d => d.Id == id), CancellationToken.None)).Dataset;
        Assert.Equal(expected, ComponentMatcher.Match(name, category, ds).State);
    }

    [Fact]
    public async Task NoCache_NoBundled_IsUnavailable()
    {
        using var tmp = new TempDir();
        using var store = Store(tmp.Path, new StubHandler(_ => throw new HttpRequestException("offline")), useBundled: false);
        var r = await store.LoadAsync(store.Definitions[0], CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(DatasetFailure.NotAvailable, r.Failure);
    }

    [Fact]
    public async Task Update_Success_WritesValidatedCache_UsedOffline()
    {
        using var tmp = new TempDir();
        var calls = 0;
        using (var store = Store(tmp.Path, new StubHandler(_ => { calls++; return Ok(Json(30)); }), useBundled: false))
        {
            var results = await store.UpdateAsync(CancellationToken.None);
            Assert.All(results, r => Assert.True(r.Updated, r.Message));
            Assert.Equal(store.Definitions.Count, calls);
        }

        using var offline = Store(tmp.Path, new StubHandler(_ => throw new HttpRequestException("offline")), useBundled: false);
        var load = await offline.LoadAsync(offline.Definitions[0], CancellationToken.None);
        Assert.True(load.Success);
        Assert.Equal(30, load.Dataset!.Records.Count);
        Assert.StartsWith("atualizada em", load.Dataset.Info.Origin);
    }

    [Fact]
    public async Task Update_Unchanged_DoesNotRewrite()
    {
        using var tmp = new TempDir();
        using var store = Store(tmp.Path, new StubHandler(_ => Ok(Json(30))), useBundled: false);
        await store.UpdateAsync(CancellationToken.None);
        var second = await store.UpdateAsync(CancellationToken.None);
        Assert.All(second, r => Assert.True(r.Unchanged));
    }

    [Fact]
    public async Task Update_InvalidPayload_KeepsPreviousCopy()
    {
        using var tmp = new TempDir();
        var payload = Json(30);
        using var store = Store(tmp.Path, new StubHandler(_ => Ok(payload)), useBundled: false);
        await store.UpdateAsync(CancellationToken.None);
        payload = """{"columns":[],"rows":[]}""";
        var results = await store.UpdateAsync(CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DatasetFailure.Invalid, r.Failure));
        var load = await store.LoadAsync(store.Definitions[0], CancellationToken.None);
        Assert.Equal(30, load.Dataset!.Records.Count);
    }

    [Fact]
    public async Task Update_NetworkError_RetriesWithLimit_AndKeepsCopy()
    {
        using var tmp = new TempDir();
        var calls = 0;
        using var store = Store(tmp.Path, new StubHandler(_ => { calls++; throw new HttpRequestException("falha"); }));
        var results = await store.UpdateAsync(CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DatasetFailure.Network, r.Failure));
        Assert.Equal(3 * store.Definitions.Count, calls);
        Assert.True((await store.LoadAsync(store.Definitions[0], CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Update_Redirect_IsNotFollowed()
    {
        using var tmp = new TempDir();
        using var store = Store(tmp.Path, new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://example.invalid/evil.json");
            return r;
        }));
        var results = await store.UpdateAsync(CancellationToken.None);
        Assert.All(results, r => Assert.Contains("redirecionamento", r.Message));
    }

    [Fact]
    public async Task Update_OversizedResponse_IsRejected()
    {
        using var tmp = new TempDir();
        using var store = Store(tmp.Path, new StubHandler(_ => Ok(new string('x', BlenderOpenDataParser.MaxBytes + 10))));
        var results = await store.UpdateAsync(CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DatasetFailure.Invalid, r.Failure));
    }

    [Fact]
    public async Task Update_Cancellation_Propagates()
    {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var store = Store(tmp.Path, new StubHandler(_ => Ok(Json(30))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(cts.Token));
    }

    [Fact]
    public async Task CorruptedCache_FallsBackToBundled()
    {
        using var tmp = new TempDir();
        using (var store = Store(tmp.Path, new StubHandler(_ => Ok(Json(30)))))
        {
            await store.UpdateAsync(CancellationToken.None);
        }

        File.AppendAllText(System.IO.Path.Combine(tmp.Path, "blender-4.5.0-cpu.json"), "corrompido", Encoding.UTF8);
        using var again = Store(tmp.Path, new StubHandler(_ => throw new HttpRequestException("offline")));
        var r = await again.LoadAsync(again.Definitions.First(d => d.Id == "blender-4.5.0-cpu"), CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal("cópia incluída no FRAMEX", r.Dataset!.Info.Origin);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Repository_RoundTrip_AndIntegrity()
    {
        using var tmp = new TempDatabase();
        var repo = new SqliteSetupEvaluationRepository(tmp.Database, NullLogger<SqliteSetupEvaluationRepository>.Instance);
        var e = Synthetic.Build();
        repo.Save(e);
        var list = repo.List();
        var summary = Assert.Single(list);
        Assert.Equal(e.Overall.Score, summary.Score);
        var back = repo.Get(e.Id)!;
        Assert.Equal(SetupReportExporter.Serialize(e), SetupReportExporter.Serialize(back));

        using (var c = tmp.Database.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE setup_evaluations SET payload_json = replace(payload_json, '\"Score\":329', '\"Score\":999')";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "UPDATE setup_evaluations SET payload_json = payload_json || ' '";
            cmd.ExecuteNonQuery();
        }

        Assert.Null(repo.Get(e.Id));
        repo.Delete(e.Id);
        Assert.Empty(repo.List());
    }

    [Fact]
    public void Repository_Overrides_AreLocalAndReplaceable()
    {
        using var tmp = new TempDatabase();
        var repo = new SqliteSetupEvaluationRepository(tmp.Database, NullLogger<SqliteSetupEvaluationRepository>.Instance);
        repo.SaveOverride(new ComponentOverride(SetupCategory.Gpu, "k", "A", "ds", Synthetic.Now));
        repo.SaveOverride(new ComponentOverride(SetupCategory.Gpu, "k", "B", "ds", Synthetic.Now));
        Assert.Equal("B", Assert.Single(repo.GetOverrides()).RecordName);
        repo.RemoveOverride(SetupCategory.Gpu, "k");
        Assert.Empty(repo.GetOverrides());
    }

    [Fact]
    public void Report_HasNoSerialNumbersOrPaths()
    {
        var e = Synthetic.Build();
        var html = SetupReportExporter.ToHtml(e);
        var json = SetupReportExporter.Serialize(e, indented: true);
        foreach (var text in new[] { html, json })
        {
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", text);
        }

        Assert.Contains("Como chegamos a essa nota", html);
        Assert.Contains("Blender Benchmark", html);
        Assert.Contains("nenhum teste foi executado neste computador", html);
    }

    private static BlenderOpenDataStore Store(string dir, HttpMessageHandler handler, bool useBundled = true) =>
        new(dir, handler, TimeProvider.System, NullLogger<BlenderOpenDataStore>.Instance, _ => TimeSpan.Zero, useBundled);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(BlenderOpenDataStore.Host, request.RequestUri!.Host);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "framex-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Arquivos temporários; o SO limpa depois.
            }
        }
    }
}
