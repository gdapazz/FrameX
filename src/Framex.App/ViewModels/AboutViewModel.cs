using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Infrastructure.Service;
using Framex.Infrastructure;
using Framex.Infrastructure.FrameCapture;

namespace Framex.App.ViewModels;

public sealed record LicenseEntry(string Component, string License, string Use);

public sealed partial class AboutViewModel : ObservableObject, IPageActivated
{
    private readonly AppPaths _paths;

    public AboutViewModel(AppPaths paths) => _paths = paths;

    public string Product => "FRAMEX";

    public string Credit => "Feito por: Gdapazz";

    public string Version => "Versão " + SessionEnvironment.Version;

    public string Runtime => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

    public string Elevation => FramexServiceStatus.Query() == ServiceState.Running
        ? FramexServiceStatus.Describe()
        : (PresentMonCaptureProvider.HasCapturePrivileges()
            ? "Em execução com privilégios de captura (administrador ou grupo Performance Log Users). "
            : "Em execução sem privilégios elevados. ") + FramexServiceStatus.Describe();

    public string DataFolder => _paths.Root;

    public string LogsFolder => _paths.LogsDirectory;

    public IReadOnlyList<LicenseEntry> Licenses { get; } =
    [
        new("LibreHardwareMonitorLib", "MPL-2.0", "Leitura de sensores de CPU, GPU e placa-mãe"),
        new("PresentMon 2.6.0 (Intel)", "MIT", "Captura de FPS e frametime via ETW; incluído no Setup.exe e executado pelo serviço"),
        new("PawnIO 2.2.0", "GPL-2.0", "Driver de kernel instalado pelo Setup.exe para temperatura, clock e consumo da CPU"),
        new("Blender Open Data (Blender Benchmark 4.5.0)", "CC0 1.0", "Medianas públicas de CPU e GPU usadas na Nota do Setup"),
        new("Microsoft.Data.Sqlite / SQLite", "MIT / domínio público", "Banco de dados local"),
        new("CommunityToolkit.Mvvm", "MIT", "Infraestrutura MVVM"),
        new("Microsoft.Extensions.*", "MIT", "Injeção de dependências e logs"),
        new("Serilog (+ Sinks.File, Formatting.Compact)", "Apache-2.0", "Logs estruturados em arquivo"),
        new("System.Management", "MIT", "Inventário de hardware via WMI"),
        new(".NET / WPF", "MIT", "Plataforma da aplicação"),
    ];

    [ObservableProperty]
    private string _noticesText = "";

    public void OnActivated()
    {
        if (NoticesText.Length > 0)
        {
            return;
        }

        var path = Path.Combine(AppPaths.ApplicationDirectory, "THIRD-PARTY-NOTICES.md");
        NoticesText = File.Exists(path) ? File.ReadAllText(path) : "Arquivo THIRD-PARTY-NOTICES.md não encontrado na pasta do aplicativo.";
    }

    [RelayCommand]
    private void OpenDataFolder() => ExportService.RevealInExplorer(_paths.Root);

    [RelayCommand]
    private void OpenLogsFolder() => ExportService.RevealInExplorer(_paths.LogsDirectory);
}
