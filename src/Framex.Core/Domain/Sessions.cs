namespace Framex.Core.Domain;

public enum SessionState
{
    Active = 0,
    Completed = 1,
    /// <summary>Sessão recuperada após falha/desligamento: os dados gravados até a interrupção são preservados.</summary>
    Incomplete = 2,
}

public enum CaptureState
{
    NotConfigured,
    Unavailable,
    Starting,
    Capturing,
    WaitingForFrames,
    AccessDenied,
    Stopped,
    Error,
    Disabled,
}

public enum EventSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public enum SessionEventType
{
    SessionStarted = 0,
    SessionEnded = 1,
    ProcessRestarted = 2,
    CaptureStateChanged = 3,
    SensorFailure = 4,
    AlertTriggered = 5,
    MonitoringPaused = 6,
    MonitoringResumed = 7,
    PerformanceDrop = 8,
    DataGap = 9,
    SamplesDropped = 10,
    AdditionalInstance = 11,
    Recovered = 12,
}

public sealed record SessionEvent(DateTimeOffset Timestamp, SessionEventType Type, EventSeverity Severity, string Message);

public sealed class Session
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? GameId { get; init; }
    public string GameName { get; init; } = "";
    public string ExecutableName { get; init; } = "";
    public bool GameConfirmed { get; init; } = true;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public SessionState State { get; set; }
    public string AppVersion { get; init; } = "";
    public string HardwareFingerprint { get; init; } = "";
    public string? Resolution { get; init; }
    public string MonitoringProfile { get; init; } = "";
    public CaptureState InitialCaptureState { get; init; }
    public SessionSummary? Summary { get; set; }

    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

public readonly record struct MetricSample(DateTimeOffset Timestamp, MetricKind Metric, double Value);

/// <summary>Estatística de uma métrica com metodologia explícita.</summary>
public sealed record MetricStatistic(
    MetricKind Metric,
    double? Average,
    double? Minimum,
    double? Maximum,
    int SampleCount,
    double CoveragePercent,
    string Method);

public sealed record DataGap(DateTimeOffset Start, DateTimeOffset End, string Scope)
{
    public TimeSpan Duration => End - Start;
}

public sealed record FrameSummary(
    long FrameCount,
    long InvalidFrameCount,
    double? AverageFps,
    double? MinFps1s,
    double? MaxFps1s,
    double? Low1PercentFps,
    double? Low01PercentFps,
    double? AverageFrameTimeMs,
    double? P50FrameTimeMs,
    double? P95FrameTimeMs,
    double? P99FrameTimeMs,
    double? P999FrameTimeMs,
    double? MaxFrameTimeMs,
    IReadOnlyList<HistogramBin> Distribution,
    IReadOnlyList<string> Notes);

public sealed record HistogramBin(double FromMs, double ToMs, long Count);

public sealed record SessionSummary(
    TimeSpan Duration,
    FrameSummary? Frames,
    IReadOnlyList<MetricStatistic> Metrics,
    IReadOnlyList<DataGap> Gaps,
    IReadOnlyList<string> Limitations,
    DateTimeOffset ComputedAt)
{
    public MetricStatistic? Get(MetricKind kind) => Metrics.FirstOrDefault(m => m.Metric == kind);
}

/// <summary>Linha resumida usada nas listagens e filtros de histórico.</summary>
public sealed record SessionListItem(
    Guid Id,
    Guid? GameId,
    string GameName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionState State,
    double? AverageFps,
    double? Low1PercentFps,
    double? MaxCpuTemp,
    double? MaxGpuTemp,
    bool GameConfirmed)
{
    public TimeSpan Duration => (EndedAt ?? StartedAt) - StartedAt;
}

public sealed record SessionQuery(
    string? Search = null,
    Guid? GameId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    TimeSpan? MinDuration = null,
    double? MinAverageFps = null,
    SessionSort Sort = SessionSort.NewestFirst,
    int Limit = 500);

public enum SessionSort
{
    NewestFirst,
    OldestFirst,
    LongestFirst,
    HighestFps,
    LowestFps,
    HottestGpu,
}
