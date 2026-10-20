using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Alerts;
using Framex.Core.Customization;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Persistence;
using Framex.Infrastructure.Service;
using Framex.Infrastructure;
using Framex.Infrastructure.FrameCapture;
using Microsoft.Extensions.Logging;

namespace Framex.App.ViewModels;

public enum SettingsSection
{
    General,
    Capture,
    Alerts,
    Hotkeys,
    Data,
}

public sealed partial class AlertRuleRow : ObservableObject
{
    public AlertRuleRow(AlertRule rule)
    {
        Rule = rule;
        _name = rule.Name;
        _enabled = rule.Enabled;
        _threshold = rule.Threshold;
        _below = rule.Comparison == AlertComparison.Below;
        _durationSeconds = (int)rule.MinDuration.TotalSeconds;
        _cooldownMinutes = Math.Max(0, (int)rule.Cooldown.TotalMinutes);
        _gameId = rule.GameId;
    }

    public AlertRule Rule { get; }

    public bool HasThreshold => Rule.Subject is AlertSubject.Metric or AlertSubject.VramPercent or AlertSubject.LiveFps or AlertSubject.FrameTimeSpike;

    public string SubjectText => Rule.Subject switch
    {
        AlertSubject.Metric when Rule.Metric is { } m && MetricCatalog.TryGet(m, out var d) => d.Label,
        AlertSubject.VramPercent => "VRAM em uso (% do total)",
        AlertSubject.LiveFps => "FPS ao vivo (janela de 1 s)",
        AlertSubject.FrameTimeSpike => "Pior frametime da última janela de 1 s",
        AlertSubject.SensorUnavailable when Rule.Metric is { } m && MetricCatalog.TryGet(m, out var d) => "Sensor indisponível: " + d.Label,
        AlertSubject.CaptureInterrupted => "Captura de quadros interrompida durante a sessão",
        AlertSubject.UnconfirmedGame => "Jogo detectado sem confirmação",
        _ => Rule.Subject.ToString(),
    };

    public string UnitText => Rule.Subject switch
    {
        AlertSubject.Metric when Rule.Metric is { } m && MetricCatalog.TryGet(m, out var d) => d.Unit switch
        {
            MetricUnit.Celsius => "°C",
            MetricUnit.Percent => "%",
            MetricUnit.Megabyte => "MiB",
            MetricUnit.Megahertz => "MHz",
            MetricUnit.Watt => "W",
            _ => "",
        },
        AlertSubject.VramPercent => "%",
        AlertSubject.LiveFps => "FPS",
        AlertSubject.FrameTimeSpike => "ms",
        _ => "",
    };

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _threshold;
    [ObservableProperty] private bool _below;
    [ObservableProperty] private int _durationSeconds;
    [ObservableProperty] private int _cooldownMinutes;
    [ObservableProperty] private Guid? _gameId;

    public event EventHandler? Changed;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Rule.Name = string.IsNullOrWhiteSpace(Name) ? Rule.Name : Name.Trim();
        Rule.Enabled = Enabled;
        Rule.Threshold = double.IsFinite(Threshold) ? Threshold : Rule.Threshold;
        Rule.Comparison = Below ? AlertComparison.Below : AlertComparison.Above;
        Rule.MinDuration = TimeSpan.FromSeconds(Math.Clamp(DurationSeconds, 0, 3600));
        Rule.Cooldown = TimeSpan.FromMinutes(Math.Clamp(CooldownMinutes, 0, 1440));
        Rule.GameId = GameId;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record AlertTemplate(string Label, AlertSubject Subject, MetricKind? Metric, AlertComparison Comparison, double Threshold);

public sealed partial class SettingsViewModel : ObservableObject, IPageActivated
{
    private readonly SettingsService _settings;
    private readonly HybridFrameCaptureProvider _capture;
    private readonly IAlertHistoryRepository _alertHistory;
    private readonly IGameRepository _games;
    private readonly HotkeyService _hotkeys;
    private readonly DialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly MainViewModel _main;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly DispatcherTimer _saveDelay;
    private bool _loading;
    private bool _rulesDirty;

