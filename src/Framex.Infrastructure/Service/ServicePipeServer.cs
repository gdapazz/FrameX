using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Monitoring;
using Framex.Infrastructure.Native;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Service;

/// <summary>
/// Servidor do serviço FRAMEX. Atende leituras de sensores (sob demanda, sem custo quando o app está fechado) e uma
/// captura de quadros por vez. Regras de segurança:
/// <list type="bullet">
/// <item>o pipe só aceita conexões locais de usuários autenticados; somente SYSTEM/Administradores criam instâncias;</item>
/// <item>nenhum caminho, comando ou argumento livre é aceito do cliente: apenas o PID a capturar, validado;</item>
/// <item>o PID precisa pertencer à mesma sessão do Windows do cliente que pediu;</item>
/// <item>mensagens têm tamanho limitado e JSON inválido encerra só aquela conexão.</item>
/// </list>
/// </summary>
public sealed class ServicePipeServer : IAsyncDisposable
{
    private const int MaxConnections = 8;
    private static readonly TimeSpan SensorCacheAge = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SensorIdleClose = TimeSpan.FromMinutes(2);

    private readonly Func<ISensorProvider> _sensorFactory;
    private readonly IFrameCaptureProvider _capture;
    private readonly Func<bool> _driverInstalled;
    private readonly TimeProvider _time;
    private readonly ILogger<ServicePipeServer> _logger;
    private readonly string _pipeName;
    private readonly bool _restrictToSameSession;
    private readonly Lock _sensorGate = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);

    private ISensorProvider? _sensors;
    private DateTimeOffset _sensorsUsedAt;
    private (DateTimeOffset At, SensorSnapshotDto Dto)? _cached;
    private CancellationTokenSource? _activeCapture;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _idleLoop;

    public ServicePipeServer(Func<ISensorProvider> sensorFactory, IFrameCaptureProvider capture, Func<bool> driverInstalled, TimeProvider time,
        ILogger<ServicePipeServer> logger, string pipeName = ServiceProtocol.PipeName, bool restrictToSameSession = true)
    {
        _sensorFactory = sensorFactory;
        _capture = capture;
        _driverInstalled = driverInstalled;
        _time = time;
        _logger = logger;
        _pipeName = pipeName;
        _restrictToSameSession = restrictToSameSession;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _idleLoop = Task.Run(() => IdleLoopAsync(_cts.Token));
        _logger.LogInformation("Serviço FRAMEX aguardando conexões em \\\\.\\pipe\\{Pipe}", _pipeName);
    }

    private NamedPipeServerStream CreatePipe(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using (var current = WindowsIdentity.GetCurrent())
        {
            // Em testes o servidor roda como usuário comum; o dono do processo precisa poder criar novas instâncias.
            if (current.User is { } user)
            {
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, security);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var first = true;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                await _connections.WaitAsync(token).ConfigureAwait(false);
                pipe = CreatePipe(first);
                first = false;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // FirstPipeInstance falha se outro processo já criou o pipe: não atendemos sob um nome sequestrado.
                _logger.LogError(ex, "Não foi possível criar o pipe do serviço");
                _connections.Release();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _connections.Release();
                return;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Conexão abortada antes de começar");
                await pipe.DisposeAsync().ConfigureAwait(false);
                _connections.Release();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(pipe, token).ConfigureAwait(false);
                }
                finally
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var reader = new ServiceProtocol.LineReader(pipe);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await reader.ReadAsync<ServiceRequest>(token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                switch (request.Type)
                {
                    case RequestTypes.Sensors:
                        await ServiceProtocol.WriteAsync(pipe, ReadSensors(), token).ConfigureAwait(false);
                        break;
                    case RequestTypes.Inventory:
                        await ServiceProtocol.WriteAsync(pipe, ReadInventory(), token).ConfigureAwait(false);
                        break;
                    case RequestTypes.Probe:
                        var status = _capture.Probe();
                        await ServiceProtocol.WriteAsync(pipe, new CaptureStatusDto(status.State, status.Message, status.Detail), token).ConfigureAwait(false);
                        break;
                    case RequestTypes.Capture:
                        await CaptureAsync(pipe, reader, request, token).ConfigureAwait(false);
                        return;
                    default:
                        await ServiceProtocol.WriteAsync(pipe, new ErrorDto(ErrorDto.ErrorType, "Solicitação desconhecida."), token).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Serviço encerrando.
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Conexão do pipe encerrada");
        }
    }

    internal SensorSnapshotDto ReadSensors()
    {
        lock (_sensorGate)
        {
            var now = _time.GetUtcNow();
            _sensorsUsedAt = now;
            if (_cached is { } c && now - c.At < SensorCacheAge)
            {
                return c.Dto;
            }

            var provider = EnsureSensors();
            var result = provider.Read(now);
            var dto = new SensorSnapshotDto(
                ServiceProtocol.Version,
                _driverInstalled(),
                result.Sensors.Select(s => new SensorDto(s.Id, s.ComponentId, s.ComponentName, s.Component, s.SensorType, s.Name, s.Value, s.Unit, s.Availability, s.Note)).ToList(),
                result.Metrics.Select(kv => new MetricDto(kv.Key, kv.Value.Value, kv.Value.Availability, kv.Value.Reason, kv.Value.SourceSensorId)).ToList(),
                result.PerCoreLoad?.ToList());
            _cached = (now, dto);
            return dto;
        }
    }

    private InventoryDto ReadInventory()
    {
        lock (_sensorGate)
        {
            _sensorsUsedAt = _time.GetUtcNow();
            var inv = EnsureSensors().DescribeHardware();
            return new InventoryDto(ServiceProtocol.Version, inv?.Components.ToList() ?? []);
        }
    }

    private ISensorProvider EnsureSensors()
    {
        if (_sensors is null)
        {
            _sensors = _sensorFactory();
            _sensors.Initialize(new MonitoringOptions());
            _logger.LogInformation("Sensores abertos sob demanda (driver PawnIO instalado: {Driver})", _driverInstalled());
        }

        return _sensors;
    }

    /// <summary>Libera o LibreHardwareMonitor quando nenhum app pede leituras há algum tempo.</summary>
    private async Task IdleLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                lock (_sensorGate)
                {
                    if (_sensors is not null && _time.GetUtcNow() - _sensorsUsedAt > SensorIdleClose)
                    {
                        CloseSensors();
                        _logger.LogInformation("Sensores liberados por inatividade");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Serviço encerrando.
        }
    }

    private void CloseSensors()
    {
        try
        {
            _sensors?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao liberar os sensores");
        }

        _sensors = null;
        _cached = null;
    }

    private async Task CaptureAsync(NamedPipeServerStream pipe, ServiceProtocol.LineReader reader, ServiceRequest request, CancellationToken token)
    {
        var validation = ValidateCaptureTarget(pipe, request.ProcessId);
        if (validation is not null)
        {
            _logger.LogWarning("Captura recusada: {Reason}", validation);
            await ServiceProtocol.WriteAsync(pipe, new CaptureEventDto(CaptureEventDto.StatusType,
                new CaptureStatusDto(CaptureState.Error, "Captura recusada pelo serviço FRAMEX.", validation)), token).ConfigureAwait(false);
            return;
        }

        // Uma captura por vez: um novo pedido encerra o anterior.
        var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var previous = Interlocked.Exchange(ref _activeCapture, session);
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
        }

        await _captureGate.WaitAsync(token).ConfigureAwait(false);
        var writeLock = new SemaphoreSlim(1, 1);
        async Task Send(CaptureEventDto evt)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await ServiceProtocol.WriteAsync(pipe, evt, session.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                await session.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        void OnFrames(object? sender, IReadOnlyList<FrameSample> frames)
        {
            var payload = frames.Select(f => new[] { (f.Timestamp - DateTimeOffset.UnixEpoch).TotalMilliseconds, f.FrameTimeMs, f.Displayed ? 1d : 0d }).ToList();
            _ = Send(new CaptureEventDto(CaptureEventDto.FramesType, Frames: payload));
        }

        void OnStatus(object? sender, CaptureStatus s) =>
            _ = Send(new CaptureEventDto(CaptureEventDto.StatusType, new CaptureStatusDto(s.State, s.Message, s.Detail)));

        _capture.FramesReceived += OnFrames;
        _capture.StatusChanged += OnStatus;
        try
        {
            await _capture.StartAsync(request.ProcessId!.Value, SafeName(request.ExecutableName), new FrameCaptureOptions { TrackGpu = request.TrackGpu }, session.Token).ConfigureAwait(false);
            var current = _capture.Status;
            await Send(new CaptureEventDto(CaptureEventDto.StatusType, new CaptureStatusDto(current.State, current.Message, current.Detail))).ConfigureAwait(false);

            // O cliente não envia mais nada; o fim da leitura (conexão fechada) encerra a captura.
            while (!session.IsCancellationRequested)
            {
                if (await reader.ReadLineAsync(session.Token).ConfigureAwait(false) is null)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Fluxo de captura encerrado");
        }
        finally
        {
            _capture.FramesReceived -= OnFrames;
            _capture.StatusChanged -= OnStatus;
            await _capture.StopAsync().ConfigureAwait(false);
            Interlocked.CompareExchange(ref _activeCapture, null, session);
            session.Dispose();
            _captureGate.Release();
            _logger.LogInformation("Captura encerrada (cliente desconectado ou substituído)");
        }
    }

    private static string SafeName(string? name)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "jogo" : name.Trim();
        return n.Length > 120 ? n[..120] : n;
    }

    /// <summary>Retorna o motivo da recusa, ou null se o PID pode ser capturado em nome deste cliente.</summary>
    private string? ValidateCaptureTarget(NamedPipeServerStream pipe, int? pid)
    {
        if (pid is not > 4)
        {
            return "PID inválido.";
        }

        try
        {
            using var target = System.Diagnostics.Process.GetProcessById(pid.Value);
            if (!_restrictToSameSession)
            {
                return null;
            }

            if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
                || !NativeMethods.ProcessIdToSessionId(clientPid, out var clientSession))
            {
                return "Não foi possível identificar a sessão do solicitante.";
            }

            return target.SessionId == (int)clientSession ? null : "O processo pertence a outra sessão do Windows.";
        }
        catch (ArgumentException)
        {
            return "O processo não está em execução.";
        }
        catch (InvalidOperationException)
        {
            return "O processo não está em execução.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var t in new[] { _acceptLoop, _idleLoop })
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
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
        lock (_sensorGate)
        {
            CloseSensors();
        }

        _cts?.Dispose();
    }
}
