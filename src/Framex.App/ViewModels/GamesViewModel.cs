using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Domain;
using Framex.Core.GameDetection;
using Framex.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Framex.App.ViewModels;

public sealed partial class GameItemViewModel : ObservableObject
{
    private readonly Action<GameItemViewModel> _save;
    private bool _loading;

    public GameItemViewModel(Game game, GameStatistics? stats, Action<GameItemViewModel> save)
    {
        _save = save;
        _loading = true;
        Game = game;
        _captureFrames = game.Profile.CaptureFrames;
        _showOverlay = game.Profile.ShowOverlay;
        _alertsEnabled = game.Profile.AlertsEnabled;
        _overlayLayoutId = game.Profile.OverlayLayoutId;
        Stats = stats;
        _loading = false;
    }

    public Game Game { get; }

    public GameStatistics? Stats { get; }

    public string Name => Game.Name;

    public bool IsPending => Game.Status == GameStatus.Pending;

    public bool IsRemoved => Game.Status == GameStatus.Removed;

    public bool IsConfirmed => Game.Status == GameStatus.Confirmed;

    public string StatusText => Game.Status switch
    {
        GameStatus.Confirmed => "Confirmado",
        GameStatus.Pending => "Aguardando confirmação",
        _ => "Removido (não monitorado)",
    };

    public string Executables => string.Join(", ", Game.Executables.Select(e => e.ExecutableName));

    public string Source => Game.Source ?? "Cadastro manual";

    public string Initial => Game.Name.Length > 0 ? Game.Name[..1].ToUpperInvariant() : "?";

    public string SessionsText => Stats is null || Stats.SessionCount == 0 ? "Nenhuma sessão" : Stats.SessionCount == 1 ? "1 sessão" : $"{Stats.SessionCount} sessões";

    public string TotalText => Stats is null ? Fmt.Missing : Fmt.Duration(Stats.TotalMonitored);

    public string LastText => Stats?.LastSession is { } last ? Fmt.LocalTime(last) : Fmt.Missing;

    public string AverageFpsText => Fmt.Fps(Stats?.AverageFps);

    [ObservableProperty]
    private bool _captureFrames;

    [ObservableProperty]
    private bool _showOverlay;

    [ObservableProperty]
    private bool _alertsEnabled;

    [ObservableProperty]
    private string? _overlayLayoutId;

    partial void OnCaptureFramesChanged(bool value) => Persist();
    partial void OnShowOverlayChanged(bool value) => Persist();
    partial void OnAlertsEnabledChanged(bool value) => Persist();
    partial void OnOverlayLayoutIdChanged(string? value) => Persist();

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        Game.Profile.CaptureFrames = CaptureFrames;
        Game.Profile.ShowOverlay = ShowOverlay;
        Game.Profile.AlertsEnabled = AlertsEnabled;
        Game.Profile.OverlayLayoutId = string.IsNullOrEmpty(OverlayLayoutId) ? null : OverlayLayoutId;
        _save(this);
    }
}

public sealed record LayoutChoice(string? Id, string Name);

public sealed partial class GamesViewModel : ObservableObject, IPageActivated
{
    private readonly IGameRepository _games;
    private readonly IProcessSnapshotProvider _processes;
    private readonly GameDetectionService _detection;
    private readonly DialogService _dialogs;
    private readonly SettingsService _settings;
    private readonly ReportsViewModel _reports;
    private readonly Navigator _navigator;
    private readonly ILogger<GamesViewModel> _logger;
    private List<GameItemViewModel> _all = [];