    public SettingsViewModel(SettingsService settings, HybridFrameCaptureProvider capture, IAlertHistoryRepository alertHistory, IGameRepository games,
        HotkeyService hotkeys, DialogService dialogs, AppPaths paths, MainViewModel main, ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _capture = capture;
        _alertHistory = alertHistory;
        _games = games;
        _hotkeys = hotkeys;
        _dialogs = dialogs;
        _paths = paths;
        _main = main;
        _logger = logger;
        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDelay.Tick += (_, _) => Flush();
        IsElevated = FramexServiceStatus.CaptureAvailable();
    }

    [ObservableProperty]
    private SettingsSection _section;

    public bool IsElevated { get; }

    public IReadOnlyList<Choice<CloseBehavior>> CloseBehaviors { get; } =
        [new(CloseBehavior.Ask, "Perguntar"), new(CloseBehavior.MinimizeToTray, "Minimizar para a bandeja"), new(CloseBehavior.Exit, "Encerrar o FRAMEX")];

    public IReadOnlyList<Choice<PerformanceProfile>> Profiles { get; } =
    [
        new(PerformanceProfile.Economy, "Economia — sensores a cada 2 s, interface a cada 2 s, captura sem dados de GPU"),
        new(PerformanceProfile.Balanced, "Equilibrado — sensores e interface a cada 1 s (recomendado)"),
        new(PerformanceProfile.Detailed, "Detalhado — sensores e interface a cada 0,5 s, overlay até 4 atualizações/s"),
    ];

    public IReadOnlyList<Choice<AlertChannel>> Channels { get; } =
    [
        new(AlertChannel.Windows, "Notificação do Windows"), new(AlertChannel.Overlay, "Indicador no overlay"),
        new(AlertChannel.WindowsAndOverlay, "Windows e overlay"), new(AlertChannel.None, "Somente registrar no histórico"),
    ];

    public IReadOnlyList<AlertTemplate> AlertTemplates { get; } =
    [
        new("Temperatura da GPU acima de…", AlertSubject.Metric, MetricKind.GpuTemperature, AlertComparison.Above, 80),
        new("Hotspot da GPU acima de…", AlertSubject.Metric, MetricKind.GpuHotspot, AlertComparison.Above, 95),
        new("Temperatura da CPU acima de…", AlertSubject.Metric, MetricKind.CpuTemperaturePackage, AlertComparison.Above, 85),
        new("Uso da CPU acima de…", AlertSubject.Metric, MetricKind.CpuLoad, AlertComparison.Above, 95),
        new("Uso da GPU abaixo de…", AlertSubject.Metric, MetricKind.GpuLoad, AlertComparison.Below, 50),
        new("RAM em uso acima de… (%)", AlertSubject.Metric, MetricKind.RamLoad, AlertComparison.Above, 90),
        new("VRAM em uso acima de… (%)", AlertSubject.VramPercent, null, AlertComparison.Above, 90),
        new("FPS abaixo de…", AlertSubject.LiveFps, null, AlertComparison.Below, 60),
        new("Pico de frametime acima de… (ms)", AlertSubject.FrameTimeSpike, null, AlertComparison.Above, 50),
        new("Sensor de temperatura da CPU indisponível", AlertSubject.SensorUnavailable, MetricKind.CpuTemperaturePackage, AlertComparison.Above, 0),
    ];

    public ObservableCollection<AlertRuleRow> Rules { get; } = [];
    public ObservableCollection<AlertRecord> AlertHistory { get; } = [];
    public ObservableCollection<Choice<Guid?>> GameChoices { get; } = [];
    public ObservableCollection<string> HotkeyFailures { get; } = [];

    [ObservableProperty] private AlertTemplate? _templateToAdd;

    // Geral
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private Choice<CloseBehavior>? _closeChoice;
    [ObservableProperty] private bool _showTrayIcon;
    [ObservableProperty] private Choice<PerformanceProfile>? _profile;

