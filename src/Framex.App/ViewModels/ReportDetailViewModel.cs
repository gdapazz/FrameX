using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Controls;
using Framex.App.Services;
using Framex.Core.Domain;
using Framex.Core.Monitoring;
using Framex.Core.Persistence;
using Framex.Core.Reporting;
using Microsoft.Extensions.Logging;

namespace Framex.App.ViewModels;

public sealed record StatCard(string Label, string Value, string? Hint = null, bool Accent = false);

public sealed record MetricRow(string Label, string Average, string Minimum, string Maximum, string Coverage, string Method);

public sealed record EventRow(double Offset, string Time, string Type, string Message, EventSeverity Severity);

public sealed class ChartGroup(string title, IReadOnlyList<ChartSeries> series, double yMin, double yMax, double gapSeconds)
{
    public string Title { get; } = title;
    public IReadOnlyList<ChartSeries> Series { get; } = series;
    public double YMin { get; } = yMin;
    public double YMax { get; } = yMax;
    public double GapSeconds { get; } = gapSeconds;
    public bool HasData => Series.Any(s => s.X.Length > 0);
}

public sealed partial class ReportDetailViewModel : ObservableObject
{
    private readonly ExportService _export;
    private readonly ISessionRepository _sessions;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly Navigator _navigator;
    private readonly ILogger<ReportDetailViewModel> _logger;
    private SessionReport? _report;
    private double[] _fpsX = [];
    private double[] _fpsY = [];

    public ReportDetailViewModel(ExportService export, ISessionRepository sessions, SettingsService settings, DialogService dialogs, Navigator navigator, ILogger<ReportDetailViewModel> logger)
    {
        _export = export;
        _sessions = sessions;
        _settings = settings;
        _dialogs = dialogs;
        _navigator = navigator;
        _logger = logger;
    }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private bool _incomplete;

    [ObservableProperty]
    private bool _unconfirmed;

    [ObservableProperty]
    private bool _hasFrames;

    [ObservableProperty]
    private double _totalSeconds;

    [ObservableProperty]
    private double _viewStart;

    [ObservableProperty]
    private double _viewEnd;

    [ObservableProperty]
    private double _hoverX = double.NaN;

    [ObservableProperty]
    private string _rangeText = "Sessão completa";

    [ObservableProperty]
    private string? _rangeStats;

