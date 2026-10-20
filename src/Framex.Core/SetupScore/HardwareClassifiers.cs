using System.Globalization;
using System.Text.RegularExpressions;

namespace Framex.Core.SetupScore;

public sealed record PciIdentity(ushort VendorId, ushort DeviceId, string ModelKey);

/// <summary>Classificadores puros usados pelo inventário (testáveis sem hardware).</summary>
public static partial class HardwareClassifiers
{
    private static readonly string[] VirtualGpuMarkers =
        ["parsec", "virtual", "basic display", "basic render", "remote", "indirect", "vmware", "virtualbox", "citrix", "hyper-v", "spacedesk", "displaylink"];

    /// <summary>Extrai VEN/DEV de um PNPDeviceID ("PCI\VEN_10DE&amp;DEV_21C4&amp;SUBSYS_13D310DE&amp;REV_A1\..."); a chave omite revisão e instância.</summary>
    public static PciIdentity? ParsePci(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return null;
        }

        var m = PciPattern().Match(pnpDeviceId);
        if (!m.Success)
        {
            return null;
        }

        var ven = ushort.Parse(m.Groups["ven"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var dev = ushort.Parse(m.Groups["dev"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var key = $"PCI\\VEN_{ven:X4}&DEV_{dev:X4}" + (m.Groups["sub"].Success ? "&SUBSYS_" + m.Groups["sub"].Value.ToUpperInvariant() : "");
        return new PciIdentity(ven, dev, key);
    }

    /// <summary>
    /// Dedicada × integrada × virtual, apenas por evidências explícitas (barramento do dispositivo e família no nome).
    /// Na dúvida, <see cref="GpuKind.Unknown"/> — nunca presume que o primeiro adaptador é a GPU de jogos.
    /// </summary>
    public static (GpuKind Kind, string Reason) ClassifyGpu(string name, string? pnpDeviceId, ushort? vendor)
    {
        var lower = name.ToLowerInvariant();
        var pnp = pnpDeviceId?.ToUpperInvariant() ?? "";
        if (pnp.StartsWith("ROOT\\", StringComparison.Ordinal) || pnp.StartsWith("SWD\\", StringComparison.Ordinal))
        {
            return (GpuKind.Virtual, "Dispositivo de software (não é um adaptador PCI físico).");
        }

        if (VirtualGpuMarkers.Any(lower.Contains))
        {
            return (GpuKind.Virtual, "O nome indica um adaptador virtual ou de exibição remota.");
        }

        switch (vendor)
        {
            case 0x10DE:
                return (GpuKind.Dedicated, "GPUs NVIDIA para PC são placas dedicadas ou módulos dedicados em notebooks.");
            case 0x1002:
                if (AmdDedicated().IsMatch(lower))
                {
                    return (GpuKind.Dedicated, "Família Radeon RX/Pro/R9/R7 (dedicada).");
                }

                if (AmdIntegrated().IsMatch(lower))
                {
                    return (GpuKind.Integrated, "Nome de gráfico integrado AMD (Radeon Graphics / Vega / série M de APU).");
                }

                break;
            case 0x8086:
                if (IntelArcDedicated().IsMatch(lower))
                {
                    return (GpuKind.Dedicated, "Intel Arc com número de modelo de placa dedicada.");
                }

                if (IntelIntegrated().IsMatch(lower))
                {
                    return (GpuKind.Integrated, "Gráfico integrado Intel (UHD/HD/Iris/Arc Graphics).");
                }

                break;
        }

        return (GpuKind.Unknown, "Não foi possível confirmar se o adaptador é dedicado ou integrado.");
    }

    /// <summary>MSFT_PhysicalDisk.MediaType: 3 = HDD, 4 = SSD, 5 = SCM; 0 = não especificado.</summary>
    public static DriveMedia MediaFromMsft(int? mediaType) => mediaType switch
    {
        3 => DriveMedia.Hdd,
        4 or 5 => DriveMedia.Ssd,
        _ => DriveMedia.Unknown,
    };

    /// <summary>MSFT_PhysicalDisk.BusType (documentação STORAGE_BUS_TYPE).</summary>
    public static DriveBus BusFromMsft(int? busType) => busType switch
    {
        1 => DriveBus.Scsi,
        2 or 3 => DriveBus.Ata,
        7 => DriveBus.Usb,
        8 => DriveBus.Raid,
        9 => DriveBus.Iscsi,
        10 => DriveBus.Sas,
        11 => DriveBus.Sata,
        12 or 13 => DriveBus.SdCard,
        14 => DriveBus.Virtual,
        15 => DriveBus.FileBackedVirtual,
        16 => DriveBus.StorageSpaces,
        17 => DriveBus.Nvme,
        null or 0 => DriveBus.Unknown,
        _ => DriveBus.Other,
    };

    /// <summary>Win32_DiskDrive.InterfaceType (fallback, menos preciso: NVMe costuma aparecer como "SCSI").</summary>
    public static DriveBus BusFromInterfaceType(string? interfaceType) => interfaceType?.Trim().ToUpperInvariant() switch
    {
        "USB" => DriveBus.Usb,
        "IDE" => DriveBus.Ata,
        "1394" => DriveBus.Other,
        "SCSI" => DriveBus.Scsi,
        "HDC" => DriveBus.Ata,
        _ => DriveBus.Unknown,
    };

    /// <summary>Win32_PhysicalMemory.SMBIOSMemoryType (tabela SMBIOS tipo 17). Valores não listados ficam desconhecidos.</summary>
    public static string? MemoryTypeFromSmbios(int? smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    public static string? ArchitectureName(int? code) => code switch
    {
        0 => "x86",
        5 => "ARM",
        6 => "Itanium",
        9 => "x64",
        12 => "ARM64",
        _ => null,
    };

    [GeneratedRegex(@"PCI\\VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})(?:&SUBSYS_(?<sub>[0-9A-F]{8}))?", RegexOptions.IgnoreCase)]
    private static partial Regex PciPattern();

    [GeneratedRegex(@"\brx\s*\d|\bradeon\s+pro\b|\br9\s|\br7\s|\bfirepro\b|\bradeon\s+vii\b")]
    private static partial Regex AmdDedicated();

    [GeneratedRegex(@"radeon\s*\(tm\)\s*graphics|radeon\s+graphics|vega\s+\d+\s+graphics|radeon\s+\d{3}m\b|radeon\s+r\d\s+graphics")]
    private static partial Regex AmdIntegrated();

    [GeneratedRegex(@"\barc\s*\(tm\)\s*[ab]\d{3}|\barc\s+[ab]\d{3}")]
    private static partial Regex IntelArcDedicated();

    [GeneratedRegex(@"\buhd\b|\bhd\s+graphics|\biris\b|\barc\s*\(tm\)\s*graphics|\barc\s+graphics|intel\s*\(r\)\s*graphics")]
    private static partial Regex IntelIntegrated();
}