    // Captura e jogos
    [ObservableProperty] private bool _captureFrames;
    [ObservableProperty] private string? _presentMonPath;
    [ObservableProperty] private string _captureStatus = "";
    [ObservableProperty] private string _captureDetail = "";
    [ObservableProperty] private string? _detectedPresentMon;
    [ObservableProperty] private bool _autoDetectStoreGames;
    [ObservableProperty] private bool _recordUnconfirmedGames;
    [ObservableProperty] private int _exitGraceSeconds;
    [ObservableProperty] private int _minSessionSeconds;

    // Alertas
    [ObservableProperty] private bool _alertsEnabled;
    [ObservableProperty] private Choice<AlertChannel>? _channelChoice;

    // Atalhos
    [ObservableProperty] private string _hotkeyOverlay = "";
    [ObservableProperty] private string _hotkeyWindow = "";
    [ObservableProperty] private string _hotkeyPause = "";
    [ObservableProperty] private string _hotkeyEdit = "";

    // Relatórios e dados
    [ObservableProperty] private bool _autoOpenReport;
    [ObservableProperty] private bool _notifySessionEnd;
    [ObservableProperty] private string _exportFolder = "";
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private bool _overlayOnlyDuringGames;
    [ObservableProperty] private string _databaseSize = "";

    public string DataFolder => _paths.Root;
    public string LogsFolder => _paths.LogsDirectory;
    public string ToolsFolder => _paths.ToolsDirectory;
    public string AppToolsFolder => Path.Combine(AppPaths.ApplicationDirectory, "tools");

    public void OnActivated()
    {
        _loading = true;
        var s = _settings.Current;
        StartWithWindows = s.StartWithWindows;
        StartMinimized = s.StartMinimized;
        CloseChoice = CloseBehaviors.First(c => c.Value == s.CloseBehavior);
        ShowTrayIcon = s.ShowTrayIcon;
        Profile = Profiles.First(p => p.Value == s.Profile);
        CaptureFrames = s.CaptureFrames;
        PresentMonPath = s.PresentMonPath;
        AutoDetectStoreGames = s.AutoDetectStoreGames;
        RecordUnconfirmedGames = s.RecordUnconfirmedGames;
        ExitGraceSeconds = s.ExitGraceSeconds;
        MinSessionSeconds = s.MinSessionSeconds;
        AlertsEnabled = s.AlertsEnabled;
        ChannelChoice = Channels.First(c => c.Value == s.AlertChannel);
        HotkeyOverlay = s.Hotkeys.ToggleOverlay;
        HotkeyWindow = s.Hotkeys.ToggleMainWindow;
        HotkeyPause = s.Hotkeys.PauseRecording;
        HotkeyEdit = s.Hotkeys.OverlayEditMode;
        AutoOpenReport = s.AutoOpenReport;
        NotifySessionEnd = s.NotifySessionEnd;
        ExportFolder = s.ExportFolder ?? _paths.DefaultExportDirectory;
        RetentionDays = s.DetailedRetentionDays;
        OverlayEnabled = s.OverlayEnabled;
        OverlayOnlyDuringGames = s.OverlayOnlyDuringGames;
        TemplateToAdd = AlertTemplates[0];
        _loading = false;

        LoadRules();
        RefreshCapture();
        RefreshHotkeyFailures();
        _ = LoadHistoryAsync();
        var db = new FileInfo(_paths.DatabasePath);
        DatabaseSize = db.Exists ? $"{db.Length / 1024.0 / 1024.0:0.0} MB" : Fmt.Missing;
    }

