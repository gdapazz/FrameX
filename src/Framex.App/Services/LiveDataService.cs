using System.Windows.Threading;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Monitoring;

namespace Framex.App.Services;

/// <summary>Série recente de uma métrica para gráficos ao vivo. Valores ausentes ficam como NaN (lacunas visíveis).</summary>
public sealed class LiveSeries(int capacity) : IReadOnlyList<double>
{
    private readonly RingBuffer<double> _values = new(capacity);

    public int Capacity { get; } = capacity;

    public int Count => _values.Count;

    public double this[int index] => _values[index];

    public void Add(double? value) => _values.Add(value ?? double.NaN);

    public void Clear() => _values.Clear();

    public IEnumerator<double> GetEnumerator()
    {
        for (var i = 0; i < _values.Count; i++)
        {
            yield return _values[i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Ritmo único da interface: a cada intervalo (definido pelo perfil de desempenho) lê o último snapshot e as métricas de quadros,
/// alimenta o histórico curto e notifica as telas visíveis. Evita que cada tela crie seu próprio timer.
/// </summary>
public sealed class LiveDataService
{
    public const int HistoryLength = 120;

    private readonly IHardwareMonitor _monitor;
    private readonly LiveFrameAggregator _frames;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _recentFrames = new(512);

    public LiveDataService(IHardwareMonitor monitor, LiveFrameAggregator frames)
    {
        _monitor = monitor;
        _frames = frames;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
    }

    public HardwareSnapshot Snapshot { get; private set; } = HardwareSnapshot.Empty;

    public LiveFrameMetrics Frames { get; private set; }

    /// <summary>Frametimes individuais mais recentes (até 240), para o gráfico de frametime ao vivo.</summary>
    public IReadOnlyList<double> RecentFrameTimes => _recentFrames;

    public LiveSeries CpuLoad { get; } = new(HistoryLength);
    public LiveSeries GpuLoad { get; } = new(HistoryLength);
    public LiveSeries CpuTemperature { get; } = new(HistoryLength);
    public LiveSeries GpuTemperature { get; } = new(HistoryLength);
    public LiveSeries RamLoad { get; } = new(HistoryLength);
    public LiveSeries VramLoad { get; } = new(HistoryLength);
    public LiveSeries Fps { get; } = new(HistoryLength);

    public event EventHandler? Tick;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : value;
    }

    public void Start()
    {
        Update();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Update()
    {
        Snapshot = _monitor.Latest;
        Frames = _frames.Compute();
        _frames.CopyRecentFrameTimes(_recentFrames, 240);

        var s = Snapshot;
        CpuLoad.Add(Val(s, MetricKind.CpuLoad));
        GpuLoad.Add(Val(s, MetricKind.GpuLoad));
        CpuTemperature.Add(CpuTemperatureOf(s));
        GpuTemperature.Add(Val(s, MetricKind.GpuTemperature));
        RamLoad.Add(Val(s, MetricKind.RamLoad));
        VramLoad.Add(VramPercent(s));
        Fps.Add(Frames.Fps);

        Tick?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Valor atual apenas se disponível (leituras desatualizadas ou inválidas viram ausência).</summary>
    public static double? Val(HardwareSnapshot s, MetricKind kind)
    {
        var v = s[kind];
        return v.HasValue ? v.Value : null;
    }

    public static double? VramPercent(HardwareSnapshot s)
    {
        var used = Val(s, MetricKind.VramUsed);
        var total = Val(s, MetricKind.VramTotal);
        return used is not null && total is > 0 ? Math.Clamp(used.Value / total.Value * 100, 0, 100) : null;
    }

    public static double? CpuTemperatureOf(HardwareSnapshot s) =>
        Val(s, MetricKind.CpuTemperaturePackage) ?? Val(s, MetricKind.CpuTemperatureCoreMax);
}
