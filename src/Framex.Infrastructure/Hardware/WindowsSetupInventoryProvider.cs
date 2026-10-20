using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Framex.Core.SetupScore;
using Framex.Infrastructure.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Framex.Infrastructure.Hardware;

/// <summary>
/// Inventário da Nota do Setup, totalmente local e sem privilégios de administrador:
/// CPU (Win32_Processor), GPU (Win32_VideoController + chave do driver no registro para a memória dedicada de 64 bits),
/// RAM (GetPhysicallyInstalledSystemMemory + Win32_PhysicalMemory) e discos físicos (MSFT_PhysicalDisk, com
/// Win32_DiskDrive como alternativa). Partições e volumes nunca são usados para capacidade.
/// </summary>
public sealed class WindowsSetupInventoryProvider : ISetupInventoryProvider
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\";
    private readonly ILogger<WindowsSetupInventoryProvider> _logger;

    public WindowsSetupInventoryProvider(ILogger<WindowsSetupInventoryProvider> logger) => _logger = logger;

    public Task<SetupInventory> CollectAsync(CancellationToken cancellationToken) => Task.Run(() => Collect(cancellationToken), cancellationToken);

    private SetupInventory Collect(CancellationToken ct)
    {
        var warnings = new List<string>();
        var cpus = Safe("CPU", ReadCpus, warnings, []);
        ct.ThrowIfCancellationRequested();
        var gpus = Safe("GPU", ReadGpus, warnings, []);
        ct.ThrowIfCancellationRequested();
        var memory = Safe("RAM", ReadMemory, warnings, MemoryInventory.Empty);
        ct.ThrowIfCancellationRequested();
        var drives = Safe("armazenamento", () => ReadDrives(warnings), warnings, []);
        return new SetupInventory(DateTimeOffset.UtcNow, cpus, gpus, memory, drives, warnings);
    }

    private T Safe<T>(string area, Func<T> fn, List<string> warnings, T fallback)
    {
        try
        {
            return fn();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Falha ao identificar {Area} para a Nota do Setup", area);
            warnings.Add($"Não foi possível consultar {area}: {ex.Message}");
            return fallback;
        }
    }

    private static List<ManagementBaseObject> Query(string wql, string? scope = null)
    {
        using var searcher = scope is null ? new ManagementObjectSearcher(wql) : new ManagementObjectSearcher(scope, wql);
        searcher.Options.Timeout = TimeSpan.FromSeconds(20);
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().ToList();
    }

    private static object? Prop(ManagementBaseObject mo, string name)
    {
        try
        {
            return mo[name];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static string? Str(ManagementBaseObject mo, string name) => Prop(mo, name)?.ToString()?.Trim() is { Length: > 0 } s ? s : null;

    private static long? Long(ManagementBaseObject mo, string name) => Prop(mo, name) is { } v && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static int? Int(ManagementBaseObject mo, string name) => Long(mo, name) is { } l and >= int.MinValue and <= int.MaxValue ? (int)l : null;

    private static List<CpuIdentity> ReadCpus()
    {
        var list = new List<CpuIdentity>();
        foreach (var mo in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, Architecture FROM Win32_Processor"))
        {
            using (mo)
            {
                if (Str(mo, "Name") is not { } name)
                {
                    continue;
                }

                var manufacturer = Str(mo, "Manufacturer") switch
                {
                    "GenuineIntel" => "Intel",
                    "AuthenticAMD" => "AMD",
                    var other => other,
                };
                list.Add(new CpuIdentity(name, manufacturer, Int(mo, "NumberOfCores") is > 0 and var c ? c : null, Int(mo, "NumberOfLogicalProcessors") is > 0 and var t ? t : null,
                    Int(mo, "MaxClockSpeed") is > 0 and var mhz ? mhz : null, HardwareClassifiers.ArchitectureName(Int(mo, "Architecture")), "WMI Win32_Processor"));
            }
        }

        return list;
    }

    private List<GpuAdapter> ReadGpus()
    {
        var list = new List<GpuAdapter>();
        foreach (var mo in Query("SELECT Name, PNPDeviceID, AdapterRAM, AdapterCompatibility FROM Win32_VideoController"))
        {
            using (mo)
            {
                if (Str(mo, "Name") is not { } name)
                {
                    continue;
                }

                var pnp = Str(mo, "PNPDeviceID");
                var pci = HardwareClassifiers.ParsePci(pnp);
                var (kind, reason) = HardwareClassifiers.ClassifyGpu(name, pnp, pci?.VendorId);
                var (vram, vramSource) = DedicatedMemory(pnp, Long(mo, "AdapterRAM"));
                var vendor = pci is not null ? GpuAdapter.VendorName(pci.VendorId) : Str(mo, "AdapterCompatibility");
                var key = pci?.ModelKey ?? "DEV:" + Hash(pnp ?? name)[..16];
                if (list.Any(g => g.Key == key))
                {
                    key += "#" + list.Count(g => g.Key.StartsWith(key, StringComparison.Ordinal));
                }

                list.Add(new GpuAdapter(key, name, vendor, pci?.VendorId, pci?.DeviceId, vram, vramSource, kind, reason, "WMI Win32_VideoController"));
            }
        }

        return list;
    }

    /// <summary>
    /// Win32_VideoController.AdapterRAM é de 32 bits (satura perto de 4 GiB). O valor de 64 bits do driver fica em
    /// HardwareInformation.qwMemorySize na chave de classe do dispositivo, localizada pelo valor "Driver" da chave Enum.
    /// </summary>
    private (long? Bytes, string? Source) DedicatedMemory(string? pnp, long? adapterRam)
    {
        if (pnp is not null)
        {
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp);
                if (enumKey?.GetValue("Driver") is string driver && driver.Length < 200)
                {
                    using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClass + driver);
                    var value = classKey?.GetValue("HardwareInformation.qwMemorySize") ?? classKey?.GetValue("HardwareInformation.MemorySize");
                    var bytes = value switch
                    {
                        long l => l,
                        int i => (long)(uint)i,
                        byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
                        byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0),
                        _ => (long?)null,
                    };
                    if (bytes is > 0)
                    {
                        return (bytes, "Registro do driver de vídeo");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Registro do adaptador {Pnp} inacessível", pnp);
            }
        }

        return adapterRam is > 0 and < 0xFFF00000L ? (adapterRam, "WMI AdapterRAM") : (null, null);
    }

    private static MemoryInventory ReadMemory()
    {
        long? installed = null;
        if (NativeMethods.GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0)
        {
            installed = checked((long)kb * 1024);
        }

        var modules = new List<MemoryModule>();
        foreach (var mo in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            using (mo)
            {
                var cap = Long(mo, "Capacity");
                if (cap is not > 0)
                {
                    continue;
                }

                var speed = Int(mo, "ConfiguredClockSpeed") is > 0 and var cs ? cs : Int(mo, "Speed") is > 0 and var s ? s : (int?)null;
                var type = HardwareClassifiers.MemoryTypeFromSmbios(Int(mo, "SMBIOSMemoryType")) ?? HardwareClassifiers.MemoryTypeFromSmbios(Int(mo, "MemoryType"));
                modules.Add(new MemoryModule(cap.Value, speed, type, Clean(Str(mo, "Manufacturer")), Clean(Str(mo, "PartNumber"))));
            }
        }

        long? total = modules.Count > 0 ? modules.Sum(m => m.CapacityBytes) : null;
        return new MemoryInventory(installed ?? total, installed is not null ? "firmware (SMBIOS)" : total is not null ? "soma dos módulos (WMI)" : null, total, modules);
    }

    private static string? Clean(string? value) =>
        value is null || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || value.Equals("Undefined", StringComparison.OrdinalIgnoreCase)
        || value.All(c => c == '0' || c == ' ') ? null : value;

    private List<PhysicalDrive> ReadDrives(List<string> warnings)
    {
        try
        {
            var msft = Query("SELECT DeviceId, FriendlyName, Model, Size, MediaType, BusType, UniqueId FROM MSFT_PhysicalDisk", @"\\.\root\Microsoft\Windows\Storage");
            var list = new List<PhysicalDrive>();
            foreach (var mo in msft)
            {
                using (mo)
                {
                    var model = Str(mo, "Model") ?? Str(mo, "FriendlyName") ?? "Disco sem nome";
                    var size = Long(mo, "Size");
                    var bus = HardwareClassifiers.BusFromMsft(Int(mo, "BusType"));
                    var identity = Str(mo, "UniqueId") ?? Str(mo, "DeviceId") ?? model;
                    list.Add(new PhysicalDrive("disk:" + Hash(identity + "|" + size)[..20], model, size, HardwareClassifiers.MediaFromMsft(Int(mo, "MediaType")), bus,
                        bus is DriveBus.Usb or DriveBus.SdCard ? true : null, "MSFT_PhysicalDisk"));
                }
            }

            if (list.Count > 0)
            {
                return list;
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogInformation(ex, "MSFT_PhysicalDisk indisponível; usando Win32_DiskDrive");
            warnings.Add("A API de armazenamento moderna (MSFT_PhysicalDisk) não respondeu; o tipo de mídia (HDD/SSD) pode ficar desconhecido.");
        }

        var fallback = new List<PhysicalDrive>();
        foreach (var mo in Query("SELECT Model, Size, InterfaceType, MediaType, PNPDeviceID, SerialNumber FROM Win32_DiskDrive"))
        {
            using (mo)
            {
                var model = Str(mo, "Model") ?? "Disco sem nome";
                var pnp = Str(mo, "PNPDeviceID") ?? "";
                var bus = pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase) ? DriveBus.Nvme : HardwareClassifiers.BusFromInterfaceType(Str(mo, "InterfaceType"));
                var media = Str(mo, "MediaType") ?? "";
                bool? removable = media.Contains("Removable", StringComparison.OrdinalIgnoreCase) || media.Contains("External", StringComparison.OrdinalIgnoreCase) ? true
                    : media.Contains("Fixed", StringComparison.OrdinalIgnoreCase) ? false : null;
                var size = Long(mo, "Size");
                var identity = Str(mo, "SerialNumber") ?? pnp;
                fallback.Add(new PhysicalDrive("disk:" + Hash(identity + "|" + model + "|" + size)[..20], model, size, DriveMedia.Unknown, bus, removable, "Win32_DiskDrive"));
            }
        }

        return fallback;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
