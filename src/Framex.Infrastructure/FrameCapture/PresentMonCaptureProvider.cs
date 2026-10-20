using System.Diagnostics;
using System.Security.Principal;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Infrastructure.Native;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.FrameCapture;

/// <summary>
/// Captura de quadros via PresentMon (Intel, licença MIT) executado como processo auxiliar.
/// O PresentMon lê eventos ETW do DXGI/D3D9/DxgKrnl: não injeta código no jogo e não interage com antitrapaça.
/// Requisitos: executável do PresentMon fornecido pelo usuário (o FRAMEX não baixa executáveis) e
/// privilégios de administrador ou associação ao grupo "Performance Log Users".
/// </summary>
public sealed class PresentMonCaptureProvider : IFrameCaptureProvider
{
    public const string SessionName = "FRAMEX_Capture";
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NoFramesTimeout = TimeSpan.FromSeconds(5);
    private static readonly SecurityIdentifier PerformanceLogUsers = new("S-1-5-32-559");

    private readonly Func<string?> _configuredPath;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<PresentMonCaptureProvider> _logger;
    private readonly ChildProcessJob _job = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _startStop = new(1, 1);
    private readonly Queue<string> _stderrTail = new();

    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private Task? _pump;
    private List<FrameSample> _batch = new(512);
    private DateTimeOffset _lastFrameAt;
    private CaptureStatus _status = new(CaptureState.Stopped, "Captura de quadros inativa.");
    private bool _stopping;
    private int _disposed;

    private readonly IReadOnlyList<string> _searchDirectories;

    public PresentMonCaptureProvider(Func<string?> configuredPath, AppPaths paths, TimeProvider time, ILogger<PresentMonCaptureProvider> logger)
        : this(configuredPath, paths, [Path.Combine(AppPaths.ApplicationDirectory, "tools"), paths.ToolsDirectory], time, logger)
    {
    }

    /// <summary>
    /// <paramref name="searchDirectories"/> restringe onde o executável é procurado. O serviço (LocalSystem) usa apenas a
    /// pasta de instalação, que só administradores podem alterar, para nunca executar um arquivo gravável pelo usuário.
    /// </summary>
    public PresentMonCaptureProvider(Func<string?> configuredPath, AppPaths paths, IReadOnlyList<string> searchDirectories, TimeProvider time, ILogger<PresentMonCaptureProvider> logger)
    {
        _configuredPath = configuredPath;
        _paths = paths;
        _searchDirectories = searchDirectories;
        _time = time;
        _logger = logger;
    }

    public string Name => "PresentMon";

    public CaptureStatus Status { get { lock (_gate) { return _status; } } }

    public event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;
    public event EventHandler<CaptureStatus>? StatusChanged;

