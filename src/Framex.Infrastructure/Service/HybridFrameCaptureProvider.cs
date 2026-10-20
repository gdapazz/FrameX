using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Infrastructure.FrameCapture;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Service;

/// <summary>
/// Usa o serviço FRAMEX quando ele está em execução (captura sem elevação nem configuração) e, sem ele,
/// o PresentMon local, que exige o executável e permissão ETW.
/// </summary>
public sealed class HybridFrameCaptureProvider : IFrameCaptureProvider
{
    private readonly ServiceFrameCaptureProvider _service;
    private readonly PresentMonCaptureProvider _local;
    private readonly ILogger<HybridFrameCaptureProvider> _logger;
    private IFrameCaptureProvider _active;

    public HybridFrameCaptureProvider(ServiceFrameCaptureProvider service, PresentMonCaptureProvider local, ILogger<HybridFrameCaptureProvider> logger)
    {
        _service = service;
        _local = local;
        _logger = logger;
        _active = local;
        foreach (var p in new IFrameCaptureProvider[] { service, local })
        {
            p.FramesReceived += (s, e) => { if (ReferenceEquals(s, _active)) { FramesReceived?.Invoke(this, e); } };
            p.StatusChanged += (s, e) => { if (ReferenceEquals(s, _active)) { StatusChanged?.Invoke(this, e); } };
        }
    }

    public string Name => _active.Name;

    public CaptureStatus Status => _active.Status;

    /// <summary>A última verificação encontrou o serviço FRAMEX respondendo.</summary>
    public bool UsingService { get; private set; }

    public PresentMonCaptureProvider Local => _local;

    public event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;
    public event EventHandler<CaptureStatus>? StatusChanged;

    public CaptureStatus Probe()
    {
        if (_active.Status.State is CaptureState.Capturing or CaptureState.WaitingForFrames or CaptureState.Starting)
        {
            return _active.Status;
        }

        var service = _service.TryProbeService();
        UsingService = service is not null;
        return service ?? _local.Probe();
    }

    public async Task StartAsync(int processId, string executableName, FrameCaptureOptions options, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        var service = _service.TryProbeService();
        UsingService = service is not null;
        _active = service is not null ? _service : _local;
        _logger.LogInformation("Captura de quadros via {Provider}", _active.Name);
        await _active.StartAsync(processId, executableName, options, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync() => _active.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync().ConfigureAwait(false);
        await _local.DisposeAsync().ConfigureAwait(false);
    }
}
