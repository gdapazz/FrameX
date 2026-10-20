using Framex.Core.SetupScore;

namespace Framex.Tests.SetupScore;

public class StorageCapacityTests
{
    private static PhysicalDrive D(string key, long bytes, DriveMedia media = DriveMedia.Ssd, DriveBus bus = DriveBus.Nvme, string model = "Disco", bool? removable = null) =>
        Synthetic.Drive(key, bytes, media, bus, model, removable);

    [Fact]
    public void SingleSsd()
    {
        var r = StorageCapacityAggregator.Aggregate([D("a", 512_110_190_592)]);
        Assert.Equal(512_110_190_592, r.TotalBytes);
        Assert.Equal(1, r.IncludedCount);
        Assert.Equal("512 GB", StorageCapacityAggregator.FormatNominal(r.TotalBytes));
    }

    [Fact]
    public void TwoSsds_AreSummed()
    {
        var r = StorageCapacityAggregator.Aggregate([D("a", 500_107_862_016), D("b", 1_000_204_886_016, bus: DriveBus.Sata)]);
        Assert.Equal(1_500_312_748_032, r.TotalBytes);
        Assert.Equal("1,5 TB", StorageCapacityAggregator.FormatNominal(r.TotalBytes));
    }

    [Fact]
    public void Nvme256_PlusSata120_Is376GbNominal()
    {
        var r = StorageCapacityAggregator.Aggregate([D("nvme", 256_060_514_304, bus: DriveBus.Nvme), D("sata", 120_034_123_776, bus: DriveBus.Sata)]);
        Assert.Equal("376 GB", StorageCapacityAggregator.FormatNominal(r.TotalBytes));
        Assert.Equal("350,3 GiB", StorageCapacityAggregator.FormatBinary(r.TotalBytes));
        Assert.Equal(["SSD NVMe", "SSD SATA"], r.Drives.Select(d => d.Drive.TypeLabel));
    }

    [Fact]
    public void Hdd_PlusSsd_AreBothCounted_TypeIsDescriptiveOnly()
    {
        var r = StorageCapacityAggregator.Aggregate([D("h", 2_000_398_934_016, DriveMedia.Hdd, DriveBus.Sata), D("s", 500_107_862_016)]);
        Assert.Equal(2, r.IncludedCount);
        Assert.Equal(2_500_506_796_032, r.TotalBytes);
        Assert.Equal("HDD", r.Drives[0].Drive.TypeLabel);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(2_000_000_000_000_000L)]
    public void InvalidCapacity_IsNotSummed_AndCountsAsUncertain(long size)
    {
        var r = StorageCapacityAggregator.Aggregate([D("ok", 256_000_000_000), D("bad", size)]);
        Assert.Equal(256_000_000_000, r.TotalBytes);
        Assert.Equal(DriveDecision.ExcludedInvalidCapacity, r.Drives[1].Decision);
        Assert.Equal(1, r.UncertainCount);
    }

    [Fact]
    public void NullCapacity_IsInvalid()
    {
        var r = StorageCapacityAggregator.Aggregate([new PhysicalDrive("x", "Disco", null, DriveMedia.Ssd, DriveBus.Sata, false, "t")]);
        Assert.Equal(0, r.IncludedCount);
        Assert.Equal(DriveDecision.ExcludedInvalidCapacity, r.Drives[0].Decision);
    }

    [Fact]
    public void NetworkDrive_IsExcluded()
    {
        var r = StorageCapacityAggregator.Aggregate([D("a", 256_000_000_000), D("net", 4_000_000_000_000, bus: DriveBus.Iscsi)]);
        Assert.Equal(256_000_000_000, r.TotalBytes);
        Assert.Equal(DriveDecision.ExcludedNetwork, r.Drives[1].Decision);
    }

    [Fact]
    public void MultiplePartitions_OfOneDevice_CountOnce()
    {
        // O agregador trabalha por dispositivo; se o mesmo disco aparecer duas vezes (ex.: por partições), entra uma única vez.
        var r = StorageCapacityAggregator.Aggregate([D("disk0", 1_000_204_886_016), D("disk0", 1_000_204_886_016)]);
        Assert.Equal(1_000_204_886_016, r.TotalBytes);
        Assert.Equal(DriveDecision.ExcludedDuplicate, r.Drives[1].Decision);
    }

