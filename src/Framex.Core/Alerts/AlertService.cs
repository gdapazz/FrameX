using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.GameDetection;
using Framex.Core.Monitoring;
using Framex.Core.Persistence;
using Framex.Core.SessionTracking;
using Microsoft.Extensions.Logging;

namespace Framex.Core.Alerts;

public interface IAlertRuleSource
{
    IReadOnlyList<AlertRule> Rules { get; }
}

/// <summary>Conecta snapshots, quadros e eventos de sessão ao <see cref="AlertEngine"/> e registra o histórico.</summary>
public sealed class AlertService : IDisposable
{
    private readonly IHardwareMonitor _hardware;
    private readonly LiveFrameAggregator _frames;
    private readonly SessionRecorder _recorder;
    private readonly GameDetectionService _detection;
    private readonly IAlertRuleSource _rules;
    private readonly IAlertHistoryRepository _history;
    private readonly ILogger<AlertService> _logger;
    private readonly AlertEngine _engine = new();
    private readonly Lock _gate = new();

    public AlertService(IHardwareMonitor hardware, LiveFrameAggregator frames, SessionRecorder recorder, GameDetectionService detection,
        IAlertRuleSource rules, IAlertHistoryRepository history, ILogger<AlertService> logger)
    {
        _hardware = hardware;
        _frames = frames;
        _recorder = recorder;
        _detection = detection;
        _rules = rules;
        _history = history;
        _logger = logger;
    }

    public bool GloballyEnabled { get; set; } = true;

    public event EventHandler<AlertNotification>? AlertRaised;

    public void Attach()
    {
        _hardware.SnapshotAvailable += OnSnapshot;
        _recorder.CaptureInterrupted += OnCaptureInterrupted;
        _detection.PendingGameDiscovered += OnPendingGame;
    }

    private void OnSnapshot(object? sender, HardwareSnapshot snapshot)
    {
        if (!GloballyEnabled)
        {
            return;
        }

        var game = _recorder.ActiveGame;
        if (game is not null && !game.Game.Profile.AlertsEnabled)
        {
            return;
        }

        IReadOnlyList<AlertNotification> fired;
        lock (_gate)
        {
            var ctx = new AlertContext(snapshot, _frames.Compute(), _recorder.Current is not null, game?.Game.Id, snapshot.Timestamp);
            fired = _engine.Evaluate(_rules.Rules, ctx);
        }

        foreach (var n in fired)
        {
            Publish(n);
        }
    }

    private void OnCaptureInterrupted(object? sender, string message) =>
        RaiseEvent(AlertSubject.CaptureInterrupted, "A captura de quadros foi interrompida: " + message);

    private void OnPendingGame(object? sender, Game game) =>
        RaiseEvent(AlertSubject.UnconfirmedGame, $"\"{game.Name}\" foi detectado automaticamente. Confirme ou corrija a associação na página Jogos.");

    private void RaiseEvent(AlertSubject subject, string message)
    {
        if (!GloballyEnabled)
        {
            return;
        }

        AlertNotification? n;
        lock (_gate)
        {
            n = _engine.RaiseEvent(_rules.Rules, subject, message, _recorder.ActiveGame?.Game.Id, DateTimeOffset.UtcNow);
        }

        if (n is not null)
        {
            Publish(n);
        }
    }

    private void Publish(AlertNotification n)
    {
        var session = _recorder.Current;
        try
        {
            _history.Add(new AlertRecord(n.Timestamp, n.RuleId, n.Title, n.Message, session?.Id, session?.GameName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gravar histórico de alertas");
        }

        _recorder.RecordEvent(SessionEventType.AlertTriggered, n.Severity, $"{n.Title}: {n.Message}");
        _logger.LogInformation("Alerta {Rule}: {Message}", n.RuleId, n.Message);
        AlertRaised?.Invoke(this, n);
    }

    public void Dispose()
    {
        _hardware.SnapshotAvailable -= OnSnapshot;
        _recorder.CaptureInterrupted -= OnCaptureInterrupted;
        _detection.PendingGameDiscovered -= OnPendingGame;
    }
}
