using Framex.Core.SetupScore;

namespace Framex.Tests.SetupScore;

/// <summary>Dados sintéticos da Nota do Setup — usados apenas em testes, nunca no aplicativo.</summary>
internal static class Synthetic
{
    public static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static BenchmarkDatasetDefinition CpuDef { get; } = new("test-cpu", SetupCategory.Cpu, "CPU", "Base sintética · CPU", "4.5.0", "https://example.invalid/cpu", null);

    public static BenchmarkDatasetDefinition NvidiaDef { get; } = new("test-nvidia", SetupCategory.Gpu, "OptiX", "Base sintética · NVIDIA", "4.5.0", "https://example.invalid/optix", 0x10DE);

    public static BenchmarkDatasetDefinition AmdDef { get; } = new("test-amd", SetupCategory.Gpu, "HIP", "Base sintética · AMD", "4.5.0", "https://example.invalid/hip", 0x1002);

    public static IReadOnlyList<BenchmarkDatasetDefinition> Definitions { get; } = [CpuDef, NvidiaDef, AmdDef];

    public static BenchmarkDataset Dataset(BenchmarkDatasetDefinition def, IEnumerable<(string Name, double Score, int Samples)> rows, IEnumerable<RejectedRecord>? rejected = null)
    {
        var records = rows.Select(r => new BenchmarkRecord(r.Name, r.Score, r.Samples)).ToList();
        var rej = rejected?.ToList() ?? [];
        return new BenchmarkDataset(def, new BenchmarkDatasetInfo(def.Id, def.Title, def.SourceUrl, "teste", "4.5.0", Now, "00", records.Count, rej.Count, "sintética"), records, rej);
    }

    public static BenchmarkDataset Cpus(IEnumerable<RejectedRecord>? rejected = null) => Dataset(CpuDef,
    [
        ("Intel Core i5-10400F CPU @ 2.90GHz", 113.7, 37),
        ("Intel Core i5-10400 CPU @ 2.90GHz", 112.0, 40),
        ("AMD Ryzen 5 3600 6-Core Processor", 130.3, 156),
        ("AMD Ryzen 5 3600X 6-Core Processor", 136.0, 80),
        ("AMD Ryzen 7 5800X3D 8-Core Processor", 250.0, 60),
        ("AMD Ryzen 7 5800X 8-Core Processor", 240.0, 90),
        ("Intel Xeon CPU E3-1230 V2 @ 3.30GHz", 34.1, 2),
        ("Intel Core i7-12700H", 260.0, 30),
        ("Intel Core i7-12700", 280.0, 30),
        ("Intel Core i7-12700K", 300.0, 50),
        ("Intel Core i7-12700KF", 301.0, 45),
    ], rejected);

    public static BenchmarkDataset Nvidia() => Dataset(NvidiaDef,
    [
        ("NVIDIA GeForce GTX 1660 SUPER", 748.0, 112),
        ("NVIDIA GeForce GTX 1660", 700.0, 90),
        ("NVIDIA GeForce GTX 1660 Ti", 790.0, 100),
        ("NVIDIA GeForce RTX 3060", 3300.0, 500),
        ("NVIDIA GeForce RTX 3060 Ti", 4000.0, 300),
        ("NVIDIA GeForce RTX 3060 Laptop GPU", 2600.0, 200),
        ("NVIDIA GeForce RTX 4090", 13000.0, 400),
    ]);

    public static BenchmarkDataset Amd() => Dataset(AmdDef,
    [
        ("AMD Radeon RX 6600", 933.3, 89),
        ("AMD Radeon RX 6600 XT", 1050.0, 70),
        ("AMD Radeon RX 7900 XTX", 4100.0, 60),
    ]);

    public static CpuIdentity Cpu(string name = "Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz") => new(name, "Intel", 6, 12, 2904, "x64", "teste");

    public static GpuAdapter Gpu(string name, ushort vendor, GpuKind kind, ushort device = 0x1234, string? key = null) =>
        new(key ?? $"PCI\\VEN_{vendor:X4}&DEV_{device:X4}", name, GpuAdapter.VendorName(vendor), vendor, device, 6L << 30, "teste", kind, "teste", "teste");

    public static MemoryInventory Ram(double gib = 16, int modules = 2) =>
        new((long)(gib * 1073741824), "teste", (long)(gib * 1073741824), Enumerable.Range(0, modules).Select(_ => new MemoryModule((long)(gib / modules * 1073741824), 3200, "DDR4", "Kingston", "KF432")).ToList());

