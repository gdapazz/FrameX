using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Customization;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.GameDetection;
using Framex.Core.Monitoring;
using Framex.Core.Persistence;
using Framex.Core.SessionTracking;

namespace Framex.App.ViewModels;

public sealed partial class StatLine(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private string _value = Fmt.Missing;

    [ObservableProperty]
    private string? _note;
}

/// <summary>Medidor do painel: percentual principal + linhas de detalhe. Percentual nulo = indisponível.</summary>
public sealed partial class GaugeTile(string title, string icon) : ObservableObject
{
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public ObservableCollection<StatLine> Lines { get; } = [];

    [ObservableProperty]
    private double? _percent;

    [ObservableProperty]
    private string _percentText = Fmt.Missing;

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string? _unavailableReason;

    public StatLine Line(string label)
    {
        var line = Lines.FirstOrDefault(l => l.Label == label);
        if (line is null)
        {
            line = new StatLine(label);
            Lines.Add(line);
        }

        return line;
    }
}

public sealed partial class CoreLoadItem(int index) : ObservableObject
{
    public string Label { get; } = (index + 1).ToString();

    [ObservableProperty]
    private double _load;
}

public sealed record DashboardWidgetItem(DashboardWidget Widget, bool Wide);

/// <summary>Série derivada (ex.: °C → °F) sem copiar dados.</summary>
public sealed class ConvertedSeries(IReadOnlyList<double> source, Func<double, double> convert) : IReadOnlyList<double>
{
    public double this[int index] => convert(source[index]);
    public int Count => source.Count;
    public IEnumerator<double> GetEnumerator() => source.Select(convert).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed partial class DashboardViewModel : ObservableObject, IPageActivated
{
    private readonly LiveDataService _live;
    private readonly IHardwareMonitor _monitor;
    private readonly SessionRecorder _recorder;
    private readonly GameDetectionService _detection;
    private readonly IFrameCaptureProvider _capture;
    private readonly IGameRepository _games;
    private readonly ISessionRepository _sessions;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly Navigator _navigator;
    private readonly DialogService _dialogs;

    public DashboardViewModel(LiveDataService live, IHardwareMonitor monitor, SessionRecorder recorder, GameDetectionService detection, IFrameCaptureProvider capture,
        IGameRepository games, ISessionRepository sessions, SettingsService settings, ThemeService theme, Navigator navigator, DialogService dialogs)
    {
        _live = live;
        _monitor = monitor;
        _recorder = recorder;
        _detection = detection;
        _capture = capture;
        _games = games;
        _sessions = sessions;
        _settings = settings;
        _theme = theme;
        _navigator = navigator;
        _dialogs = dialogs;

        Cpu = new GaugeTile("CPU", "I.Hardware");
        Gpu = new GaugeTile("GPU", "I.Monitor");
        Ram = new GaugeTile("RAM", "I.Memory");
        Vram = new GaugeTile("VRAM", "I.Memory");
        CpuTemperatureSeries = new ConvertedSeries(live.CpuTemperature, ToDisplayTemperature);
        GpuTemperatureSeries = new ConvertedSeries(live.GpuTemperature, ToDisplayTemperature);

        live.Tick += (_, _) => Update();
        recorder.SessionCompleted += (_, _) => Ui.Post(() => _ = LoadRecentAsync());
        settings.Changed += (_, _) => Ui.Post(BuildWidgets);
        theme.ThemeApplied += (_, _) => GaugeMode = theme.Active.Gauges;
        GaugeMode = theme.Active.Gauges;
        BuildWidgets();
    }

    public LiveDataService Live => _live;

    public GaugeTile Cpu { get; }
    public GaugeTile Gpu { get; }
    public GaugeTile Ram { get; }
    public GaugeTile Vram { get; }

    public IReadOnlyList<double> CpuTemperatureSeries { get; }
    public IReadOnlyList<double> GpuTemperatureSeries { get; }

    public ObservableCollection<DashboardWidgetItem> Widgets { get; } = [];
    public ObservableCollection<CoreLoadItem> Cores { get; } = [];
    public ObservableCollection<StatLine> SystemInfo { get; } = [];
    public ObservableCollection<SessionListItem> RecentSessions { get; } = [];

    [ObservableProperty]
    private GaugeStyle _gaugeMode;

    [ObservableProperty]
    private int _revision;

    [ObservableProperty]
    private string _fpsText = Fmt.Missing;

    [ObservableProperty]
    private string _frameTimeText = Fmt.Missing;

    [ObservableProperty]
    private string _low1Text = Fmt.Missing;

    [ObservableProperty]
    private string _maxFrameTimeText = Fmt.Missing;

    [ObservableProperty]
    private string _frameStateText = "";

    [ObservableProperty]
    private bool _hasFrames;

    [ObservableProperty]
    private string _temperatureUnit = "°C";

    // Sessão
    [ObservableProperty]
    private bool _sessionActive;

    [ObservableProperty]
    private string _sessionGame = "Nenhum jogo em execução";

    [ObservableProperty]
    private string _sessionExecutable = "";

    [ObservableProperty]
    private string _sessionDuration = "";

    [ObservableProperty]
    private bool _sessionUnconfirmed;

    [ObservableProperty]
    private string _captureText = "";

    [ObservableProperty]
    private bool _paused;

    private double ToDisplayTemperature(double c) => double.IsFinite(c) ? UnitFormatter.ConvertTemperature(c, _settings.Current.TemperatureUnit) : double.NaN;

    public void OnActivated()
    {
        Update();
        BuildSystemInfo();
        _ = LoadRecentAsync();
    }

    private void BuildWidgets()
    {
        var layout = _settings.Current.Dashboard;
        var items = layout.Widgets.Where(w => w.Visible).Select(w => new DashboardWidgetItem(w.Widget, w.Size == WidgetSize.Wide)).ToList();
        if (items.SequenceEqual(Widgets))
        {
            return;
        }

        Widgets.Clear();
        foreach (var i in items)
        {
            Widgets.Add(i);
        }
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            var items = await Task.Run(() => _sessions.Query(new SessionQuery(Limit: 5)));
            RecentSessions.Clear();
            foreach (var i in items)
            {
                RecentSessions.Add(i);
            }
        }
        catch (Exception)
        {
            RecentSessions.Clear();
        }
    }