    [Theory]
    [InlineData(DriveBus.Virtual, "Disco")]
    [InlineData(DriveBus.FileBackedVirtual, "Disco")]
    [InlineData(DriveBus.StorageSpaces, "Storage Space")]
    [InlineData(DriveBus.Sata, "VBOX HARDDISK")]
    [InlineData(DriveBus.Scsi, "Msft Virtual Disk")]
    public void VirtualDrives_AreExcluded(DriveBus bus, string model)
    {
        var r = StorageCapacityAggregator.Aggregate([D("v", 100_000_000_000, bus: bus, model: model)]);
        Assert.Equal(0, r.IncludedCount);
        Assert.Equal(DriveDecision.ExcludedVirtual, r.Drives[0].Decision);
    }

    [Theory]
    [InlineData(DriveBus.Usb, null)]
    [InlineData(DriveBus.SdCard, null)]
    [InlineData(DriveBus.Sata, true)]
    public void RemovableDrives_AreExcludedByDefault(DriveBus bus, bool? removable)
    {
        var r = StorageCapacityAggregator.Aggregate([D("r", 64_000_000_000, bus: bus, removable: removable)]);
        Assert.Equal(DriveDecision.ExcludedRemovable, r.Drives[0].Decision);
        Assert.Equal(0, r.TotalBytes);
    }

    [Fact]
    public void UnknownBus_IsNotSummed_AndIsUncertain()
    {
        var r = StorageCapacityAggregator.Aggregate([D("u", 500_000_000_000, DriveMedia.Unknown, DriveBus.Unknown)]);
        Assert.Equal(DriveDecision.ExcludedUnclassified, r.Drives[0].Decision);
        Assert.Equal(1, r.UncertainCount);
    }

    [Fact]
    public void GbVsGib_AreReportedSeparately()
    {
        const long oneTb = 1_000_000_000_000;
        Assert.Equal("1 TB", StorageCapacityAggregator.FormatNominal(oneTb));
        Assert.Equal("931,3 GiB", StorageCapacityAggregator.FormatBinary(oneTb));
    }

    [Fact]
    public void TotalLargerThanInt32_IsExact()
    {
        var r = StorageCapacityAggregator.Aggregate([D("a", 8_001_563_222_016, DriveMedia.Hdd, DriveBus.Sata), D("b", 8_001_563_222_016, DriveMedia.Hdd, DriveBus.Sata), D("c", 8_001_563_222_016, DriveMedia.Hdd, DriveBus.Sas)]);
        Assert.Equal(24_004_689_666_048, r.TotalBytes);
        Assert.True(r.TotalBytes > int.MaxValue);
        Assert.Equal("24 TB", StorageCapacityAggregator.FormatNominal(r.TotalBytes));
    }

    [Fact]
    public void RaidVolume_IsCounted_WithWarning()
    {
        var r = StorageCapacityAggregator.Aggregate([D("raid", 4_000_000_000_000, bus: DriveBus.Raid, model: "RAID 1")]);
        Assert.Equal(1, r.IncludedCount);
        Assert.Single(r.Warnings);
    }

    [Theory]
    [InlineData(3, 11, DriveMedia.Hdd, DriveBus.Sata)]
    [InlineData(4, 17, DriveMedia.Ssd, DriveBus.Nvme)]
    [InlineData(0, 7, DriveMedia.Unknown, DriveBus.Usb)]
    [InlineData(4, 15, DriveMedia.Ssd, DriveBus.FileBackedVirtual)]
    [InlineData(null, null, DriveMedia.Unknown, DriveBus.Unknown)]
    public void MsftEnums_AreMapped(int? media, int? bus, DriveMedia expectedMedia, DriveBus expectedBus)
    {
        Assert.Equal(expectedMedia, HardwareClassifiers.MediaFromMsft(media));
        Assert.Equal(expectedBus, HardwareClassifiers.BusFromMsft(bus));
    }

    [Fact]
    public void M2Form_DoesNotImplyNvme()
    {
        // Um SSD M.2 SATA é reportado com barramento SATA: o rótulo não vira NVMe por causa do nome.
        var d = D("m2", 500_000_000_000, DriveMedia.Ssd, DriveBus.Sata, "Crucial MX500 M.2");
        Assert.Equal("SSD SATA", d.TypeLabel);
    }
}
