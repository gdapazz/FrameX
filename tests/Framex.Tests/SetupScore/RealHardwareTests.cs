using Framex.Core.SetupScore;
using Framex.Infrastructure.Hardware;
using Framex.Infrastructure.SetupScore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Framex.Tests.SetupScore;

/// <summary>
/// Verificação opcional no hardware real (FRAMEX_REAL_HW=1). Não faz parte da suíte padrão porque depende da máquina;
/// só confere invariantes (nunca valores fixos) e imprime o inventário para inspeção manual.
/// </summary>
public class RealHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealMachine_InventoryAndEvaluation()
    {
        if (Environment.GetEnvironmentVariable("FRAMEX_REAL_HW") != "1")
        {
            output.WriteLine("Ignorado: defina FRAMEX_REAL_HW=1 para executar no hardware real.");
            return;
        }

        var inventory = await new WindowsSetupInventoryProvider(NullLogger<WindowsSetupInventoryProvider>.Instance).CollectAsync(CancellationToken.None);
        foreach (var c in inventory.Cpus)
        {
            output.WriteLine($"CPU: {c.Name} | {c.Manufacturer} | {c.PhysicalCores}C/{c.LogicalProcessors}T | {c.ReportedClockMhz} MHz | {c.Architecture}");
        }

        foreach (var g in inventory.Gpus)
        {
            output.WriteLine($"GPU: {g.Name} | {g.Key} | {g.Kind} ({g.KindReason}) | VRAM {g.DedicatedMemoryBytes} via {g.DedicatedMemorySource}");
        }

        output.WriteLine($"RAM: instalada {inventory.Memory.InstalledBytes} ({inventory.Memory.InstalledSource}), módulos {inventory.Memory.ModulesTotalBytes}, {inventory.Memory.CommonType}, {inventory.Memory.EffectiveSpeedMts} MT/s");
        foreach (var d in inventory.Drives)
        {
            output.WriteLine($"Disco: {d.Model} | {d.SizeBytes} | {d.TypeLabel} | {d.BusLabel} | {d.Source}");
        }

        foreach (var w in inventory.Warnings)
        {
            output.WriteLine("Aviso: " + w);
        }

        Assert.NotEmpty(inventory.Cpus);
        Assert.NotEmpty(inventory.Gpus);
        Assert.True(inventory.Memory.InstalledBytes > 0);

        var dir = Path.Combine(Path.GetTempPath(), "framex-tests", Guid.NewGuid().ToString("N"));
        using var store = new BlenderOpenDataStore(dir, new HttpClientHandler { AllowAutoRedirect = false }, TimeProvider.System, NullLogger<BlenderOpenDataStore>.Instance);
        var loads = new Dictionary<string, DatasetLoadResult>();
        foreach (var def in store.Definitions)
        {
            loads[def.Id] = await store.LoadAsync(def, CancellationToken.None);
        }

        var e = SetupScorer.Build(new ScoringContext(inventory, true, store.Definitions, loads, [], Guid.NewGuid(), DateTimeOffset.UtcNow, "teste"));
        foreach (var c in e.Categories)
        {
            output.WriteLine($"{c.Category}: {c.Status} {c.Score} | {c.ComponentName} | {c.Match} | {c.MatchedRecord} | conf. {c.Confidence}");
        }

        output.WriteLine($"Geral: {e.Overall.Score} {e.Overall.Tier} {e.Overall.Completeness} cobertura {e.Overall.Coverage:P0}");
        foreach (var i in e.Issues)
        {
            output.WriteLine($"Problema: {i.Category} {i.Kind} — {i.Reason}");
        }
    }
}
