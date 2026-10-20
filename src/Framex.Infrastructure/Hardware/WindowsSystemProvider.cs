using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Framex.Core.Domain;
using Framex.Core.Monitoring;
using Framex.Infrastructure.Native;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Hardware;

/// <summary>
/// Memória física/comprometida via GetPerformanceInfo (barato, sem privilégios) e inventário estático
/// do sistema via WMI, EnumDisplayDevices, DriveInfo e GetSystemPowerStatus.
/// </summary>
public sealed class WindowsSystemProvider : ISensorProvider
{
    private const string Source = "Windows";
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly ILogger<WindowsSystemProvider> _logger;
    private HardwareInventory? _cachedInventory;

    public WindowsSystemProvider(ILogger<WindowsSystemProvider> logger) => _logger = logger;

    public string Name => "Windows";

    public int Priority => 0;

    public void Initialize(MonitoringOptions options)
    {
    }

    public ProviderReadResult Read(DateTimeOffset now)
    {
        var metrics = new Dictionary<MetricKind, MetricValue>();
        var readings = new List<SensorReading>();
        if (!NativeMethods.GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<NativeMethods.PerformanceInformation>()))
        {
            var reason = $"GetPerformanceInfo falhou (erro {Marshal.GetLastWin32Error()}).";
            foreach (var k in new[] { MetricKind.RamTotal, MetricKind.RamUsed, MetricKind.RamAvailable, MetricKind.RamLoad, MetricKind.RamCommitted, MetricKind.RamCached })
            {
                metrics[k] = MetricValue.Missing(SensorAvailability.Unavailable, reason);
            }

            return new ProviderReadResult(readings, metrics);
        }

        double page = info.PageSize;
        double Mib(nuint pages) => UnitFormatter.BytesToMebibytes(pages * page);

        var total = Mib(info.PhysicalTotal);
        var available = Mib(info.PhysicalAvailable);
        var used = total - available;
        Put(MetricKind.RamTotal, total, "Memória física total");
        Put(MetricKind.RamAvailable, available, "Memória física disponível");
        Put(MetricKind.RamUsed, used, "Memória física em uso");
        Put(MetricKind.RamCached, Mib(info.SystemCache), "Cache do sistema");
        Put(MetricKind.RamCommitted, Mib(info.CommitTotal), "Memória comprometida");
        Put(MetricKind.RamCommitLimit, Mib(info.CommitLimit), "Limite de memória comprometida");
        Put(MetricKind.RamLoad, total > 0 ? used / total * 100 : null, "Uso da memória física", MetricUnit.Percent);

        return new ProviderReadResult(readings, metrics);

