using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.GameDetection;
using Framex.Core.Monitoring;
using Framex.Core.Persistence;
using Framex.Core.Reporting;
using Microsoft.Extensions.Logging;

namespace Framex.Core.SessionTracking;

public interface ISessionEnvironment
{
    string AppVersion { get; }

    /// <summary>Resolução do monitor principal no formato "1920×1080", quando conhecida.</summary>
    string? PrimaryResolution();
}

public sealed class SessionRecorderOptions
{
    public bool CaptureFrames { get; set; } = true;
    public TimeSpan MinSessionDuration { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
    public string MonitoringProfileName { get; set; } = "Equilibrado";
    public FrameCaptureOptions Capture { get; set; } = new();
    public int MaxBufferedFrames { get; set; } = 600_000;
    public int MaxBufferedSamples { get; set; } = 200_000;
}

/// <summary>
/// Grava sessões de jogo: cria a sessão quando o jogo é detectado, acumula amostras em buffers limitados,
/// persiste em lotes periódicos (transações) e calcula o resumo no encerramento.
/// </summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private static readonly HashSet<MetricKind> SlowChangingMetrics = [MetricKind.RamTotal, MetricKind.VramTotal, MetricKind.RamCommitLimit];

    private readonly IHardwareMonitor _hardware;
    private readonly IFrameCaptureProvider _capture;
    private readonly GameDetectionService _detection;
    private readonly ISessionRepository _sessions;
    private readonly LiveFrameAggregator _live;
    private readonly ISessionEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionRecorder> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _gate = new();
    private readonly SecondBucketAggregator _buckets = new();
    private readonly Dictionary<MetricKind, double> _lastSlowValues = new();

    private List<MetricSample> _samples = new(4096);
    private List<FrameSample> _frames = new(8192);
    private List<SessionEvent> _events = new(32);
    private long _droppedFrames;
    private long _droppedSamples;
    private bool _persistFailureReported;

    private Session? _current;
    private ActiveGame? _activeGame;
    private CaptureState _lastCaptureState = CaptureState.Stopped;
    private CancellationTokenSource? _flushCts;
    private Task? _flushLoop;
    private volatile bool _paused;
    private int _disposed;

    public SessionRecorder(
        IHardwareMonitor hardware,
        IFrameCaptureProvider capture,
        GameDetectionService detection,
        ISessionRepository sessions,
        LiveFrameAggregator live,
        ISessionEnvironment environment,
        SessionRecorderOptions options,
        TimeProvider time,
        ILogger<SessionRecorder> logger)
    {
        _hardware = hardware;
        _capture = capture;
        _detection = detection;
        _sessions = sessions;
        _live = live;
        _environment = environment;
        Options = options;
        _time = time;
        _logger = logger;
    }

    public SessionRecorderOptions Options { get; }

    public Session? Current { get { lock (_gate) { return _current; } } }

    public ActiveGame? ActiveGame { get { lock (_gate) { return _activeGame; } } }

    public bool IsPaused => _paused;

    public event EventHandler<Session>? SessionStarted;
    public event EventHandler<Session>? SessionCompleted;
    public event EventHandler<Session>? SessionDiscarded;
    public event EventHandler<string>? CaptureInterrupted;

    public void Attach()
    {
        _detection.GameStarted += OnGameStarted;
        _detection.GameEnded += OnGameEnded;
        _detection.GameProcessChanged += OnGameProcessChanged;
        _hardware.SnapshotAvailable += OnSnapshot;
        _capture.FramesReceived += OnFrames;
        _capture.StatusChanged += OnCaptureStatus;

        _flushCts = new CancellationTokenSource();
        var token = _flushCts.Token;
        _flushLoop = Task.Run(() => FlushLoopAsync(token), token);
    }

