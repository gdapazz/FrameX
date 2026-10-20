using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Framex.App.Services;
using Framex.Core.Customization;
using Framex.Core.FrameCapture;
using Framex.Core.Monitoring;

namespace Framex.App.ViewModels;

public sealed partial class OverlayItemViewModel : ObservableObject
{
    public OverlayItemViewModel(OverlayItem item)
    {
        Metric = item.Metric;
        Display = item.Metric == OverlayMetric.FrameTimeGraph ? OverlayDisplay.Graph : item.Display;
    }

    public OverlayMetric Metric { get; }

    public OverlayDisplay Display { get; }

    public bool IsText => Display == OverlayDisplay.Text;
    public bool IsBar => Display == OverlayDisplay.Bar;
    public bool IsRing => Display == OverlayDisplay.Ring;
    public bool IsGraph => Display == OverlayDisplay.Graph;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _text = Fmt.Missing;

    [ObservableProperty]
    private double? _value;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private bool _hasValue;

    /// <summary>Série do gráfico: frametimes recentes (Graph de frametime) ou histórico curto da métrica.</summary>
    public List<double> Series { get; } = new(240);

    [ObservableProperty]
    private int _revision;
}

/// <summary>Dados do HUD. Atualiza em seu próprio ritmo (definido no layout, limitado pelo perfil de desempenho).</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly IHardwareMonitor _monitor;
    private readonly LiveFrameAggregator _frames;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _alertTimer;

    public OverlayViewModel(IHardwareMonitor monitor, LiveFrameAggregator frames, SettingsService settings)
    {
        _monitor = monitor;
        _frames = frames;
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Refresh();
        _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _alertTimer.Tick += (_, _) =>
        {
            _alertTimer.Stop();
            AlertText = null;
        };
    }

    public ObservableCollection<OverlayItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private OverlayLayout _layout = OverlayPresets.Create(OverlayStyle.Compact);

    [ObservableProperty]
    private bool _horizontal;

    [ObservableProperty]
    private Brush _backgroundBrush = Brushes.Black;

    [ObservableProperty]
    private Brush _textBrush = Brushes.White;

    [ObservableProperty]
    private Brush _accentBrush = Brushes.Cyan;

    [ObservableProperty]
    private Brush _borderBrush = Brushes.Transparent;

    [ObservableProperty]
    private Brush _mutedBrush = Brushes.Gray;

    [ObservableProperty]
    private string? _alertText;

    [ObservableProperty]
    private bool _editMode;

    [ObservableProperty]
    private string? _gameName;

    public void ApplyLayout(OverlayLayout layout, ThemeDefinition theme)
    {
        Layout = layout.Clone();
        Horizontal = layout.Orientation == OverlayOrientation.Horizontal;
        var bg = ThemeService.ParseColor(layout.BackgroundColor ?? theme.Background, Colors.Black);
        var text = ThemeService.ParseColor(layout.TextColor ?? theme.Text, Colors.White);
        var accent = ThemeService.ParseColor(layout.AccentColor ?? theme.Primary, Colors.Cyan);
        var border = ThemeService.ParseColor(layout.BorderColor ?? theme.Border, Colors.Gray);
        BackgroundBrush = Frozen(ThemeService.WithAlpha(bg, layout.BackgroundOpacity));
        TextBrush = Frozen(text);
        AccentBrush = Frozen(accent);
        BorderBrush = Frozen(ThemeService.WithAlpha(border, Math.Max(layout.BackgroundOpacity, 0.4)));
        MutedBrush = Frozen(ThemeService.WithAlpha(text, 0.62));

        Items.Clear();
        foreach (var item in layout.Items.Where(i => i.Visible))
        {
            Items.Add(new OverlayItemViewModel(item));
        }

        var minMs = _settings.Tuning.OverlayMinRefreshMs;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(layout.RefreshMs, minMs));
        Refresh();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void ShowAlert(string text)
    {
        AlertText = text;
        _alertTimer.Stop();
        _alertTimer.Start();
    }

    private readonly List<double> _scratch = new(256);

    private void Refresh()
    {
        var snap = _monitor.Latest;
        var frames = _frames.Compute();
        var settings = _settings.Current;
        foreach (var item in Items)
        {
            var (title, value, text, fraction) = Fmt.Overlay(item.Metric, snap, frames, settings);
            item.Title = title;
            item.Value = value;
            item.Text = text;
            item.Fraction = fraction;
            item.HasValue = value is not null;
            if (item.IsGraph)
            {
                if (item.Metric == OverlayMetric.FrameTimeGraph)
                {
                    _frames.CopyRecentFrameTimes(_scratch, 180);
                    item.Series.Clear();
                    item.Series.AddRange(_scratch);
                }
                else
                {
                    item.Series.Add(value ?? double.NaN);
                    if (item.Series.Count > 60)
                    {
                        item.Series.RemoveAt(0);
                    }
                }

                item.Revision++;
            }
        }
    }
}
