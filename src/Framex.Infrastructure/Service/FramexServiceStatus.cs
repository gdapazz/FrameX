using System.ComponentModel;
using System.ServiceProcess;
using Framex.Infrastructure.FrameCapture;

namespace Framex.Infrastructure.Service;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
}

/// <summary>Estado do serviço FRAMEX instalado pelo Setup.exe (consulta ao Gerenciador de Serviços, sem elevação).</summary>
public static class FramexServiceStatus
{
    public static ServiceState Query()
    {
        try
        {
            using var sc = new ServiceController(ServiceProtocol.ServiceName);
            return sc.Status == ServiceControllerStatus.Running ? ServiceState.Running : ServiceState.Stopped;
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
        catch (Win32Exception)
        {
            return ServiceState.NotInstalled;
        }
    }

    /// <summary>Captura de quadros e sensores de CPU disponíveis: serviço em execução ou o próprio app com privilégios.</summary>
    public static bool CaptureAvailable() => Query() == ServiceState.Running || PresentMonCaptureProvider.HasCapturePrivileges();

    public static string Describe() => Query() switch
    {
        ServiceState.Running => "Serviço FRAMEX em execução: sensores de CPU e captura de quadros sem precisar de administrador.",
        ServiceState.Stopped => "O serviço FRAMEX está instalado, mas parado. Inicie-o em services.msc (\"FRAMEX\") ou reinstale pelo Setup.exe.",
        _ => "O serviço FRAMEX não está instalado (execução sem o Setup.exe).",
    };
}