    /// <summary>Finaliza sessões que ficaram "ativas" por falha/desligamento, marcando-as como incompletas.</summary>
    public int RecoverInterruptedSessions()
    {
        var recovered = 0;
        foreach (var session in _sessions.GetActive())
        {
            try
            {
                var end = _sessions.GetLastDataTimestamp(session.Id) ?? session.StartedAt;
                var recoveredEvent = new SessionEvent(end, SessionEventType.Recovered, EventSeverity.Warning,
                    "Sessão interrompida inesperadamente (falha ou desligamento). Dados recuperados até a última gravação.");
                _sessions.Append(session.Id, [], [], [recoveredEvent]);
                var analysis = Analyze(session, end);
                _sessions.Append(session.Id, [], [], analysis.DetectedEvents);
                _sessions.Complete(session.Id, end, SessionState.Incomplete, analysis.Summary);
                recovered++;
                _logger.LogWarning("Sessão {Session} de {Game} recuperada como incompleta", session.Id, session.GameName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao recuperar a sessão {Session}", session.Id);
            }
        }

        return recovered;
    }

    public void Pause()
    {
        if (_paused)
        {
            return;
        }

        _paused = true;
        AddEvent(SessionEventType.MonitoringPaused, EventSeverity.Info, "Gravação pausada pelo usuário.");
        _ = _capture.StopAsync();
        _live.Reset();
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        AddEvent(SessionEventType.MonitoringResumed, EventSeverity.Info, "Gravação retomada.");
        var active = ActiveGame;
        if (active is not null)
        {
            _ = StartCaptureAsync(active);
        }
    }

    private void OnGameStarted(object? sender, GameStartedEventArgs e) => _ = BeginSessionAsync(e.Active);

    private void OnGameEnded(object? sender, GameEndedEventArgs e) => _ = EndSessionAsync(e.EndedAt, e.Reason);

    private void OnGameProcessChanged(object? sender, GameProcessChangedEventArgs e)
    {
        var restarted = e.Message.StartsWith("Processo reiniciado", StringComparison.Ordinal);
        AddEvent(restarted ? SessionEventType.ProcessRestarted : SessionEventType.AdditionalInstance, EventSeverity.Info, e.Message);
        lock (_gate)
        {
            _activeGame = e.Active;
        }

        if (restarted && !_paused)
        {
            _ = RestartCaptureAsync(e.Active);
        }
    }

    private async Task BeginSessionAsync(ActiveGame active)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var session = new Session
            {
                GameId = active.Game.Id,
                GameName = active.Game.Name,
                ExecutableName = active.Process.ExecutableName,
                GameConfirmed = active.Confirmed,
                StartedAt = now,
                State = SessionState.Active,
                AppVersion = _environment.AppVersion,
                HardwareFingerprint = _hardware.Inventory.Fingerprint(),
                Resolution = _environment.PrimaryResolution(),
                MonitoringProfile = Options.MonitoringProfileName,
                InitialCaptureState = _capture.Probe().State,
            };

            _sessions.Create(session);
            lock (_gate)
            {
                _current = session;
                _activeGame = active;
                _lastSlowValues.Clear();
                _droppedFrames = 0;
                _droppedSamples = 0;
            }

            _live.Reset();
            AddEvent(SessionEventType.SessionStarted, EventSeverity.Info,
                $"Sessão iniciada: {session.GameName} ({session.ExecutableName}){(active.Confirmed ? "" : " — jogo ainda não confirmado")}.");
            _logger.LogInformation("Sessão {Session} iniciada para {Game}", session.Id, session.GameName);
            SessionStarted?.Invoke(this, session);

            if (!_paused)
            {
                await StartCaptureAsync(active).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao iniciar a sessão de {Game}", active.Game.Name);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StartCaptureAsync(ActiveGame active)
    {
        if (!Options.CaptureFrames || !active.Game.Profile.CaptureFrames)
        {
            AddEvent(SessionEventType.CaptureStateChanged, EventSeverity.Info, "Captura de quadros desativada nas configurações para este jogo.");
            return;
        }

        try
        {
            await _capture.StartAsync(active.Process.ProcessId, active.Process.ExecutableName, Options.Capture, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao iniciar a captura de quadros");
            AddEvent(SessionEventType.CaptureStateChanged, EventSeverity.Error, "Falha ao iniciar a captura de quadros: " + ex.Message);
        }
    }

    private async Task RestartCaptureAsync(ActiveGame active)
    {
        await _capture.StopAsync().ConfigureAwait(false);
        await StartCaptureAsync(active).ConfigureAwait(false);
    }

    private async Task EndSessionAsync(DateTimeOffset endedAt, string reason)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = Current;
            if (session is null)
            {
                return;
            }

            await _capture.StopAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _buckets.Flush(_samples);
            }

            AddEvent(SessionEventType.SessionEnded, EventSeverity.Info, reason, endedAt);
            Flush();

            lock (_gate)
            {
                _current = null;
                _activeGame = null;
            }

            _live.Reset();

            if (endedAt - session.StartedAt < Options.MinSessionDuration)
            {
                _sessions.Delete([session.Id]);
                _logger.LogInformation("Sessão {Session} descartada por ser mais curta que {Min}", session.Id, Options.MinSessionDuration);
                SessionDiscarded?.Invoke(this, session);
                return;
            }

            var analysis = Analyze(session, endedAt);
            _sessions.Append(session.Id, [], [], analysis.DetectedEvents);
            _sessions.Complete(session.Id, endedAt, SessionState.Completed, analysis.Summary);
            session.EndedAt = endedAt;
            session.State = SessionState.Completed;
            session.Summary = analysis.Summary;
            _logger.LogInformation("Sessão {Session} concluída ({Duration})", session.Id, endedAt - session.StartedAt);
            SessionCompleted?.Invoke(this, session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao finalizar a sessão; ela será recuperada na próxima inicialização");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private SessionAnalysis Analyze(Session session, DateTimeOffset end)
    {
        var samples = _sessions.GetSamples(session.Id);
        var frames = _sessions.GetFrameTimes(session.Id);
        return SessionStatisticsCalculator.Analyze(session.StartedAt, end, samples, frames, _hardware.Options.PollInterval, _time.GetUtcNow());
    }

    private void OnSnapshot(object? sender, HardwareSnapshot snapshot)
    {
        if (_paused)
        {
            return;
        }

        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            foreach (var (kind, value) in snapshot.Metrics)
            {
                if (!value.HasValue || MetricCatalog.Get(kind).Category == MetricCategory.Frames)
                {
                    continue;
                }

                var v = value.Value!.Value;
                if (SlowChangingMetrics.Contains(kind))
                {
                    if (_lastSlowValues.TryGetValue(kind, out var last) && Math.Abs(last - v) < 0.5)
                    {
                        continue;
                    }

                    _lastSlowValues[kind] = v;
                }

                _samples.Add(new MetricSample(snapshot.Timestamp, kind, v));
            }

            TrimSamples();
        }
    }

    private void OnFrames(object? sender, IReadOnlyList<FrameSample> batch)
    {
        if (_paused)
        {
            return;
        }

        _live.Add(batch);
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            foreach (var f in batch)
            {
                _frames.Add(f);
                _buckets.Add(f, _samples);
            }

            if (_frames.Count > Options.MaxBufferedFrames)
            {
                var excess = _frames.Count - Options.MaxBufferedFrames;
                _frames.RemoveRange(0, excess);
                _droppedFrames += excess;
            }

            TrimSamples();
        }
    }

    private void TrimSamples()
    {
        if (_samples.Count > Options.MaxBufferedSamples)
        {
            var excess = _samples.Count - Options.MaxBufferedSamples;
            _samples.RemoveRange(0, excess);
            _droppedSamples += excess;
        }
    }

    private void OnCaptureStatus(object? sender, CaptureStatus status)
    {
        CaptureState previous;
        lock (_gate)
        {
            previous = _lastCaptureState;
            _lastCaptureState = status.State;
        }

        if (Current is null || previous == status.State)
        {
            return;
        }

        var severity = status.State is CaptureState.Error or CaptureState.AccessDenied or CaptureState.Unavailable ? EventSeverity.Warning : EventSeverity.Info;
        AddEvent(SessionEventType.CaptureStateChanged, severity, status.Message);
        if (previous == CaptureState.Capturing && status.State is CaptureState.Error or CaptureState.Stopped && !_paused)
        {
            CaptureInterrupted?.Invoke(this, status.Message);
        }
    }

    /// <summary>Registra um evento externo (ex.: alerta) na linha do tempo da sessão atual, se houver.</summary>
    public void RecordEvent(SessionEventType type, EventSeverity severity, string message) => AddEvent(type, severity, message);

    private void AddEvent(SessionEventType type, EventSeverity severity, string message, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            _events.Add(new SessionEvent(at ?? _time.GetUtcNow(), type, severity, message));
        }
    }

