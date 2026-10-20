using System.IO.Pipes;
using System.Text.Json;
using Framex.Core.Domain;
using Framex.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Service;

/// <summary>
/// Sensores de CPU e placa-mãe lidos pelo serviço FRAMEX (LocalSystem + driver PawnIO), entregues ao app sem elevação.
/// Sem o serviço este provedor não contribui com nada (os demais provedores continuam valendo) e tenta reconectar
/// periodicamente; nunca inventa leituras.
/// </summary>
public sealed class ServiceSensorProvider : ISensorProvider
{
    public const string SourceName = "Serviço FRAMEX";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    private readonly ILogger<ServiceSensorProvider> _logger;
    private readonly Lock _gate = new();
    private NamedPipeClientStream? _pipe;
    private ServiceProtocol.LineReader? _reader;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _loggedUnavailable;

    private readonly string _pipeName;

    public ServiceSensorProvider(ILogger<ServiceSensorProvider> logger, string pipeName = ServiceProtocol.PipeName)
    {
        _logger = logger;
        _pipeName = pipeName;
    }

    public string Name => SourceName;

    /// <summary>Antes do LibreHardwareMonitor local: no serviço os sensores de CPU têm leitura real.</summary>
    public int Priority => 5;

    public bool IsConnected { get { lock (_gate) { return _pipe is { IsConnected: true }; } } }

    /// <summary>O driver PawnIO estava instalado na última resposta do serviço.</summary>
    public bool? DriverInstalled { get; private set; }

    public void Initialize(MonitoringOptions options)
    {
    }

    public ProviderReadResult Read(DateTimeOffset now)
    {
        var snapshot = Request<SensorSnapshotDto>(new ServiceRequest(RequestTypes.Sensors), now);
        if (snapshot is null)
        {
            return ProviderReadResult.Empty;
        }

        DriverInstalled = snapshot.DriverInstalled;
        var sensors = snapshot.Sensors
            .Select(s => new SensorReading(s.Id, s.ComponentId, s.ComponentName, s.Component, s.SensorType, s.Name, s.Value, s.Unit, now, s.Availability, SourceName, s.Note))
            .ToList();
        var metrics = snapshot.Metrics.ToDictionary(m => m.Kind, m => new MetricValue(m.Value, m.Availability, m.Reason, m.SourceSensorId));
        return new ProviderReadResult(sensors, metrics, snapshot.PerCoreLoad);
    }

    public HardwareInventory? DescribeHardware()
    {
        var inventory = Request<InventoryDto>(new ServiceRequest(RequestTypes.Inventory), DateTimeOffset.UtcNow);
        return inventory is null ? null : new HardwareInventory(DateTimeOffset.UtcNow, inventory.Components);
    }

    private T? Request<T>(ServiceRequest request, DateTimeOffset now) where T : class
    {
        lock (_gate)
        {
            if (!EnsureConnected(now))
            {
                return null;
            }

            try
            {
                using var cts = new CancellationTokenSource(ReplyTimeout);
                ServiceProtocol.WriteAsync(_pipe!, request, cts.Token).GetAwaiter().GetResult();
                var line = _reader!.ReadLineAsync(cts.Token).GetAwaiter().GetResult();
                if (line is null)
                {
                    throw new IOException("O serviço encerrou a conexão.");
                }

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == ErrorDto.ErrorType)
                {
                    _logger.LogWarning("Serviço FRAMEX recusou a solicitação {Request}: {Message}", request.Type,
                        doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "?");
                    return null;
                }

                return doc.RootElement.Deserialize<T>(ServiceProtocol.Json);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Comunicação com o serviço FRAMEX interrompida; nova tentativa em {Seconds} s", RetryInterval.TotalSeconds);
                Disconnect();
                _nextAttempt = now + RetryInterval;
                return null;
            }
        }
    }

    private bool EnsureConnected(DateTimeOffset now)
    {
        if (_pipe is { IsConnected: true })
        {
            return true;
        }

        Disconnect();
        if (now < _nextAttempt)
        {
            return false;
        }

        _pipe = ServiceProtocol.TryConnectAsync(ConnectTimeout, CancellationToken.None, _pipeName).GetAwaiter().GetResult();
        if (_pipe is null)
        {
            _nextAttempt = now + RetryInterval;
            if (!_loggedUnavailable)
            {
                _logger.LogInformation("Serviço FRAMEX não encontrado; sensores de CPU dependem de elevação local");
                _loggedUnavailable = true;
            }

            return false;
        }

        _reader = new ServiceProtocol.LineReader(_pipe);
        _loggedUnavailable = false;
        _logger.LogInformation("Conectado ao serviço FRAMEX (sensores de CPU)");
        return true;
    }

    private void Disconnect()
    {
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Disconnect();
        }
    }
}
