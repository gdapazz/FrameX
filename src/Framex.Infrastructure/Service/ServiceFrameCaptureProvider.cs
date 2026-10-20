using System.IO.Pipes;
using System.Text.Json;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Service;

/// <summary>
/// Captura de quadros pelo serviço FRAMEX: o serviço executa o PresentMon (ETW) e transmite os quadros pelo pipe.
/// Fechar a conexão encerra a captura no serviço; nenhum processo fica órfão.
/// </summary>
public sealed class ServiceFrameCaptureProvider : IFrameCaptureProvider
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<ServiceFrameCaptureProvider> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _startStop = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private CaptureStatus _status = CaptureStatus.Idle;
    private CaptureStatus? _probeCache;
    private DateTimeOffset _probeAt;
    private int _disposed;

    private readonly string _pipeName;

    public ServiceFrameCaptureProvider(ILogger<ServiceFrameCaptureProvider> logger, string pipeName = ServiceProtocol.PipeName)
    {
        _logger = logger;
        _pipeName = pipeName;
    }

    public string Name => "PresentMon (serviço FRAMEX)";

    public CaptureStatus Status { get { lock (_gate) { return _status; } } }

    public event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;
    public event EventHandler<CaptureStatus>? StatusChanged;

    /// <summary>Estado do PresentMon no serviço; null se o serviço não respondeu (cache de 10 s).</summary>
    public CaptureStatus? TryProbeService()
    {
        lock (_gate)
        {
            if (_probeCache is not null && DateTimeOffset.UtcNow - _probeAt < TimeSpan.FromSeconds(10))
            {
                return _probeCache;
            }
        }

        CaptureStatus? result = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = ServiceProtocol.TryConnectAsync(TimeSpan.FromMilliseconds(400), cts.Token, _pipeName).GetAwaiter().GetResult();
            if (pipe is not null)
            {
                ServiceProtocol.WriteAsync(pipe, new ServiceRequest(RequestTypes.Probe), cts.Token).GetAwaiter().GetResult();
                var dto = new ServiceProtocol.LineReader(pipe).ReadAsync<CaptureStatusDto>(cts.Token).GetAwaiter().GetResult();
                if (dto is not null)
                {
                    result = new CaptureStatus(dto.State, dto.Message, dto.Detail);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
        {
            _logger.LogDebug(ex, "Serviço FRAMEX não respondeu à verificação de captura");
        }

        lock (_gate)
        {
            _probeCache = result;
            _probeAt = DateTimeOffset.UtcNow;
        }

        return result;
    }

    public CaptureStatus Probe()
    {
        var current = Status;
        if (current.State is CaptureState.Capturing or CaptureState.WaitingForFrames or CaptureState.Starting)
        {
            return current;
        }

        return TryProbeService() ?? new CaptureStatus(CaptureState.Error, "O serviço FRAMEX não está respondendo.",
            "Verifique o serviço \"FRAMEX\" em services.msc ou reinstale o FRAMEX pelo Setup.exe.");
    }

    public async Task StartAsync(int processId, string executableName, FrameCaptureOptions options, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        await _startStop.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(new CaptureStatus(CaptureState.Starting, $"Iniciando captura de quadros para {executableName}…"));
            var pipe = await ServiceProtocol.TryConnectAsync(ConnectTimeout, cancellationToken, _pipeName).ConfigureAwait(false);
            if (pipe is null)
            {
                SetStatus(new CaptureStatus(CaptureState.Error, "O serviço FRAMEX não está respondendo.",
                    "Verifique o serviço \"FRAMEX\" em services.msc ou reinstale o FRAMEX pelo Setup.exe."));
                return;
            }

            await ServiceProtocol.WriteAsync(pipe, new ServiceRequest(RequestTypes.Capture, processId, executableName, options.TrackGpu), cancellationToken).ConfigureAwait(false);
            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _pipe = pipe;
                _cts = cts;
            }

            _reader = Task.Run(() => ReadLoopAsync(pipe, cts.Token), cts.Token);
            _logger.LogInformation("Captura solicitada ao serviço FRAMEX para PID {Pid} ({Exe})", processId, executableName);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao iniciar a captura pelo serviço FRAMEX");
            SetStatus(new CaptureStatus(CaptureState.Error, "Falha ao iniciar a captura pelo serviço FRAMEX.", ex.Message));
        }
        finally
        {
            _startStop.Release();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        var reader = new ServiceProtocol.LineReader(pipe);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var evt = await reader.ReadAsync<CaptureEventDto>(token).ConfigureAwait(false);
                if (evt is null)
                {
                    // Uma recusa já informada pelo serviço (ex.: PID inválido) é mais útil que "conexão fechada".
                    if (Status.State != CaptureState.Error)
                    {
                        SetStatus(new CaptureStatus(CaptureState.Error, "O serviço FRAMEX encerrou a captura.", "A conexão com o serviço foi fechada."));
                    }

                    return;
                }

                if (evt.Type == CaptureEventDto.StatusType && evt.Status is { } s)
                {
                    SetStatus(new CaptureStatus(s.State, s.Message, s.Detail));
                }
                else if (evt.Type == CaptureEventDto.FramesType && evt.Frames is { Count: > 0 } frames)
                {
                    var batch = new List<FrameSample>(frames.Count);
                    foreach (var f in frames)
                    {
                        if (f.Length >= 3 && double.IsFinite(f[0]) && double.IsFinite(f[1]))
                        {
                            batch.Add(new FrameSample(DateTimeOffset.UnixEpoch.AddTicks((long)(f[0] * TimeSpan.TicksPerMillisecond)), f[1], f[2] != 0));
                        }
                    }

                    if (batch.Count > 0)
                    {
                        try
                        {
                            FramesReceived?.Invoke(this, batch);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Assinante de quadros falhou");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento solicitado.
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Fluxo de captura do serviço FRAMEX interrompido");
                SetStatus(new CaptureStatus(CaptureState.Error, "A conexão com o serviço FRAMEX foi interrompida.", ex.Message));
            }
        }
    }

    public async Task StopAsync()
    {
        await _startStop.WaitAsync().ConfigureAwait(false);
        try
        {
            NamedPipeClientStream? pipe;
            CancellationTokenSource? cts;
            lock (_gate)
            {
                pipe = _pipe;
                cts = _cts;
                _pipe = null;
                _cts = null;
            }

            if (cts is not null)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }

            if (pipe is not null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            if (_reader is not null)
            {
                try
                {
                    await _reader.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Encerramento esperado.
                }

                _reader = null;
            }

            cts?.Dispose();
            if (pipe is not null)
            {
                SetStatus(new CaptureStatus(CaptureState.Stopped, "Captura de quadros inativa."));
            }
        }
        finally
        {
            _startStop.Release();
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

        _logger.LogInformation("Captura (serviço): {State} — {Message}", status.State, status.Message);
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _startStop.Dispose();
    }
}
