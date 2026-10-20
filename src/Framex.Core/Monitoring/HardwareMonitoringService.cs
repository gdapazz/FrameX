using Framex.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Framex.Core.Monitoring;

public interface IHardwareMonitor
{
    HardwareSnapshot Latest { get; }
    HardwareInventory Inventory { get; }
    bool IsRunning { get; }
    bool IsPaused { get; }
    MonitoringOptions Options { get; }
    IReadOnlyList<ProviderHealth> ProviderHealth { get; }

    event EventHandler<HardwareSnapshot>? SnapshotAvailable;

    void Start();
    Task StopAsync();
    void Pause();
    void Resume();
    void RefreshInventory();
}

public sealed record ProviderHealth(string Name, bool Healthy, int ConsecutiveFailures, string? LastError, DateTimeOffset? LastSuccess);

/// <summary>
/// Coordena os provedores de sensores em uma tarefa de fundo, isolando falhas por provedor
/// e consolidando as métricas canônicas em um <see cref="HardwareSnapshot"/>.
/// </summary>
public sealed class HardwareMonitoringService : IHardwareMonitor, IAsyncDisposable
{
    private const int FailuresBeforeBackoff = 5;
    private static readonly TimeSpan BackoffDuration = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<ISensorProvider> _providers;
    private readonly TimeProvider _time;
    private readonly ILogger<HardwareMonitoringService> _logger;
    private readonly Dictionary<string, ProviderState> _states = new();
    private readonly Dictionary<MetricKind, (MetricValue Value, DateTimeOffset At)> _lastGood = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile HardwareSnapshot _latest = HardwareSnapshot.Empty;
    private volatile bool _paused;
    private HardwareInventory _inventory = HardwareInventory.Empty;

