using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Customization;
using Framex.Core.Monitoring;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace Framex.App.ViewModels;

public enum CustomizationTab
{
    Themes,
    Overlay,
    Dashboard,
    Interface,
}

public sealed partial class ThemeCard(ThemeDefinition theme, bool active) : ObservableObject
{
    public ThemeDefinition Theme { get; } = theme;
    public string Name => Theme.Name;
    public bool BuiltIn => Theme.BuiltIn;

    [ObservableProperty]
    private bool _isActive = active;
}

public sealed partial class ColorToken(string key, string label, string hex) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    [ObservableProperty]
    private string _hex = hex;
}

/// <summary>Edição de um tema personalizado; cada alteração dispara <see cref="Changed"/> para pré-visualização ao vivo.</summary>
public sealed partial class ThemeEditor : ObservableObject
{
    private static readonly (string Key, string Label)[] Tokens =
    [
        ("Background", "Fundo"), ("BackgroundAlt", "Fundo alternativo"), ("Surface", "Superfície"), ("SurfaceAlt", "Superfície elevada"),
        ("SurfaceHigh", "Superfície destacada"), ("Border", "Bordas"), ("Primary", "Primária"), ("Secondary", "Secundária"),
        ("Accent", "Destaque"), ("Accent2", "Destaque 2"), ("Text", "Texto"), ("TextMuted", "Texto secundário"),
        ("Success", "Sucesso"), ("Warning", "Atenção"), ("Error", "Erro"),
    ];

    public ThemeEditor(ThemeDefinition source)
    {
        Theme = source.Clone();
        foreach (var (key, label) in Tokens)
        {
            var token = new ColorToken(key, label, (string)typeof(ThemeDefinition).GetProperty(key)!.GetValue(Theme)!);
            token.PropertyChanged += OnTokenChanged;
            Colors.Add(token);
        }

        for (var i = 0; i < Theme.ChartPalette.Count; i++)
        {
            var token = new ColorToken("Chart" + i, $"Gráfico {i + 1}", Theme.ChartPalette[i]);
            token.PropertyChanged += OnTokenChanged;
            Palette.Add(token);
        }

        _name = Theme.Name;
        _cornerRadius = Theme.CornerRadius;
        _surfaceOpacity = Theme.SurfaceOpacity;
        _fontScale = Theme.FontScale;
        _spacing = Theme.Spacing;
        _glowIntensity = Theme.GlowIntensity;
        _borderThickness = Theme.BorderThickness;
        _gauges = Theme.Gauges;
        _animations = Theme.Animations;
    }

    public ThemeDefinition Theme { get; }

    public ObservableCollection<ColorToken> Colors { get; } = [];

    public ObservableCollection<ColorToken> Palette { get; } = [];

    public event EventHandler? Changed;

    [ObservableProperty] private string _name;
    [ObservableProperty] private double _cornerRadius;
    [ObservableProperty] private double _surfaceOpacity;
    [ObservableProperty] private double _fontScale;
    [ObservableProperty] private double _spacing;
    [ObservableProperty] private double _glowIntensity;
    [ObservableProperty] private double _borderThickness;
    [ObservableProperty] private GaugeStyle _gauges;
    [ObservableProperty] private AnimationLevel _animations;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Sync();
    }

    private void OnTokenChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (Colors.Count == 0)
        {
            return;
        }

        foreach (var c in Colors)
        {
            if (ThemeDefinition.IsValidColor(c.Hex))
            {
                typeof(ThemeDefinition).GetProperty(c.Key)!.SetValue(Theme, c.Hex);
            }
        }

        Theme.ChartPalette = Palette.Select(p => p.Hex).Where(ThemeDefinition.IsValidColor).ToList();
        Theme.Name = Name;
        Theme.CornerRadius = CornerRadius;
        Theme.SurfaceOpacity = SurfaceOpacity;
        Theme.FontScale = FontScale;
        Theme.Spacing = Spacing;
        Theme.GlowIntensity = GlowIntensity;
        Theme.BorderThickness = BorderThickness;
        Theme.Gauges = Gauges;
        Theme.Animations = Animations;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class OverlayItemRow(OverlayItem item) : ObservableObject
{
    public OverlayItem Item { get; } = item;

    public string Name => Fmt.OverlayMetricName(Item.Metric);

    public bool CanChooseDisplay => Item.Metric != OverlayMetric.FrameTimeGraph;

    [ObservableProperty]
    private bool _visible = item.Visible;

    [ObservableProperty]
    private OverlayDisplay _display = item.Display;
}