    [ObservableProperty]
    private IReadOnlyList<HistogramBin>? _distribution;

    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<StatCard> Conditions { get; } = [];
    public ObservableCollection<ChartGroup> Charts { get; } = [];
    public ObservableCollection<MetricRow> Metrics { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<string> Gaps { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    public IReadOnlyList<ChartMarker> Markers { get; private set; } = [];

    partial void OnViewStartChanged(double value) => UpdateRange();

    partial void OnViewEndChanged(double value) => UpdateRange();

    public void Load(Guid sessionId) => _ = LoadAsync(sessionId);

    private async Task LoadAsync(Guid sessionId)
    {
        IsLoading = true;
        try
        {
            _report = await Task.Run(() => _export.LoadReport(sessionId));
            if (_report is null)
            {
                Error = "Sessão não encontrada (pode ter sido excluída).";
                return;
            }

            Build(_report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao abrir o relatório {Session}", sessionId);
            Error = "Não foi possível abrir o relatório: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Build(SessionReport report)
    {
        var s = report.Session;
        var summary = report.Summary;
        var settings = _settings.Current;
        var end = s.EndedAt ?? report.Samples.Select(x => x.Timestamp).DefaultIfEmpty(s.StartedAt).Max();
        TotalSeconds = Math.Max(1, (end - s.StartedAt).TotalSeconds);

        Title = s.GameName;
        Subtitle = $"{Fmt.LocalTime(s.StartedAt)} · {Fmt.Duration(end - s.StartedAt)} · {s.ExecutableName}";
        Incomplete = s.State == SessionState.Incomplete;
        Unconfirmed = !s.GameConfirmed;

        var f = summary?.Frames;
        HasFrames = f is { FrameCount: > 0 };
        Cards.Clear();
        Cards.Add(new StatCard("FPS médio", Fmt.Number(f?.AverageFps, "0.0"), "Média harmônica: total de quadros ÷ tempo total dos quadros.", true));
        Cards.Add(new StatCard("1% low", Fmt.Number(f?.Low1PercentFps, "0.0"), "1000 ÷ média do 1% de frametimes mais longos (exige ≥ 100 quadros)."));
        Cards.Add(new StatCard("0,1% low", Fmt.Number(f?.Low01PercentFps, "0.0"), "1000 ÷ média do 0,1% de frametimes mais longos (exige ≥ 1000 quadros)."));
        Cards.Add(new StatCard("Frametime médio", Fmt.Ms(f?.AverageFrameTimeMs)));
        Cards.Add(new StatCard("Frametime P99", Fmt.Ms(f?.P99FrameTimeMs), "99% dos quadros foram mais rápidos que este valor (interpolação linear)."));
        Cards.Add(new StatCard("Pior frametime", Fmt.Ms(f?.MaxFrameTimeMs)));
        Cards.Add(new StatCard("FPS mín./máx. (1 s)", f is null ? Fmt.Missing : $"{Fmt.Number(f.MinFps1s)} / {Fmt.Number(f.MaxFps1s)}", "Calculado sobre janelas completas de 1 segundo."));
        Cards.Add(new StatCard("Quadros", f is null ? Fmt.Missing : f.FrameCount.ToString("N0"), f is { InvalidFrameCount: > 0 } ? $"{f.InvalidFrameCount:N0} quadros inválidos descartados." : null));
        Distribution = f?.Distribution;

        Conditions.Clear();
        Conditions.Add(new StatCard("Resolução (monitor principal)", s.Resolution ?? Fmt.Missing));
        Conditions.Add(new StatCard("Perfil de monitoramento", string.IsNullOrEmpty(s.MonitoringProfile) ? Fmt.Missing : s.MonitoringProfile));
        Conditions.Add(new StatCard("Captura no início", Fmt.Capture(s.InitialCaptureState)));
        Conditions.Add(new StatCard("Versão do FRAMEX", s.AppVersion));
        Conditions.Add(new StatCard("Hardware", s.HardwareFingerprint));

        BuildCharts(report, settings);
        BuildMetrics(summary, settings);
        BuildEvents(report);

        Gaps.Clear();
        foreach (var g in summary?.Gaps ?? [])
        {
            Gaps.Add($"{ChartText.Time((g.Start - s.StartedAt).TotalSeconds)} — {Fmt.Duration(g.Duration)} sem dados ({g.Scope})");
        }

        Notes.Clear();
        foreach (var n in (summary?.Limitations ?? []).Concat(f?.Notes ?? []))
        {
            Notes.Add(n);
        }

        if (summary is null)
        {
            Notes.Add("Resumo ainda não calculado (sessão em andamento).");
        }

        ViewStart = 0;
        ViewEnd = 0;
        UpdateRange();
    }

    private void BuildCharts(SessionReport report, Core.Customization.AppSettings settings)
    {
        var start = report.Session.StartedAt;
        var byMetric = report.Samples.GroupBy(x => x.Metric).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Timestamp).ToArray());
        Brush B(int i) => ThemeService.GetBrush("B.Chart" + i);

        ChartSeries? Series(MetricKind kind, string name, int color, Func<double, double>? convert = null, string format = "0.#")
        {
            if (!byMetric.TryGetValue(kind, out var samples) || samples.Length == 0)
            {
                return null;
            }

            var x = new double[samples.Length];
            var y = new double[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                x[i] = (samples[i].Timestamp - start).TotalSeconds;
                y[i] = convert is null ? samples[i].Value : convert(samples[i].Value);
            }

            var unit = MetricCatalog.Get(kind).Unit switch
            {
                MetricUnit.Celsius => UnitFormatter.TemperatureSuffix(settings.TemperatureUnit),
                MetricUnit.Megabyte => UnitFormatter.MemorySuffix(settings.MemoryUnits),
                _ => UnitFormatter.UnitSuffix(MetricCatalog.Get(kind).Unit).Trim(),
            };
            return new ChartSeries(name, B(color), x, y, unit, format);
        }

        static List<ChartSeries> Present(params ChartSeries?[] s) => s.Where(x => x is not null).Cast<ChartSeries>().ToList();

        double Temp(double c) => UnitFormatter.ConvertTemperature(c, settings.TemperatureUnit);
        double Mem(double mib) => UnitFormatter.MebibytesToGigabytes(mib, settings.MemoryUnits);
        var hwGap = Math.Max(5, report.Samples.Count > 0 ? 4 : 5);

        var fps = Series(MetricKind.Fps, "FPS", 0, format: "0");
        _fpsX = fps?.X ?? [];
        _fpsY = fps?.Y ?? [];

        Charts.Clear();
        Charts.Add(new ChartGroup("FPS por segundo", Present(fps), 0, double.NaN, 2.5));
        Charts.Add(new ChartGroup("Frametime (ms)", Present(Series(MetricKind.FrameTimeMax, "Pior quadro", 3, format: "0.0"), Series(MetricKind.FrameTimeAvg, "Médio", 0, format: "0.0")), 0, double.NaN, 2.5));
        Charts.Add(new ChartGroup("Uso de CPU e GPU (%)", Present(Series(MetricKind.CpuLoad, "CPU", 0, format: "0"), Series(MetricKind.GpuLoad, "GPU", 2, format: "0")), 0, 100, hwGap));
        Charts.Add(new ChartGroup("Temperaturas", Present(
            Series(MetricKind.CpuTemperaturePackage, "CPU", 0, Temp, "0"),
            Series(MetricKind.GpuTemperature, "GPU", 2, Temp, "0"),
            Series(MetricKind.GpuHotspot, "Hotspot GPU", 3, Temp, "0")), double.NaN, double.NaN, hwGap));
        Charts.Add(new ChartGroup("Memória", Present(
            Series(MetricKind.RamUsed, "RAM", 1, Mem, "0.0"),
            Series(MetricKind.VramUsed, "VRAM", 2, Mem, "0.0")), 0, double.NaN, hwGap));
        Charts.Add(new ChartGroup("Clocks (MHz)", Present(
            Series(MetricKind.CpuClockAverage, "CPU", 0, format: "0"),
            Series(MetricKind.GpuCoreClock, "GPU", 2, format: "0"),
            Series(MetricKind.GpuMemoryClock, "Memória GPU", 4, format: "0")), 0, double.NaN, hwGap));
        Charts.Add(new ChartGroup("Consumo (W)", Present(
            Series(MetricKind.CpuPower, "CPU", 0, format: "0.0"),
            Series(MetricKind.GpuPower, "GPU", 2, format: "0.0")), 0, double.NaN, hwGap));
    }

    private void BuildMetrics(SessionSummary? summary, Core.Customization.AppSettings settings)
    {
        Metrics.Clear();
        foreach (var m in summary?.Metrics ?? [])
        {
            if (!MetricCatalog.TryGet(m.Metric, out var d) || d.Category == MetricCategory.Frames)
            {
                continue;
            }

            Metrics.Add(new MetricRow(d.Label, Fmt.Metric(m.Metric, m.Average, settings), Fmt.Metric(m.Metric, m.Minimum, settings), Fmt.Metric(m.Metric, m.Maximum, settings),
                m.SampleCount == 0 ? "sem dados" : $"{m.CoveragePercent:0}% ({m.SampleCount:N0} amostras)", m.Method));
        }
    }

    private void BuildEvents(SessionReport report)
    {
        var start = report.Session.StartedAt;
        Events.Clear();
        var markers = new List<ChartMarker>();
        foreach (var e in report.Events.OrderBy(e => e.Timestamp))
        {
            var offset = (e.Timestamp - start).TotalSeconds;
            Events.Add(new EventRow(offset, ChartText.Time(offset), EventLabel(e.Type), e.Message, e.Severity));
            if (e.Type is not (SessionEventType.SessionStarted or SessionEventType.SessionEnded))
            {
                markers.Add(new ChartMarker(offset, EventLabel(e.Type) + ": " + e.Message,
                    ThemeService.GetBrush(e.Severity switch { EventSeverity.Error => "B.Error", EventSeverity.Warning => "B.Warning", _ => "B.Accent" })));
            }
        }

        Markers = markers;
        OnPropertyChanged(nameof(Markers));
    }

    public static string EventLabel(SessionEventType t) => t switch
    {
        SessionEventType.SessionStarted => "Início",
        SessionEventType.SessionEnded => "Fim",
        SessionEventType.ProcessRestarted => "Processo reiniciado",
        SessionEventType.CaptureStateChanged => "Captura",
        SessionEventType.SensorFailure => "Falha de sensor",
        SessionEventType.AlertTriggered => "Alerta",
        SessionEventType.MonitoringPaused => "Pausado",
        SessionEventType.MonitoringResumed => "Retomado",
        SessionEventType.PerformanceDrop => "Queda de desempenho",
        SessionEventType.DataGap => "Lacuna de dados",
        SessionEventType.SamplesDropped => "Amostras descartadas",
        SessionEventType.AdditionalInstance => "Instância adicional",
        SessionEventType.Recovered => "Recuperada",
        _ => t.ToString(),
    };

    private void UpdateRange()
    {
        var full = ViewEnd <= ViewStart;
        var a = full ? 0 : ViewStart;
        var b = full ? TotalSeconds : ViewEnd;
        RangeText = full ? "Sessão completa (role para ampliar · Shift+arrastar seleciona · duplo clique restaura)" : $"Intervalo {ChartText.Time(a)} – {ChartText.Time(b)} ({Fmt.Duration(TimeSpan.FromSeconds(b - a))})";

        var values = new List<double>();
        for (var i = 0; i < _fpsX.Length; i++)
        {
            if (_fpsX[i] >= a && _fpsX[i] <= b && double.IsFinite(_fpsY[i]))
            {
                values.Add(_fpsY[i]);
            }
        }

        RangeStats = values.Count == 0
            ? null
            : $"No intervalo: FPS por segundo — média {values.Average():0.0}, mínimo {values.Min():0}, máximo {values.Max():0} ({values.Count} s com quadros)";
    }

    [RelayCommand]
    private void FocusEvent(EventRow row)
    {
        var span = Math.Min(TotalSeconds, 60);
        var start = Math.Clamp(row.Offset - (span / 2), 0, Math.Max(0, TotalSeconds - span));
        ViewStart = start;
        ViewEnd = start + span;
        HoverX = row.Offset;
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ViewStart = 0;
        ViewEnd = 0;
    }

    [RelayCommand]
    private void Back() => _navigator.Go(PageKey.Reports);

    [RelayCommand]
    private async Task Export(string format)
    {
        if (_report is not null)
        {
            await _export.ExportAsync(_report, Enum.Parse<ExportFormat>(format));
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (_report is null || _report.Session.State == SessionState.Active)
        {
            return;
        }

        if (!_dialogs.Confirm("Excluir sessão", "Excluir permanentemente esta sessão e todos os seus dados?", "Excluir", danger: true))
        {
            return;
        }

        try
        {
            var id = _report.Session.Id;
            await Task.Run(() => _sessions.Delete([id]));
            _navigator.Go(PageKey.Reports);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao excluir sessão");
            _dialogs.Info("Excluir sessão", "Não foi possível excluir: " + ex.Message);
        }
    }
}
