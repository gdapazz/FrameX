using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Domain;
using Framex.Core.Monitoring;
using Framex.Infrastructure.Service;
using Framex.Infrastructure.FrameCapture;

namespace Framex.App.ViewModels;

public sealed record ComponentCard(string KindLabel, string Name, string Icon, IReadOnlyList<HardwareProperty> Properties);

public sealed partial class SensorRow(string id, string name, string type, string source) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string Source { get; } = source;

    [ObservableProperty]
    private string _value = Fmt.Missing;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _available;

    [ObservableProperty]
    private string? _note;
}

public sealed class SensorGroup(string title, string kind)
{
    public string Title { get; } = title;
    public string Kind { get; } = kind;
    public ObservableCollection<SensorRow> Sensors { get; } = [];
}

public sealed partial class HardwareViewModel : ObservableObject, IPageActivated
{
    private readonly IHardwareMonitor _monitor;
    private readonly LiveDataService _live;
    private readonly SettingsService _settings;
    private bool _active;
    private DateTimeOffset _inventoryAt = DateTimeOffset.MinValue;

    public HardwareViewModel(IHardwareMonitor monitor, LiveDataService live, SettingsService settings)
    {
        _monitor = monitor;
        _live = live;
        _settings = settings;
        live.Tick += (_, _) =>
        {
            if (_active)
            {
                Update();
            }
        };
        IsElevated = FramexServiceStatus.CaptureAvailable();
    }

    public bool IsElevated { get; }

    public ObservableCollection<ComponentCard> Components { get; } = [];
    public ObservableCollection<SensorGroup> Groups { get; } = [];
    public ObservableCollection<ProviderHealth> Providers { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private bool _onlyAvailable;

    [ObservableProperty]
    private string _sensorSummary = "";

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnOnlyAvailableChanged(bool value) => Rebuild();

    public void OnActivated()
    {
        _active = true;
        BuildInventory();
        Rebuild();
    }

    public void OnDeactivated() => _active = false;

    [RelayCommand]
    private async Task RefreshInventory()
    {
        await Task.Run(_monitor.RefreshInventory);
        BuildInventory();
    }

    private static string KindLabel(ComponentKind k) => k switch
    {
        ComponentKind.Cpu => "Processador",
        ComponentKind.Gpu => "Placa de vídeo",
        ComponentKind.Memory => "Memória",
        ComponentKind.Motherboard => "Placa-mãe",
        ComponentKind.Storage => "Armazenamento",
        ComponentKind.Network => "Rede",
        ComponentKind.Display => "Monitor",
        ComponentKind.OperatingSystem => "Sistema operacional",
        ComponentKind.Battery => "Bateria",
        _ => "Outro",
    };

    private static string KindIcon(ComponentKind k) => k switch
    {
        ComponentKind.Gpu or ComponentKind.Display => "I.Monitor",
        ComponentKind.Memory => "I.Memory",
        ComponentKind.Battery => "I.Bolt",
        ComponentKind.OperatingSystem => "I.Shield",
        _ => "I.Hardware",
    };

    private void BuildInventory()
    {
        var inv = _monitor.Inventory;
        if (inv.CollectedAt == _inventoryAt && Components.Count > 0)
        {
            return;
        }

        _inventoryAt = inv.CollectedAt;
        Components.Clear();
        foreach (var c in inv.Components.OrderBy(c => c.Kind))
        {
            Components.Add(new ComponentCard(KindLabel(c.Kind), c.Name, KindIcon(c.Kind), c.Properties));
        }
    }

    private void Rebuild()
    {
        Groups.Clear();
        Update();
    }

    private void Update()
    {
        if (Components.Count == 0 && _monitor.Inventory.Components.Count > 0)
        {
            BuildInventory();
        }

        var health = _monitor.ProviderHealth.ToList();
        if (!Providers.SequenceEqual(health))
        {
            Providers.Clear();
            foreach (var p in health)
            {
                Providers.Add(p);
            }
        }

        var snap = _live.Snapshot;
        var settings = _settings.Current;
        var q = Filter.Trim();
        var sensors = snap.Sensors
            .Where(s => !OnlyAvailable || s.IsValid)
            .Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                      || s.ComponentName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                      || s.SensorType.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var available = snap.Sensors.Count(s => s.IsValid);
        SensorSummary = snap.Sensors.Count == 0
            ? "Aguardando a primeira leitura dos sensores…"
            : $"{snap.Sensors.Count} sensores · {available} com leitura válida · {snap.Sensors.Count - available} indisponíveis";

        var groups = sensors.GroupBy(s => (s.ComponentId, s.ComponentName, s.Component)).ToList();
        foreach (var g in groups)
        {
            var group = Groups.FirstOrDefault(x => x.Title == g.Key.ComponentName);
            if (group is null)
            {
                group = new SensorGroup(g.Key.ComponentName, KindLabel(g.Key.Component));
                Groups.Add(group);
            }

            foreach (var s in g)
            {
                var row = group.Sensors.FirstOrDefault(r => r.Id == s.Id);
                if (row is null)
                {
                    row = new SensorRow(s.Id, s.Name, s.SensorType, s.Source);
                    group.Sensors.Add(row);
                }

                row.Available = s.IsValid;
                row.State = Fmt.Availability(s.Availability);
                row.Note = s.Note;
                row.Value = s.Value is null ? Fmt.Missing : FormatSensor(s, settings);
            }
        }
    }

    private static string FormatSensor(SensorReading s, Core.Customization.AppSettings settings) => s.Unit switch
    {
        MetricUnit.Celsius => Fmt.Temperature(s.Value, settings),
        MetricUnit.Megabyte => Fmt.Memory(s.Value, settings, 2),
        MetricUnit.Gigabyte => Fmt.Number(s.Value, "0.00") + " GB",
        MetricUnit.Megahertz => Fmt.Clock(s.Value),
        MetricUnit.Watt => Fmt.Power(s.Value),
        MetricUnit.Percent => Fmt.Number(s.Value, "0.0") + "%",
        MetricUnit.Volt => Fmt.Number(s.Value, "0.000") + " V",
        MetricUnit.Ampere => Fmt.Number(s.Value, "0.00") + " A",
        MetricUnit.Rpm => Fmt.Number(s.Value) + " RPM",
        MetricUnit.BytesPerSecond => UnitFormatter.FormatRate(s.Value ?? 0),
        _ => Fmt.Number(s.Value, "0.##"),
    };
}