public sealed record MonitorChoice(string? Device, string Name);

public sealed partial class WidgetRow(DashboardWidgetConfig config) : ObservableObject
{
    public DashboardWidgetConfig Config { get; } = config;

    public string Name => CustomizationViewModel.WidgetName(Config.Widget);

    [ObservableProperty]
    private bool _visible = config.Visible;

    [ObservableProperty]
    private bool _wide = config.Size == WidgetSize.Wide;
}

public sealed partial class CustomizationViewModel : ObservableObject, IPageActivated
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly OverlayService _overlay;
    private readonly DialogService _dialogs;
    private readonly ILogger<CustomizationViewModel> _logger;
    private readonly DispatcherTimer _saveDelay;
    private bool _loading;
    private bool _themesDirty;
    private bool _overlaysDirty;
    private bool _settingsDirty;

    public CustomizationViewModel(SettingsService settings, ThemeService theme, OverlayService overlay, DialogService dialogs, ILogger<CustomizationViewModel> logger)
    {
        _settings = settings;
        _theme = theme;
        _overlay = overlay;
        _dialogs = dialogs;
        _logger = logger;
        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveDelay.Tick += (_, _) => FlushSaves();
        overlay.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OverlayEditMode));
            OnPropertyChanged(nameof(OverlayPreview));
        };
    }

    [ObservableProperty]
    private CustomizationTab _tab;

    public ObservableCollection<ThemeCard> Themes { get; } = [];

    [ObservableProperty]
    private ThemeEditor? _editor;

    public ObservableCollection<OverlayLayout> Layouts { get; } = [];

    [ObservableProperty]
    private OverlayLayout? _selectedLayout;

    public ObservableCollection<OverlayItemRow> OverlayItems { get; } = [];

    public ObservableCollection<OverlayMetric> AvailableMetrics { get; } = [];

    public ObservableCollection<MonitorChoice> Monitors { get; } = [];

    public ObservableCollection<WidgetRow> Widgets { get; } = [];

    public IReadOnlyList<OverlayStyle> OverlayStyles { get; } = Enum.GetValues<OverlayStyle>();
    public IReadOnlyList<OverlayDisplay> DisplayModes { get; } = Enum.GetValues<OverlayDisplay>();
    public IReadOnlyList<GaugeStyle> GaugeStyles { get; } = Enum.GetValues<GaugeStyle>();
    public IReadOnlyList<AnimationLevel> AnimationLevels { get; } = Enum.GetValues<AnimationLevel>();

    // Propriedades do layout selecionado (espelhadas para binding com salvamento automático).
    [ObservableProperty] private string _layoutName = "";
    [ObservableProperty] private OverlayStyle _layoutStyle;
    [ObservableProperty] private bool _layoutHorizontal;
    [ObservableProperty] private double _layoutScale;
    [ObservableProperty] private double _layoutFontSize;
    [ObservableProperty] private double _layoutOpacity;
    [ObservableProperty] private double _layoutCornerRadius;
    [ObservableProperty] private double _layoutBorder;
    [ObservableProperty] private double _layoutIndicator;
    [ObservableProperty] private bool _layoutShowTitles;
    [ObservableProperty] private int _layoutRefreshMs;
    [ObservableProperty] private bool _layoutCustomColors;
    [ObservableProperty] private string _layoutBackground = "#070B14";
    [ObservableProperty] private string _layoutText = "#E7EEFC";
    [ObservableProperty] private string _layoutAccent = "#00E5FF";
    [ObservableProperty] private string _layoutBorderColor = "#22304D";
    [ObservableProperty] private MonitorChoice? _layoutMonitor;
    [ObservableProperty] private OverlayMetric? _metricToAdd;

    // Interface
    [ObservableProperty] private double _uiScale;
    [ObservableProperty] private AnimationLevel _animations;
    [ObservableProperty] private PanelDensity _density;
    [ObservableProperty] private TemperatureUnit _temperatureUnit;
    [ObservableProperty] private MemoryUnitConvention _memoryUnits;

    public bool OverlayEditMode => _overlay.EditMode;

    public bool OverlayPreview => _overlay.Preview;

    public bool IsActiveLayout => SelectedLayout is not null && SelectedLayout.Id == _settings.Current.ActiveOverlayLayoutId;

    public bool IsPresetLayout => SelectedLayout?.Id.StartsWith("preset-", StringComparison.Ordinal) == true;

    public static string WidgetName(DashboardWidget w) => w switch
    {
        DashboardWidget.CpuGauge => "Medidor de CPU",
        DashboardWidget.GpuGauge => "Medidor de GPU",
        DashboardWidget.RamGauge => "Medidor de RAM",
        DashboardWidget.VramGauge => "Medidor de VRAM",
        DashboardWidget.FramePanel => "Painel de FPS e frametime",
        DashboardWidget.LoadChart => "Gráfico de uso (CPU/GPU)",
        DashboardWidget.TemperatureChart => "Gráfico de temperaturas",
        DashboardWidget.FrameTimeChart => "Gráfico de frametime",
        DashboardWidget.SessionStatus => "Sessão atual",
        DashboardWidget.CoreLoads => "Uso por núcleo",
        DashboardWidget.SystemInfo => "Resumo do sistema",
        DashboardWidget.RecentSessions => "Sessões recentes",
        _ => w.ToString(),
    };

    public void OnActivated()
    {
        _loading = true;
        LoadThemes();
        LoadLayouts();
        LoadWidgets();
        var s = _settings.Current;
        UiScale = s.UiScale;
        Animations = s.Animations;
        Density = s.Density;
        TemperatureUnit = s.TemperatureUnit;
        MemoryUnits = s.MemoryUnits;
        _loading = false;
    }

    public void OnDeactivated()
    {
        FlushSaves();
        if (_overlay.Preview)
        {
            _overlay.SetPreview(false);
        }
    }

    private void ScheduleSave()
    {
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void FlushSaves()
    {
        _saveDelay.Stop();
        try
        {
            if (_themesDirty)
            {
                _themesDirty = false;
                _settings.SaveThemes();
            }

            if (_overlaysDirty)
            {
                _overlaysDirty = false;
                _settings.SaveOverlayLayouts();
            }

            if (_settingsDirty)
            {
                _settingsDirty = false;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar personalização");
            _dialogs.Info("Personalização", "Não foi possível salvar: " + ex.Message);
        }
    }

    // ============================== Temas ==============================

    private void LoadThemes()
    {
        Themes.Clear();
        foreach (var t in _settings.AllThemes)
        {
            Themes.Add(new ThemeCard(t, t.Id == _settings.Current.ThemeId));
        }

        var active = _settings.CustomThemes.FirstOrDefault(t => t.Id == _settings.Current.ThemeId);
        SetEditor(active);
    }

    private void SetEditor(ThemeDefinition? theme)
    {
        if (Editor is not null)
        {
            Editor.Changed -= OnEditorChanged;
        }

        Editor = theme is null ? null : new ThemeEditor(theme);
        if (Editor is not null)
        {
            Editor.Changed += OnEditorChanged;
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        if (Editor is null)
        {
            return;
        }

        var stored = _settings.CustomThemes.FirstOrDefault(t => t.Id == Editor.Theme.Id);
        if (stored is null)
        {
            return;
        }

        var copy = Editor.Theme.Clone();
        if (copy.Validate().Count > 0)
        {
            return;
        }

        var index = _settings.CustomThemes.IndexOf(stored);
        _settings.CustomThemes[index] = copy;
        _themesDirty = true;
        if (_settings.Current.ThemeId == copy.Id)
        {
            _theme.Apply(copy, _settings.Current);
        }

        var card = Themes.FirstOrDefault(c => c.Theme.Id == copy.Id);
        if (card is not null)
        {
            Themes[Themes.IndexOf(card)] = new ThemeCard(copy, card.IsActive);
        }

        ScheduleSave();
    }

    [RelayCommand]
    private void ApplyTheme(ThemeCard card)
    {
        _settings.Current.ThemeId = card.Theme.Id;
        _settingsDirty = true;
        _theme.Apply(card.Theme, _settings.Current);
        foreach (var c in Themes)
        {
            c.IsActive = c.Theme.Id == card.Theme.Id;
        }

        SetEditor(card.BuiltIn ? null : _settings.CustomThemes.FirstOrDefault(t => t.Id == card.Theme.Id));
        FlushSaves();
    }

    [RelayCommand]
    private void DuplicateTheme(ThemeCard card)
    {
        var copy = card.Theme.Clone();
        copy.Id = "custom-" + Guid.NewGuid().ToString("N")[..12];
        copy.BuiltIn = false;
        copy.Name = Truncate(card.Theme.Name + " (personalizado)", 60);
        _settings.CustomThemes.Add(copy);
        _themesDirty = true;
        LoadThemes();
        ApplyTheme(Themes.First(t => t.Theme.Id == copy.Id));
    }

    [RelayCommand]
    private void DeleteTheme(ThemeCard card)
    {
        if (card.BuiltIn || !_dialogs.Confirm("Excluir tema", $"Excluir o tema \"{card.Name}\"?", "Excluir", danger: true))
        {
            return;
        }

        _settings.CustomThemes.RemoveAll(t => t.Id == card.Theme.Id);
        _themesDirty = true;
        if (_settings.Current.ThemeId == card.Theme.Id)
        {
            _settings.Current.ThemeId = BuiltInThemes.DefaultId;
            _settingsDirty = true;
            _theme.Apply(BuiltInThemes.Get(BuiltInThemes.DefaultId), _settings.Current);
        }

        FlushSaves();
        LoadThemes();
    }

    [RelayCommand]
    private void ExportTheme(ThemeCard card) => ExportPackage(ConfigPackageSerializer.Export(card.Theme), "tema-" + card.Name);

    [RelayCommand]
    private void ImportTheme()
    {
        var json = ReadPackage("Importar tema");
        if (json is null)
        {
            return;
        }

        var result = ConfigPackageSerializer.ImportTheme(json);
        if (result.Value is null)
        {
            ShowErrors("Importar tema", result.Errors);
            return;
        }

        var theme = result.Value;
        theme.BuiltIn = false;
        theme.Id = "custom-" + Guid.NewGuid().ToString("N")[..12];
        _settings.CustomThemes.Add(theme);
        _themesDirty = true;
        FlushSaves();
        LoadThemes();
        _dialogs.Info("Importar tema", $"Tema \"{theme.Name}\" importado. Clique em Aplicar para usá-lo.");
    }

    // ============================== Overlay ==============================

    private void LoadLayouts()
    {
        var selectedId = SelectedLayout?.Id ?? _settings.Current.ActiveOverlayLayoutId;
        Layouts.Clear();
        foreach (var l in _settings.OverlayLayouts)
        {
            Layouts.Add(l);
        }

        Monitors.Clear();
        Monitors.Add(new MonitorChoice(null, "Monitor principal"));
        foreach (var screen in Forms.Screen.AllScreens.Where(s => !s.Primary))
        {
            Monitors.Add(new MonitorChoice(screen.DeviceName, $"{screen.DeviceName.TrimStart('\\', '.')} ({screen.Bounds.Width}×{screen.Bounds.Height})"));
        }

        SelectedLayout = Layouts.FirstOrDefault(l => l.Id == selectedId) ?? Layouts.FirstOrDefault();
    }

    partial void OnSelectedLayoutChanged(OverlayLayout? value)
    {
        var wasLoading = _loading;
        _loading = true;
        OverlayItems.Clear();
        if (value is not null)
        {
            LayoutName = value.Name;
            LayoutStyle = value.Style;
            LayoutHorizontal = value.Orientation == OverlayOrientation.Horizontal;
            LayoutScale = value.Scale;
            LayoutFontSize = value.FontSize;
            LayoutOpacity = value.BackgroundOpacity;
            LayoutCornerRadius = value.CornerRadius;
            LayoutBorder = value.BorderThickness;
            LayoutIndicator = value.IndicatorThickness;
            LayoutShowTitles = value.ShowTitles;
            LayoutRefreshMs = value.RefreshMs;
            LayoutCustomColors = value.BackgroundColor is not null;
            var t = _theme.Active;
            LayoutBackground = value.BackgroundColor ?? t.Background;
            LayoutText = value.TextColor ?? t.Text;
            LayoutAccent = value.AccentColor ?? t.Primary;
            LayoutBorderColor = value.BorderColor ?? t.Border;
            LayoutMonitor = Monitors.FirstOrDefault(m => m.Device == value.MonitorDevice) ?? Monitors.FirstOrDefault();
            foreach (var item in value.Items)
            {
                AddItemRow(item);
            }
        }

        RefreshAvailableMetrics();
        _loading = wasLoading;
        OnPropertyChanged(nameof(IsActiveLayout));
        OnPropertyChanged(nameof(IsPresetLayout));
    }

    private void AddItemRow(OverlayItem item)
    {
        var row = new OverlayItemRow(item);
        row.PropertyChanged += (_, _) =>
        {
            row.Item.Visible = row.Visible;
            row.Item.Display = row.Display;
            LayoutChanged();
        };
        OverlayItems.Add(row);
    }

    private void RefreshAvailableMetrics()
    {
        AvailableMetrics.Clear();
        foreach (var m in Enum.GetValues<OverlayMetric>().Where(m => OverlayItems.All(i => i.Item.Metric != m)))
        {
            AvailableMetrics.Add(m);
        }

        MetricToAdd = AvailableMetrics.Count > 0 ? AvailableMetrics[0] : null;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null)
        {
            return;
        }

        if (e.PropertyName.StartsWith("Layout", StringComparison.Ordinal) && e.PropertyName != nameof(Layouts))
        {
            LayoutChanged();
        }
        else if (e.PropertyName is nameof(UiScale) or nameof(Animations) or nameof(Density) or nameof(TemperatureUnit) or nameof(MemoryUnits))
        {
            var s = _settings.Current;
            s.UiScale = UiScale;
            s.Animations = Animations;
            s.Density = Density;
            s.TemperatureUnit = TemperatureUnit;
            s.MemoryUnits = MemoryUnits;
            _settingsDirty = true;
            _theme.Apply(_settings.ActiveTheme, s);
            ScheduleSave();
        }
    }

    partial void OnLayoutStyleChanged(OverlayStyle oldValue, OverlayStyle newValue)
    {
        if (_loading || SelectedLayout is null || newValue == OverlayStyle.Custom)
        {
            return;
        }

        // Trocar o estilo aplica a seleção de métricas correspondente.
        _loading = true;
        SelectedLayout.Items = OverlayPresets.ItemsFor(newValue);
        OverlayItems.Clear();
        foreach (var i in SelectedLayout.Items)
        {
            AddItemRow(i);
        }

        LayoutHorizontal = newValue == OverlayStyle.Minimal;
        RefreshAvailableMetrics();
        _loading = false;
    }

    private void LayoutChanged()
    {
        var l = SelectedLayout;
        if (l is null || _loading)
        {
            return;
        }

        l.Name = string.IsNullOrWhiteSpace(LayoutName) ? l.Name : Truncate(LayoutName.Trim(), 60);
        l.Style = LayoutStyle;
        l.Orientation = LayoutHorizontal ? OverlayOrientation.Horizontal : OverlayOrientation.Vertical;
        l.Scale = LayoutScale;
        l.FontSize = LayoutFontSize;
        l.BackgroundOpacity = LayoutOpacity;
        l.CornerRadius = LayoutCornerRadius;
        l.BorderThickness = LayoutBorder;
        l.IndicatorThickness = LayoutIndicator;
        l.ShowTitles = LayoutShowTitles;
        l.RefreshMs = LayoutRefreshMs;
        l.MonitorDevice = LayoutMonitor?.Device;
        if (LayoutCustomColors)
        {
            l.BackgroundColor = ThemeDefinition.IsValidColor(LayoutBackground) ? LayoutBackground : null;
            l.TextColor = ThemeDefinition.IsValidColor(LayoutText) ? LayoutText : null;
            l.AccentColor = ThemeDefinition.IsValidColor(LayoutAccent) ? LayoutAccent : null;
            l.BorderColor = ThemeDefinition.IsValidColor(LayoutBorderColor) ? LayoutBorderColor : null;
        }
        else
        {
            l.BackgroundColor = l.TextColor = l.AccentColor = l.BorderColor = null;
        }

        l.Items = OverlayItems.Select(r => r.Item).ToList();
        l.Validate();
        _overlaysDirty = true;
        _overlay.ApplyLayout();
        ScheduleSave();
    }

    [RelayCommand]
    private void SetActiveLayout()
    {
        if (SelectedLayout is null)
        {
            return;
        }

        _settings.Current.ActiveOverlayLayoutId = SelectedLayout.Id;
        _settingsDirty = true;
        FlushSaves();
        _overlay.ApplyLayout();
        OnPropertyChanged(nameof(IsActiveLayout));
    }

    [RelayCommand]
    private void NewLayout()
    {
        var layout = OverlayPresets.Create(OverlayStyle.Compact, "Meu layout");
        layout.Id = Guid.NewGuid().ToString("N");
        layout.Style = OverlayStyle.Custom;
        _settings.OverlayLayouts.Add(layout);
        _overlaysDirty = true;
        FlushSaves();
        LoadLayouts();
        SelectedLayout = Layouts.First(l => l.Id == layout.Id);
    }

    [RelayCommand]
    private void DuplicateLayout()
    {
        if (SelectedLayout is null)
        {
            return;
        }

        var copy = SelectedLayout.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Truncate(SelectedLayout.Name + " (cópia)", 60);
        _settings.OverlayLayouts.Add(copy);
        _overlaysDirty = true;
        FlushSaves();
        LoadLayouts();
        SelectedLayout = Layouts.First(l => l.Id == copy.Id);
    }

    [RelayCommand]
    private void DeleteLayout()
    {
        var l = SelectedLayout;
        if (l is null || IsPresetLayout || !_dialogs.Confirm("Excluir layout", $"Excluir o layout \"{l.Name}\"?", "Excluir", danger: true))
        {
            return;
        }

        _settings.OverlayLayouts.RemoveAll(x => x.Id == l.Id);
        if (_settings.Current.ActiveOverlayLayoutId == l.Id)
        {
            _settings.Current.ActiveOverlayLayoutId = _settings.OverlayLayouts[0].Id;
            _settingsDirty = true;
        }

        _overlaysDirty = true;
        FlushSaves();
        SelectedLayout = null;
        LoadLayouts();
        _overlay.ApplyLayout();
    }

    [RelayCommand]
    private void ResetPreset()
    {
        var l = SelectedLayout;
        if (l is null || !IsPresetLayout)
        {
            return;
        }

        var preset = OverlayPresets.Defaults().FirstOrDefault(p => p.Id == l.Id);
        if (preset is null)
        {
            return;
        }

        var index = _settings.OverlayLayouts.FindIndex(x => x.Id == l.Id);
        _settings.OverlayLayouts[index] = preset;
        _overlaysDirty = true;
        FlushSaves();
        SelectedLayout = null;
        LoadLayouts();
        SelectedLayout = Layouts.First(x => x.Id == preset.Id);
        _overlay.ApplyLayout();
    }

    [RelayCommand]
    private void MoveItem(OverlayItemRow row) => Move(OverlayItems, row, -1);

    [RelayCommand]
    private void MoveItemDown(OverlayItemRow row) => Move(OverlayItems, row, 1);

    private void Move<T>(ObservableCollection<T> list, T item, int delta)
    {
        var i = list.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count)
        {
            return;
        }

        list.Move(i, j);
        if (typeof(T) == typeof(OverlayItemRow))
        {
            LayoutChanged();
        }
        else
        {
            WidgetsChanged();
        }
    }

    [RelayCommand]
    private void RemoveItem(OverlayItemRow row)
    {
        OverlayItems.Remove(row);
        RefreshAvailableMetrics();
        LayoutChanged();
    }

    [RelayCommand]
    private void AddItem()
    {
        if (MetricToAdd is not { } metric)
        {
            return;
        }

        AddItemRow(new OverlayItem { Metric = metric, Display = metric == OverlayMetric.FrameTimeGraph ? OverlayDisplay.Graph : OverlayDisplay.Text });
        RefreshAvailableMetrics();
        LayoutChanged();
    }

    [RelayCommand]
    private void PlaceOverlay(string corner)
    {
        if (SelectedLayout is null)
        {
            return;
        }

        (SelectedLayout.PositionX, SelectedLayout.PositionY) = corner switch
        {
            "TopLeft" => (0.01, 0.02),
            "TopRight" => (0.99, 0.02),
            "BottomLeft" => (0.01, 0.97),
            "BottomRight" => (0.99, 0.97),
            _ => (0.5, 0.02),
        };
        _overlaysDirty = true;
        _overlay.ApplyLayout();
        ScheduleSave();
    }

    [RelayCommand]
    private void TogglePreview() => _overlay.SetPreview(!_overlay.Preview);

    [RelayCommand]
    private void ToggleEditPosition()
    {
        if (SelectedLayout is not null && !IsActiveLayout)
        {
            SetActiveLayout();
        }

        _overlay.ToggleEditMode();
        if (!_overlay.EditMode)
        {
            // Posição salva pela janela; recarrega para refletir.
            OnSelectedLayoutChanged(SelectedLayout);
        }
    }

    [RelayCommand]
    private void ExportLayout()
    {
        if (SelectedLayout is not null)
        {
            ExportPackage(ConfigPackageSerializer.Export(SelectedLayout), "overlay-" + SelectedLayout.Name);
        }
    }

    [RelayCommand]
    private void ImportLayout()
    {
        var json = ReadPackage("Importar layout de overlay");
        if (json is null)
        {
            return;
        }

        var result = ConfigPackageSerializer.ImportOverlay(json);
        if (result.Value is null)
        {
            ShowErrors("Importar layout", result.Errors);
            return;
        }

        var layout = result.Value;
        layout.Id = Guid.NewGuid().ToString("N");
        _settings.OverlayLayouts.Add(layout);
        _overlaysDirty = true;
        FlushSaves();
        LoadLayouts();
        SelectedLayout = Layouts.First(l => l.Id == layout.Id);
    }

    // ============================== Painel ==============================

    private void LoadWidgets()
    {
        Widgets.Clear();
        foreach (var w in _settings.Current.Dashboard.Widgets)
        {
            var row = new WidgetRow(w);
            row.PropertyChanged += (_, _) =>
            {
                row.Config.Visible = row.Visible;
                row.Config.Size = row.Wide ? WidgetSize.Wide : WidgetSize.Small;
                WidgetsChanged();
            };
            Widgets.Add(row);
        }
    }

    private void WidgetsChanged()
    {
        _settings.Current.Dashboard.Widgets = Widgets.Select(w => w.Config).ToList();
        _settingsDirty = true;
        FlushSaves();
    }

    [RelayCommand]
    private void MoveWidget(WidgetRow row) => Move(Widgets, row, -1);

    [RelayCommand]
    private void MoveWidgetDown(WidgetRow row) => Move(Widgets, row, 1);

    [RelayCommand]
    private void ResetDashboard()
    {
        _settings.Current.Dashboard = new DashboardLayout();
        _settingsDirty = true;
        FlushSaves();
        LoadWidgets();
    }

    [RelayCommand]
    private void ExportDashboard() => ExportPackage(ConfigPackageSerializer.Export(_settings.Current.Dashboard), "painel");

    [RelayCommand]
    private void ImportDashboard()
    {
        var json = ReadPackage("Importar layout do painel");
        if (json is null)
        {
            return;
        }

        var result = ConfigPackageSerializer.ImportDashboard(json);
        if (result.Value is null)
        {
            ShowErrors("Importar layout do painel", result.Errors);
            return;
        }

        _settings.Current.Dashboard = result.Value;
        _settingsDirty = true;
        FlushSaves();
        LoadWidgets();
    }

    // ============================== Arquivos ==============================

    private const string PackageFilter = "Configuração FRAMEX (*.framex.json)|*.framex.json|JSON (*.json)|*.json";

    private void ExportPackage(string json, string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = _dialogs.SaveFile("Exportar configuração", PackageFilter, safe + ".framex.json", null);
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exportar configuração");
            _dialogs.Info("Exportar", "Não foi possível salvar o arquivo: " + ex.Message);
        }
    }

    /// <summary>Lê o arquivo como texto (limite de tamanho aplicado); o conteúdo é apenas desserializado e validado, nunca executado.</summary>
    private string? ReadPackage(string title)
    {
        var path = _dialogs.OpenFile(title, PackageFilter);
        if (path is null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > ConfigPackage.MaxFileBytes)
            {
                _dialogs.Info(title, $"Arquivo grande demais ({info.Length / 1024} KB). O limite é {ConfigPackage.MaxFileBytes / 1024} KB.");
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler arquivo de configuração");
            _dialogs.Info(title, "Não foi possível ler o arquivo: " + ex.Message);
            return null;
        }
    }

    private void ShowErrors(string title, IReadOnlyList<string> errors) =>
        _dialogs.Info(title, "O arquivo não foi importado:\n• " + string.Join("\n• ", errors));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