    /// <summary>Persiste os buffers pendentes em uma transação. Em caso de falha, os dados retornam ao buffer.</summary>
    public void Flush()
    {
        Guid sessionId;
        List<MetricSample> samples;
        List<FrameSample> frames;
        List<SessionEvent> events;
        lock (_gate)
        {
            if (_current is null || (_samples.Count == 0 && _frames.Count == 0 && _events.Count == 0))
            {
                return;
            }

            if (_droppedFrames > 0 || _droppedSamples > 0)
            {
                _events.Add(new SessionEvent(_time.GetUtcNow(), SessionEventType.SamplesDropped, EventSeverity.Warning,
                    $"Buffer cheio: {_droppedFrames} quadro(s) e {_droppedSamples} amostra(s) descartados (persistência atrasada)."));
                _droppedFrames = 0;
                _droppedSamples = 0;
            }

            sessionId = _current.Id;
            samples = _samples;
            frames = _frames;
            events = _events;
            _samples = new List<MetricSample>(Math.Max(1024, samples.Count));
            _frames = new List<FrameSample>(Math.Max(1024, frames.Count));
            _events = new List<SessionEvent>(16);
        }

        try
        {
            _sessions.Append(sessionId, samples, frames, events);
            _persistFailureReported = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao persistir lote da sessão {Session}; os dados serão retentados", sessionId);
            lock (_gate)
            {
                samples.AddRange(_samples);
                frames.AddRange(_frames);
                events.AddRange(_events);
                _samples = samples;
                _frames = frames;
                _events = events;
                if (!_persistFailureReported)
                {
                    _events.Add(new SessionEvent(_time.GetUtcNow(), SessionEventType.DataGap, EventSeverity.Error, "Falha temporária ao gravar no banco de dados."));
                    _persistFailureReported = true;
                }

                TrimSamples();
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Options.FlushInterval, _time);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no laço de persistência");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _detection.GameStarted -= OnGameStarted;
        _detection.GameEnded -= OnGameEnded;
        _detection.GameProcessChanged -= OnGameProcessChanged;
        _hardware.SnapshotAvailable -= OnSnapshot;
        _capture.FramesReceived -= OnFrames;
        _capture.StatusChanged -= OnCaptureStatus;

        if (Current is not null)
        {
            await EndSessionAsync(_time.GetUtcNow(), "FRAMEX encerrado durante a sessão.").ConfigureAwait(false);
        }

        if (_flushCts is not null)
        {
            await _flushCts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_flushLoop is not null)
                {
                    await _flushLoop.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento esperado.
            }

            _flushCts.Dispose();
        }

        _lifecycle.Dispose();
    }
}