        void Put(MetricKind kind, double? value, string label, MetricUnit unit = MetricUnit.Megabyte)
        {
            var v = SensorValidator.Validate(value, unit, "windows/memory/" + kind);
            metrics[kind] = v;
            readings.Add(new SensorReading("windows/memory/" + kind, "windows/memory", "Memória do sistema", ComponentKind.Memory, "Memory",
                label, v.Value, unit, now, v.Availability, Source, v.Reason));
        }
    }

    public HardwareInventory? DescribeHardware()
    {
        if (_cachedInventory is not null)
        {
            // Discos e monitores mudam; o restante é estático durante a execução.
            var dynamicParts = DescribeDisks().Concat(DescribeDisplays()).Concat(DescribePower()).ToList();
            var staticParts = _cachedInventory.Components.Where(c => c.Kind is not (ComponentKind.Storage or ComponentKind.Display or ComponentKind.Battery));
            return new HardwareInventory(DateTimeOffset.UtcNow, staticParts.Concat(dynamicParts).ToList());
        }

        var components = new List<HardwareComponentInfo>();
        components.AddRange(Safe("SO", DescribeOperatingSystem));
        components.AddRange(Safe("CPU", DescribeProcessor));
        components.AddRange(Safe("RAM", DescribeMemory));
        components.AddRange(Safe("GPU", DescribeVideoControllers));
        components.AddRange(Safe("Placa-mãe", DescribeBoard));
        components.AddRange(Safe("Discos", DescribeDisks));
        components.AddRange(Safe("Monitores", DescribeDisplays));
        components.AddRange(Safe("Energia", DescribePower));
        _cachedInventory = new HardwareInventory(DateTimeOffset.UtcNow, components);
        return _cachedInventory;
    }

    private IEnumerable<HardwareComponentInfo> Safe(string area, Func<IEnumerable<HardwareComponentInfo>> fn)
    {
        try
        {
            return fn().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao coletar inventário de {Area}", area);
            return [];
        }
    }

    private static IEnumerable<ManagementObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            yield return mo;
        }
    }

    private static string Str(ManagementBaseObject mo, string prop) => mo[prop]?.ToString()?.Trim() ?? "";

    private static IEnumerable<HardwareComponentInfo> DescribeOperatingSystem()
    {
        foreach (var mo in Query("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
        {
            using (mo)
            {
                yield return new HardwareComponentInfo("windows/os", ComponentKind.OperatingSystem, Str(mo, "Caption"),
                [
                    new("Versão", Str(mo, "Version")),
                    new("Build", Str(mo, "BuildNumber")),
                    new("Arquitetura", Str(mo, "OSArchitecture")),
                    new(".NET", Environment.Version.ToString()),
                ]);
            }
        }
    }

    private static IEnumerable<HardwareComponentInfo> DescribeProcessor()
    {
        var index = 0;
        foreach (var mo in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor"))
        {
            using (mo)
            {
                yield return new HardwareComponentInfo($"windows/cpu/{index++}", ComponentKind.Cpu, Str(mo, "Name"),
                [
                    new("Fabricante", Str(mo, "Manufacturer")),
                    new("Núcleos", Str(mo, "NumberOfCores")),
                    new("Threads", Str(mo, "NumberOfLogicalProcessors")),
                    new("Frequência base", Str(mo, "MaxClockSpeed") + " MHz"),
                    new("Cache L2", Str(mo, "L2CacheSize") + " KB"),
                    new("Cache L3", Str(mo, "L3CacheSize") + " KB"),
                ]);
            }
        }
    }

    private static IEnumerable<HardwareComponentInfo> DescribeMemory()
    {
        ulong total = 0;
        var modules = new List<HardwareProperty>();
        var slot = 0;
        foreach (var mo in Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory"))
        {
            using (mo)
            {
                var cap = mo["Capacity"] is { } c ? Convert.ToUInt64(c, CultureInfo.InvariantCulture) : 0;
                total += cap;
                var speed = Str(mo, "ConfiguredClockSpeed");
                if (speed.Length == 0 || speed == "0")
                {
                    speed = Str(mo, "Speed");
                }

                modules.Add(new HardwareProperty($"Módulo {++slot} ({Str(mo, "DeviceLocator")})",
                    $"{cap / 1073741824.0:0.#} GiB · {speed} MT/s · {Str(mo, "Manufacturer")} {Str(mo, "PartNumber")}".Trim()));
            }
        }

        var props = new List<HardwareProperty>
        {
            new("Total", $"{total / 1073741824.0:0.#} GiB"),
            new("Convenção", "GiB = 1024³ bytes"),
        };
        props.AddRange(modules);
        yield return new HardwareComponentInfo("windows/ram", ComponentKind.Memory, "Memória RAM", props);
    }

    private static IEnumerable<HardwareComponentInfo> DescribeVideoControllers()
    {
        var index = 0;
        foreach (var mo in Query("SELECT Name, DriverVersion, DriverDate, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController"))
        {
            using (mo)
            {
                var date = Str(mo, "DriverDate");
                if (date.Length >= 8)
                {
                    date = $"{date[6..8]}/{date[4..6]}/{date[..4]}";
                }

                yield return new HardwareComponentInfo($"windows/gpu/{index++}", ComponentKind.Gpu, Str(mo, "Name"),
                [
                    new("Driver", Str(mo, "DriverVersion")),
                    new("Data do driver", date),
                    new("Processador de vídeo", Str(mo, "VideoProcessor")),
                    new("Fonte", "WMI"),
                ]);
            }
        }
    }

    private static IEnumerable<HardwareComponentInfo> DescribeBoard()
    {
        foreach (var mo in Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
        {
            using (mo)
            {
                yield return new HardwareComponentInfo("windows/board", ComponentKind.Motherboard, $"{Str(mo, "Manufacturer")} {Str(mo, "Product")}".Trim(),
                [
                    new("Fabricante", Str(mo, "Manufacturer")),
                    new("Modelo", Str(mo, "Product")),
                    new("Revisão", Str(mo, "Version")),
                ]);
            }
        }

        foreach (var mo in Query("SELECT SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS"))
        {
            using (mo)
            {
                yield return new HardwareComponentInfo("windows/bios", ComponentKind.Motherboard, "BIOS/UEFI",
                [
                    new("Fabricante", Str(mo, "Manufacturer")),
                    new("Versão", Str(mo, "SMBIOSBIOSVersion")),
                ]);
            }
        }
    }

    private static IEnumerable<HardwareComponentInfo> DescribeDisks()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            var free = drive.AvailableFreeSpace / 1073741824.0;
            var size = drive.TotalSize / 1073741824.0;
            yield return new HardwareComponentInfo("windows/drive/" + drive.Name, ComponentKind.Storage, $"Unidade {drive.Name.TrimEnd('\\')}",
            [
                new("Rótulo", drive.VolumeLabel),
                new("Sistema de arquivos", drive.DriveFormat),
                new("Capacidade", size.ToString("0.#", Pt) + " GiB"),
                new("Livre", $"{free.ToString("0.#", Pt)} GiB ({(size > 0 ? free / size * 100 : 0).ToString("0", Pt)}%)"),
            ]);
        }
    }

    public static IReadOnlyList<(string Device, string Name, int Width, int Height, int Hz, bool Primary)> EnumerateDisplays()
    {
        var list = new List<(string, string, int, int, int, bool)>();
        var adapter = new NativeMethods.DisplayDevice { cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) != 0)
            {
                var mode = new NativeMethods.DevMode { dmSize = (short)Marshal.SizeOf<NativeMethods.DevMode>() };
                var monitor = new NativeMethods.DisplayDevice { cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
                var monitorName = NativeMethods.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceString : "Monitor";
                if (NativeMethods.EnumDisplaySettings(adapter.DeviceName, NativeMethods.EnumCurrentSettings, ref mode))
                {
                    list.Add((adapter.DeviceName, monitorName, mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency,
                        (adapter.StateFlags & NativeMethods.DisplayDevicePrimaryDevice) != 0));
                }
            }

            adapter = new NativeMethods.DisplayDevice { cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
        }

        return list;
    }

    private static IEnumerable<HardwareComponentInfo> DescribeDisplays()
    {
        foreach (var d in EnumerateDisplays())
        {
            yield return new HardwareComponentInfo("windows/display/" + d.Device, ComponentKind.Display, d.Name + (d.Primary ? " (principal)" : ""),
            [
                new("Resolução", $"{d.Width}×{d.Height}"),
                new("Atualização", $"{d.Hz} Hz"),
                new("Dispositivo", d.Device),
            ]);
        }
    }

    private static IEnumerable<HardwareComponentInfo> DescribePower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryFlag == 128 || status.BatteryFlag == 255)
        {
            yield break;
        }

        yield return new HardwareComponentInfo("windows/battery", ComponentKind.Battery, "Bateria",
        [
            new("Carga", status.BatteryLifePercent <= 100 ? $"{status.BatteryLifePercent}%" : "Desconhecida"),
            new("Alimentação", status.ACLineStatus switch { 1 => "Na tomada", 0 => "Bateria", _ => "Desconhecida" }),
        ]);
    }

    public void Dispose()
    {
    }
}
