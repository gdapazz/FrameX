namespace Framex.Core.Customization;

public enum OverlayStyle
{
    Minimal,
    Compact,
    Detailed,
    PerformancePanel,
    FpsOnly,
    Custom,
}

public enum OverlayMetric
{
    Fps,
    FrameTime,
    Low1Percent,
    FrameTimeGraph,
    CpuLoad,
    CpuTemperature,
    CpuClock,
    CpuPower,
    GpuLoad,
    GpuTemperature,
    GpuHotspot,
    GpuClock,
    GpuPower,
    Ram,
    Vram,
    Clock,
}

public enum OverlayDisplay
{
    Text,
    Bar,
    Graph,
    Ring,
}

public enum OverlayOrientation
{
    Vertical,
    Horizontal,
}

public sealed class OverlayItem
{
    public OverlayMetric Metric { get; set; }
    public OverlayDisplay Display { get; set; } = OverlayDisplay.Text;
    public bool Visible { get; set; } = true;
}

public sealed class OverlayLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Meu layout";
    public OverlayStyle Style { get; set; } = OverlayStyle.Compact;
    public List<OverlayItem> Items { get; set; } = [];
    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Vertical;

    /// <summary>Posição relativa (0–1) dentro da área de trabalho do monitor escolhido.</summary>
    public double PositionX { get; set; } = 0.01;
    public double PositionY { get; set; } = 0.02;

    /// <summary>Nome do dispositivo do monitor (ex.: \\.\DISPLAY1); nulo = monitor principal.</summary>
    public string? MonitorDevice { get; set; }

    public double Scale { get; set; } = 1.0;
    public double FontSize { get; set; } = 13;
    public double Width { get; set; }
    public double Height { get; set; }
    public double BackgroundOpacity { get; set; } = 0.78;
    public double CornerRadius { get; set; } = 10;
    public double BorderThickness { get; set; } = 1;
    public double IndicatorThickness { get; set; } = 4;
    public bool ShowTitles { get; set; } = true;
    public int RefreshMs { get; set; } = 500;

    /// <summary>Cores opcionais (#RRGGBB); nulas herdam do tema ativo.</summary>
    public string? BackgroundColor { get; set; }
    public string? TextColor { get; set; }
    public string? AccentColor { get; set; }
    public string? BorderColor { get; set; }

    public OverlayLayout Clone()
    {
        var c = (OverlayLayout)MemberwiseClone();
        c.Items = Items.Select(i => new OverlayItem { Metric = i.Metric, Display = i.Display, Visible = i.Visible }).ToList();
        return c;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
        {
            errors.Add("Nome do layout inválido.");
        }

        if (Items.Count > 32)
        {
            errors.Add("Layout com itens demais (máximo 32).");
        }

        foreach (var color in new[] { BackgroundColor, TextColor, AccentColor, BorderColor })
        {
            if (color is not null && !ThemeDefinition.IsValidColor(color))
            {
                errors.Add($"Cor inválida: {color}");
            }
        }

        if (!Enum.IsDefined(Style) || Items.Any(i => !Enum.IsDefined(i.Metric) || !Enum.IsDefined(i.Display)))
        {
            errors.Add("Valores desconhecidos no layout.");
        }

        PositionX = Math.Clamp(PositionX, 0, 1);
        PositionY = Math.Clamp(PositionY, 0, 1);
        Scale = Math.Clamp(Scale, 0.5, 3);
        FontSize = Math.Clamp(FontSize, 8, 40);
        Width = Math.Clamp(Width, 0, 2000);
        Height = Math.Clamp(Height, 0, 2000);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        CornerRadius = Math.Clamp(CornerRadius, 0, 30);
        BorderThickness = Math.Clamp(BorderThickness, 0, 4);
        IndicatorThickness = Math.Clamp(IndicatorThickness, 1, 12);
        RefreshMs = Math.Clamp(RefreshMs, 100, 5000);
        return errors;
    }
}