    public HardwareMonitoringService(IEnumerable<ISensorProvider> providers, MonitoringOptions options, TimeProvider time, ILogger<HardwareMonitoringService> logger)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
        Options = options;
        _time = time;
        _logger = logger;
        foreach (var p in _providers)
        {
            _states[p.Name] = new ProviderState();
        }
    }

    public HardwareSnapshot Latest => _latest;
    public HardwareInventory Inventory { get { lock (_gate) { return _inventory; } } }
    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsPaused => _paused;
    public MonitoringOptions Options { get; }

    public IReadOnlyList<ProviderHealth> ProviderHealth
    {
        get
        {
            lock (_gate)
            {
                return _states.Select(kv => new ProviderHealth(kv.Key, kv.Value.Initialized && kv.Value.ConsecutiveFailures == 0,
                    kv.Value.ConsecutiveFailures, kv.Value.LastError, kv.Value.LastSuccess)).ToList();
            }
        }
    }

    public event EventHandler<HardwareSnapshot>? SnapshotAvailable;

    public void Start()
    {
        if (IsRunning)
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
        _loop = null;
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void RefreshInventory()
    {
        var components = new List<HardwareComponentInfo>();
        foreach (var provider in _providers)
        {
            try
            {
                var inv = provider.DescribeHardware();
                if (inv is not null)
                {
                    foreach (var c in inv.Components)
                    {
                        MergeComponent(components, c);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao descrever hardware pelo provedor {Provider}", provider.Name);
            }
        }

        lock (_gate)
        {
            _inventory = new HardwareInventory(_time.GetUtcNow(), components);
        }
    }

    /// <summary>
    /// Une descrições do mesmo componente vindas de fontes diferentes: CPU única por máquina,
    /// GPUs pelo nome; o nome do primeiro provedor (maior prioridade) é mantido.
    /// </summary>
    internal static void MergeComponent(List<HardwareComponentInfo> components, HardwareComponentInfo incoming)
    {
        var index = components.FindIndex(x => x.Id == incoming.Id
            || (incoming.Kind == ComponentKind.Cpu && x.Kind == ComponentKind.Cpu && components.Count(c => c.Kind == ComponentKind.Cpu) == 1)
            || (incoming.Kind == ComponentKind.Gpu && x.Kind == ComponentKind.Gpu && string.Equals(x.Name, incoming.Name, StringComparison.OrdinalIgnoreCase)));
        if (index < 0)
        {
            components.Add(incoming);
            return;
        }

        var existing = components[index];
        var props = existing.Properties.ToList();
        props.AddRange(incoming.Properties.Where(p => props.All(e => e.Name != p.Name)));
        components[index] = existing with { Properties = props };
    }

    /// <summary>Executa uma coleta imediatamente (usado pelo loop e por testes).</summary>
    public HardwareSnapshot CollectOnce()
    {
        var now = _time.GetUtcNow();
        var sensors = new List<SensorReading>();
        var sensorIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var merged = new Dictionary<MetricKind, MetricValue>();
        IReadOnlyList<double> perCore = [];
        string? primaryGpu = null;

        foreach (var provider in _providers)
        {
            var state = _states[provider.Name];
            if (!EnsureInitialized(provider, state, now))
            {
                continue;
            }

            if (state.BackoffUntil is { } until && until > now)
            {
                continue;
            }

            try
            {
                var result = provider.Read(now);
                MergeSensors(sensors, sensorIndex, result.Sensors);
                foreach (var (kind, value) in result.Metrics)
                {
                    if (!merged.TryGetValue(kind, out var existing) || (!existing.HasValue && value.HasValue))
                    {
                        merged[kind] = value;
                    }
                }

                if (perCore.Count == 0 && result.PerCoreLoad is { Count: > 0 } pc)
                {
                    perCore = pc;
                }

                primaryGpu ??= result.PrimaryGpuId;
                lock (_gate)
                {
                    state.ConsecutiveFailures = 0;
                    state.LastError = null;
                    state.LastSuccess = now;
                    state.BackoffUntil = null;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    state.ConsecutiveFailures++;
                    state.LastError = ex.Message;
                    if (state.ConsecutiveFailures >= FailuresBeforeBackoff)
                    {
                        state.BackoffUntil = now + BackoffDuration;
                    }
                }

                _logger.LogWarning(ex, "Falha de leitura no provedor {Provider} (falhas consecutivas: {Count})", provider.Name, state.ConsecutiveFailures);
            }
        }

        ApplyStaleness(merged, now);
        var snapshot = new HardwareSnapshot(now, merged, sensors, perCore, primaryGpu);
        _latest = snapshot;
        return snapshot;
    }

    /// <summary>
    /// O mesmo sensor pode vir de dois provedores (ex.: CPU pelo serviço FRAMEX e pelo LibreHardwareMonitor local sem
    /// elevação). Mantém uma entrada por Id: a de maior prioridade, salvo se só a outra tiver leitura válida.
    /// </summary>
    internal static void MergeSensors(List<SensorReading> sensors, Dictionary<string, int> index, IReadOnlyList<SensorReading> incoming)
    {
        foreach (var s in incoming)
        {
            if (!index.TryGetValue(s.Id, out var i))
            {
                index[s.Id] = sensors.Count;
                sensors.Add(s);
            }
            else if (!sensors[i].IsValid && s.IsValid)
            {
                sensors[i] = s;
            }
        }
    }

    private void ApplyStaleness(Dictionary<MetricKind, MetricValue> merged, DateTimeOffset now)
    {
        foreach (var kind in merged.Keys.ToList())
        {
            var value = merged[kind];
            if (value.HasValue)
            {
                _lastGood[kind] = (value, now);
            }
        }

        foreach (var (kind, last) in _lastGood)
        {
            if (merged.TryGetValue(kind, out var current) && current.HasValue)
            {
                continue;
            }

            var age = now - last.At;
            if (age <= Options.StaleAfter)
            {
                merged[kind] = new MetricValue(last.Value.Value, SensorAvailability.Stale, $"Última leitura válida há {age.TotalSeconds:0} s.", last.Value.SourceSensorId);
            }
            else if (!merged.ContainsKey(kind))
            {
                merged[kind] = MetricValue.Missing(SensorAvailability.Unavailable, "O sensor deixou de responder.");
            }
        }
    }

    private bool EnsureInitialized(ISensorProvider provider, ProviderState state, DateTimeOffset now)
    {
        if (state.Initialized)
        {
            return true;
        }

        if (state.BackoffUntil is { } until && until > now)
        {
            return false;
        }

        try
        {
            provider.Initialize(Options);
            state.Initialized = true;
            _logger.LogInformation("Provedor de sensores {Provider} inicializado", provider.Name);
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                state.ConsecutiveFailures++;
                state.LastError = ex.Message;
                state.BackoffUntil = now + BackoffDuration;
            }

            _logger.LogError(ex, "Falha ao inicializar o provedor {Provider}", provider.Name);
            return false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        RefreshInventory();
        var interval = Options.PollInterval;
        using var timer = new PeriodicTimer(interval, _time);
        do
        {
            if (_paused)
            {
                continue;
            }

            var snapshot = CollectOnce();
            try
            {
                SnapshotAvailable?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assinante de snapshot de hardware falhou");
            }

            if (Options.PollInterval != interval)
            {
                interval = Options.PollInterval;
                timer.Period = interval;
            }
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var p in _providers)
        {
            try
            {
                p.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao liberar o provedor {Provider}", p.Name);
            }
        }
    }

    private sealed class ProviderState
    {
        public bool Initialized;
        public int ConsecutiveFailures;
        public string? LastError;
        public DateTimeOffset? LastSuccess;
        public DateTimeOffset? BackoffUntil;
    }
}
