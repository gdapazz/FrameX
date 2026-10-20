using System.Security.Principal;
using Framex.Core.Domain;
using Framex.Core.Monitoring;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Hardware;

/// <summary>
/// Sensores de CPU, GPU e placa-mãe via LibreHardwareMonitorLib (MPL-2.0).
/// Sem privilégios de administrador e sem o driver PawnIO, temperaturas/frequências/potência da CPU
/// não ficam disponíveis; esses casos são reportados como indisponíveis, nunca como zero.
/// </summary>
public sealed class LibreHardwareSensorProvider : ISensorProvider
{
    private const string Source = "LibreHardwareMonitor";
    private readonly ILogger<LibreHardwareSensorProvider> _logger;
    private readonly Lock _gate = new();
    private Computer? _computer;
    private MonitoringOptions _options = new();
    private readonly bool _elevated;
    private readonly bool _includeGpu;
    private readonly bool _pawnIo;

    public LibreHardwareSensorProvider(ILogger<LibreHardwareSensorProvider> logger)
        : this(logger, includeGpu: true)
    {
    }

    /// <summary><paramref name="includeGpu"/> = false é usado pelo serviço, que só complementa CPU e placa-mãe (a GPU é lida pelo app sem elevação).</summary>
    public LibreHardwareSensorProvider(ILogger<LibreHardwareSensorProvider> logger, bool includeGpu)
    {
        _logger = logger;
        _includeGpu = includeGpu;
        _pawnIo = IsPawnIoInstalled();
        using var identity = WindowsIdentity.GetCurrent();
        _elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>O LibreHardwareMonitor acessa os registradores da CPU pelo driver PawnIO; esta é a mesma verificação que ele faz.</summary>
    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("DisplayVersion") is string v && v.Length > 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string Name => "LibreHardwareMonitor";

    public int Priority => 10;

    public void Initialize(MonitoringOptions options)
    {
        lock (_gate)
        {
            _options = options;
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = _includeGpu,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = false,
                IsStorageEnabled = options.CollectStorage,
                IsNetworkEnabled = options.CollectNetwork,
                IsControllerEnabled = false,
                IsBatteryEnabled = false,
                IsPsuEnabled = false,
            };
            _computer.Open();
            _logger.LogInformation("LibreHardwareMonitor aberto ({Count} dispositivos; elevado: {Elevated})", _computer.Hardware.Count, _elevated);
        }
    }

    private string Ring0Reason => _elevated
        ? (_pawnIo
            ? "Sensor sem leitura: o hardware não é suportado pelo LibreHardwareMonitor."
            : "Sensor sem leitura: o driver PawnIO não está instalado (o Setup.exe do FRAMEX o instala).")
        : "Requer o serviço do FRAMEX, instalado pelo Setup.exe (ou executar como administrador com o driver PawnIO).";

