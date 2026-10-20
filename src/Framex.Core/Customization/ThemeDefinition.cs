using System.Text.RegularExpressions;

namespace Framex.Core.Customization;

public enum AnimationLevel
{
    Off,
    Reduced,
    Full,
}

public enum GaugeStyle
{
    Ring,
    Arc,
    Bar,
}

/// <summary>Tokens visuais de um tema. Cores no formato #RRGGBB ou #AARRGGBB.</summary>
public sealed partial class ThemeDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool BuiltIn { get; set; }

    public string Background { get; set; } = "#070B14";
    public string BackgroundAlt { get; set; } = "#0B1220";
    public string Surface { get; set; } = "#0F1626";
    public string SurfaceAlt { get; set; } = "#151E33";
    public string SurfaceHigh { get; set; } = "#1B2742";
    public string Border { get; set; } = "#22304D";
    public string Primary { get; set; } = "#00E5FF";
    public string Secondary { get; set; } = "#19F5B5";
    public string Accent { get; set; } = "#A259FF";
    public string Accent2 { get; set; } = "#FF3D9A";
    public string Text { get; set; } = "#E7EEFC";
    public string TextMuted { get; set; } = "#8A9BBD";
    public string Success { get; set; } = "#2EE58A";
    public string Warning { get; set; } = "#FFB020";
    public string Error { get; set; } = "#FF4D5E";
    public List<string> ChartPalette { get; set; } = ["#00E5FF", "#19F5B5", "#A259FF", "#FF3D9A", "#FFB020", "#4D7CFF"];

    public double CornerRadius { get; set; } = 12;
    public double SurfaceOpacity { get; set; } = 1.0;
    public double FontScale { get; set; } = 1.0;
    public double Spacing { get; set; } = 1.0;
    public double GlowIntensity { get; set; } = 0.55;
    public double BorderThickness { get; set; } = 1;
    public AnimationLevel Animations { get; set; } = AnimationLevel.Full;
    public GaugeStyle Gauges { get; set; } = GaugeStyle.Ring;

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColor();

    public static bool IsValidColor(string? value) => value is not null && HexColor().IsMatch(value);

    public ThemeDefinition Clone()
    {
        var t = (ThemeDefinition)MemberwiseClone();
        t.ChartPalette = [.. ChartPalette];
        return t;
    }

    /// <summary>Valida e normaliza o tema. Retorna a lista de problemas; valores numéricos fora da faixa são limitados.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, value) in Colors())
        {
            if (!IsValidColor(value))
            {
                errors.Add($"Cor inválida em '{name}': {value}");
            }
        }

        if (ChartPalette.Count is < 1 or > 12 || ChartPalette.Any(c => !IsValidColor(c)))
        {
            errors.Add("Paleta de gráficos inválida (1 a 12 cores #RRGGBB).");
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
        {
            errors.Add("Nome do tema inválido.");
        }

        CornerRadius = Math.Clamp(CornerRadius, 0, 24);
        SurfaceOpacity = Math.Clamp(SurfaceOpacity, 0.6, 1);
        FontScale = Math.Clamp(FontScale, 0.8, 1.4);
        Spacing = Math.Clamp(Spacing, 0.7, 1.5);
        GlowIntensity = Math.Clamp(GlowIntensity, 0, 1);
        BorderThickness = Math.Clamp(BorderThickness, 0, 3);
        return errors;
    }

    public IEnumerable<(string Name, string Value)> Colors()
    {
        yield return (nameof(Background), Background);
        yield return (nameof(BackgroundAlt), BackgroundAlt);
        yield return (nameof(Surface), Surface);
        yield return (nameof(SurfaceAlt), SurfaceAlt);
        yield return (nameof(SurfaceHigh), SurfaceHigh);
        yield return (nameof(Border), Border);
        yield return (nameof(Primary), Primary);
        yield return (nameof(Secondary), Secondary);
        yield return (nameof(Accent), Accent);
        yield return (nameof(Accent2), Accent2);
        yield return (nameof(Text), Text);
        yield return (nameof(TextMuted), TextMuted);
        yield return (nameof(Success), Success);
        yield return (nameof(Warning), Warning);
        yield return (nameof(Error), Error);
    }
}

public static class BuiltInThemes
{
    public const string DefaultId = "cyber-neon";

