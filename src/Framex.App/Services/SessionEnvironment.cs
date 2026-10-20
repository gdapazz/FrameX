using System.Reflection;
using Framex.Core.SessionTracking;
using Framex.Infrastructure.Hardware;

namespace Framex.App.Services;

public sealed class SessionEnvironment : ISessionEnvironment
{
    public static string Version { get; } =
        typeof(SessionEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SessionEnvironment).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public string AppVersion => Version;

    public string? PrimaryResolution()
    {
        var primary = WindowsSystemProvider.EnumerateDisplays().FirstOrDefault(d => d.Primary);
        return primary.Width > 0 ? $"{primary.Width}×{primary.Height} @ {primary.Hz} Hz" : null;
    }
}
