using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Framex.App.Services;

/// <summary>Encaminha erros de binding do WPF para o log estruturado (habilitado apenas em builds Debug).</summary>
public sealed class BindingErrorListener(ILogger logger) : TraceListener
{
    private readonly HashSet<string> _seen = [];

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        lock (_seen)
        {
            if (_seen.Count > 500 || !_seen.Add(message))
            {
                return;
            }
        }

        logger.LogWarning("Binding WPF: {Message}", message);
    }
}