    public static IReadOnlyList<ThemeDefinition> All { get; } =
    [
        new() { Id = "cyber-neon", Name = "Cyber Neon", BuiltIn = true },
        new()
        {
            Id = "arctic-blue", Name = "Arctic Blue", BuiltIn = true,
            Background = "#060D18", BackgroundAlt = "#0A1626", Surface = "#0E1C30", SurfaceAlt = "#13253D", SurfaceHigh = "#1A304D", Border = "#24405F",
            Primary = "#5CC8FF", Secondary = "#9BE7FF", Accent = "#3D7BFF", Accent2 = "#C2F0FF", Text = "#EAF6FF", TextMuted = "#86A6C4",
            ChartPalette = ["#5CC8FF", "#9BE7FF", "#3D7BFF", "#C2F0FF", "#FFC94D", "#7A8CFF"],
        },
        new()
        {
            Id = "toxic-green", Name = "Toxic Green", BuiltIn = true,
            Background = "#050B07", BackgroundAlt = "#08130C", Surface = "#0C1A11", SurfaceAlt = "#112418", SurfaceHigh = "#173020", Border = "#1F3F2A",
            Primary = "#7CFF4F", Secondary = "#C6FF3D", Accent = "#22E3A0", Accent2 = "#F5FF6B", Text = "#EAFBEA", TextMuted = "#8DB394",
            ChartPalette = ["#7CFF4F", "#C6FF3D", "#22E3A0", "#F5FF6B", "#FFB020", "#4DD8FF"],
        },
        new()
        {
            Id = "purple-haze", Name = "Purple Haze", BuiltIn = true,
            Background = "#0A0614", BackgroundAlt = "#110A20", Surface = "#170E2B", SurfaceAlt = "#1E1338", SurfaceHigh = "#271A47", Border = "#352560",
            Primary = "#B07CFF", Secondary = "#FF7CE5", Accent = "#6E5BFF", Accent2 = "#FFB4F0", Text = "#F2EAFF", TextMuted = "#A493C4",
            ChartPalette = ["#B07CFF", "#FF7CE5", "#6E5BFF", "#FFB4F0", "#FFC94D", "#5CE1FF"],
        },
        new()
        {
            Id = "crimson-core", Name = "Crimson Core", BuiltIn = true,
            Background = "#0D0507", BackgroundAlt = "#16080B", Surface = "#1E0B10", SurfaceAlt = "#281016", SurfaceHigh = "#33151D", Border = "#4A1E29",
            Primary = "#FF3D5A", Secondary = "#FF8A3D", Accent = "#FF2E9A", Accent2 = "#FFD23D", Text = "#FFECEE", TextMuted = "#C4939B",
            Warning = "#FFD23D", Error = "#FF6B6B",
            ChartPalette = ["#FF3D5A", "#FF8A3D", "#FF2E9A", "#FFD23D", "#9B7CFF", "#3DD5FF"],
        },
        new()
        {
            Id = "amoled-black", Name = "AMOLED Black", BuiltIn = true,
            Background = "#000000", BackgroundAlt = "#050505", Surface = "#0A0A0A", SurfaceAlt = "#111111", SurfaceHigh = "#181818", Border = "#262626",
            Primary = "#00E5FF", Secondary = "#19F5B5", Accent = "#A259FF", Accent2 = "#FF3D9A", Text = "#F5F5F5", TextMuted = "#8C8C8C",
            GlowIntensity = 0.35,
        },
        new()
        {
            Id = "graphite", Name = "Graphite", BuiltIn = true,
            Background = "#111316", BackgroundAlt = "#16191D", Surface = "#1B1F24", SurfaceAlt = "#22272D", SurfaceHigh = "#2A3037", Border = "#363D46",
            Primary = "#E0E6EE", Secondary = "#9FB3C8", Accent = "#F2A93B", Accent2 = "#6FC3DF", Text = "#F0F3F7", TextMuted = "#97A1AD",
            ChartPalette = ["#E0E6EE", "#F2A93B", "#6FC3DF", "#9FB3C8", "#E46F6F", "#8FD18F"],
            GlowIntensity = 0.15,
        },
        new()
        {
            Id = "classic-minimal", Name = "Classic Minimal", BuiltIn = true,
            Background = "#0E1014", BackgroundAlt = "#121419", Surface = "#171A20", SurfaceAlt = "#1C2028", SurfaceHigh = "#232833", Border = "#2B3140",
            Primary = "#4F8CFF", Secondary = "#42D392", Accent = "#8C7BFF", Accent2 = "#FF7B7B", Text = "#ECEFF4", TextMuted = "#8F98A8",
            ChartPalette = ["#4F8CFF", "#42D392", "#8C7BFF", "#FF7B7B", "#F5B94A", "#5BC8E0"],
            GlowIntensity = 0, CornerRadius = 8,
        },
    ];

    public static ThemeDefinition Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
