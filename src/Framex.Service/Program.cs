using System.Security.AccessControl;
using System.Security.Principal;
using Framex.Infrastructure;
using Framex.Infrastructure.FrameCapture;
using Framex.Infrastructure.Hardware;
using Framex.Infrastructure.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace Framex.Service;

/// <summary>
/// Serviço do Windows instalado pelo Setup.exe (LocalSystem, início automático). Não abre portas de rede, não lê
/// arquivos do usuário e só executa o PresentMon que está na pasta de instalação.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var logs = PrepareLogDirectory();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("App", "FRAMEX.Service")
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logs, "framex-service-.jsonl"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true)
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddWindowsService(o => o.ServiceName = ServiceProtocol.ServiceName);
            builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: false);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHostedService<PipeHostedService>();
            await builder.Build().RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Serviço FRAMEX encerrado por erro");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Logs em %ProgramData%\FRAMEX\service-logs, graváveis só por SYSTEM e Administradores (usuários apenas leem).</summary>
    private static string PrepareLogDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FRAMEX", "service-logs");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        try
        {
            var info = Directory.CreateDirectory(dir);
            info.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
            // Execução manual sem elevação (diagnóstico): usa a pasta temporária do usuário.
            dir = Path.Combine(Path.GetTempPath(), "framex-service-logs");
            Directory.CreateDirectory(dir);
        }

        return dir;
    }
}

internal sealed class PipeHostedService(TimeProvider time, ILoggerFactory loggers) : IHostedService
{
    private ServicePipeServer? _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // O serviço fica em {app}\service; o PresentMon incluído no instalador fica em {app}\tools.
        var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var toolsDir = Path.Combine(installRoot, "tools");
        var paths = new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FRAMEX", "service-data"));
        var capture = new PresentMonCaptureProvider(() => null, paths, [toolsDir], time, loggers.CreateLogger<PresentMonCaptureProvider>());
        _server = new ServicePipeServer(
            () => new LibreHardwareSensorProvider(loggers.CreateLogger<LibreHardwareSensorProvider>(), includeGpu: false),
            capture,
            LibreHardwareSensorProvider.IsPawnIoInstalled,
            time,
            loggers.CreateLogger<ServicePipeServer>());
        _server.Start();
        loggers.CreateLogger("FRAMEX.Service").LogInformation("Serviço FRAMEX iniciado; PresentMon procurado em {Tools}; PawnIO instalado: {Driver}",
            toolsDir, LibreHardwareSensorProvider.IsPawnIoInstalled());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
