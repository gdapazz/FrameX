using System.Globalization;

namespace Framex.Core.SetupScore;

public enum DriveDecision
{
    Included,
    ExcludedRemovable,
    ExcludedNetwork,
    ExcludedVirtual,
    ExcludedDuplicate,
    ExcludedInvalidCapacity,
    ExcludedUnclassified,
}

public sealed record DriveAssessment(PhysicalDrive Drive, DriveDecision Decision, string Reason)
{
    public bool Included => Decision == DriveDecision.Included;
}

public sealed record StorageCapacityResult(long TotalBytes, IReadOnlyList<DriveAssessment> Drives, IReadOnlyList<string> Warnings)
{
    public int IncludedCount => Drives.Count(d => d.Included);

    /// <summary>Unidades que não puderam ser classificadas ou medidas com segurança (afetam a confiabilidade).</summary>
    public int UncertainCount => Drives.Count(d => d.Decision is DriveDecision.ExcludedUnclassified or DriveDecision.ExcludedInvalidCapacity);
}

/// <summary>
/// Soma a capacidade total nominal dos dispositivos físicos elegíveis. Política padrão:
/// entram unidades internas (NVMe, SATA, ATA, SAS, SCSI e RAID de controladora); ficam fora removíveis (USB, SD),
/// de rede (iSCSI), virtuais (VHD, máquinas virtuais, Espaços de Armazenamento) e duplicatas.
/// Usa sempre a capacidade do dispositivo, nunca o espaço livre ou o tamanho de partições.
/// </summary>
public static class StorageCapacityAggregator
{
    /// <summary>Limite de sanidade por dispositivo (1 PB). Valores acima indicam leitura inválida.</summary>
    public const long MaxPlausibleDriveBytes = 1_000_000_000_000_000;

    private static readonly string[] VirtualModelMarkers =
    [
        "virtual disk", "msft virtual", "vmware", "vbox harddisk", "virtualbox", "qemu harddisk", "qemu nvme", "xen virtual", "vhd", "parallels",
    ];

    public static StorageCapacityResult Aggregate(IEnumerable<PhysicalDrive> drives)
    {
        var assessments = new List<DriveAssessment>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;

        foreach (var drive in drives)
        {
            var decision = Classify(drive, out var reason);
            if (decision == DriveDecision.Included && !seen.Add(drive.DeviceKey))
            {
                decision = DriveDecision.ExcludedDuplicate;
                reason = "O mesmo dispositivo físico foi reportado mais de uma vez (outro caminho de consulta); contado uma única vez.";
            }

            if (decision == DriveDecision.Included)
            {
                total = checked(total + drive.SizeBytes!.Value);
                if (drive.Bus == DriveBus.Raid)
                {
                    warnings.Add($"\"{drive.Model}\" é um volume RAID de controladora: a capacidade é a que a controladora informa, não a soma dos discos membros.");
                }
            }

            assessments.Add(new DriveAssessment(drive, decision, reason));
        }

        return new StorageCapacityResult(total, assessments, warnings);
    }

    private static DriveDecision Classify(PhysicalDrive drive, out string reason)
    {
        var model = drive.Model.ToLowerInvariant();
        if (drive.Bus is DriveBus.Virtual or DriveBus.FileBackedVirtual || VirtualModelMarkers.Any(model.Contains))
        {
            reason = "Unidade virtual (arquivo de disco ou máquina virtual): não é armazenamento físico deste computador.";
            return DriveDecision.ExcludedVirtual;
        }

        if (drive.Bus == DriveBus.StorageSpaces)
        {
            reason = "Volume de Espaços de Armazenamento: a capacidade já é contada pelos discos físicos do pool.";
            return DriveDecision.ExcludedVirtual;
        }

        if (drive.Bus == DriveBus.Iscsi)
        {
            reason = "Unidade de rede (iSCSI): não entra na nota de armazenamento local.";
            return DriveDecision.ExcludedNetwork;
        }

        if (drive.Bus is DriveBus.Usb or DriveBus.SdCard || drive.Removable == true)
        {
            reason = "Dispositivo removível (USB/cartão): pode ser desconectado e não entra na nota por padrão.";
            return DriveDecision.ExcludedRemovable;
        }

        if (drive.SizeBytes is not { } size || size <= 0 || size > MaxPlausibleDriveBytes)
        {
            reason = "O Windows não informou uma capacidade válida para este dispositivo; ele não foi somado.";
            return DriveDecision.ExcludedInvalidCapacity;
        }

        if (drive.Bus is DriveBus.Unknown or DriveBus.Other)
        {
            reason = "Não foi possível determinar a interface do dispositivo para confirmar que é uma unidade interna; ele não foi somado.";
            return DriveDecision.ExcludedUnclassified;
        }

        reason = $"Unidade interna ({drive.TypeLabel}).";
        return DriveDecision.Included;
    }

    /// <summary>Capacidade nominal em unidades decimais (1 GB = 10⁹ bytes), como anunciada pelos fabricantes.</summary>
    public static string FormatNominal(long bytes)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var gb = bytes / 1e9;
        return gb >= 1000
            ? (gb / 1000).ToString("0.##", pt) + " TB"
            : gb.ToString(gb >= 10 ? "0" : "0.#", pt) + " GB";
    }

    /// <summary>Mesma capacidade em unidades binárias (1 GiB = 1024³ bytes), como o Explorador do Windows exibe.</summary>
    public static string FormatBinary(long bytes)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var gib = bytes / 1073741824.0;
        return gib >= 1024
            ? (gib / 1024).ToString("0.##", pt) + " TiB"
            : gib.ToString("0.#", pt) + " GiB";
    }
}
