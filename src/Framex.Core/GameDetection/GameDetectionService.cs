using Framex.Core.Domain;
using Framex.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Framex.Core.GameDetection;

public sealed class GameDetectionOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Tempo de espera após o encerramento do processo antes de finalizar a sessão (reinícios rápidos continuam a mesma sessão).</summary>
    public TimeSpan ExitGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    public bool AutoDetectStoreGames { get; set; } = true;

    /// <summary>Se verdadeiro, jogos detectados por heurística são gravados (marcados como não confirmados) até o usuário decidir.</summary>
    public bool RecordUnconfirmedGames { get; set; } = true;
}

public sealed record ActiveGame(Game Game, ProcessInfo Process, bool Confirmed, DateTimeOffset DetectedAt);

public sealed class GameStartedEventArgs(ActiveGame active) : EventArgs
{
    public ActiveGame Active { get; } = active;
}

public sealed class GameEndedEventArgs(ActiveGame active, DateTimeOffset endedAt, string reason) : EventArgs
{
    public ActiveGame Active { get; } = active;
    public DateTimeOffset EndedAt { get; } = endedAt;
    public string Reason { get; } = reason;
}

public sealed class GameProcessChangedEventArgs(ActiveGame active, string message) : EventArgs
{
    public ActiveGame Active { get; } = active;
    public string Message { get; } = message;
}

/// <summary>
/// Observa processos periodicamente (polling leve, sem privilégios) e emite início/fim de jogo.
/// Apenas um jogo é acompanhado por vez; instâncias adicionais são registradas como eventos.
/// </summary>
public sealed class GameDetectionService : IAsyncDisposable
{
    private readonly IProcessSnapshotProvider _processes;
    private readonly IGameRepository _games;
    private readonly TimeProvider _time;
    private readonly ILogger<GameDetectionService> _logger;
    private readonly Lock _gate = new();

    private IReadOnlyList<Game> _library = [];
    private IReadOnlySet<string> _ignored = new HashSet<string>();
    private bool _libraryDirty = true;
    private ActiveGame? _active;
    private DateTimeOffset? _exitedAt;
    private int _lastInstanceCount;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public GameDetectionService(IProcessSnapshotProvider processes, IGameRepository games, GameDetectionOptions options, TimeProvider time, ILogger<GameDetectionService> logger)
    {
        _processes = processes;
        _games = games;
        Options = options;
        _time = time;
        _logger = logger;
    }

    public GameDetectionOptions Options { get; }

    public ActiveGame? Active { get { lock (_gate) { return _active; } } }

    public event EventHandler<GameStartedEventArgs>? GameStarted;
    public event EventHandler<GameEndedEventArgs>? GameEnded;
    public event EventHandler<GameProcessChangedEventArgs>? GameProcessChanged;
    public event EventHandler<Game>? PendingGameDiscovered;

    /// <summary>Indica que a biblioteca mudou (cadastro/correção/remoção) e deve ser relida.</summary>
    public void InvalidateLibrary() => _libraryDirty = true;

    public void Start()
    {
        if (_loop is { IsCompleted: false })
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento esperado.
        }

