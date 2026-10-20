using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Customization;
using Framex.Core.SetupScore;
using Framex.Infrastructure;
using Framex.Infrastructure.SetupScore;
using Microsoft.Extensions.Logging;

namespace Framex.App.ViewModels;

public enum SetupPageState
{
    Preparation,
    Running,
    Result,
}

public sealed record PrepItem(string Category, string Icon, string Name, string Detail, bool Pending);

public sealed partial class StepItem(EvaluationStep step, string label) : ObservableObject
{
    public EvaluationStep Step { get; } = step;

    public string Label { get; } = label;

    [ObservableProperty]
    private StepState _state;

    [ObservableProperty]
    private string? _detail;
}

/// <summary>Escolha manual: um registro da base para um componente, ou qual adaptador gráfico avaliar.</summary>
public sealed record ChoiceItem(SetupCategory Category, string DeviceKey, string RecordName, string DatasetId, string Label, string Detail, bool IsAdapter);

public sealed record DriveRow(string Model, string Type, string Capacity, string Status, bool Included);

public sealed record CategoryCard(
    SetupCategory Category,
    string Title,
    string Icon,
    string Name,
    int? Score,
    string ScoreText,
    string StatusText,
    bool Scored,
    bool Excluded,
    string WeightText,
    string BasisTag,
    string MatchText,
    string SourceText,
    string ConfidenceText,
    string BasisText,
    string? Reason,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<DriveRow> Drives,
    IReadOnlyList<ChoiceItem> Choices,
    bool CanClearOverride)
{
    public bool HasChoices => Choices.Count > 0;

    public bool HasDrives => Drives.Count > 0;

    public double? GaugeValue => Score;
}

public sealed record IssueItem(
    string Category,
    string Severity,
    string DetectedName,
    string State,
    string Source,
    string Reason,
    string Consequence,
    string? Action,
    string Details,
    IReadOnlyList<ChoiceItem> Choices,
    bool CanRetry)
{
    public bool HasChoices => Choices.Count > 0;
}

public sealed record MathRow(string Category, string Score, string BaseWeight, string EffectiveWeight, string Contribution, bool Participated);

public sealed record HistoryItem(Guid Id, string Date, string Score, string Tier, string Completeness, string Details);

public sealed record SetupCompareRow(string Category, string Previous, string Current, string Delta, int Sign);

public sealed record DatasetRow(string Title, string Detail);

public sealed partial class SetupScoreViewModel : ObservableObject, IPageActivated
{
    private readonly SetupEvaluationService _service;
    private readonly BlenderOpenDataStore _store;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<SetupScoreViewModel> _logger;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _identifyCts;
    private SetupEvaluation? _current;
    private IReadOnlyList<SetupEvaluationSummary> _summaries = [];
    private bool _identified;

    public SetupScoreViewModel(SetupEvaluationService service, BlenderOpenDataStore store, ThemeService theme, DialogService dialogs, SettingsService settings, AppPaths paths,
        ILogger<SetupScoreViewModel> logger)
    {
        _service = service;
        _store = store;
        _theme = theme;
        _dialogs = dialogs;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        theme.ThemeApplied += (_, _) => OnPropertyChanged(nameof(Animations));
        ResetSteps();
        ResetDetected();
    }

    public ObservableCollection<PrepItem> Detected { get; } = [];

    public ObservableCollection<StepItem> Steps { get; } = [];

    public ObservableCollection<CategoryCard> Cards { get; } = [];

    public ObservableCollection<IssueItem> Issues { get; } = [];

