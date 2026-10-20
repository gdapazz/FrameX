namespace Framex.Core.SetupScore;

public enum SetupCategory
{
    Gpu,
    Cpu,
    Ram,
    Storage,
}

public enum GpuKind
{
    Dedicated,
    Integrated,
    Virtual,
    Unknown,
}

public enum DriveMedia
{
    Unknown,
    Hdd,
    Ssd,
}

public enum DriveBus
{
    Unknown,
    Nvme,
    Sata,
    Ata,
    Scsi,
    Sas,
    Raid,
    Usb,
    SdCard,
    Iscsi,
    Virtual,
    FileBackedVirtual,
    StorageSpaces,
    Other,
}

/// <summary>Processador reportado pelo Windows. Campos nulos = não informados pela API (nunca presumidos).</summary>
public sealed record CpuIdentity(
    string Name,
    string? Manufacturer,
    int? PhysicalCores,
    int? LogicalProcessors,
    int? ReportedClockMhz,
    string? Architecture,
    string Source);

/// <summary>Adaptador gráfico. <see cref="Key"/> identifica o modelo (VEN/DEV/SUBSYS), não a unidade física.</summary>
public sealed record GpuAdapter(
    string Key,
    string Name,
    string? Vendor,
    ushort? PciVendorId,
    ushort? PciDeviceId,
    long? DedicatedMemoryBytes,
    string? DedicatedMemorySource,
    GpuKind Kind,
    string KindReason,
    string Source)
{
    public static string VendorName(ushort? pciVendor) => pciVendor switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        _ => "Desconhecido",
    };
}

public sealed record MemoryModule(long CapacityBytes, int? SpeedMts, string? Type, string? Manufacturer, string? PartNumber);

/// <summary>
/// Memória principal instalada. <see cref="InstalledBytes"/> é a capacidade física instalada,
/// nunca a memória livre ou em uso.
/// </summary>
public sealed record MemoryInventory(
    long? InstalledBytes,
    string? InstalledSource,
    long? ModulesTotalBytes,
    IReadOnlyList<MemoryModule> Modules)
{
    public static MemoryInventory Empty { get; } = new(null, null, null, []);

    /// <summary>Tipo comum a todos os módulos (ex.: DDR4) ou nulo se desconhecido/misto.</summary>
    public string? CommonType => Modules.Count > 0 && Modules.All(m => m.Type is not null) && Modules.Select(m => m.Type).Distinct().Count() == 1
        ? Modules[0].Type
        : null;

    /// <summary>Menor velocidade configurada entre os módulos (a memória opera na do módulo mais lento), se todas forem conhecidas.</summary>
    public int? EffectiveSpeedMts => Modules.Count > 0 && Modules.All(m => m.SpeedMts is > 0) ? Modules.Min(m => m.SpeedMts) : null;
}

/// <summary>
/// Dispositivo físico de armazenamento. <see cref="DeviceKey"/> é um identificador derivado (hash) usado só
/// para deduplicação; o número de série nunca é guardado nem exibido.
/// </summary>
public sealed record PhysicalDrive(
    string DeviceKey,
    string Model,
    long? SizeBytes,
    DriveMedia Media,
    DriveBus Bus,
    bool? Removable,
    string Source)
{
    public string TypeLabel => (Media, Bus) switch
    {
        (DriveMedia.Hdd, _) => "HDD",
        (_, DriveBus.Nvme) => "SSD NVMe",
        (DriveMedia.Ssd, DriveBus.Sata or DriveBus.Ata) => "SSD SATA",
        (DriveMedia.Ssd, _) => "SSD",
        (_, DriveBus.Sata or DriveBus.Ata) => "Unidade SATA (tipo de mídia desconhecido)",
        _ => "Tipo desconhecido",
    };

    public string BusLabel => Bus switch
    {
        DriveBus.Nvme => "NVMe",
        DriveBus.Sata => "SATA",
        DriveBus.Ata => "ATA",
        DriveBus.Scsi => "SCSI",
        DriveBus.Sas => "SAS",
        DriveBus.Raid => "RAID",
        DriveBus.Usb => "USB",
        DriveBus.SdCard => "SD/MMC",
        DriveBus.Iscsi => "iSCSI (rede)",
        DriveBus.Virtual or DriveBus.FileBackedVirtual => "Virtual",
        DriveBus.StorageSpaces => "Espaços de Armazenamento",
        DriveBus.Other => "Outro",
        _ => "Desconhecida",
    };
}

/// <summary>Inventário local usado pela Nota do Setup. Coletado sem internet e sem privilégios de administrador.</summary>
public sealed record SetupInventory(
    DateTimeOffset CollectedAt,
    IReadOnlyList<CpuIdentity> Cpus,
    IReadOnlyList<GpuAdapter> Gpus,
    MemoryInventory Memory,
    IReadOnlyList<PhysicalDrive> Drives,
    IReadOnlyList<string> Warnings)
{
    public static SetupInventory Empty { get; } = new(DateTimeOffset.MinValue, [], [], MemoryInventory.Empty, [], []);
}

public interface ISetupInventoryProvider
{
    Task<SetupInventory> CollectAsync(CancellationToken cancellationToken);
}