    public GamesViewModel(IGameRepository games, IProcessSnapshotProvider processes, GameDetectionService detection, DialogService dialogs,
        SettingsService settings, ReportsViewModel reports, Navigator navigator, ILogger<GamesViewModel> logger)
    {
        _games = games;
        _processes = processes;
        _detection = detection;
        _dialogs = dialogs;
        _settings = settings;
        _reports = reports;
        _navigator = navigator;
        _logger = logger;
        detection.PendingGameDiscovered += (_, _) => Ui.Post(() => _ = RefreshAsync());
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];
    public ObservableCollection<GameItemViewModel> Pending { get; } = [];
    public ObservableCollection<string> Ignored { get; } = [];
    public ObservableCollection<ProcessInfo> RunningProcesses { get; } = [];
    public ObservableCollection<LayoutChoice> LayoutChoices { get; } = [];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _showRemoved;

    [ObservableProperty]
    private bool _pickerOpen;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "";

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnShowRemovedChanged(bool value) => ApplyFilter();

    public void OnActivated() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var (games, stats, ignored) = await Task.Run(() => (_games.GetAll(includeRemoved: true), _games.GetStatistics(), _games.GetIgnoredExecutables()));
            _all = games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new GameItemViewModel(g, stats.GetValueOrDefault(g.Id), Save))
                .ToList();
            Ignored.Clear();
            foreach (var exe in ignored.OrderBy(x => x))
            {
                Ignored.Add(exe);
            }

            LayoutChoices.Clear();
            LayoutChoices.Add(new LayoutChoice(null, "Layout padrão do overlay"));
            foreach (var l in _settings.OverlayLayouts)
            {
                LayoutChoices.Add(new LayoutChoice(l.Id, l.Name));
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar a biblioteca de jogos");
            _dialogs.Info("Jogos", "Não foi possível carregar a biblioteca: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        bool Match(GameItemViewModel g) => q.Length == 0
                                          || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                          || g.Executables.Contains(q, StringComparison.OrdinalIgnoreCase);
        Games.Clear();
        foreach (var g in _all.Where(g => !g.IsPending && (ShowRemoved || !g.IsRemoved) && Match(g)))
        {
            Games.Add(g);
        }

        Pending.Clear();
        foreach (var g in _all.Where(g => g.IsPending && Match(g)))
        {
            Pending.Add(g);
        }

        var confirmed = _all.Count(g => g.IsConfirmed);
        Summary = $"{confirmed} confirmado(s) · {Pending.Count} aguardando confirmação · {_all.Count(g => g.IsRemoved)} removido(s)";
    }

    private void Save(GameItemViewModel item) => _ = SaveAsync(item.Game);

    private async Task SaveAsync(Game game)
    {
        try
        {
            await Task.Run(() => _games.Upsert(game));
            _detection.InvalidateLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar o jogo {Game}", game.Name);
            _dialogs.Info("Jogos", "Não foi possível salvar as alterações: " + ex.Message);
        }
    }

    private async Task UpdateAsync(GameItemViewModel item, Action<Game> change)
    {
        change(item.Game);
        await SaveAsync(item.Game);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Confirm(GameItemViewModel item)
    {
        var name = _dialogs.Prompt("Confirmar jogo", $"Este executável ({item.Executables}) é um jogo? Confirme ou corrija o nome.", item.Name);
        if (name is not null)
        {
            await UpdateAsync(item, g =>
            {
                g.Name = name;
                g.Status = GameStatus.Confirmed;
            });
        }
    }

    [RelayCommand]
    private async Task Rename(GameItemViewModel item)
    {
        var name = _dialogs.Prompt("Renomear jogo", "Novo nome do jogo (relatórios antigos mantêm o nome registrado na época).", item.Name);
        if (name is not null)
        {
            await UpdateAsync(item, g => g.Name = name);
        }
    }

    [RelayCommand]
    private async Task Remove(GameItemViewModel item)
    {
        if (_dialogs.Confirm("Remover jogo", $"\"{item.Name}\" deixará de ser monitorado. Os relatórios existentes são mantidos.", "Remover", danger: true))
        {
            await UpdateAsync(item, g => g.Status = GameStatus.Removed);
        }
    }

    [RelayCommand]
    private async Task NotAGame(GameItemViewModel item)
    {
        if (!_dialogs.Confirm("Não é um jogo", $"Os executáveis ({item.Executables}) serão ignorados pela detecção automática. Você pode desfazer na lista de ignorados.", "Ignorar"))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var exe in item.Game.Executables)
                {
                    _games.SetIgnored(exe.ExecutableName, true);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ignorar executável");
        }

        await UpdateAsync(item, g => g.Status = GameStatus.Removed);
    }

    [RelayCommand]
    private async Task Restore(GameItemViewModel item) => await UpdateAsync(item, g => g.Status = GameStatus.Confirmed);

    [RelayCommand]
    private async Task AddExecutable(GameItemViewModel item)
    {
        var path = _dialogs.OpenFile("Associar executável a " + item.Name, "Executáveis (*.exe)|*.exe");
        if (path is null)
        {
            return;
        }

        var exe = Path.GetFileName(path);
        if (item.Game.Matches(exe))
        {
            return;
        }

        await UpdateAsync(item, g => g.Executables.Add(new GameExecutable(exe, path)));
    }

    [RelayCommand]
    private async Task Unignore(string exe)
    {
        try
        {
            await Task.Run(() => _games.SetIgnored(exe, false));
            _detection.InvalidateLibrary();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remover {Exe} da lista de ignorados", exe);
        }
    }

    [RelayCommand]
    private async Task AddFromFile()
    {
        var path = _dialogs.OpenFile("Adicionar jogo", "Executáveis (*.exe)|*.exe");
        if (path is not null)
        {
            await AddGameAsync(Path.GetFileName(path), path, Path.GetFileNameWithoutExtension(path));
        }
    }

    [RelayCommand]
    private void OpenPicker()
    {
        RunningProcesses.Clear();
        var known = _all.SelectMany(g => g.Game.Executables).Select(e => e.ExecutableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _processes.GetProcesses()
                     .Where(p => p.HasVisibleWindow && !known.Contains(p.ExecutableName))
                     .GroupBy(p => p.ExecutableName, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.OrderByDescending(x => x.WorkingSetBytes).First())
                     .OrderByDescending(p => p.WorkingSetBytes))
        {
            RunningProcesses.Add(p);
        }

        PickerOpen = true;
    }

    [RelayCommand]
    private void ClosePicker() => PickerOpen = false;

    [RelayCommand]
    private async Task AddRunning(ProcessInfo process)
    {
        PickerOpen = false;
        var suggested = string.IsNullOrWhiteSpace(process.WindowTitle) ? Path.GetFileNameWithoutExtension(process.ExecutableName) : process.WindowTitle!;
        await AddGameAsync(process.ExecutableName, process.ExecutablePath, suggested);
    }

    private async Task AddGameAsync(string exe, string? path, string suggestedName)
    {
        var existing = _all.FirstOrDefault(g => g.Game.Matches(exe));
        if (existing is not null && !existing.IsRemoved)
        {
            _dialogs.Info("Adicionar jogo", $"{exe} já está associado a \"{existing.Name}\".");
            return;
        }

        var name = _dialogs.Prompt("Adicionar jogo", $"Nome do jogo para {exe}:", suggestedName);
        if (name is null)
        {
            return;
        }

        if (existing is not null)
        {
            await UpdateAsync(existing, g =>
            {
                g.Name = name;
                g.Status = GameStatus.Confirmed;
            });
            return;
        }

        var game = new Game
        {
            Name = name,
            Status = GameStatus.Confirmed,
            Source = "Cadastro manual",
            Executables = [new GameExecutable(exe, path)],
        };
        try
        {
            await Task.Run(() =>
            {
                _games.SetIgnored(exe, false);
                _games.Upsert(game);
            });
            _detection.InvalidateLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao adicionar jogo");
            _dialogs.Info("Adicionar jogo", "Não foi possível salvar: " + ex.Message);
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void ShowReports(GameItemViewModel item)
    {
        _reports.FilterByGame(item.Game.Id);
        _navigator.Go(PageKey.Reports);
    }
}