    public static bool HasCapturePrivileges()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(PerformanceLogUsers);
    }

    /// <summary>Procura o executável: caminho configurado, pasta "tools" ao lado do app ou em %LOCALAPPDATA%\FRAMEX\tools.</summary>
    public string? LocateExecutable()
    {
        var configured = _configuredPath();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (var dir in _searchDirectories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var found = Directory.EnumerateFiles(dir, "PresentMon*.exe", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Contains("Service", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public CaptureStatus Probe()
    {
        if (LocateExecutable() is null)
        {
            return new CaptureStatus(CaptureState.NotConfigured, "PresentMon não encontrado.",
                $"Baixe o PresentMon (console) do repositório oficial GameTechDev/PresentMon e coloque o .exe em \"{(_searchDirectories.Count > 0 ? _searchDirectories[^1] : _paths.ToolsDirectory)}\" ou indique o caminho nas Configurações.");
        }

        if (!HasCapturePrivileges())
        {
            return new CaptureStatus(CaptureState.AccessDenied, "Sem permissão para captura ETW.",
                "Instale o FRAMEX pelo Setup.exe (o serviço do FRAMEX faz a captura sem configuração), execute o FRAMEX como administrador ou adicione seu usuário ao grupo \"Performance Log Users\" (Usuários de log de desempenho) e reinicie a sessão do Windows.");
        }

        var current = Status;
        return current.State is CaptureState.Capturing or CaptureState.WaitingForFrames or CaptureState.Starting
            ? current
            : new CaptureStatus(CaptureState.Stopped, "PresentMon pronto.");
    }

    public async Task StartAsync(int processId, string executableName, FrameCaptureOptions options, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        await _startStop.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var probe = Probe();
            var exe = LocateExecutable();
            if (exe is null)
            {
                SetStatus(probe);
                return;
            }

            if (probe.State == CaptureState.AccessDenied)
            {
                // Tenta mesmo assim: algumas configurações concedem acesso ETW por política.
                _logger.LogWarning("Iniciando PresentMon sem privilégios detectados; a captura pode falhar");
            }

            var args = new List<string>
            {
                "--process_id", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--output_stdout", "--no_console_stats", "--qpc_time_ms",
                "--session_name", SessionName, "--stop_existing_session",
                "--terminate_on_proc_exit", "--no_track_input",
            };
            if (!options.TrackGpu)
            {
                args.Add("--no_track_gpu");
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            SetStatus(new CaptureStatus(CaptureState.Starting, $"Iniciando captura de quadros para {executableName}…"));
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            if (!_job.Assign(process))
            {
                _logger.LogWarning("Não foi possível associar o PresentMon ao Job Object; ele será encerrado manualmente");
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Não foi possível reduzir a prioridade do PresentMon");
            }

            var parser = new PresentMonCsvParser
            {
                Anchor = (_time.GetUtcNow(), Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency),
            };

            lock (_gate)
            {
                _process = process;
                _stderrTail.Clear();
                _batch = new List<FrameSample>(512);
                _lastFrameAt = _time.GetUtcNow();
                _stopping = false;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _reader = Task.Run(() => ReadLoopAsync(process, parser, processId, token), token);
            _pump = Task.Run(() => PumpAsync(token), token);
            _ = Task.Run(() => ReadErrorsAsync(process, token), token);
            SetStatus(new CaptureStatus(CaptureState.WaitingForFrames, "Aguardando quadros do jogo…",
                "Se nada aparecer, o jogo pode estar minimizado, usar uma API não rastreada pelo PresentMon ou estar protegido."));
            _logger.LogInformation("PresentMon iniciado para PID {Pid} ({Exe})", processId, executableName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao iniciar o PresentMon");
            SetStatus(new CaptureStatus(CaptureState.Error, "Falha ao iniciar o PresentMon.", ex.Message));
        }
        finally
        {
            _startStop.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, PresentMonCsvParser parser, int pid, CancellationToken token)
    {
        try
        {
            var reader = process.StandardOutput;
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!parser.HasHeader)
                {
                    if (parser.TrySetHeader(line))
                    {
                        _logger.LogInformation("Cabeçalho do PresentMon reconhecido; frametime = {Column}", parser.FrameTimeColumn);
                    }

                    continue;
                }

                if (PresentMonCsvParser.LooksLikeHeader(line))
                {
                    parser.TrySetHeader(line);
                    continue;
                }

                if (parser.TryParse(line, pid, _time.GetUtcNow(), out var frame))
                {
                    lock (_gate)
                    {
                        _batch.Add(frame);
                        _lastFrameAt = _time.GetUtcNow();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Leitura da saída do PresentMon interrompida");
        }

        OnProcessFinished(process);
    }

    private void OnProcessFinished(Process process)
    {
        bool stopping;
        string tail;
        lock (_gate)
        {
            stopping = _stopping;
            tail = string.Join(" ", _stderrTail);
        }

        if (stopping)
        {
            return;
        }

        int? exitCode = null;
        try
        {
            if (process.WaitForExit(2000))
            {
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            // Processo já liberado.
        }

        if (tail.Contains("access denied", StringComparison.OrdinalIgnoreCase) || tail.Contains("elevat", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(new CaptureStatus(CaptureState.AccessDenied, "O PresentMon não tem permissão para capturar.",
                "Instale o FRAMEX pelo Setup.exe (serviço de captura), execute-o como administrador ou adicione o usuário ao grupo \"Performance Log Users\". Detalhe: " + tail));
        }
        else if (exitCode is 0 or null)
        {
            SetStatus(new CaptureStatus(CaptureState.Stopped, "Captura de quadros encerrada (processo do jogo finalizado).", tail.Length > 0 ? tail : null));
        }
        else
        {
            SetStatus(new CaptureStatus(CaptureState.Error, $"O PresentMon encerrou com código {exitCode}.", tail.Length > 0 ? tail : null));
        }
    }

    private async Task ReadErrorsAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                _logger.LogInformation("PresentMon: {Line}", line);
                lock (_gate)
                {
                    _stderrTail.Enqueue(line.Trim());
                    while (_stderrTail.Count > 10)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento esperado.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Leitura do stderr do PresentMon interrompida");
        }
    }

    /// <summary>Entrega lotes de quadros a cada 250 ms e acompanha a ausência de quadros.</summary>
    private async Task PumpAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(BatchInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                List<FrameSample> batch;
                DateTimeOffset last;
                lock (_gate)
                {
                    batch = _batch;
                    last = _lastFrameAt;
                    if (batch.Count > 0)
                    {
                        _batch = new List<FrameSample>(Math.Max(64, batch.Count));
                    }
                }

                var state = Status.State;
                if (batch.Count > 0)
                {
                    if (state != CaptureState.Capturing)
                    {
                        SetStatus(new CaptureStatus(CaptureState.Capturing, "Capturando quadros via PresentMon."));
                    }

                    try
                    {
                        FramesReceived?.Invoke(this, batch);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Assinante de quadros falhou");
                    }
                }
                else if (state == CaptureState.Capturing && _time.GetUtcNow() - last > NoFramesTimeout)
                {
                    SetStatus(new CaptureStatus(CaptureState.WaitingForFrames, "Nenhum quadro recebido nos últimos segundos.",
                        "O jogo pode estar minimizado, pausado ou em uma tela que não renderiza."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento esperado.
        }
    }

    public async Task StopAsync()
    {
        await _startStop.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_gate)
            {
                process = _process;
                _process = null;
                _stopping = true;
            }

            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao encerrar o PresentMon");
                }
                finally
                {
                    process.Dispose();
                }

                await CleanupEtwSessionAsync().ConfigureAwait(false);
            }

            foreach (var t in new[] { _reader, _pump })
            {
                if (t is null)
                {
                    continue;
                }

                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Encerramento esperado.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tarefa de captura terminou com erro");
                }
            }

            _reader = null;
            _pump = null;
            _cts?.Dispose();
            _cts = null;

            if (process is not null)
            {
                SetStatus(new CaptureStatus(CaptureState.Stopped, "Captura de quadros inativa."));
            }
        }
        finally
        {
            _startStop.Release();
        }
    }

    /// <summary>Ao matar o PresentMon, a sessão ETW pode continuar aberta; esta chamada a encerra.</summary>
    private async Task CleanupEtwSessionAsync()
    {
        var exe = LocateExecutable();
        if (exe is null)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--session_name");
            psi.ArgumentList.Add(SessionName);
            psi.ArgumentList.Add("--terminate_existing_session");
            using var p = Process.Start(psi);
            if (p is null)
            {
                return;
            }

            _job.Assign(p);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                p.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao encerrar a sessão ETW do PresentMon");
        }
    }

    private void SetStatus(CaptureStatus status)
    {
        lock (_gate)
        {
            if (_status.State == status.State && _status.Message == status.Message)
            {
                return;
            }

            _status = status;
        }

        _logger.LogInformation("Captura: {State} — {Message}", status.State, status.Message);
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _job.Dispose();
        _startStop.Dispose();
    }
}
