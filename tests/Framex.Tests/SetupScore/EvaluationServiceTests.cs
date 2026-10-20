using Framex.Core.SetupScore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests.SetupScore;

public class EvaluationServiceTests
{
    private sealed class SyncProgress : IProgress<StepUpdate>
    {
        public List<StepUpdate> Updates { get; } = [];

        public void Report(StepUpdate value)
        {
            lock (Updates)
            {
                Updates.Add(value);
            }
        }
    }

    private static (SetupEvaluationService Service, FakeInventory Inventory, FakeDatasets Datasets, InMemorySetupRepository Repo) Create(SetupEvaluationOptions? options = null)
    {
        var inv = new FakeInventory();
        var ds = new FakeDatasets();
        var repo = new InMemorySetupRepository();
        var svc = new SetupEvaluationService(inv, ds, repo, () => "teste", new ManualTimeProvider(Synthetic.Now), options ?? new SetupEvaluationOptions(),
            NullLogger<SetupEvaluationService>.Instance);
        return (svc, inv, ds, repo);
    }

    [Fact]
    public async Task CompleteEvaluation_ReportsAllStepsInOrder_AndSaves()
    {
        var (svc, _, _, repo) = Create();
        var progress = new SyncProgress();
        var outcome = await svc.EvaluateAsync(new SetupEvaluationRequest(true), progress, CancellationToken.None);

        Assert.True(outcome.Saved);
        Assert.Single(repo.Saved);
        Assert.Equal(EvaluationCompleteness.Complete, outcome.Evaluation.Overall.Completeness);
        var finals = progress.Updates.Where(u => u.State != StepState.Running).Select(u => u.Step).ToList();
        Assert.Equal([EvaluationStep.Identify, EvaluationStep.QueryDatabases, EvaluationStep.Validate, EvaluationStep.Score, EvaluationStep.Report], finals);
        Assert.DoesNotContain(progress.Updates, u => u.State == StepState.Failed);
    }

    [Fact]
    public async Task StorageOption_IsFrozenAtStart()
    {
        var (svc, inv, _, _) = Create();
        var request = new SetupEvaluationRequest(false);
        inv.Delay = TimeSpan.FromMilliseconds(50);
        var outcome = await svc.EvaluateAsync(request, null, CancellationToken.None);
        Assert.False(outcome.Evaluation.IncludeStorage);
        Assert.Equal(CategoryStatus.ExcludedByUser, outcome.Evaluation.Get(SetupCategory.Storage).Status);
    }

    [Fact]
    public async Task Cancel_ThrowsOperationCanceled_AndDoesNotSave()
    {
        var (svc, inv, _, repo) = Create();
        inv.Delay = TimeSpan.FromSeconds(10);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.EvaluateAsync(new SetupEvaluationRequest(true), null, cts.Token));
        Assert.Empty(repo.Saved);
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public async Task InventoryTimeout_IsReportedAsTimeout_AndAllowsRetry()
    {
        var (svc, inv, _, repo) = Create(new SetupEvaluationOptions { InventoryTimeout = TimeSpan.FromMilliseconds(100) });
        inv.Delay = TimeSpan.FromSeconds(10);
        var progress = new SyncProgress();
        await Assert.ThrowsAsync<TimeoutException>(() => svc.EvaluateAsync(new SetupEvaluationRequest(true), progress, CancellationToken.None));
        Assert.Contains(progress.Updates, u => u.Step == EvaluationStep.Identify && u.State == StepState.Failed);

        inv.Delay = TimeSpan.Zero;
        var retry = await svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None);
        Assert.NotNull(retry.Evaluation.Overall.Score);
        Assert.Single(repo.Saved);
    }

    [Fact]
    public async Task DatasetTimeout_BecomesSourceUnavailable_NotException()
    {
        var (svc, _, ds, _) = Create(new SetupEvaluationOptions { DatasetTimeout = TimeSpan.FromMilliseconds(80) });
        ds.Delay = TimeSpan.FromSeconds(10);
        var progress = new SyncProgress();
        var outcome = await svc.EvaluateAsync(new SetupEvaluationRequest(true), progress, CancellationToken.None);
        Assert.Contains(outcome.Evaluation.Issues, i => i.Kind == IssueKind.SourceUnavailable && i.ErrorCode == "Timeout");
        Assert.Null(outcome.Evaluation.Overall.Score);
        Assert.Contains(progress.Updates, u => u.Step == EvaluationStep.QueryDatabases && u.State == StepState.Warning);
    }

    [Fact]
    public async Task NoCache_ProducesIncompleteEvaluation_WithExplanations()
    {
        var (svc, _, ds, _) = Create();
        ds.Results.Clear();
        var outcome = await svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None);
        Assert.Equal(EvaluationCompleteness.Incomplete, outcome.Evaluation.Overall.Completeness);
        Assert.Equal(2, outcome.Evaluation.Issues.Count(i => i.Kind == IssueKind.SourceUnavailable));
        Assert.Equal(750, outcome.Evaluation.Get(SetupCategory.Ram).Score);
    }

    [Fact]
    public async Task InventoryException_Propagates_AndReleasesGate()
    {
        var (svc, inv, _, _) = Create();
        inv.Throw = new InvalidOperationException("WMI parado");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None));
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public async Task ConcurrentEvaluation_IsRejected()
    {
        var (svc, inv, _, _) = Create();
        inv.Delay = TimeSpan.FromMilliseconds(300);
        var first = svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None));
        await first;
    }

    [Fact]
    public async Task SaveFailure_StillReturnsResult_WithWarning()
    {
        var (svc, _, _, repo) = Create();
        repo.FailOnSave = true;
        var progress = new SyncProgress();
        var outcome = await svc.EvaluateAsync(new SetupEvaluationRequest(true), progress, CancellationToken.None);
        Assert.False(outcome.Saved);
        Assert.NotNull(outcome.SaveError);
        Assert.Contains(progress.Updates, u => u.Step == EvaluationStep.Report && u.State == StepState.Warning);
    }

    [Fact]
    public async Task History_ReopensWithoutRequery()
    {
        var (svc, inv, _, repo) = Create();
        var outcome = await svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None);
        var calls = inv.Calls;
        var reopened = repo.Get(outcome.Evaluation.Id);
        Assert.Equal(outcome.Evaluation.Overall.Score, reopened!.Overall.Score);
        Assert.Equal(calls, inv.Calls);
    }

    [Fact]
    public async Task OverallTimeout_IsTimeoutException()
    {
        var (svc, _, ds, _) = Create(new SetupEvaluationOptions { OverallTimeout = TimeSpan.FromMilliseconds(100), DatasetTimeout = TimeSpan.FromSeconds(10) });
        ds.Delay = TimeSpan.FromSeconds(5);
        await Assert.ThrowsAsync<TimeoutException>(() => svc.EvaluateAsync(new SetupEvaluationRequest(true), null, CancellationToken.None));
    }
}
