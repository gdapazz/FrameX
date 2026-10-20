using Microsoft.Extensions.Logging;

namespace Framex.Core.SetupScore;

public enum EvaluationStep
{
    Identify,
    QueryDatabases,
    Validate,
    Score,
    Report,
}

public enum StepState
{
    Pending,
    Running,
    Done,
    Warning,
    Failed,
}

public sealed record StepUpdate(EvaluationStep Step, StepState State, string? Detail);

public sealed record SetupEvaluationRequest(bool IncludeStorage);

public sealed record SetupEvaluationOutcome(SetupEvaluation Evaluation, bool Saved, string? SaveError);

public sealed class SetupEvaluationOptions
{
    public TimeSpan InventoryTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan DatasetTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// Orquestra uma avaliação: identificação local → carregamento das bases (cache local) → correspondência e validação →
/// cálculo → persistência. Tudo fora da thread da interface, com cancelamento, tempo limite e uma avaliação por vez.
/// As opções da solicitação são copiadas no início e não mudam durante a execução.
/// </summary>
public sealed class SetupEvaluationService
{
    private readonly ISetupInventoryProvider _inventory;
    private readonly IBenchmarkDatasetSource _datasets;
    private readonly ISetupEvaluationRepository _repository;
    private readonly Func<string> _appVersion;
    private readonly TimeProvider _time;
    private readonly SetupEvaluationOptions _options;
    private readonly ILogger<SetupEvaluationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SetupEvaluationService(
        ISetupInventoryProvider inventory,
        IBenchmarkDatasetSource datasets,
        ISetupEvaluationRepository repository,
        Func<string> appVersion,
        TimeProvider time,
        SetupEvaluationOptions options,
        ILogger<SetupEvaluationService> logger)
    {
        _inventory = inventory;
        _datasets = datasets;
        _repository = repository;
        _appVersion = appVersion;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public ISetupEvaluationRepository Repository => _repository;

    public IBenchmarkDatasetSource Datasets => _datasets;

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Somente a identificação local (estado de preparação). Não consulta bases.</summary>
    public async Task<SetupInventory> IdentifyAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.InventoryTimeout);
        try
        {
            return await Task.Run(() => _inventory.CollectAsync(cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A identificação do hardware excedeu o tempo limite.");
        }
    }

    public async Task<SetupEvaluationOutcome> EvaluateAsync(SetupEvaluationRequest request, IProgress<StepUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Já existe uma avaliação em andamento.");
        }

        var includeStorage = request.IncludeStorage;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_options.OverallTimeout);
        var ct = overall.Token;
        try
        {
            progress?.Report(new StepUpdate(EvaluationStep.Identify, StepState.Running, null));
            SetupInventory inventory;
            try
            {
                inventory = await IdentifyAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Identificação do hardware excedeu o tempo limite");
                progress?.Report(new StepUpdate(EvaluationStep.Identify, StepState.Failed, ex.Message));
                throw;
            }

            var identifyWarn = inventory.Cpus.Count == 0 || inventory.Gpus.Count == 0 || inventory.Memory.InstalledBytes is null || inventory.Warnings.Count > 0;
            progress?.Report(new StepUpdate(EvaluationStep.Identify, identifyWarn ? StepState.Warning : StepState.Done,
                $"{inventory.Cpus.Count} CPU · {inventory.Gpus.Count} adaptador(es) gráfico(s) · {inventory.Memory.Modules.Count} módulo(s) de RAM · {inventory.Drives.Count} unidade(s)"));

            progress?.Report(new StepUpdate(EvaluationStep.QueryDatabases, StepState.Running, null));
            var overrides = SafeOverrides();
            var definitions = _datasets.Definitions;
            var gpuSelection = SetupScorer.SelectGpu(inventory, overrides);
            var needed = new[] { SetupScorer.CpuDefinition(definitions), SetupScorer.GpuDefinition(definitions, gpuSelection.Selected) }
                .Where(d => d is not null).Select(d => d!).ToList();
            var loads = new Dictionary<string, DatasetLoadResult>(StringComparer.Ordinal);
            foreach (var def in needed)
            {
                ct.ThrowIfCancellationRequested();
                loads[def.Id] = await LoadAsync(def, ct).ConfigureAwait(false);
            }

            var failed = loads.Values.Where(l => !l.Success).ToList();
            progress?.Report(new StepUpdate(EvaluationStep.QueryDatabases, failed.Count > 0 ? StepState.Warning : StepState.Done,
                failed.Count > 0 ? $"{failed.Count} base(s) indisponível(is)" : string.Join(" · ", loads.Values.Select(l => $"{l.Definition.Title}: {l.Dataset!.Records.Count} registros"))));

            progress?.Report(new StepUpdate(EvaluationStep.Validate, StepState.Running, null));
            ct.ThrowIfCancellationRequested();
            var evaluation = SetupScorer.Build(new ScoringContext(inventory, includeStorage, definitions, loads, overrides, Guid.NewGuid(), _time.GetUtcNow(), _appVersion()));
            var unconfirmed = evaluation.Categories.Count(c => c.Status == CategoryStatus.Unavailable);
            progress?.Report(new StepUpdate(EvaluationStep.Validate, unconfirmed > 0 ? StepState.Warning : StepState.Done,
                unconfirmed > 0 ? $"{unconfirmed} categoria(s) sem dados confirmados" : "Todas as categorias confirmadas"));

            progress?.Report(new StepUpdate(EvaluationStep.Score, StepState.Running, null));
            progress?.Report(new StepUpdate(EvaluationStep.Score, evaluation.Overall.Score is null ? StepState.Warning : StepState.Done,
                SetupScoreText.Completeness(evaluation.Overall.Completeness)));

            progress?.Report(new StepUpdate(EvaluationStep.Report, StepState.Running, null));
            ct.ThrowIfCancellationRequested();
            var (saved, error) = Persist(evaluation);
            progress?.Report(new StepUpdate(EvaluationStep.Report, saved ? StepState.Done : StepState.Warning, saved ? "Salvo no histórico" : error));
            _logger.LogInformation("Nota do Setup: {Score} ({Completeness}), metodologia {Version}", evaluation.Overall.Score?.ToString() ?? "—", evaluation.Overall.Completeness, evaluation.MethodologyVersion);
            return new SetupEvaluationOutcome(evaluation, saved, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A avaliação excedeu o tempo limite.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DatasetLoadResult> LoadAsync(BenchmarkDatasetDefinition def, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.DatasetTimeout);
        try
        {
            return await Task.Run(() => _datasets.LoadAsync(def, cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Carregamento da base {Id} excedeu o tempo limite", def.Id);
            return new DatasetLoadResult(def, null, DatasetFailure.Timeout, "O carregamento excedeu o tempo limite.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao carregar a base {Id}", def.Id);
            return new DatasetLoadResult(def, null, DatasetFailure.NotAvailable, ex.Message);
        }
    }

    private IReadOnlyList<ComponentOverride> SafeOverrides()
    {
        try
        {
            return _repository.GetOverrides();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível ler as escolhas manuais da Nota do Setup");
            return [];
        }
    }

    private (bool Saved, string? Error) Persist(SetupEvaluation evaluation)
    {
        try
        {
            _repository.Save(evaluation);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar a avaliação {Id}", evaluation.Id);
            return (false, "Não foi possível salvar no histórico: " + ex.Message);
        }
    }
}
