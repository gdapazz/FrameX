using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Domain;
using Framex.Core.Persistence;
using Framex.Core.SessionTracking;
using Microsoft.Extensions.Logging;

namespace Framex.App.ViewModels;

public sealed partial class SessionRowViewModel(SessionListItem item, AppSettingsAccessor settings) : ObservableObject
{
    public SessionListItem Item { get; } = item;

    public string Game => Item.GameName;

    public string Started => Fmt.LocalTime(Item.StartedAt);

    public string Duration => Fmt.Duration(Item.Duration);

    public string AverageFps => Fmt.Fps(Item.AverageFps);

    public string Low1 => Fmt.Fps(Item.Low1PercentFps);

    public string MaxCpuTemp => Fmt.Temperature(Item.MaxCpuTemp, settings.Current);

    public string MaxGpuTemp => Fmt.Temperature(Item.MaxGpuTemp, settings.Current);

    public bool Incomplete => Item.State == SessionState.Incomplete;

    public bool Active => Item.State == SessionState.Active;

    public bool Unconfirmed => !Item.GameConfirmed;

    public string StateText => Item.State switch
    {
        SessionState.Active => "Em andamento",
        SessionState.Incomplete => "Incompleta",
        _ => "Concluída",
    };

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Acesso tardio às configurações atuais (as linhas formatam com as unidades vigentes).</summary>
public sealed class AppSettingsAccessor(SettingsService settings)
{
    public Core.Customization.AppSettings Current => settings.Current;
}

public sealed record Choice<T>(T Value, string Label);

public sealed partial class ReportsViewModel : ObservableObject, IPageActivated
{
    private readonly ISessionRepository _sessions;
    private readonly IGameRepository _games;
    private readonly Navigator _navigator;
    private readonly DialogService _dialogs;
    private readonly ExportService _export;
    private readonly AppSettingsAccessor _settings;
    private readonly ILogger<ReportsViewModel> _logger;
    private Guid? _pendingGameFilter;
    private CancellationTokenSource? _searchDelay;

    public ReportsViewModel(ISessionRepository sessions, IGameRepository games, Navigator navigator, DialogService dialogs, ExportService export,
        SettingsService settings, SessionRecorder recorder, ILogger<ReportsViewModel> logger)
    {
        _sessions = sessions;
        _games = games;
        _navigator = navigator;
        _dialogs = dialogs;
        _export = export;
        _settings = new AppSettingsAccessor(settings);
        _logger = logger;
        _period = Periods[0];
        _sort = Sorts[0];
        _minDuration = MinDurations[0];
        _gameFilter = new Choice<Guid?>(null, "Todos os jogos");
        GameChoices.Add(_gameFilter);
        recorder.SessionCompleted += (_, _) => Ui.Post(() => _ = LoadAsync());
    }

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    public ObservableCollection<Choice<Guid?>> GameChoices { get; } = [];

    public IReadOnlyList<Choice<int>> Periods { get; } =
    [
        new(0, "Todo o período"), new(1, "Últimas 24 horas"), new(7, "Últimos 7 dias"), new(30, "Últimos 30 dias"), new(90, "Últimos 90 dias"),
    ];

    public IReadOnlyList<Choice<SessionSort>> Sorts { get; } =
    [
        new(SessionSort.NewestFirst, "Mais recentes"), new(SessionSort.OldestFirst, "Mais antigas"), new(SessionSort.LongestFirst, "Mais longas"),
        new(SessionSort.HighestFps, "Maior FPS médio"), new(SessionSort.LowestFps, "Menor FPS médio"), new(SessionSort.HottestGpu, "GPU mais quente"),
    ];

    public IReadOnlyList<Choice<int>> MinDurations { get; } =
    [
        new(0, "Qualquer duração"), new(1, "Mais de 1 min"), new(5, "Mais de 5 min"), new(15, "Mais de 15 min"), new(60, "Mais de 1 h"),
    ];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private Choice<Guid?> _gameFilter;

    [ObservableProperty]
    private Choice<int> _period;

    [ObservableProperty]
    private Choice<SessionSort> _sort;

    [ObservableProperty]
    private Choice<int>? _minDuration;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private int _selectedCount;