    public void OnDeactivated() => Flush();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Section) or nameof(CaptureStatus) or nameof(CaptureDetail) or nameof(DetectedPresentMon)
                or nameof(DatabaseSize) or nameof(TemplateToAdd))
        {
            return;
        }

        var s = _settings.Current;
        s.StartWithWindows = StartWithWindows;
        s.StartMinimized = StartMinimized;
        s.CloseBehavior = CloseChoice?.Value ?? s.CloseBehavior;
        s.ShowTrayIcon = ShowTrayIcon;
        s.Profile = Profile?.Value ?? s.Profile;
        s.CaptureFrames = CaptureFrames;
        s.PresentMonPath = string.IsNullOrWhiteSpace(PresentMonPath) ? null : PresentMonPath.Trim();
        s.AutoDetectStoreGames = AutoDetectStoreGames;
        s.RecordUnconfirmedGames = RecordUnconfirmedGames;
        s.ExitGraceSeconds = ExitGraceSeconds;
        s.MinSessionSeconds = MinSessionSeconds;
        s.AlertsEnabled = AlertsEnabled;
        s.AlertChannel = ChannelChoice?.Value ?? s.AlertChannel;
        s.Hotkeys.ToggleOverlay = HotkeyOverlay;
        s.Hotkeys.ToggleMainWindow = HotkeyWindow;
        s.Hotkeys.PauseRecording = HotkeyPause;
        s.Hotkeys.OverlayEditMode = HotkeyEdit;
        s.AutoOpenReport = AutoOpenReport;
        s.NotifySessionEnd = NotifySessionEnd;
        s.ExportFolder = string.Equals(ExportFolder, _paths.DefaultExportDirectory, StringComparison.OrdinalIgnoreCase) ? null : ExportFolder;
        s.DetailedRetentionDays = RetentionDays;
        s.OverlayEnabled = OverlayEnabled;
        s.OverlayOnlyDuringGames = OverlayOnlyDuringGames;
        _saveDelay.Stop();
        _saveDelay.Start();

        if (e.PropertyName == nameof(PresentMonPath))
        {
            RefreshCapture();
        }
    }

    private void Flush()
    {
        _saveDelay.Stop();
        try
        {
            _settings.Save();
            if (_rulesDirty)
            {
                _rulesDirty = false;
                _settings.SaveRules(Rules.Select(r => r.Rule));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar configurações");
            _dialogs.Info("Configurações", "Não foi possível salvar: " + ex.Message);
        }

        RefreshHotkeyFailures();
        _main.InvalidateProbe();
    }

    private void RefreshHotkeyFailures()
    {
        HotkeyFailures.Clear();
        foreach (var f in _hotkeys.Failures)
        {
            HotkeyFailures.Add(f);
        }
    }

    // ---------------- Captura ----------------

    [RelayCommand]
    private void RefreshCapture()
    {
        var status = _capture.Probe();
        DetectedPresentMon = _capture.UsingService ? "Incluído na instalação (serviço FRAMEX)" : _capture.Local.LocateExecutable();
        CaptureStatus = Fmt.Capture(status.State);
        CaptureDetail = status.Message + (status.Detail is null ? "" : "\n" + status.Detail);
        if (status.State == CaptureState.Stopped || status.State == CaptureState.Capturing)
        {
            CaptureStatus = "Pronto para capturar";
            CaptureDetail = _capture.UsingService
                ? "O serviço FRAMEX faz a captura com o PresentMon incluído na instalação; não é preciso configurar nada nem executar como administrador."
                : "PresentMon encontrado e privilégios de captura disponíveis.";
        }
    }

    [RelayCommand]
    private void BrowsePresentMon()
    {
        var path = _dialogs.OpenFile("Localizar PresentMon (console)", "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executáveis (*.exe)|*.exe");
        if (path is not null)
        {
            PresentMonPath = path;
        }
    }

    [RelayCommand]
    private void ClearPresentMon() => PresentMonPath = null;

    [RelayCommand]
    private void OpenToolsFolder() => ExportService.RevealInExplorer(_paths.ToolsDirectory);

    [RelayCommand]
    private void OpenPresentMonPage() => OpenUrl("https://github.com/GameTechDev/PresentMon/releases");

    private void OpenUrl(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível abrir o navegador");
            _dialogs.Info("Abrir link", "Abra manualmente no navegador:\n" + url);
        }
    }

    /// <summary>Reinicia o FRAMEX com elevação explícita (UAC). Nunca é feito automaticamente.</summary>
    [RelayCommand]
    private void RestartElevated()
    {
        if (!_dialogs.Confirm("Executar como administrador",
                "Use isto só se o FRAMEX não foi instalado pelo Setup.exe. O Windows pedirá confirmação (UAC). Com privilégios elevados, o FRAMEX pode capturar quadros e, se o driver PawnIO estiver instalado, ler temperatura, clock e consumo da CPU. A sessão atual será encerrada e o FRAMEX reiniciado.",
                "Reiniciar como administrador"))
        {
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--after-restart");
            using (Process.Start(psi))
            {
            }

            _ = ((App)Application.Current).ExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Usuário recusou o UAC: nada a fazer.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao reiniciar elevado");
            _dialogs.Info("Executar como administrador", "Não foi possível reiniciar: " + ex.Message);
        }
    }

    // ---------------- Alertas ----------------

    private void LoadRules()
    {
        Rules.Clear();
        GameChoices.Clear();
        GameChoices.Add(new Choice<Guid?>(null, "Todos os jogos"));
        try
        {
            foreach (var g in _games.GetAll().OrderBy(g => g.Name))
            {
                GameChoices.Add(new Choice<Guid?>(g.Id, g.Name));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao listar jogos para regras de alerta");
        }

        foreach (var rule in _settings.Rules)
        {
            AddRow(CloneRule(rule));
        }
    }

    private static AlertRule CloneRule(AlertRule r) => new()
    {
        Id = r.Id, Name = r.Name, Enabled = r.Enabled, Subject = r.Subject, Metric = r.Metric, Comparison = r.Comparison, Threshold = r.Threshold,
        MinDuration = r.MinDuration, Cooldown = r.Cooldown, GameId = r.GameId, OnlyDuringSessions = r.OnlyDuringSessions,
    };

    private void AddRow(AlertRule rule)
    {
        var row = new AlertRuleRow(rule);
        row.Changed += (_, _) =>
        {
            _rulesDirty = true;
            _saveDelay.Stop();
            _saveDelay.Start();
        };
        Rules.Add(row);
    }

    [RelayCommand]
    private void AddRule()
    {
        if (TemplateToAdd is not { } t)
        {
            return;
        }

        AddRow(new AlertRule
        {
            Name = t.Label.Replace("…", "").Replace(" de", "").Trim(),
            Subject = t.Subject,
            Metric = t.Metric,
            Comparison = t.Comparison,
            Threshold = t.Threshold,
            MinDuration = TimeSpan.FromSeconds(10),
            Cooldown = TimeSpan.FromMinutes(5),
        });
        _rulesDirty = true;
        Flush();
    }

    [RelayCommand]
    private void RemoveRule(AlertRuleRow row)
    {
        Rules.Remove(row);
        _rulesDirty = true;
        Flush();
    }

    [RelayCommand]
    private void RestoreDefaultRules()
    {
        if (!_dialogs.Confirm("Restaurar regras", "Substituir todas as regras de alerta pelas regras iniciais?", "Restaurar"))
        {
            return;
        }

        Rules.Clear();
        foreach (var r in DefaultAlertRules.Create())
        {
            AddRow(r);
        }

        _rulesDirty = true;
        Flush();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var items = await Task.Run(() => _alertHistory.GetRecent(30));
            AlertHistory.Clear();
            foreach (var i in items)
            {
                AlertHistory.Add(i);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao carregar histórico de alertas");
        }
    }

    // ---------------- Dados ----------------

    [RelayCommand]
    private void BrowseExportFolder()
    {
        var folder = _dialogs.PickFolder("Pasta padrão para relatórios exportados", ExportFolder);
        if (folder is not null)
        {
            ExportFolder = folder;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => ExportService.RevealInExplorer(_paths.Root);

    [RelayCommand]
    private void OpenLogsFolder() => ExportService.RevealInExplorer(_paths.LogsDirectory);

    [RelayCommand]
    private void ResetHotkeys()
    {
        var d = new HotkeySettings();
        HotkeyOverlay = d.ToggleOverlay;
        HotkeyWindow = d.ToggleMainWindow;
        HotkeyPause = d.PauseRecording;
        HotkeyEdit = d.OverlayEditMode;
    }
}
