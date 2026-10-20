namespace Framex.Infrastructure;

/// <summary>Locais de dados do FRAMEX. Tudo fica no perfil local do usuário; nada é enviado para fora da máquina.</summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FRAMEX");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ToolsDirectory);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "framex.db");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string ToolsDirectory => Path.Combine(Root, "tools");

    public string DefaultExportDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FRAMEX", "Relatórios");

    public static string ApplicationDirectory => AppContext.BaseDirectory;
}