    partial void OnSearchChanged(string value)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        var token = _searchDelay.Token;
        _ = Task.Delay(300, token).ContinueWith(_ => Ui.Post(() => _ = LoadAsync()), token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    partial void OnGameFilterChanged(Choice<Guid?> value) => _ = LoadAsync();

    partial void OnPeriodChanged(Choice<int> value) => _ = LoadAsync();

    partial void OnSortChanged(Choice<SessionSort> value) => _ = LoadAsync();

    partial void OnMinDurationChanged(Choice<int>? value) => _ = LoadAsync();

    public void OnActivated() => _ = LoadAsync();

    public void FilterByGame(Guid gameId) => _pendingGameFilter = gameId;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var games = await Task.Run(() => _games.GetAll(includeRemoved: true));
            var selectedId = _pendingGameFilter ?? GameFilter.Value;
            _pendingGameFilter = null;
            SyncGameChoices(games, selectedId);

            var query = new SessionQuery(
                Search: string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
                GameId: GameFilter.Value,
                From: Period.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-Period.Value) : null,
                MinDuration: MinDuration is { Value: > 0 } d ? TimeSpan.FromMinutes(d.Value) : null,
                Sort: Sort.Value);
            var items = await Task.Run(() => _sessions.Query(query));
            Sessions.Clear();
            foreach (var i in items)
            {
                var row = new SessionRowViewModel(i, _settings);
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SessionRowViewModel.IsSelected))
                    {
                        SelectedCount = Sessions.Count(s => s.IsSelected);
                    }
                };
                Sessions.Add(row);
            }

            SelectedCount = 0;
            Summary = items.Count == 0
                ? "Nenhuma sessão encontrada com os filtros atuais."
                : $"{items.Count} sessão(ões) · {Fmt.Duration(TimeSpan.FromTicks(items.Sum(i => i.Duration.Ticks)))} monitorados" + (items.Count >= query.Limit ? $" · exibindo as {query.Limit} primeiras" : "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao consultar o histórico");
            Summary = "Não foi possível carregar o histórico: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SyncGameChoices(IReadOnlyList<Game> games, Guid? selectedId)
    {
        var wanted = new List<Choice<Guid?>> { new(null, "Todos os jogos") };
        wanted.AddRange(games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new Choice<Guid?>(g.Id, g.Name)));
        if (!wanted.SequenceEqual(GameChoices))
        {
            GameChoices.Clear();
            foreach (var c in wanted)
            {
                GameChoices.Add(c);
            }
        }

        var target = GameChoices.FirstOrDefault(c => c.Value == selectedId) ?? GameChoices[0];
        if (!Equals(target, GameFilter))
        {
#pragma warning disable MVVMTK0034 // Atribuição direta para não disparar nova consulta durante a sincronização.
            _gameFilter = target;
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(GameFilter));
        }
    }

    [RelayCommand]
    private void Open(SessionRowViewModel row) => _navigator.OpenReport(row.Item.Id);

    [RelayCommand]
    private void CompareSelected()
    {
        var selected = Sessions.Where(s => s.IsSelected).ToList();
        if (selected.Count != 2)
        {
            _dialogs.Info("Comparar sessões", "Selecione exatamente duas sessões para comparar.");
            return;
        }

        _navigator.Compare(selected[0].Item.Id, selected[1].Item.Id);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var s in Sessions)
        {
            s.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        var selected = Sessions.Where(s => s.IsSelected && !s.Active).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!_dialogs.Confirm("Excluir sessões", $"Excluir permanentemente {selected.Count} sessão(ões) e todos os seus dados? Esta ação não pode ser desfeita.", "Excluir", danger: true))
        {
            return;
        }

        try
        {
            await Task.Run(() => _sessions.Delete(selected.Select(s => s.Item.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao excluir sessões");
            _dialogs.Info("Excluir sessões", "Não foi possível excluir: " + ex.Message);
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportSelected(string format)
    {
        var selected = Sessions.Where(s => s.IsSelected).Select(s => s.Item.Id).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Info("Exportar", "Selecione ao menos uma sessão.");
            return;
        }

        await _export.ExportManyAsync(selected, Enum.Parse<Core.Reporting.ExportFormat>(format));
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        GameFilter = GameChoices[0];
        Period = Periods[0];
        Sort = Sorts[0];
        MinDuration = MinDurations[0];
    }
}
