using System.Windows;
using System.Windows.Threading;

namespace Framex.App.Services;

/// <summary>Despacho para a thread da interface (eventos do núcleo chegam em threads de fundo).</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}
