using System.Text.Json;
using System.Text.Json.Serialization;

namespace Framex.Core.Customization;

public enum ConfigPackageKind
{
    Theme,
    OverlayLayout,
    DashboardLayout,
}

/// <summary>
/// Arquivo de configuração versionado (.framex.json). Contém apenas dados: nenhum campo é interpretado como código,
/// caminho ou comando, e o conteúdo é validado antes de ser aplicado.
/// </summary>
public sealed class ConfigPackage
{
    public const string FormatId = "framex-config";
    public const int CurrentVersion = 1;
    public const int MaxFileBytes = 256 * 1024;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public ConfigPackageKind Kind { get; set; }
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public ThemeDefinition? Theme { get; set; }
    public OverlayLayout? Overlay { get; set; }
    public DashboardLayout? Dashboard { get; set; }
}

public sealed record ImportResult<T>(T? Value, IReadOnlyList<string> Errors) where T : class
{
    public bool Success => Value is not null && Errors.Count == 0;
}

public static class ConfigPackageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static string Export(ThemeDefinition theme)
    {
        var t = theme.Clone();
        t.BuiltIn = false;
        return JsonSerializer.Serialize(new ConfigPackage { Kind = ConfigPackageKind.Theme, Theme = t }, Options);
    }

    public static string Export(OverlayLayout layout) =>
        JsonSerializer.Serialize(new ConfigPackage { Kind = ConfigPackageKind.OverlayLayout, Overlay = layout.Clone() }, Options);

    public static string Export(DashboardLayout layout) =>
        JsonSerializer.Serialize(new ConfigPackage { Kind = ConfigPackageKind.DashboardLayout, Dashboard = layout }, Options);

    public static ImportResult<ThemeDefinition> ImportTheme(string json)
    {
        var (pkg, errors) = Parse(json, ConfigPackageKind.Theme);
        if (pkg?.Theme is not { } theme)
        {
            return new(null, errors.Count > 0 ? errors : ["O arquivo não contém um tema."]);
        }

        theme.BuiltIn = false;
        theme.Id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        theme.ChartPalette ??= [];
        errors.AddRange(theme.Validate());
        return new(errors.Count == 0 ? theme : null, errors);
    }

    public static ImportResult<OverlayLayout> ImportOverlay(string json)
    {
        var (pkg, errors) = Parse(json, ConfigPackageKind.OverlayLayout);
        if (pkg?.Overlay is not { } layout)
        {
            return new(null, errors.Count > 0 ? errors : ["O arquivo não contém um layout de overlay."]);
        }

        layout.Id = Guid.NewGuid().ToString("N");
        layout.Items ??= [];
        errors.AddRange(layout.Validate());
        return new(errors.Count == 0 ? layout : null, errors);
    }

    public static ImportResult<DashboardLayout> ImportDashboard(string json)
    {
        var (pkg, errors) = Parse(json, ConfigPackageKind.DashboardLayout);
        if (pkg?.Dashboard is not { } layout)
        {
            return new(null, errors.Count > 0 ? errors : ["O arquivo não contém um layout de dashboard."]);
        }

        layout.Widgets ??= [];
        layout.Normalize();
        return new(layout, errors);
    }

    private static (ConfigPackage? Package, List<string> Errors) Parse(string json, ConfigPackageKind expected)
    {
        var errors = new List<string>();
        if (json is not null && json.Length > ConfigPackage.MaxFileBytes)
        {
            errors.Add("Arquivo grande demais para um pacote de configuração.");
            return (null, errors);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            errors.Add("Arquivo vazio.");
            return (null, errors);
        }

        ConfigPackage? pkg;
        try
        {
            pkg = JsonSerializer.Deserialize<ConfigPackage>(json, Options);
        }
        catch (JsonException ex)
        {
            errors.Add("JSON inválido: " + ex.Message);
            return (null, errors);
        }

        if (pkg is null || pkg.Format != ConfigPackage.FormatId)
        {
            errors.Add("Este arquivo não é um pacote de configuração do FRAMEX.");
            return (null, errors);
        }

        if (pkg.Version > ConfigPackage.CurrentVersion || pkg.Version < 1)
        {
            errors.Add($"Versão de pacote não suportada ({pkg.Version}).");
            return (null, errors);
        }

        if (pkg.Kind != expected)
        {
            errors.Add($"Tipo de pacote inesperado: {pkg.Kind}.");
            return (null, errors);
        }

        return (pkg, errors);
    }
}
