using Framex.Core.Alerts;
using Framex.Core.Monitoring;

namespace Framex.Core.Customization;

public enum PerformanceProfile
{
    Economy,
    Balanced,
    Detailed,
}

public enum CloseBehavior
{
    Ask,
    MinimizeToTray,
    Exit,
}

public enum PanelDensity
{
    Comfortable,
    Compact,
}

public enum DashboardWidget
{
    CpuGauge,
    GpuGauge,
    RamGauge,
    VramGauge,
    FramePanel,
    LoadChart,
    TemperatureChart,
    FrameTimeChart,
    SessionStatus,
    CoreLoads,
    SystemInfo,
    RecentSessions,
}

public enum WidgetSize
{
    Small,
    Wide,
}

public sealed class DashboardWidgetConfig
{
    public DashboardWidget Widget { get; set; }
    public bool Visible { get; set; } = true;
    public WidgetSize Size { get; set; } = WidgetSize.Small;
}

public sealed class DashboardLayout
{
    public List<DashboardWidgetConfig> Widgets { get; set; } = Default();

    public static List<DashboardWidgetConfig> Default() =>
    [
        new() { Widget = DashboardWidget.FramePanel, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.SessionStatus, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.CpuGauge },
        new() { Widget = DashboardWidget.GpuGauge },
        new() { Widget = DashboardWidget.RamGauge },
        new() { Widget = DashboardWidget.VramGauge },
        new() { Widget = DashboardWidget.LoadChart, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.TemperatureChart, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.FrameTimeChart, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.CoreLoads, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.SystemInfo, Size = WidgetSize.Wide },
        new() { Widget = DashboardWidget.RecentSessions, Size = WidgetSize.Wide },
    ];

    /// <summary>Garante que todos os widgets existam uma única vez (layouts antigos ou importados).</summary>
    public void Normalize()
    {
        var seen = new HashSet<DashboardWidget>();
        Widgets = Widgets.Where(w => Enum.IsDefined(w.Widget) && seen.Add(w.Widget)).ToList();
        foreach (var missing in Default().Where(d => !seen.Contains(d.Widget)))
        {
            Widgets.Add(missing);
        }
    }
}

public sealed class HotkeySettings
{
    public string ToggleOverlay { get; set; } = "Ctrl+Shift+F10";
    public string ToggleMainWindow { get; set; } = "Ctrl+Shift+F11";
    public string PauseRecording { get; set; } = "Ctrl+Shift+F9";
    public string OverlayEditMode { get; set; } = "Ctrl+Shift+F8";
}

/// <summary>Preferências do usuário, persistidas como JSON no banco local.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Geral / janela
    public string Language { get; set; } = "pt-BR";
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;
    public bool ShowTrayIcon { get; set; } = true;

    // Unidades
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;
    public MemoryUnitConvention MemoryUnits { get; set; } = MemoryUnitConvention.Binary;

    // Desempenho
    public PerformanceProfile Profile { get; set; } = PerformanceProfile.Balanced;
    public string? PreferredGpuId { get; set; }

    // Captura / jogos
    public bool CaptureFrames { get; set; } = true;
    public string? PresentMonPath { get; set; }
    public bool AutoDetectStoreGames { get; set; } = true;
    public bool RecordUnconfirmedGames { get; set; } = true;
    public int ExitGraceSeconds { get; set; } = 10;
    public int MinSessionSeconds { get; set; } = 20;

    // Relatórios
    public bool AutoOpenReport { get; set; } = true;
    public bool NotifySessionEnd { get; set; } = true;
    public string? ExportFolder { get; set; }
    public int DetailedRetentionDays { get; set; }

    // Alertas / notificações
    public bool AlertsEnabled { get; set; } = true;
    public AlertChannel AlertChannel { get; set; } = AlertChannel.Windows;

    // Interface
    public string ThemeId { get; set; } = BuiltInThemes.DefaultId;
    public double UiScale { get; set; } = 1.0;
    public AnimationLevel Animations { get; set; } = AnimationLevel.Full;
    public PanelDensity Density { get; set; } = PanelDensity.Comfortable;
    public DashboardLayout Dashboard { get; set; } = new();

    // Overlay
    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayOnlyDuringGames { get; set; } = true;
    public string ActiveOverlayLayoutId { get; set; } = "preset-compact";

    public HotkeySettings Hotkeys { get; set; } = new();

    public void Normalize()
    {
        UiScale = Math.Clamp(UiScale, 0.8, 1.5);
        ExitGraceSeconds = Math.Clamp(ExitGraceSeconds, 0, 120);
        MinSessionSeconds = Math.Clamp(MinSessionSeconds, 0, 600);
        DetailedRetentionDays = Math.Clamp(DetailedRetentionDays, 0, 3650);
        Dashboard ??= new DashboardLayout();
        Dashboard.Normalize();
        Hotkeys ??= new HotkeySettings();
    }
}

/// <summary>Parâmetros de coleta derivados do perfil de desempenho.</summary>
public sealed record PerformanceTuning(TimeSpan SensorInterval, TimeSpan UiInterval, int OverlayMinRefreshMs, TimeSpan ProcessPollInterval, bool TrackGpuInCapture, bool ChartsEnabled)
{
    public static PerformanceTuning For(PerformanceProfile profile) => profile switch
    {
        PerformanceProfile.Economy => new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 1000, TimeSpan.FromSeconds(4), false, false),
        PerformanceProfile.Detailed => new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), 250, TimeSpan.FromSeconds(2), true, true),
        _ => new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 500, TimeSpan.FromSeconds(2), true, true),
    };

    public static string DisplayName(PerformanceProfile profile) => profile switch
    {
        PerformanceProfile.Economy => "Economia",
        PerformanceProfile.Detailed => "Detalhado",
        _ => "Equilibrado",
    };
}
