using Microsoft.Win32;

namespace Framex.Infrastructure.Windows;

/// <summary>
/// Inicialização com o Windows pela chave HKCU\...\Run (por usuário, sem privilégios e visível
/// na aba "Inicializar" do Gerenciador de Tarefas).
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FRAMEX";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string executablePath, bool minimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executablePath}\"" + (minimized ? " --minimized" : ""));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
