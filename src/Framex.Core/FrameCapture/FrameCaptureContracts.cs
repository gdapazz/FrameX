using Framex.Core.Domain;

namespace Framex.Core.FrameCapture;

/// <summary>Um quadro apresentado, com o intervalo desde o Present() anterior.</summary>
public readonly record struct FrameSample(DateTimeOffset Timestamp, double FrameTimeMs, bool Displayed = true);

public sealed record CaptureStatus(CaptureState State, string Message, string? Detail = null)
{
    public static CaptureStatus Idle { get; } = new(CaptureState.Stopped, "Captura de quadros inativa.");
}

/// <summary>
/// Mecanismo de captura de métricas de apresentação de quadros (ex.: PresentMon).
/// Nunca injeta código no processo do jogo.
/// </summary>
public interface IFrameCaptureProvider : IAsyncDisposable
{
    string Name { get; }

    CaptureStatus Status { get; }

    /// <summary>Verifica se o mecanismo está instalado/configurado, sem iniciar captura.</summary>
    CaptureStatus Probe();

    event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;

    event EventHandler<CaptureStatus>? StatusChanged;

    Task StartAsync(int processId, string executableName, FrameCaptureOptions options, CancellationToken cancellationToken);

    Task StopAsync();
}

public sealed class FrameCaptureOptions
{
    /// <summary>Desativa o rastreamento de GPU no PresentMon para reduzir custo (modo Economia).</summary>
    public bool TrackGpu { get; set; } = true;

    public bool TrackDisplay { get; set; } = true;
}