        _cts.Dispose();
        _cts = null;
        EndActive("Monitoramento encerrado pelo FRAMEX.");
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Options.PollInterval, _time);
        do
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na detecção de jogos");
            }
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    /// <summary>Uma iteração de detecção. Público para testes determinísticos.</summary>
    public void Tick()
    {
        if (_libraryDirty)
        {
            _library = _games.GetAll(includeRemoved: true);
            _ignored = _games.GetIgnoredExecutables();
            _libraryDirty = false;
        }

        var now = _time.GetUtcNow();
        var processes = _processes.GetProcesses();
        var detected = GameDetectionEngine.Detect(processes, _library, _ignored, Options.AutoDetectStoreGames);

        ActiveGame? current;
        lock (_gate)
        {
            current = _active;
        }

        if (current is not null)
        {
            HandleActive(current, processes, detected, now);
            return;
        }

        var pick = detected.FirstOrDefault(d => d.Kind == DetectionKind.Known)
                   ?? (Options.RecordUnconfirmedGames ? detected.FirstOrDefault(d => d.Kind == DetectionKind.Heuristic) : null);
        if (pick is null)
        {
            return;
        }

        var game = pick.Game ?? RegisterPending(pick);
        var confirmed = game.Status == GameStatus.Confirmed;
        var active = new ActiveGame(game, pick.Process, confirmed, now);
        lock (_gate)
        {
            _active = active;
            _exitedAt = null;
            _lastInstanceCount = pick.InstanceCount;
        }

        _logger.LogInformation("Jogo detectado: {Game} (PID {Pid}, confirmado: {Confirmed})", game.Name, pick.Process.ProcessId, confirmed);
        GameStarted?.Invoke(this, new GameStartedEventArgs(active));
        if (pick.InstanceCount > 1)
        {
            GameProcessChanged?.Invoke(this, new GameProcessChangedEventArgs(active, $"{pick.InstanceCount} instâncias de {game.Name} em execução; acompanhando o PID {pick.Process.ProcessId}."));
        }
    }

    private void HandleActive(ActiveGame current, IReadOnlyList<ProcessInfo> processes, IReadOnlyList<DetectedGame> detected, DateTimeOffset now)
    {
        var stillAlive = processes.Any(p => p.ProcessId == current.Process.ProcessId
                                            && string.Equals(p.ExecutableName, current.Process.ExecutableName, StringComparison.OrdinalIgnoreCase));
        var sameGame = detected.FirstOrDefault(d => d.Game?.Id == current.Game.Id
                                                    || (d.Game is null && current.Game.Matches(d.Process.ExecutableName)));
        if (stillAlive)
        {
            if (sameGame is not null && sameGame.InstanceCount != _lastInstanceCount)
            {
                _lastInstanceCount = sameGame.InstanceCount;
                if (sameGame.InstanceCount > 1)
                {
                    GameProcessChanged?.Invoke(this, new GameProcessChangedEventArgs(current, $"Instância adicional de {current.Game.Name} detectada ({sameGame.InstanceCount} processos)."));
                }
            }

            _exitedAt = null;
            return;
        }

        if (sameGame is not null)
        {
            var replacement = current with { Process = sameGame.Process };
            lock (_gate)
            {
                _active = replacement;
                _exitedAt = null;
            }

            _logger.LogInformation("Processo do jogo {Game} reiniciado: PID {Old} → {New}", current.Game.Name, current.Process.ProcessId, sameGame.Process.ProcessId);
            GameProcessChanged?.Invoke(this, new GameProcessChangedEventArgs(replacement, $"Processo reiniciado (PID {current.Process.ProcessId} → {sameGame.Process.ProcessId})."));
            return;
        }

        _exitedAt ??= now;
        if (now - _exitedAt.Value >= Options.ExitGracePeriod)
        {
            EndActive("Processo do jogo encerrado.", _exitedAt.Value);
        }
    }

    private void EndActive(string reason, DateTimeOffset? endedAt = null)
    {
        ActiveGame? ended;
        lock (_gate)
        {
            ended = _active;
            _active = null;
            _exitedAt = null;
        }

        if (ended is not null)
        {
            _logger.LogInformation("Sessão de jogo encerrada: {Game} ({Reason})", ended.Game.Name, reason);
            GameEnded?.Invoke(this, new GameEndedEventArgs(ended, endedAt ?? _time.GetUtcNow(), reason));
        }
    }

    /// <summary>Força o encerramento da sessão atual (ex.: usuário pediu para parar).</summary>
    public void EndCurrent(string reason) => EndActive(reason);

    private Game RegisterPending(DetectedGame pick)
    {
        var game = new Game
        {
            Name = pick.SuggestedName,
            Status = GameStatus.Pending,
            Source = "Detecção automática (biblioteca de loja)",
            Executables = [new GameExecutable(pick.Process.ExecutableName, pick.Process.ExecutablePath)],
        };
        try
        {
            _games.Upsert(game);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao registrar jogo pendente {Game}", game.Name);
        }

        _libraryDirty = true;
        PendingGameDiscovered?.Invoke(this, game);
        return game;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
