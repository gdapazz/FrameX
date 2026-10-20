using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Framex.Core.Domain;

namespace Framex.Infrastructure.Service;

/// <summary>
/// Protocolo entre o app (usuário comum) e o serviço FRAMEX (LocalSystem): uma mensagem JSON por linha, em UTF-8,
/// por um named pipe local. O nome inclui a versão; app e serviço vêm do mesmo instalador.
/// </summary>
public static class ServiceProtocol
{
    public const string ServiceName = "FramexService";
    public const string PipeName = "FRAMEX.Service.v1";
    public const int Version = 1;

    /// <summary>Limite por mensagem: uma linha maior que isso encerra a conexão (proteção contra abuso de memória).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var buffer = new byte[bytes.Length + 1];
        bytes.CopyTo(buffer, 0);
        buffer[^1] = (byte)'\n';
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lê mensagens delimitadas por '\n'. Retorna null no fim do fluxo; lança <see cref="InvalidDataException"/> se exceder o limite.</summary>
    public sealed class LineReader(Stream stream)
    {
        private readonly byte[] _chunk = new byte[16 * 1024];
        private readonly MemoryStream _pending = new();
        private int _offset;
        private int _count;

        public async Task<T?> ReadAsync<T>(CancellationToken ct) where T : class
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<T>(line, Json);
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_count > 0)
                {
                    var nl = Array.IndexOf(_chunk, (byte)'\n', _offset, _count);
                    if (nl >= 0)
                    {
                        _pending.Write(_chunk, _offset, nl - _offset);
                        _count -= nl - _offset + 1;
                        _offset = nl + 1;
                        var text = Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length);
                        _pending.SetLength(0);
                        return text;
                    }

                    _pending.Write(_chunk, _offset, _count);
                    _count = 0;
                    if (_pending.Length > MaxMessageBytes)
                    {
                        throw new InvalidDataException("Mensagem do pipe excede o limite.");
                    }
                }

                _offset = 0;
                _count = await stream.ReadAsync(_chunk, ct).ConfigureAwait(false);
                if (_count == 0)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>Conecta ao serviço; retorna null se ele não estiver em execução ou não responder no prazo.</summary>
    public static async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken ct, string pipeName = PipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }
}

public static class RequestTypes
{
    public const string Sensors = "sensors";
    public const string Inventory = "inventory";
    public const string Probe = "probe";
    public const string Capture = "capture";
}

public sealed record ServiceRequest(string Type, int? ProcessId = null, string? ExecutableName = null, bool TrackGpu = true);

public sealed record SensorDto(
    string Id, string ComponentId, string ComponentName, ComponentKind Component, string SensorType, string Name,
    double? Value, MetricUnit Unit, SensorAvailability Availability, string? Note);

public sealed record MetricDto(MetricKind Kind, double? Value, SensorAvailability Availability, string? Reason, string? SourceSensorId);

public sealed record SensorSnapshotDto(int Version, bool DriverInstalled, List<SensorDto> Sensors, List<MetricDto> Metrics, List<double>? PerCoreLoad);

public sealed record InventoryDto(int Version, List<HardwareComponentInfo> Components);

public sealed record CaptureStatusDto(CaptureState State, string Message, string? Detail);

/// <summary>Evento do fluxo de captura: mudança de estado ou lote de quadros ([ms Unix UTC, frametime ms, exibido 0/1]).</summary>
public sealed record CaptureEventDto(string Type, CaptureStatusDto? Status = null, List<double[]>? Frames = null)
{
    public const string StatusType = "status";
    public const string FramesType = "frames";
}

public sealed record ErrorDto(string Type, string Message)
{
    public const string ErrorType = "error";
}
