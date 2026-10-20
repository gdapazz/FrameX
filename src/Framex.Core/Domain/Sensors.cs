namespace Framex.Core.Domain;

public enum SensorAvailability
{
    /// <summary>Leitura válida e recente.</summary>
    Available,
    /// <summary>O sensor existe, mas não retornou valor (ex.: falta de permissão ou driver).</summary>
    Unavailable,
    /// <summary>O hardware/plataforma não oferece este sensor.</summary>
    Unsupported,
    /// <summary>A última leitura válida é antiga demais para ser considerada atual.</summary>
    Stale,
    /// <summary>O valor recebido está fora da faixa física plausível ou é NaN/infinito.</summary>
    Invalid,
}

public enum ComponentKind
{
    Cpu,
    Gpu,
    Memory,
    Motherboard,
    Storage,
    Network,
    Display,
    OperatingSystem,
    Battery,
    Other,
}

/// <summary>Medição normalizada de um sensor individual.</summary>
public sealed record SensorReading(
    string Id,
    string ComponentId,
    string ComponentName,
    ComponentKind Component,
    string SensorType,
    string Name,
    double? Value,
    MetricUnit Unit,
    DateTimeOffset Timestamp,
    SensorAvailability Availability,
    string Source,
    string? Note = null)
{
    public bool IsValid => Availability == SensorAvailability.Available && Value.HasValue;
}

/// <summary>Valor de uma métrica canônica, com seu estado de disponibilidade.</summary>
public readonly record struct MetricValue(double? Value, SensorAvailability Availability, string? Reason = null, string? SourceSensorId = null)
{
    public bool HasValue => Availability == SensorAvailability.Available && Value.HasValue;

    public static MetricValue Of(double value, string? sourceSensorId = null) => new(value, SensorAvailability.Available, null, sourceSensorId);

    public static MetricValue Missing(SensorAvailability availability, string reason) => new(null, availability, reason);

    public static readonly MetricValue NotDetected = new(null, SensorAvailability.Unsupported, "Sensor não detectado neste computador.");
}

/// <summary>Resultado de uma coleta completa de todos os provedores de sensores.</summary>
public sealed class HardwareSnapshot
{
    public HardwareSnapshot(DateTimeOffset timestamp, IReadOnlyDictionary<MetricKind, MetricValue> metrics, IReadOnlyList<SensorReading> sensors, IReadOnlyList<double> perCoreLoad, string? primaryGpuId)
    {
        Timestamp = timestamp;
        Metrics = metrics;
        Sensors = sensors;
        PerCoreLoad = perCoreLoad;
        PrimaryGpuId = primaryGpuId;
    }

    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<MetricKind, MetricValue> Metrics { get; }
    public IReadOnlyList<SensorReading> Sensors { get; }
    public IReadOnlyList<double> PerCoreLoad { get; }
    public string? PrimaryGpuId { get; }

    public MetricValue this[MetricKind kind] => Metrics.TryGetValue(kind, out var v) ? v : MetricValue.NotDetected;

    public static HardwareSnapshot Empty { get; } = new(DateTimeOffset.MinValue, new Dictionary<MetricKind, MetricValue>(), [], [], null);
}

public sealed record HardwareProperty(string Name, string Value);

public sealed record HardwareComponentInfo(string Id, ComponentKind Kind, string Name, IReadOnlyList<HardwareProperty> Properties);

public sealed record HardwareInventory(DateTimeOffset CollectedAt, IReadOnlyList<HardwareComponentInfo> Components)
{
    public static HardwareInventory Empty { get; } = new(DateTimeOffset.MinValue, []);

    public string? FirstName(ComponentKind kind) => Components.FirstOrDefault(c => c.Kind == kind)?.Name;

    public IEnumerable<HardwareComponentInfo> OfKind(ComponentKind kind) => Components.Where(c => c.Kind == kind);

    /// <summary>Resumo compacto, sem dados pessoais, usado para comparar condições entre sessões.</summary>
    public string Fingerprint()
    {
        var cpu = FirstName(ComponentKind.Cpu) ?? "?";
        var gpus = string.Join(" + ", OfKind(ComponentKind.Gpu).Select(g => g.Name));
        var ram = Components.FirstOrDefault(c => c.Kind == ComponentKind.Memory)?.Properties.FirstOrDefault(p => p.Name == "Total")?.Value ?? "?";
        return $"{cpu} | {gpus} | {ram}";
    }
}