    public static PhysicalDrive Drive(string key, long sizeBytes, DriveMedia media = DriveMedia.Ssd, DriveBus bus = DriveBus.Nvme, string model = "Disco", bool? removable = null) =>
        new(key, model, sizeBytes, media, bus, removable, "teste");

    public static SetupInventory Inventory(
        IReadOnlyList<CpuIdentity>? cpus = null,
        IReadOnlyList<GpuAdapter>? gpus = null,
        MemoryInventory? ram = null,
        IReadOnlyList<PhysicalDrive>? drives = null) =>
        new(Now, cpus ?? [Cpu()], gpus ?? [Gpu("NVIDIA GeForce GTX 1660 SUPER", 0x10DE, GpuKind.Dedicated)], ram ?? Ram(),
            drives ?? [Drive("a", 256_060_514_304), Drive("b", 120_034_123_776, bus: DriveBus.Sata)], []);

    public static Dictionary<string, DatasetLoadResult> AllLoaded() => new()
    {
        [CpuDef.Id] = new DatasetLoadResult(CpuDef, Cpus(), DatasetFailure.None, null),
        [NvidiaDef.Id] = new DatasetLoadResult(NvidiaDef, Nvidia(), DatasetFailure.None, null),
        [AmdDef.Id] = new DatasetLoadResult(AmdDef, Amd(), DatasetFailure.None, null),
    };

    public static SetupEvaluation Build(SetupInventory? inventory = null, bool includeStorage = true, Dictionary<string, DatasetLoadResult>? loads = null, IReadOnlyList<ComponentOverride>? overrides = null) =>
        SetupScorer.Build(new ScoringContext(inventory ?? Inventory(), includeStorage, Definitions, loads ?? AllLoaded(), overrides ?? [], Guid.Parse("11111111-1111-1111-1111-111111111111"), Now, "teste"));
}

internal sealed class FakeInventory : ISetupInventoryProvider
{
    public SetupInventory Inventory { get; set; } = Synthetic.Inventory();

    public TimeSpan Delay { get; set; }

    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public async Task<SetupInventory> CollectAsync(CancellationToken cancellationToken)
    {
        Calls++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        return Inventory;
    }
}

internal sealed class FakeDatasets : IBenchmarkDatasetSource
{
    public Dictionary<string, DatasetLoadResult> Results { get; } = Synthetic.AllLoaded();

    public TimeSpan Delay { get; set; }

    public IReadOnlyList<BenchmarkDatasetDefinition> Definitions => Synthetic.Definitions;

    public async Task<DatasetLoadResult> LoadAsync(BenchmarkDatasetDefinition definition, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Results.TryGetValue(definition.Id, out var r) ? r : new DatasetLoadResult(definition, null, DatasetFailure.NotAvailable, "ausente");
    }

    public Task<IReadOnlyList<DatasetUpdateResult>> UpdateAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DatasetUpdateResult>>([]);
}

internal sealed class InMemorySetupRepository : ISetupEvaluationRepository
{
    public List<SetupEvaluation> Saved { get; } = [];

    public List<ComponentOverride> Overrides { get; } = [];

    public bool FailOnSave { get; set; }

    public void Save(SetupEvaluation evaluation)
    {
        if (FailOnSave)
        {
            throw new IOException("disco cheio");
        }

        Saved.Add(evaluation);
    }

    public IReadOnlyList<SetupEvaluationSummary> List(int limit = 50) => Saved
        .Select(e => new SetupEvaluationSummary(e.Id, e.CreatedAt, e.MethodologyVersion, e.IncludeStorage, e.Overall.Completeness, e.Overall.Score, e.Overall.Coverage)).ToList();

    public SetupEvaluation? Get(Guid id) => Saved.FirstOrDefault(e => e.Id == id);

    public void Delete(Guid id) => Saved.RemoveAll(e => e.Id == id);

    public IReadOnlyList<ComponentOverride> GetOverrides() => Overrides;

    public void SaveOverride(ComponentOverride value)
    {
        Overrides.RemoveAll(o => o.Category == value.Category && o.DeviceKey == value.DeviceKey);
        Overrides.Add(value);
    }

    public void RemoveOverride(SetupCategory category, string deviceKey) => Overrides.RemoveAll(o => o.Category == category && o.DeviceKey == deviceKey);
}