public static class OverlayPresets
{
    public static List<OverlayItem> ItemsFor(OverlayStyle style) => style switch
    {
        OverlayStyle.FpsOnly => [new() { Metric = OverlayMetric.Fps }],
        OverlayStyle.Minimal =>
        [
            new() { Metric = OverlayMetric.Fps },
            new() { Metric = OverlayMetric.CpuTemperature },
            new() { Metric = OverlayMetric.GpuTemperature },
        ],
        OverlayStyle.Compact =>
        [
            new() { Metric = OverlayMetric.Fps },
            new() { Metric = OverlayMetric.FrameTime },
            new() { Metric = OverlayMetric.CpuLoad, Display = OverlayDisplay.Bar },
            new() { Metric = OverlayMetric.GpuLoad, Display = OverlayDisplay.Bar },
            new() { Metric = OverlayMetric.Ram },
            new() { Metric = OverlayMetric.Vram },
        ],
        OverlayStyle.Detailed =>
        [
            new() { Metric = OverlayMetric.Fps },
            new() { Metric = OverlayMetric.Low1Percent },
            new() { Metric = OverlayMetric.FrameTime },
            new() { Metric = OverlayMetric.FrameTimeGraph, Display = OverlayDisplay.Graph },
            new() { Metric = OverlayMetric.CpuLoad, Display = OverlayDisplay.Bar },
            new() { Metric = OverlayMetric.CpuTemperature },
            new() { Metric = OverlayMetric.CpuClock },
            new() { Metric = OverlayMetric.GpuLoad, Display = OverlayDisplay.Bar },
            new() { Metric = OverlayMetric.GpuTemperature },
            new() { Metric = OverlayMetric.GpuClock },
            new() { Metric = OverlayMetric.GpuPower },
            new() { Metric = OverlayMetric.Ram, Display = OverlayDisplay.Bar },
            new() { Metric = OverlayMetric.Vram, Display = OverlayDisplay.Bar },
        ],
        OverlayStyle.PerformancePanel =>
        [
            new() { Metric = OverlayMetric.Fps, Display = OverlayDisplay.Ring },
            new() { Metric = OverlayMetric.CpuLoad, Display = OverlayDisplay.Ring },
            new() { Metric = OverlayMetric.GpuLoad, Display = OverlayDisplay.Ring },
            new() { Metric = OverlayMetric.FrameTimeGraph, Display = OverlayDisplay.Graph },
            new() { Metric = OverlayMetric.Low1Percent },
            new() { Metric = OverlayMetric.CpuTemperature },
            new() { Metric = OverlayMetric.GpuTemperature },
            new() { Metric = OverlayMetric.Vram, Display = OverlayDisplay.Bar },
        ],
        _ => [new() { Metric = OverlayMetric.Fps }, new() { Metric = OverlayMetric.FrameTime }],
    };

    public static OverlayLayout Create(OverlayStyle style, string? name = null) => new()
    {
        Id = "preset-" + style.ToString().ToLowerInvariant(),
        Name = name ?? StyleName(style),
        Style = style,
        Items = ItemsFor(style),
        Orientation = style == OverlayStyle.Minimal ? OverlayOrientation.Horizontal : OverlayOrientation.Vertical,
        FontSize = style == OverlayStyle.FpsOnly ? 22 : 13,
    };

    public static string StyleName(OverlayStyle style) => style switch
    {
        OverlayStyle.Minimal => "Minimalista",
        OverlayStyle.Compact => "Compacto",
        OverlayStyle.Detailed => "Detalhado",
        OverlayStyle.PerformancePanel => "Painel de desempenho",
        OverlayStyle.FpsOnly => "Somente FPS",
        _ => "Personalizado",
    };

    public static List<OverlayLayout> Defaults() =>
        [Create(OverlayStyle.Compact), Create(OverlayStyle.Minimal), Create(OverlayStyle.Detailed), Create(OverlayStyle.PerformancePanel), Create(OverlayStyle.FpsOnly)];
}
