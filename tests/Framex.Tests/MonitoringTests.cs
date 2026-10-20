using Framex.Core.Domain;
using Framex.Core.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests;

public class MonitoringTests
{
    private sealed class StubProvider(string name, int priority, Func<ProviderReadResult> read) : ISensorProvider
    {
        public int Reads { get; private set; }
        public string Name => name;
        public int Priority => priority;
        public void Initialize(MonitoringOptions options) { }

        public ProviderReadResult Read(DateTimeOffset now)
        {
            Reads++;
            return read();
        }

        public HardwareInventory? DescribeHardware() => null;
        public void Dispose() { }
    }

    private static ProviderReadResult With(params (MetricKind, MetricValue)[] values) =>
        new([], values.ToDictionary(v => v.Item1, v => v.Item2));

    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    public void CelsiusToFahrenheit(double c, double f) => Assert.Equal(f, UnitFormatter.CelsiusToFahrenheit(c), 6);

    [Fact]
    public void MemoryConventions_AreDistinct()
    {
        Assert.Equal(16, UnitFormatter.MebibytesToGigabytes(16384, MemoryUnitConvention.Binary), 6);
        Assert.Equal(17.179869184, UnitFormatter.MebibytesToGigabytes(16384, MemoryUnitConvention.Decimal), 6);
        Assert.Contains("GiB", UnitFormatter.FormatMemory(16384, MemoryUnitConvention.Binary));
        Assert.Contains("GB", UnitFormatter.FormatMemory(16384, MemoryUnitConvention.Decimal));
    }

    [Fact]
    public void Format_NullIsDash_NotZero() => Assert.Equal("—", UnitFormatter.Format(null, MetricUnit.Celsius));

    [Theory]
    [InlineData(double.NaN, MetricUnit.Celsius, SensorAvailability.Invalid)]
    [InlineData(500.0, MetricUnit.Celsius, SensorAvailability.Invalid)]
    [InlineData(55.0, MetricUnit.Celsius, SensorAvailability.Available)]
    [InlineData(-3.0, MetricUnit.Percent, SensorAvailability.Invalid)]
    public void Validator_RejectsImplausibleValues(double value, MetricUnit unit, SensorAvailability expected) =>
        Assert.Equal(expected, SensorValidator.Validate(value, unit).Availability);

    [Fact]
    public void Validator_NullIsUnavailable() =>
        Assert.Equal(SensorAvailability.Unavailable, SensorValidator.Validate(null, MetricUnit.Celsius).Availability);

    [Fact]
    public void FailingProvider_DoesNotBreakOthers()
    {
        var good = new StubProvider("good", 1, () => With((MetricKind.GpuLoad, MetricValue.Of(40))));
        var bad = new StubProvider("bad", 0, () => throw new InvalidOperationException("sensor quebrado"));
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var svc = new HardwareMonitoringService([good, bad], new MonitoringOptions(), time, NullLogger<HardwareMonitoringService>.Instance);

        var snap = svc.CollectOnce();
        Assert.Equal(40, snap[MetricKind.GpuLoad].Value);
        Assert.Contains(svc.ProviderHealth, h => h.Name == "bad" && !h.Healthy && h.LastError == "sensor quebrado");
    }

    [Fact]
    public void RepeatedFailures_TriggerBackoff()
    {
        var bad = new StubProvider("bad", 0, () => throw new InvalidOperationException("x"));
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var svc = new HardwareMonitoringService([bad], new MonitoringOptions(), time, NullLogger<HardwareMonitoringService>.Instance);
        for (var i = 0; i < 10; i++)
        {
            svc.CollectOnce();
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(5, bad.Reads);
    }

    [Fact]
    public void HigherPriorityValueWins_ButMissingFallsBack()
    {
        var primary = new StubProvider("p", 0, () => With((MetricKind.RamUsed, MetricValue.Of(1000)), (MetricKind.GpuLoad, MetricValue.NotDetected)));
        var secondary = new StubProvider("s", 1, () => With((MetricKind.RamUsed, MetricValue.Of(2000)), (MetricKind.GpuLoad, MetricValue.Of(70))));
        var svc = new HardwareMonitoringService([secondary, primary], new MonitoringOptions(), TimeProvider.System, NullLogger<HardwareMonitoringService>.Instance);
        var snap = svc.CollectOnce();
        Assert.Equal(1000, snap[MetricKind.RamUsed].Value);
        Assert.Equal(70, snap[MetricKind.GpuLoad].Value);
    }

    [Fact]
    public void DisconnectedSensor_BecomesStaleThenUnavailable()
    {
        var connected = true;
        var p = new StubProvider("p", 0, () => connected ? With((MetricKind.GpuTemperature, MetricValue.Of(60))) : ProviderReadResult.Empty);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var svc = new HardwareMonitoringService([p], new MonitoringOptions { StaleAfter = TimeSpan.FromSeconds(5) }, time, NullLogger<HardwareMonitoringService>.Instance);
        svc.CollectOnce();

        connected = false;
        time.Advance(TimeSpan.FromSeconds(2));
        var stale = svc.CollectOnce()[MetricKind.GpuTemperature];
        Assert.Equal(SensorAvailability.Stale, stale.Availability);
        Assert.False(stale.HasValue);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(SensorAvailability.Unavailable, svc.CollectOnce()[MetricKind.GpuTemperature].Availability);
    }

    [Fact]
    public void UnknownMetric_IsNotDetected_NotZero()
    {
        var snap = HardwareSnapshot.Empty;
        Assert.Equal(SensorAvailability.Unsupported, snap[MetricKind.CpuTemperaturePackage].Availability);
        Assert.Null(snap[MetricKind.CpuTemperaturePackage].Value);
    }

    [Fact]
    public void Inventory_MergesSameComponentFromDifferentSources()
    {
        var list = new List<HardwareComponentInfo>
        {
            new("wmi/cpu", ComponentKind.Cpu, "Intel(R) Xeon(R) CPU E3-1230 V2", [new("Núcleos", "4")]),
            new("wmi/gpu", ComponentKind.Gpu, "NVIDIA GeForce GTX 1660 SUPER", [new("Driver", "1")]),
        };
        HardwareMonitoringService.MergeComponent(list, new("/intelcpu/0", ComponentKind.Cpu, "Intel Xeon E3-1230 V2", [new("Fonte", "LHM")]));
        HardwareMonitoringService.MergeComponent(list, new("/gpu-nvidia/0", ComponentKind.Gpu, "NVIDIA GeForce GTX 1660 SUPER", [new("VRAM", "6 GiB")]));
        HardwareMonitoringService.MergeComponent(list, new("/gpu-intel/0", ComponentKind.Gpu, "Intel UHD", []));

        Assert.Single(list, c => c.Kind == ComponentKind.Cpu);
        Assert.Equal(2, list.Count(c => c.Kind == ComponentKind.Gpu));
        Assert.Contains(list[0].Properties, p => p.Name == "Fonte");
        Assert.Contains(list[1].Properties, p => p.Name == "VRAM");
    }
}