    private void BuildSystemInfo()
    {
        var inv = _monitor.Inventory;
        SystemInfo.Clear();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SystemInfo.Add(new StatLine(label) { Value = value });
            }
        }

        Add("Processador", inv.FirstName(ComponentKind.Cpu));
        foreach (var gpu in inv.OfKind(ComponentKind.Gpu))
        {
            Add("Placa de vídeo", gpu.Name);
        }

        var total = LiveDataService.Val(_live.Snapshot, MetricKind.RamTotal);
        Add("Memória", total is null ? null : Fmt.Memory(total, _settings.Current, 0));
        Add("Sistema", inv.FirstName(ComponentKind.OperatingSystem));
        foreach (var d in inv.OfKind(ComponentKind.Display).Take(2))
        {
            Add("Monitor", d.Name);
        }

        if (SystemInfo.Count == 0)
        {
            SystemInfo.Add(new StatLine("Inventário") { Value = "Coletando informações do hardware…" });
        }
    }

    private void Update()
    {
        var s = _live.Snapshot;
        var settings = _settings.Current;
        var frames = _live.Frames;
        TemperatureUnit = UnitFormatter.TemperatureSuffix(settings.TemperatureUnit);
        Revision++;

        // Quadros
        HasFrames = frames.Fps is not null;
        FpsText = Fmt.Fps(frames.Fps);
        FrameTimeText = Fmt.Ms(frames.Fps is > 0 ? 1000 / frames.Fps : null);
        Low1Text = Fmt.Fps(frames.Low1PercentFps);
        MaxFrameTimeText = Fmt.Ms(frames.MaxFrameTimeMs);

        var active = _recorder.ActiveGame;
        var capture = _capture.Status;
        if (HasFrames)
        {
            FrameStateText = frames.Low1PercentFps is null ? "1% low aparece após 100 quadros na janela de 30 s." : "Janela de 1 s · 1% low dos últimos 30 s";
        }
        else if (active is null)
        {
            FrameStateText = "FPS é medido apenas enquanto um jogo é monitorado (captura via PresentMon, sem injeção).";
        }
        else
        {
            FrameStateText = capture.Message;
        }

        // CPU
        double? V(MetricKind k) => LiveDataService.Val(s, k);
        Cpu.Percent = V(MetricKind.CpuLoad);
        Cpu.PercentText = Fmt.Percent(Cpu.Percent);
        Cpu.UnavailableReason = Cpu.Percent is null ? s[MetricKind.CpuLoad].Reason : null;
        SetLine(Cpu, "Temperatura", Fmt.Temperature(LiveDataService.CpuTemperatureOf(s), settings), s[MetricKind.CpuTemperaturePackage]);
        SetLine(Cpu, "Clock médio", Fmt.Clock(V(MetricKind.CpuClockAverage)), s[MetricKind.CpuClockAverage]);
        SetLine(Cpu, "Consumo", Fmt.Power(V(MetricKind.CpuPower)), s[MetricKind.CpuPower]);

        // GPU
        Gpu.Percent = V(MetricKind.GpuLoad);
        Gpu.PercentText = Fmt.Percent(Gpu.Percent);
        Gpu.UnavailableReason = Gpu.Percent is null ? s[MetricKind.GpuLoad].Reason : null;
        SetLine(Gpu, "Temperatura", Fmt.Temperature(V(MetricKind.GpuTemperature), settings), s[MetricKind.GpuTemperature]);
        SetLine(Gpu, "Hotspot", Fmt.Temperature(V(MetricKind.GpuHotspot), settings), s[MetricKind.GpuHotspot]);
        SetLine(Gpu, "Clock", Fmt.Clock(V(MetricKind.GpuCoreClock)), s[MetricKind.GpuCoreClock]);
        SetLine(Gpu, "Consumo", Fmt.Power(V(MetricKind.GpuPower)), s[MetricKind.GpuPower]);

        // RAM
        Ram.Percent = V(MetricKind.RamLoad);
        Ram.PercentText = Fmt.Percent(Ram.Percent);
        Ram.UnavailableReason = Ram.Percent is null ? s[MetricKind.RamLoad].Reason : null;
        SetLine(Ram, "Em uso", Fmt.Memory(V(MetricKind.RamUsed), settings), s[MetricKind.RamUsed]);
        SetLine(Ram, "Disponível", Fmt.Memory(V(MetricKind.RamAvailable), settings), s[MetricKind.RamAvailable]);
        SetLine(Ram, "Total", Fmt.Memory(V(MetricKind.RamTotal), settings), s[MetricKind.RamTotal]);
        SetLine(Ram, "Confirmada", Fmt.Memory(V(MetricKind.RamCommitted), settings), s[MetricKind.RamCommitted]);

        // VRAM
        Vram.Percent = LiveDataService.VramPercent(s);
        Vram.PercentText = Fmt.Percent(Vram.Percent);
        Vram.UnavailableReason = Vram.Percent is null ? s[MetricKind.VramUsed].Reason ?? "VRAM não informada pelo driver." : null;
        SetLine(Vram, "Em uso", Fmt.Memory(V(MetricKind.VramUsed), settings), s[MetricKind.VramUsed]);
        SetLine(Vram, "Total", Fmt.Memory(V(MetricKind.VramTotal), settings), s[MetricKind.VramTotal]);
        SetLine(Vram, "Compartilhada", Fmt.Memory(V(MetricKind.VramSharedUsed), settings), s[MetricKind.VramSharedUsed]);

        // Núcleos
        var loads = s.PerCoreLoad;
        if (Cores.Count != loads.Count)
        {
            Cores.Clear();
            for (var i = 0; i < loads.Count; i++)
            {
                Cores.Add(new CoreLoadItem(i));
            }
        }

        for (var i = 0; i < loads.Count; i++)
        {
            Cores[i].Load = loads[i];
        }

        // Sessão
        SessionActive = active is not null;
        Paused = _recorder.IsPaused;
        if (active is null)
        {
            SessionGame = "Nenhum jogo em execução";
            SessionExecutable = "O FRAMEX inicia uma sessão automaticamente quando detecta um jogo.";
            SessionDuration = "";
            SessionUnconfirmed = false;
            CaptureText = "";
        }
        else
        {
            SessionGame = active.Game.Name;
            SessionExecutable = $"{active.Process.ExecutableName} · PID {active.Process.ProcessId}";
            var started = _recorder.Current?.StartedAt ?? active.DetectedAt;
            SessionDuration = Fmt.Duration(DateTimeOffset.UtcNow - started);
            SessionUnconfirmed = !active.Confirmed;
            CaptureText = Fmt.Capture(capture.State) + " — " + capture.Message;
        }

        if (SystemInfo.Count <= 1 && _monitor.Inventory.Components.Count > 0)
        {
            BuildSystemInfo();
        }
    }

    private static void SetLine(GaugeTile tile, string label, string value, MetricValue source)
    {
        var line = tile.Line(label);
        line.Value = value;
        line.Note = source.HasValue ? null : source.Availability == SensorAvailability.Stale ? "Leitura desatualizada" : source.Reason;
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_recorder.IsPaused)
        {
            _recorder.Resume();
        }
        else
        {
            _recorder.Pause();
        }

        Paused = _recorder.IsPaused;
    }

    [RelayCommand]
    private void EndSession()
    {
        if (_recorder.ActiveGame is null)
        {
            return;
        }

        if (_dialogs.Confirm("Encerrar sessão", "A sessão atual será finalizada e o relatório gerado. Se o jogo continuar aberto, uma nova sessão só começará quando ele for reiniciado ou detectado novamente.", "Encerrar"))
        {
            _detection.EndCurrent("Sessão encerrada manualmente pelo usuário.");
        }
    }

    [RelayCommand]
    private async Task ConfirmGame()
    {
        var active = _recorder.ActiveGame;
        if (active is null)
        {
            return;
        }

        var name = _dialogs.Prompt("Confirmar jogo", $"Confirme o nome do jogo associado a {active.Process.ExecutableName}.", active.Game.Name);
        if (name is null)
        {
            return;
        }

        var game = active.Game;
        game.Name = name;
        game.Status = GameStatus.Confirmed;
        await Task.Run(() => _games.Upsert(game));
        _detection.InvalidateLibrary();
    }

    [RelayCommand]
    private void OpenSession(SessionListItem item) => _navigator.OpenReport(item.Id);

    [RelayCommand]
    private void OpenReports() => _navigator.Go(PageKey.Reports);

    [RelayCommand]
    private void OpenHardware() => _navigator.Go(PageKey.Hardware);

    [RelayCommand]
    private void CustomizeDashboard() => _navigator.Go(PageKey.Customization);
}