    public ProviderReadResult Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_computer is null)
            {
                return ProviderReadResult.Empty;
            }

            var readings = new List<SensorReading>(256);
            var metrics = new Dictionary<MetricKind, MetricValue>();
            var perCore = new List<double>();

            var gpus = new List<IHardware>();
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                    {
                        sub.Update();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Falha ao atualizar {Hardware}", hw.Name);
                    continue;
                }

                Collect(hw, readings, now);
                foreach (var sub in hw.SubHardware)
                {
                    Collect(sub, readings, now);
                }

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        if (!metrics.ContainsKey(MetricKind.CpuLoad))
                        {
                            MapCpu(hw, metrics, perCore);
                        }

                        break;
                    case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                        gpus.Add(hw);
                        break;
                }
            }

            var primary = ChoosePrimaryGpu(gpus, _options.PreferredGpuId);
            if (primary is not null)
            {
                MapGpu(primary, metrics);
            }

            return new ProviderReadResult(readings, metrics, perCore, primary?.Identifier.ToString());
        }
    }

    internal static IHardware? ChoosePrimaryGpu(IReadOnlyList<IHardware> gpus, string? preferredId)
    {
        if (gpus.Count == 0)
        {
            return null;
        }

        return gpus.FirstOrDefault(g => g.Identifier.ToString() == preferredId)
               ?? gpus.FirstOrDefault(g => g.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
               ?? gpus[0];
    }

    private void MapCpu(IHardware cpu, Dictionary<MetricKind, MetricValue> metrics, List<double> perCore)
    {
        var sensors = cpu.Sensors;
        var total = Find(sensors, SensorType.Load, "CPU Total");
        metrics[MetricKind.CpuLoad] = Validate(total, MetricUnit.Percent);

        var package = Find(sensors, SensorType.Temperature, "CPU Package")
                      ?? Find(sensors, SensorType.Temperature, "Core (Tctl/Tdie)")
                      ?? Find(sensors, SensorType.Temperature, "Core (Tctl)");
        metrics[MetricKind.CpuTemperaturePackage] = package is null
            ? MetricValue.NotDetected
            : Ring0Aware(package, MetricUnit.Celsius);

        var coreMax = Find(sensors, SensorType.Temperature, "Core Max");
        metrics[MetricKind.CpuTemperatureCoreMax] = coreMax is null ? MetricValue.NotDetected : Ring0Aware(coreMax, MetricUnit.Celsius);

        var clocks = sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal) && s.Value is > 0).ToList();
        metrics[MetricKind.CpuClockAverage] = clocks.Count > 0
            ? SensorValidator.Validate(clocks.Average(s => s.Value!.Value), MetricUnit.Megahertz, "cpu/clock/avg")
            : MetricValue.Missing(SensorAvailability.Unavailable, Ring0Reason);

        var power = Find(sensors, SensorType.Power, "CPU Package") ?? Find(sensors, SensorType.Power, "Package");
        if (power is null)
        {
            metrics[MetricKind.CpuPower] = MetricValue.NotDetected;
        }
        else if (power.Value is null or <= 0)
        {
            // Sem acesso aos MSRs o LHM informa 0 W: isso não é uma leitura real.
            metrics[MetricKind.CpuPower] = MetricValue.Missing(SensorAvailability.Unavailable, Ring0Reason);
        }
        else
        {
            metrics[MetricKind.CpuPower] = Validate(power, MetricUnit.Watt);
        }

        var threadLoads = sensors
            .Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal) && s.Value.HasValue)
            .OrderBy(s => s.Index)
            .Select(s => (double)Math.Clamp(s.Value!.Value, 0f, 100f));
        perCore.AddRange(threadLoads);
    }

    private static void MapGpu(IHardware gpu, Dictionary<MetricKind, MetricValue> metrics)
    {
        var s = gpu.Sensors;
        var load = Find(s, SensorType.Load, "GPU Core") ?? Find(s, SensorType.Load, "D3D 3D");
        metrics[MetricKind.GpuLoad] = Validate(load, MetricUnit.Percent);
        metrics[MetricKind.GpuTemperature] = Validate(Find(s, SensorType.Temperature, "GPU Core"), MetricUnit.Celsius);
        metrics[MetricKind.GpuHotspot] = Validate(Find(s, SensorType.Temperature, "GPU Hot Spot"), MetricUnit.Celsius);
        metrics[MetricKind.GpuCoreClock] = Validate(Find(s, SensorType.Clock, "GPU Core"), MetricUnit.Megahertz);
        metrics[MetricKind.GpuMemoryClock] = Validate(Find(s, SensorType.Clock, "GPU Memory"), MetricUnit.Megahertz);

        var power = Find(s, SensorType.Power, "GPU Package") ?? Find(s, SensorType.Power, "GPU PPT")
                    ?? Find(s, SensorType.Power, "GPU Power") ?? s.FirstOrDefault(x => x.SensorType == SensorType.Power);
        metrics[MetricKind.GpuPower] = Validate(power, MetricUnit.Watt);

        var fan = s.FirstOrDefault(x => x.SensorType == SensorType.Fan);
        metrics[MetricKind.GpuFanRpm] = Validate(fan, MetricUnit.Rpm);
        metrics[MetricKind.GpuMemoryControllerLoad] = Validate(Find(s, SensorType.Load, "GPU Memory Controller"), MetricUnit.Percent);

        var used = Find(s, SensorType.SmallData, "GPU Memory Used");
        var dedicated = Find(s, SensorType.SmallData, "D3D Dedicated Memory Used");
        metrics[MetricKind.VramUsed] = Validate(used ?? dedicated, MetricUnit.Megabyte);
        metrics[MetricKind.VramTotal] = Validate(Find(s, SensorType.SmallData, "GPU Memory Total") ?? Find(s, SensorType.SmallData, "D3D Dedicated Memory Total"), MetricUnit.Megabyte);
        metrics[MetricKind.VramDedicatedUsedD3D] = Validate(dedicated, MetricUnit.Megabyte);
        metrics[MetricKind.VramSharedUsed] = Validate(Find(s, SensorType.SmallData, "D3D Shared Memory Used"), MetricUnit.Megabyte);
    }

    private MetricValue Ring0Aware(ISensor sensor, MetricUnit unit) =>
        sensor.Value is null
            ? MetricValue.Missing(SensorAvailability.Unavailable, Ring0Reason)
            : SensorValidator.Validate(sensor.Value, unit, sensor.Identifier.ToString());

    private static MetricValue Validate(ISensor? sensor, MetricUnit unit) =>
        sensor is null ? MetricValue.NotDetected : SensorValidator.Validate(sensor.Value, unit, sensor.Identifier.ToString());

    private static ISensor? Find(ISensor[] sensors, SensorType type, string name) =>
        sensors.FirstOrDefault(s => s.SensorType == type && string.Equals(s.Name, name, StringComparison.Ordinal));

    private void Collect(IHardware hw, List<SensorReading> readings, DateTimeOffset now)
    {
        var component = KindOf(hw.HardwareType);
        foreach (var sensor in hw.Sensors)
        {
            var (unit, factor) = UnitOf(sensor.SensorType);
            double? value = sensor.Value.HasValue ? sensor.Value.Value * factor : null;
            SensorAvailability availability;
            string? note = null;
            if (value is null)
            {
                availability = SensorAvailability.Unavailable;
                note = component == ComponentKind.Cpu || hw.HardwareType == HardwareType.SuperIO ? Ring0Reason : "O sensor não retornou valor.";
            }
            else if (!double.IsFinite(value.Value))
            {
                availability = SensorAvailability.Invalid;
                value = null;
                note = "Valor inválido.";
            }
            else if (component == ComponentKind.Cpu && sensor.SensorType == SensorType.Power && value <= 0)
            {
                // Sem acesso aos MSRs o LHM informa 0 W: isso não é uma leitura real.
                availability = SensorAvailability.Unavailable;
                value = null;
                note = Ring0Reason;
            }
            else
            {
                availability = SensorAvailability.Available;
            }

            readings.Add(new SensorReading(
                sensor.Identifier.ToString(),
                hw.Identifier.ToString(),
                hw.Name,
                component,
                sensor.SensorType.ToString(),
                sensor.Name,
                value,
                unit,
                now,
                availability,
                Source,
                note));
        }
    }

    private static (MetricUnit Unit, double Factor) UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => (MetricUnit.Celsius, 1),
        SensorType.Load or SensorType.Control or SensorType.Level => (MetricUnit.Percent, 1),
        SensorType.Clock or SensorType.Frequency => (MetricUnit.Megahertz, 1),
        SensorType.Power => (MetricUnit.Watt, 1),
        SensorType.Fan => (MetricUnit.Rpm, 1),
        SensorType.SmallData => (MetricUnit.Megabyte, 1),
        SensorType.Data => (MetricUnit.Gigabyte, 1),
        SensorType.Voltage => (MetricUnit.Volt, 1),
        SensorType.Current => (MetricUnit.Ampere, 1),
        SensorType.Throughput => (MetricUnit.BytesPerSecond, 1),
        _ => (MetricUnit.None, 1),
    };

    private static ComponentKind KindOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => ComponentKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => ComponentKind.Gpu,
        HardwareType.Memory => ComponentKind.Memory,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => ComponentKind.Motherboard,
        HardwareType.Storage => ComponentKind.Storage,
        HardwareType.Network => ComponentKind.Network,
        HardwareType.Battery => ComponentKind.Battery,
        _ => ComponentKind.Other,
    };

    public HardwareInventory? DescribeHardware()
    {
        lock (_gate)
        {
            if (_computer is null)
            {
                return null;
            }

            var list = new List<HardwareComponentInfo>();
            foreach (var hw in _computer.Hardware)
            {
                var kind = KindOf(hw.HardwareType);
                if (kind is not (ComponentKind.Cpu or ComponentKind.Gpu or ComponentKind.Motherboard))
                {
                    continue;
                }

                var props = new List<HardwareProperty> { new("Fonte", Source), new("Identificador", hw.Identifier.ToString()) };
                if (kind == ComponentKind.Gpu)
                {
                    props.Insert(0, new HardwareProperty("Fabricante", hw.HardwareType switch
                    {
                        HardwareType.GpuNvidia => "NVIDIA",
                        HardwareType.GpuAmd => "AMD",
                        _ => "Intel",
                    }));
                    var total = Find(hw.Sensors, SensorType.SmallData, "GPU Memory Total");
                    if (total?.Value is { } mib)
                    {
                        props.Add(new HardwareProperty("VRAM dedicada", $"{mib / 1024:0.#} GiB"));
                    }
                }

                if (kind == ComponentKind.Cpu)
                {
                    var cores = hw.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal)).Count();
                    if (cores > 0)
                    {
                        props.Add(new HardwareProperty("Núcleos (LHM)", cores.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }

                    var tempsWork = hw.Sensors.Any(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
                    props.Add(new HardwareProperty("Sensores de temperatura", tempsWork ? "Disponíveis"
                        : _elevated ? (_pawnIo ? "Sem leitura (hardware não suportado)" : "Sem leitura (driver PawnIO ausente)")
                        : "Requer o serviço do FRAMEX (Setup.exe)"));
                }

                list.Add(new HardwareComponentInfo(hw.Identifier.ToString(), kind, hw.Name, props));
            }

            return new HardwareInventory(DateTimeOffset.UtcNow, list);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao fechar o LibreHardwareMonitor");
            }

            _computer = null;
        }
    }
}