    public ObservableCollection<MathRow> MathRows { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<DatasetRow> Datasets { get; } = [];

    public ObservableCollection<HistoryItem> History { get; } = [];

    public ObservableCollection<SetupCompareRow> Comparison { get; } = [];

    public AnimationLevel Animations => _theme.Animations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparation), nameof(IsRunningState), nameof(IsResult))]
    private SetupPageState _state = SetupPageState.Preparation;

    public bool IsPreparation => State == SetupPageState.Preparation;

    public bool IsRunningState => State == SetupPageState.Running;

    public bool IsResult => State == SetupPageState.Result;

    [ObservableProperty]
    private bool _includeStorage = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand), nameof(CancelCommand), nameof(UpdateDatabasesCommand), nameof(ChooseCommand), nameof(ClearOverrideCommand), nameof(RedoCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isIdentifying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateDatabasesCommand), nameof(EvaluateCommand))]
    private bool _isUpdating;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private string _databaseStatus = "Verificando bases locais…";

    // ---------- Resultado ----------
    [ObservableProperty]
    private double? _overallValue;

    [ObservableProperty]
    private string _overallText = "—";

    [ObservableProperty]
    private string _tierText = "";

    [ObservableProperty]
    private string _completenessText = "";

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private string _coverageText = "";

    [ObservableProperty]
    private string _confidenceText = "";

    [ObservableProperty]
    private string _resultMeta = "";

    [ObservableProperty]
    private string _formulaText = "";

    [ObservableProperty]
    private string? _historyBanner;

    [ObservableProperty]
    private bool _showIssues;

    [ObservableProperty]
    private bool _showMath;

    [ObservableProperty]
    private string? _comparisonTitle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareWithPreviousCommand))]
    private bool _hasPrevious;

    public bool HasIssues => Issues.Count > 0;

    public bool HasHistory => History.Count > 0;

    public bool HasComparison => Comparison.Count > 0;

    public string IssuesTitle => Issues.Count == 1 ? "1 ocorrência" : $"{Issues.Count} ocorrências";

    public void OnActivated()
    {
        RefreshHistory();
        _ = RefreshDatabaseStatusAsync();
        if (!_identified && !IsRunning)
        {
            _ = IdentifyAsync();
        }
    }

    /// <summary>Sair da página cancela consultas em andamento (sem salvar resultado parcial).</summary>
    public void OnDeactivated() => CancelAll();

    public void CancelAll()
    {
        _cts?.Cancel();
        _identifyCts?.Cancel();
    }

    private void ResetSteps()
    {
        Steps.Clear();
        Steps.Add(new StepItem(EvaluationStep.Identify, "Identificando componentes"));
        Steps.Add(new StepItem(EvaluationStep.QueryDatabases, "Consultando bases de hardware"));
        Steps.Add(new StepItem(EvaluationStep.Validate, "Validando especificações"));
        Steps.Add(new StepItem(EvaluationStep.Score, "Calculando pontuações"));
        Steps.Add(new StepItem(EvaluationStep.Report, "Preparando o relatório"));
    }

    private void ResetDetected()
    {
        Detected.Clear();
        Detected.Add(new PrepItem("GPU", "I.Gpu", "Identificação pendente", "", true));
        Detected.Add(new PrepItem("CPU", "I.Hardware", "Identificação pendente", "", true));
        Detected.Add(new PrepItem("RAM", "I.Memory", "Identificação pendente", "", true));
        Detected.Add(new PrepItem("Armazenamento", "I.Storage", "Identificação pendente", "", true));
    }

    [RelayCommand]
    private async Task IdentifyAsync()
    {
        if (IsIdentifying || IsRunning)
        {
            return;
        }

        IsIdentifying = true;
        _identifyCts = new CancellationTokenSource();
        try
        {
            var inv = await _service.IdentifyAsync(_identifyCts.Token);
            ShowDetected(inv);
            _identified = true;
        }
        catch (OperationCanceledException)
        {
            ResetDetected();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha na identificação para a Nota do Setup");
            ResetDetected();
            SetMessage("Não foi possível identificar o hardware agora: " + ex.Message, true);
        }
        finally
        {
            IsIdentifying = false;
            _identifyCts?.Dispose();
            _identifyCts = null;
        }
    }

    private void ShowDetected(SetupInventory inv)
    {
        Detected.Clear();
        var gpu = SetupScorer.SelectGpu(inv, SafeOverrides());
        var gpuName = gpu.Selected?.Name ?? (gpu.Ambiguous ? "Escolha necessária: " + string.Join(" · ", gpu.Eligible.Select(g => g.Name)) : "Identificação pendente");
        var gpuDetail = gpu.Selected is { } g
            ? $"{SetupScoreText.GpuKindLabel(g.Kind)}{(g.DedicatedMemoryBytes is { } v ? " · " + SetupScoreText.Number(v / 1073741824.0) + " GiB dedicados" : "")}"
              + (inv.Gpus.Count > 1 ? $" · {inv.Gpus.Count} adaptadores detectados" : "")
            : gpu.Reason;
        Detected.Add(new PrepItem("GPU", "I.Gpu", gpuName, gpuDetail, gpu.Selected is null));

        var cpu = inv.Cpus.FirstOrDefault();
        Detected.Add(new PrepItem("CPU", "I.Hardware", cpu?.Name ?? "Identificação pendente",
            cpu is null ? "O Windows não reportou o processador." : $"{cpu.PhysicalCores?.ToString() ?? "?"} núcleos · {cpu.LogicalProcessors?.ToString() ?? "?"} threads", cpu is null));

        var mem = inv.Memory;
        var ram = mem.InstalledBytes ?? mem.ModulesTotalBytes;
        Detected.Add(new PrepItem("RAM", "I.Memory", ram is { } r ? SetupScoreText.Number(r / 1073741824.0) + " GiB instalados" : "Identificação pendente",
            ram is null ? "Capacidade instalada não informada." : string.Join(" · ", new[] { mem.CommonType, mem.EffectiveSpeedMts is { } s ? s + " MT/s" : null, mem.Modules.Count > 0 ? mem.Modules.Count + " módulo(s)" : null }.Where(x => x is not null)),
            ram is null));

        var agg = StorageCapacityAggregator.Aggregate(inv.Drives);
        Detected.Add(new PrepItem("Armazenamento", "I.Storage",
            agg.IncludedCount > 0 ? $"{StorageCapacityAggregator.FormatNominal(agg.TotalBytes)} nominais" : "Identificação pendente",
            agg.IncludedCount > 0 ? string.Join(" + ", agg.Drives.Where(d => d.Included).Select(d => $"{StorageCapacityAggregator.FormatNominal(d.Drive.SizeBytes!.Value)} {d.Drive.TypeLabel}")) : "Nenhuma unidade interna elegível encontrada.",
            agg.IncludedCount == 0));
    }

    private IReadOnlyList<ComponentOverride> SafeOverrides()
    {
        try
        {
            return _service.Repository.GetOverrides();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler escolhas manuais");
            return [];
        }
    }

    private bool CanEvaluate() => !IsRunning && !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private async Task EvaluateAsync()
    {
        _identifyCts?.Cancel();
        ResetSteps();
        SetMessage(null);
        HistoryBanner = null;
        IsRunning = true;
        State = SetupPageState.Running;
        var request = new SetupEvaluationRequest(IncludeStorage);
        _cts = new CancellationTokenSource();
        var progress = new Progress<StepUpdate>(OnStep);
        try
        {
            var outcome = await _service.EvaluateAsync(request, progress, _cts.Token);
            ShowResult(outcome.Evaluation, null);
            if (!outcome.Saved)
            {
                SetMessage(outcome.SaveError ?? "O resultado não foi salvo no histórico.", true);
            }

            RefreshHistory();
            _identified = false;
        }
        catch (OperationCanceledException)
        {
            State = _current is null ? SetupPageState.Preparation : SetupPageState.Result;
            SetMessage("Avaliação cancelada. Nenhum resultado novo foi salvo.", false);
        }
        catch (TimeoutException ex)
        {
            State = SetupPageState.Preparation;
            SetMessage(ex.Message + " Nenhum resultado foi salvo; você pode tentar novamente.", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha inesperada na avaliação do setup");
            State = SetupPageState.Preparation;
            SetMessage("A avaliação falhou: " + ex.Message + " Nenhum resultado foi salvo; tente novamente.", true);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnStep(StepUpdate u)
    {
        var step = Steps.FirstOrDefault(s => s.Step == u.Step);
        if (step is null)
        {
            return;
        }

        step.State = u.State;
        step.Detail = u.Detail;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private Task RedoAsync() => EvaluateAsync();

    [RelayCommand]
    private void BackToPreparation()
    {
        if (IsRunning)
        {
            return;
        }

        State = SetupPageState.Preparation;
        HistoryBanner = null;
        if (!_identified)
        {
            _ = IdentifyAsync();
        }
    }

    [RelayCommand]
    private void ToggleIssues() => ShowIssues = !ShowIssues;

    [RelayCommand]
    private void ToggleMath() => ShowMath = !ShowMath;

    private void SetMessage(string? text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    // ---------- Escolha manual ----------
    private bool CanChoose(ChoiceItem? item) => item is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseAsync(ChoiceItem? item)
    {
        if (item is null)
        {
            return;
        }

        var text = item.IsAdapter
            ? $"Avaliar \"{item.Label}\" como a GPU deste computador? A escolha fica salva apenas neste computador."
            : $"Usar o registro \"{item.RecordName}\" da base para \"{item.DeviceKey[(item.DeviceKey.IndexOf(':') + 1)..]}\"?\n\nUse apenas se for exatamente o seu modelo. A escolha fica salva apenas neste computador, não altera a base e pode ser desfeita.";
        if (!_dialogs.Confirm("Escolha manual", text, "Confirmar e reavaliar"))
        {
            return;
        }

        try
        {
            _service.Repository.SaveOverride(new ComponentOverride(item.Category, item.IsAdapter ? ComponentOverride.GpuAdapterSelectionKey : item.DeviceKey,
                item.RecordName, item.DatasetId, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar escolha manual");
            _dialogs.Info("Escolha manual", "Não foi possível salvar a escolha: " + ex.Message);
            return;
        }

        await EvaluateAsync();
    }

    private bool CanClear(CategoryCard? card) => card is { CanClearOverride: true } && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearOverrideAsync(CategoryCard? card)
    {
        if (card is null || _current is null)
        {
            return;
        }

        try
        {
            var repo = _service.Repository;
            if (card.Category == SetupCategory.Gpu)
            {
                var gpu = _current.Gpus.FirstOrDefault(g => g.Selected);
                if (gpu is not null)
                {
                    repo.RemoveOverride(SetupCategory.Gpu, gpu.Key);
                }

                repo.RemoveOverride(SetupCategory.Gpu, ComponentOverride.GpuAdapterSelectionKey);
            }
            else
            {
                repo.RemoveOverride(card.Category, "cpu:" + HardwareNameNormalizer.Strict(card.Name));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remover escolha manual");
            _dialogs.Info("Escolha manual", "Não foi possível remover a escolha: " + ex.Message);
            return;
        }

        await EvaluateAsync();
    }

    // ---------- Bases ----------
    private bool CanUpdate() => !IsRunning && !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateDatabasesAsync()
    {
        if (!_dialogs.Confirm("Atualizar bases de hardware",
                "O FRAMEX vai baixar as listas públicas de resultados do Blender Open Data (opendata.blender.org, licença CC0).\n\n"
                + "Somente as listas completas são baixadas: nenhuma informação do seu computador é enviada. Cada arquivo é validado antes de substituir a cópia atual; se algo falhar, a cópia anterior é mantida.\n\n"
                + "A metodologia da nota não muda com a atualização, mas os valores medianos da base podem mudar.",
                "Atualizar"))
        {
            return;
        }

        IsUpdating = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var results = await Task.Run(() => _store.UpdateAsync(cts.Token));
            var lines = results.Select(r => $"• {Title(r.DatasetId)}: {(r.Updated ? "atualizada — " : r.Unchanged ? "" : "não atualizada — ")}{r.Message}");
            _dialogs.Info("Atualização das bases", string.Join("\n", lines));
        }
        catch (OperationCanceledException)
        {
            _dialogs.Info("Atualização das bases", "A atualização excedeu o tempo limite. As cópias anteriores foram mantidas.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao atualizar bases da Nota do Setup");
            _dialogs.Info("Atualização das bases", "A atualização falhou: " + ex.Message + "\nAs cópias anteriores foram mantidas.");
        }
        finally
        {
            IsUpdating = false;
            await RefreshDatabaseStatusAsync();
        }

        static string Title(string id) => BlenderOpenDataStore.AllDefinitions.FirstOrDefault(d => d.Id == id)?.Title ?? id;
    }

    private async Task RefreshDatabaseStatusAsync()
    {
        try
        {
            var status = await Task.Run(_store.Status);
            var ok = status.Where(s => s.Success).ToList();
            if (ok.Count == 0)
            {
                DatabaseStatus = "Nenhuma base disponível neste computador. Use \"Atualizar bases\".";
                return;
            }

            var newest = ok.Max(s => s.Dataset!.Info.RetrievedAt);
            var origins = ok.Select(s => s.Dataset!.Info.Origin).Distinct().ToList();
            DatabaseStatus = $"Blender Open Data {SetupScoreMethodology.BlenderVersion} (CC0) · {ok.Count} de {status.Count} bases disponíveis offline · dados de {newest.ToLocalTime():dd/MM/yyyy}"
                             + (origins.Count == 1 ? $" ({origins[0]})" : "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao verificar as bases locais");
            DatabaseStatus = "Não foi possível verificar as bases locais: " + ex.Message;
        }
    }

    // ---------- Resultado ----------
    private void ShowResult(SetupEvaluation e, string? banner)
    {
        _current = e;
        HistoryBanner = banner;
        var o = e.Overall;
        OverallValue = o.Score;
        OverallText = o.Score?.ToString(SetupScoreText.Pt) ?? "—";
        TierText = o.Tier ?? "Sem nota geral";
        IsComplete = o.Completeness == EvaluationCompleteness.Complete;
        CompletenessText = SetupScoreText.Completeness(o.Completeness);
        CoverageText = "Cobertura dos dados: " + SetupScoreText.Percent(o.Coverage);
        ConfidenceText = "Confiabilidade: " + SetupScoreText.Confidence(e.Confidence);
        ResultMeta = $"{e.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · metodologia {e.MethodologyVersion} · armazenamento {(e.IncludeStorage ? "considerado" : "desativado")}";

        Cards.Clear();
        foreach (var c in e.Categories)
        {
            Cards.Add(BuildCard(e, c));
        }

        Issues.Clear();
        foreach (var i in e.Issues.OrderByDescending(i => i.Severity))
        {
            var choices = Cards.FirstOrDefault(c => c.Category == i.Category)?.Choices ?? [];
            var details = new StringBuilder()
                .Append("Horário: ").Append(i.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", SetupScoreText.Pt))
                .Append("\nEtapa: ").Append(i.Stage)
                .Append("\nCódigo: ").Append(i.ErrorCode)
                .Append("\nNome reportado: ").Append(i.DetectedName)
                .Append("\nFonte: ").Append(i.Source);
            if (i.Candidates.Count > 0)
            {
                details.Append("\nCandidatos: ").Append(string.Join(" · ", i.Candidates));
            }

            Issues.Add(new IssueItem(SetupScoreText.Category(i.Category), i.Severity.ToString(), i.DetectedName, i.State, i.Source, i.Reason, i.Consequence, i.Action,
                details.ToString(), choices,
                i.Kind is IssueKind.SourceUnavailable or IssueKind.InvalidSource or IssueKind.IncompleteLocalName or IssueKind.InsufficientLocalData or IssueKind.NotInDatabase));
        }

        MathRows.Clear();
        foreach (var c in e.Categories)
        {
            MathRows.Add(new MathRow(SetupScoreText.Category(c.Category), c.Score?.ToString(SetupScoreText.Pt) ?? SetupScoreText.Status(c.Status),
                SetupScoreText.Percent(c.BaseWeight), SetupScoreText.Percent(c.EffectiveWeight),
                c.Score is { } s ? SetupScoreText.Number(s * c.EffectiveWeight, "0.00") : "—", c.Status == CategoryStatus.Scored));
        }

        var scored = e.Categories.Where(c => c.Status == CategoryStatus.Scored).ToList();
        FormulaText = o.Score is null
            ? "Nota geral não calculada: falta pelo menos uma categoria obrigatória (GPU, CPU ou RAM)."
            : $"S = round( {string.Join(" + ", scored.Select(c => $"{SetupScoreText.Number(c.BaseWeight, "0.00")}×{c.Score}"))} ) / {SetupScoreText.Number(scored.Sum(c => c.BaseWeight), "0.00")} = "
              + $"{SetupScoreText.Number(scored.Sum(c => c.BaseWeight * c.Score!.Value) / scored.Sum(c => c.BaseWeight), "0.00")} → {o.Score}";

        Notes.Clear();
        foreach (var n in o.Notes)
        {
            Notes.Add(n);
        }

        Notes.Add("CPU e GPU: escala logística sobre o logaritmo da mediana do Blender Benchmark, com âncoras fixas da metodologia "
                  + $"{e.MethodologyVersion} (CPU: {SetupScoreMethodology.CpuScale.Low} → 50 e {SetupScoreMethodology.CpuScale.High} → 950; GPU: {SetupScoreMethodology.GpuScale.Low} → 50 e {SetupScoreMethodology.GpuScale.High} → 950). "
                  + "Razões iguais de desempenho valem distâncias iguais; dobrar o resultado não dobra a nota.");
        Notes.Add("RAM e armazenamento: estimativa técnica pela capacidade instalada, com retornos decrescentes. Tipo, frequência e interface são exibidos, mas não alteram a nota.");
        Notes.Add("O FRAMEX não executou nenhum teste de desempenho neste computador: os valores de CPU e GPU são medianas públicas de renderização (Cycles), não medições de jogos.");

        Datasets.Clear();
        foreach (var d in e.Datasets)
        {
            Datasets.Add(new DatasetRow(d.Title, $"{d.License} · obtida em {d.RetrievedAt.ToLocalTime():dd/MM/yyyy} ({d.Origin}) · {d.RecordCount} registros válidos, {d.RejectedCount} rejeitados · SHA-256 {d.Sha256[..12]}…"));
        }

        Comparison.Clear();
        ComparisonTitle = null;
        ShowIssues = e.Issues.Any(i => i.Severity == IssueSeverity.Error);
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssuesTitle));
        OnPropertyChanged(nameof(HasComparison));
        UpdateHasPrevious();
        State = SetupPageState.Result;
    }

    private static string CategoryTitle(SetupCategory c) => c switch
    {
        SetupCategory.Gpu => "Placa de vídeo",
        SetupCategory.Cpu => "Processador",
        SetupCategory.Ram => "Memória RAM",
        _ => "Armazenamento",
    };

    private static string CategoryIcon(SetupCategory c) => c switch
    {
        SetupCategory.Gpu => "I.Gpu",
        SetupCategory.Cpu => "I.Hardware",
        SetupCategory.Ram => "I.Memory",
        _ => "I.Storage",
    };

    private CategoryCard BuildCard(SetupEvaluation e, CategoryEvaluation c)
    {
        var weight = $"Peso {SetupScoreText.Percent(c.BaseWeight)}";
        if (c.Status == CategoryStatus.Scored && Math.Abs(c.EffectiveWeight - c.BaseWeight) > 1e-9)
        {
            weight += $" · efetivo {SetupScoreText.Percent(c.EffectiveWeight)}";
        }

        var dataset = e.Datasets.FirstOrDefault(d => d.Id == c.DatasetId);
        var source = c.Basis switch
        {
            ScoreBasis.MeasuredBenchmark => dataset?.Title ?? "Blender Open Data",
            ScoreBasis.CapacityCurve => "Inventário local do Windows",
            _ => dataset?.Title ?? (c.Category is SetupCategory.Ram or SetupCategory.Storage ? "Inventário local do Windows" : "—"),
        };
        var tag = c.Basis switch
        {
            ScoreBasis.MeasuredBenchmark => "Resultado medido (base pública)",
            ScoreBasis.CapacityCurve => "Estimativa técnica",
            _ => c.Status == CategoryStatus.ExcludedByUser ? "Desativado" : "Sem dados confirmados",
        };

        var choices = new List<ChoiceItem>();
        if (c.Category == SetupCategory.Gpu && e.Issues.Any(i => i.Kind == IssueKind.GpuSelectionAmbiguous))
        {
            choices.AddRange(e.Gpus.Where(g => g.Kind != GpuKind.Virtual)
                .Select(g => new ChoiceItem(SetupCategory.Gpu, g.Key, g.Key, "", g.Name, $"{g.Vendor} · {SetupScoreText.GpuKindLabel(g.Kind)}", true)));
        }
        else if (c.Category is SetupCategory.Cpu or SetupCategory.Gpu && c.Status == CategoryStatus.Unavailable && c.DatasetId is not null && c.Candidates.Count > 0)
        {
            var key = c.Category == SetupCategory.Cpu ? "cpu:" + HardwareNameNormalizer.Strict(c.ComponentName) : e.Gpus.FirstOrDefault(g => g.Selected)?.Key;
            if (key is not null)
            {
                choices.AddRange(c.Candidates.Select(name => new ChoiceItem(c.Category, key, name, c.DatasetId, name, "Registro da base", false)));
            }
        }

        var drives = c.Category == SetupCategory.Storage
            ? e.Drives.Select(d => new DriveRow(d.Model, d.Type, d.SizeBytes is { } b ? StorageCapacityAggregator.FormatNominal(b) : "—", d.Reason, d.Decision == DriveDecision.Included)).ToList()
            : [];

        var canClear = c.Match == MatchState.UserSelected
                       || (c.Category == SetupCategory.Gpu && e.Gpus.Count(g => g.Kind != GpuKind.Virtual) > 1 && SafeOverrides().Any(o => o.DeviceKey == ComponentOverride.GpuAdapterSelectionKey));

        return new CategoryCard(c.Category, CategoryTitle(c.Category), CategoryIcon(c.Category), c.ComponentName, c.Score,
            c.Score?.ToString(SetupScoreText.Pt) ?? "—", SetupScoreText.Status(c.Status), c.Status == CategoryStatus.Scored, c.Status == CategoryStatus.ExcludedByUser,
            weight, tag, SetupScoreText.Match(c.Match), source, SetupScoreText.Confidence(c.Confidence), c.BasisDescription, c.UnavailableReason, c.Facts, drives, choices, canClear);
    }

    // ---------- Histórico ----------
    private void RefreshHistory()
    {
        try
        {
            var list = _service.Repository.List(30);
            _summaries = list;
            History.Clear();
            foreach (var h in list)
            {
                History.Add(new HistoryItem(h.Id, h.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SetupScoreText.Pt), h.Score?.ToString(SetupScoreText.Pt) ?? "—",
                    h.Score is { } s ? SetupScoreMethodology.TierFor(s).Name : "Sem nota geral", SetupScoreText.Completeness(h.Completeness),
                    $"Metodologia {h.MethodologyVersion} · armazenamento {(h.IncludeStorage ? "considerado" : "desativado")} · cobertura {SetupScoreText.Percent(h.Coverage)}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao carregar o histórico da Nota do Setup");
        }

        OnPropertyChanged(nameof(HasHistory));
        UpdateHasPrevious();
    }

    private void UpdateHasPrevious() => HasPrevious = _current is not null && _summaries.Any(s => s.Id != _current.Id && s.CreatedAt < _current.CreatedAt);

    [RelayCommand]
    private void OpenHistory(HistoryItem? item)
    {
        if (item is null || IsRunning)
        {
            return;
        }

        SetupEvaluation? e;
        try
        {
            e = _service.Repository.Get(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao abrir avaliação {Id}", item.Id);
            e = null;
        }

        if (e is null)
        {
            _dialogs.Info("Histórico", "Esta avaliação não pôde ser aberta: o registro está corrompido ou em um formato desconhecido.");
            return;
        }

        SetMessage(null);
        ShowResult(e, $"Avaliação salva em {item.Date} (metodologia {e.MethodologyVersion}). Exibida como foi calculada, sem nova consulta.");
    }

    [RelayCommand]
    private void DeleteHistory(HistoryItem? item)
    {
        if (item is null || !_dialogs.Confirm("Excluir avaliação", $"Excluir a avaliação de {item.Date} do histórico?", "Excluir", danger: true))
        {
            return;
        }

        try
        {
            _service.Repository.Delete(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao excluir avaliação");
            _dialogs.Info("Histórico", "Não foi possível excluir: " + ex.Message);
        }

        RefreshHistory();
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void CompareWithPrevious()
    {
        if (_current is null)
        {
            return;
        }

        var previousSummary = _summaries.Where(s => s.Id != _current.Id && s.CreatedAt < _current.CreatedAt).OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        var previous = previousSummary is null ? null : _service.Repository.Get(previousSummary.Id);
        if (previous is null)
        {
            _dialogs.Info("Comparar", "Não há uma avaliação anterior válida para comparar.");
            return;
        }

        Comparison.Clear();
        Comparison.Add(Row("Nota geral", previous.Overall.Score, _current.Overall.Score));
        foreach (var c in _current.Categories)
        {
            Comparison.Add(Row(SetupScoreText.Category(c.Category), previous.Get(c.Category).Score, c.Score));
        }

        ComparisonTitle = $"Comparação com {previous.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}"
                          + (previous.MethodologyVersion != _current.MethodologyVersion ? $" · atenção: metodologias diferentes ({previous.MethodologyVersion} × {_current.MethodologyVersion})" : "")
                          + (previous.IncludeStorage != _current.IncludeStorage ? " · a opção de armazenamento era diferente" : "")
                          + (!previous.Datasets.Select(d => d.Sha256).SequenceEqual(_current.Datasets.Select(d => d.Sha256)) ? " · versões das bases diferentes" : "");
        OnPropertyChanged(nameof(HasComparison));

        static SetupCompareRow Row(string label, int? before, int? after)
        {
            var delta = before is { } b && after is { } a ? a - b : (int?)null;
            return new SetupCompareRow(label, before?.ToString(SetupScoreText.Pt) ?? "—", after?.ToString(SetupScoreText.Pt) ?? "—",
                delta is { } d ? (d > 0 ? "+" : "") + d.ToString(SetupScoreText.Pt) : "—", Math.Sign(delta ?? 0));
        }
    }

    // ---------- Exportação ----------
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_current is null)
        {
            return;
        }

        var folder = _settings.Current.ExportFolder is { Length: > 0 } f ? f : _paths.DefaultExportDirectory;
        var path = _dialogs.SaveFile("Exportar Nota do Setup", "Relatório HTML (*.html)|*.html|Dados JSON (*.json)|*.json",
            SetupReportExporter.SuggestFileName(_current, ".html"), folder);
        if (path is null)
        {
            return;
        }

        var evaluation = _current;
        try
        {
            var json = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
            await Task.Run(() => File.WriteAllText(path, json ? SetupReportExporter.Serialize(evaluation, indented: true) : SetupReportExporter.ToHtml(evaluation), new UTF8Encoding(false)));
            if (_dialogs.Show("Exportação concluída", "Arquivo salvo.\n" + Path.GetFileName(path), new DialogButton("Fechar"), new DialogButton("Mostrar na pasta", Primary: true)) == 1)
            {
                ExportService.RevealInExplorer(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exportar a Nota do Setup");
            _dialogs.Info("Exportar", "Não foi possível salvar o arquivo: " + ex.Message);
        }
    }
}
