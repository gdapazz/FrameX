using Framex.Core.Domain;

namespace Framex.Core.Monitoring;

/// <summary>
/// Fonte de leituras de sensores (LibreHardwareMonitor, APIs do Windows, provedores de teste...).
/// Implementações não devem lançar exceções por um sensor isolado: devem marcá-lo como indisponível.
/// </summary>
public interface ISensorProvider : IDisposable
{
    string Name { get; }

    /// <summary>Menor valor vence quando dois provedores informam a mesma métrica canônica.</summary>
    int Priority { get; }

    void Initialize(MonitoringOptions options);

    ProviderReadResult Read(DateTimeOffset now);

    HardwareInventory? DescribeHardware();
}

public sealed record ProviderReadResult(
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyDictionary<MetricKind, MetricValue> Metrics,
    IReadOnlyList<double>? PerCoreLoad = null,
    string? PrimaryGpuId = null)
{
    public static ProviderReadResult Empty { get; } = new([], new Dictionary<MetricKind, MetricValue>());
}

public sealed class MonitoringOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Leituras mais antigas que isso passam a ser consideradas desatualizadas.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(5);

    public string? PreferredGpuId { get; set; }

    public bool CollectStorage { get; set; }

    public bool CollectNetwork { get; set; }
}
