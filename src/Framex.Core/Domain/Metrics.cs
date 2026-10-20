namespace Framex.Core.Domain;

/// <summary>
/// Métricas canônicas do FRAMEX. Os valores numéricos são persistidos no banco: nunca renumere.
/// </summary>
public enum MetricKind
{
    CpuLoad = 1,
    CpuTemperaturePackage = 2,
    CpuTemperatureCoreMax = 3,
    CpuClockAverage = 4,
    CpuPower = 5,

    GpuLoad = 20,
    GpuTemperature = 21,
    GpuHotspot = 22,
    GpuCoreClock = 23,
    GpuMemoryClock = 24,
    GpuPower = 25,
    GpuFanRpm = 26,
    GpuMemoryControllerLoad = 27,

    VramUsed = 40,
    VramTotal = 41,
    VramSharedUsed = 42,
    VramDedicatedUsedD3D = 43,

    RamUsed = 60,
    RamTotal = 61,
    RamAvailable = 62,
    RamCommitted = 63,
    RamCommitLimit = 64,
    RamCached = 65,
    RamLoad = 66,

    Fps = 80,
    FrameTimeAvg = 81,
    FrameTimeMax = 82,
    FrameCount = 83,
}

public enum MetricUnit
{
    None,
    Percent,
    Celsius,
    Megahertz,
    Watt,
    Rpm,
    Megabyte,
    Fps,
    Milliseconds,
    Count,
    Volt,
    Ampere,
    Gigabyte,
    BytesPerSecond,
}

public enum MetricCategory
{
    Cpu,
    Gpu,
    Vram,
    Ram,
    Frames,
}

public sealed record MetricDescriptor(MetricKind Kind, string Label, string ShortLabel, MetricUnit Unit, MetricCategory Category);

public static class MetricCatalog
{
    private static readonly Dictionary<MetricKind, MetricDescriptor> Map = new MetricDescriptor[]
    {
        new(MetricKind.CpuLoad, "Uso da CPU", "CPU", MetricUnit.Percent, MetricCategory.Cpu),
        new(MetricKind.CpuTemperaturePackage, "Temperatura da CPU (pacote)", "CPU °C", MetricUnit.Celsius, MetricCategory.Cpu),
        new(MetricKind.CpuTemperatureCoreMax, "Temperatura máx. dos núcleos", "Núcleo máx.", MetricUnit.Celsius, MetricCategory.Cpu),
        new(MetricKind.CpuClockAverage, "Frequência média da CPU", "CPU MHz", MetricUnit.Megahertz, MetricCategory.Cpu),
        new(MetricKind.CpuPower, "Potência da CPU (pacote)", "CPU W", MetricUnit.Watt, MetricCategory.Cpu),
        new(MetricKind.GpuLoad, "Uso da GPU", "GPU", MetricUnit.Percent, MetricCategory.Gpu),
        new(MetricKind.GpuTemperature, "Temperatura da GPU", "GPU °C", MetricUnit.Celsius, MetricCategory.Gpu),
        new(MetricKind.GpuHotspot, "Hotspot da GPU", "Hotspot", MetricUnit.Celsius, MetricCategory.Gpu),
        new(MetricKind.GpuCoreClock, "Frequência do núcleo da GPU", "GPU MHz", MetricUnit.Megahertz, MetricCategory.Gpu),
        new(MetricKind.GpuMemoryClock, "Frequência da memória da GPU", "Mem MHz", MetricUnit.Megahertz, MetricCategory.Gpu),
        new(MetricKind.GpuPower, "Potência da GPU", "GPU W", MetricUnit.Watt, MetricCategory.Gpu),
        new(MetricKind.GpuFanRpm, "Ventoinha da GPU", "Fan", MetricUnit.Rpm, MetricCategory.Gpu),
        new(MetricKind.GpuMemoryControllerLoad, "Controlador de memória da GPU", "MemCtl", MetricUnit.Percent, MetricCategory.Gpu),
        new(MetricKind.VramUsed, "VRAM utilizada", "VRAM", MetricUnit.Megabyte, MetricCategory.Vram),
        new(MetricKind.VramTotal, "VRAM total", "VRAM total", MetricUnit.Megabyte, MetricCategory.Vram),
        new(MetricKind.VramSharedUsed, "Memória de GPU compartilhada em uso", "Compart.", MetricUnit.Megabyte, MetricCategory.Vram),
        new(MetricKind.VramDedicatedUsedD3D, "VRAM dedicada (D3D)", "VRAM D3D", MetricUnit.Megabyte, MetricCategory.Vram),
        new(MetricKind.RamUsed, "RAM em uso", "RAM", MetricUnit.Megabyte, MetricCategory.Ram),
        new(MetricKind.RamTotal, "RAM física total", "RAM total", MetricUnit.Megabyte, MetricCategory.Ram),
        new(MetricKind.RamAvailable, "RAM disponível", "Disponível", MetricUnit.Megabyte, MetricCategory.Ram),
        new(MetricKind.RamCommitted, "Memória comprometida", "Commit", MetricUnit.Megabyte, MetricCategory.Ram),
        new(MetricKind.RamCommitLimit, "Limite de memória comprometida", "Commit máx.", MetricUnit.Megabyte, MetricCategory.Ram),
        new(MetricKind.RamCached, "Cache do sistema", "Cache", MetricUnit.Megabyte, MetricCategory.Ram),
        new(MetricKind.RamLoad, "Uso da RAM", "RAM %", MetricUnit.Percent, MetricCategory.Ram),
        new(MetricKind.Fps, "FPS", "FPS", MetricUnit.Fps, MetricCategory.Frames),
        new(MetricKind.FrameTimeAvg, "Frametime médio", "Frametime", MetricUnit.Milliseconds, MetricCategory.Frames),
        new(MetricKind.FrameTimeMax, "Frametime máximo", "Pico", MetricUnit.Milliseconds, MetricCategory.Frames),
        new(MetricKind.FrameCount, "Quadros no intervalo", "Quadros", MetricUnit.Count, MetricCategory.Frames),
    }.ToDictionary(d => d.Kind);

    public static IReadOnlyCollection<MetricDescriptor> All => Map.Values;

    public static MetricDescriptor Get(MetricKind kind) => Map[kind];

    public static bool TryGet(MetricKind kind, out MetricDescriptor descriptor) => Map.TryGetValue(kind, out descriptor!);
}
