using System.Windows;
using System.Windows.Threading;
using Framex.App.Services;
using Framex.App.ViewModels;
using Framex.App.Views;
using Framex.Core.Alerts;
using Framex.Core.Domain;
using Framex.Core.SessionTracking;
using Framex.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Framex.App;

public partial class App : Application
{
    private const string MutexName = @"Local\FRAMEX.SingleInstance";
    private const string ActivateEventName = @"Local\FRAMEX.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private AppHost? _host;
    private MainWindow? _window;
    private Guid? _lastReport;
    private string _hotkeySignature = "";
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var afterRestart = args.Contains("--after-restart", StringComparer.OrdinalIgnoreCase);

        if (!AcquireSingleInstance(afterRestart))
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => _host?.Logger.LogCritical(ex.ExceptionObject as Exception, "Exceção não tratada");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            _host?.Logger.LogError(ex.Exception, "Tarefa com exceção não observada");
            ex.SetObserved();
        };

        try
        {
            _host = new AppHost(new AppPaths());
            _host.InitializeData();
        }
        catch (Exception ex)
        {
            _host?.Logger.LogCritical(ex, "Falha ao inicializar os dados");
            MessageBox.Show("O FRAMEX não conseguiu abrir o banco de dados local:\n\n" + ex.Message + "\n\nVerifique as permissões da pasta %LOCALAPPDATA%\\FRAMEX.",
                "FRAMEX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

#if DEBUG
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener(_host.Logger));
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
#endif
        var settings = _host.Get<SettingsService>();
        var theme = _host.Get<ThemeService>();
        theme.Apply(settings.ActiveTheme, settings.Current);

        _host.Start();

        var main = _host.Get<MainViewModel>();
        foreach (var notice in _host.StartupNotices)
        {
            main.AddNotice(notice);
        }

        _window = new MainWindow(main, settings, theme, _host.Get<DialogService>());
        _window.ExitRequested += (_, _) => _ = ExitAsync();
        MainWindow = _window;

        SetupTray(settings);
        SetupHotkeys(settings);
        SetupEvents(settings);

        _host.Get<LiveDataService>().Start();
        _host.Get<OverlayService>().Initialize();
        main.UpdateStatus();

        settings.Changed += (_, _) => Ui.Post(() => OnSettingsChanged(settings));

        // "--minimized" vem do registro de inicialização com o Windows quando "Iniciar minimizado" está ativo.
        if (!startMinimized || !settings.Current.ShowTrayIcon)
        {
            _window.Show();
        }
    }

    private bool AcquireSingleInstance(bool afterRestart)
    {
        _mutex = new Mutex(true, MutexName, out var created);
        if (!created && afterRestart)
        {
            // Reinício solicitado (ex.: elevação): aguarda a instância anterior terminar.
            try
            {
                created = _mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                created = true;
            }
        }

        if (!created)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // A outra instância ainda está iniciando.
            }

            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Ui.Post(() => _window?.ShowFromTray()), null, Timeout.Infinite, false);
        return true;
    }

    private void SetupTray(SettingsService settings)
    {
        var tray = _host!.Get<TrayService>();
        tray.OpenRequested += (_, _) => _window?.ShowFromTray();
        tray.ToggleOverlayRequested += (_, _) => _host.Get<OverlayService>().Toggle();
        tray.TogglePauseRequested += (_, _) => _host.Get<MainViewModel>().TogglePauseCommand.Execute(null);
        tray.OpenLastReportRequested += (_, _) => OpenLastReport();
        tray.ExitRequested += (_, _) => _ = ExitAsync();
        tray.NotificationClicked += (_, _) => OpenLastReport();
        if (settings.Current.ShowTrayIcon)
        {
            tray.Show();
        }

        _host.Get<LiveDataService>().Tick += (_, _) => UpdateTray();
    }

    private void UpdateTray()
    {
        var recorder = _host!.Get<SessionRecorder>();
        _host.Get<TrayService>().UpdateState(recorder.ActiveGame?.Game.Name, recorder.IsPaused, _host.Get<OverlayService>().IsVisible, _lastReport is not null);
    }

    private void OpenLastReport()
    {
        if (_lastReport is { } id)
        {
            _window?.ShowFromTray();
            _host!.Get<Navigator>().OpenReport(id);
        }
    }

    private void SetupHotkeys(SettingsService settings)
    {
        var h = settings.Current.Hotkeys;
        var signature = $"{h.ToggleOverlay}|{h.ToggleMainWindow}|{h.PauseRecording}|{h.OverlayEditMode}";
        if (signature == _hotkeySignature)
        {
            return;
        }

        _hotkeySignature = signature;
        var hotkeys = _host!.Get<HotkeyService>();
        hotkeys.UnregisterAll();
        hotkeys.Register("Mostrar/ocultar overlay", h.ToggleOverlay, () => _host.Get<OverlayService>().Toggle());
        hotkeys.Register("Mostrar/ocultar FRAMEX", h.ToggleMainWindow, () =>
        {
            if (_window is { IsVisible: true, IsActive: true })
            {
                _window.Hide();
            }
            else
            {
                _window?.ShowFromTray();
            }
        });
        hotkeys.Register("Pausar/retomar gravação", h.PauseRecording, () => _host.Get<MainViewModel>().TogglePauseCommand.Execute(null));
        hotkeys.Register("Editar posição do overlay", h.OverlayEditMode, () => _host.Get<OverlayService>().ToggleEditMode());
        foreach (var failure in hotkeys.Failures)
        {
            _host.Get<MainViewModel>().AddNotice("Atalho não registrado — " + failure + ". Altere em Configurações › Atalhos.");
        }
    }

    private void SetupEvents(SettingsService settings)
    {
        var recorder = _host!.Get<SessionRecorder>();
        var tray = _host.Get<TrayService>();
        var overlay = _host.Get<OverlayService>();

        recorder.SessionCompleted += (_, session) => Ui.Post(() =>
        {
            _lastReport = session.Id;
            var s = settings.Current;
            var fps = session.Summary?.Frames?.AverageFps;
            if (s.NotifySessionEnd)
            {
                tray.Notify("Sessão concluída: " + session.GameName,
                    $"Duração {Fmt.Duration(session.Duration)}" + (fps is null ? " · sem dados de quadros" : $" · FPS médio {fps:0}") + ". Clique para ver o relatório.");
            }

            if (s.AutoOpenReport && _window is { IsVisible: true })
            {
                _host.Get<Navigator>().OpenReport(session.Id);
            }
        });

        _host.Get<AlertService>().AlertRaised += (_, alert) => Ui.Post(() =>
        {
            var channel = settings.Current.AlertChannel;
            if (channel is AlertChannel.Windows or AlertChannel.WindowsAndOverlay)
            {
                tray.Notify(alert.Title, alert.Message, alert.Severity != EventSeverity.Info);
            }

            if (channel is AlertChannel.Overlay or AlertChannel.WindowsAndOverlay)
            {
                overlay.ShowAlert(alert.Title + ": " + alert.Message);
            }
        });
    }

    private void OnSettingsChanged(SettingsService settings)
    {
        if (_host is null || _exiting)
        {
            return;
        }

        _host.ApplyRuntimeSettings();
        SetupHotkeys(settings);
        var tray = _host.Get<TrayService>();
        if (settings.Current.ShowTrayIcon)
        {
            tray.Show();
        }
        else
        {
            tray.Hide();
        }

        _host.Get<OverlayService>().Evaluate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Logger.LogError(e.Exception, "Erro não tratado na interface");
        e.Handled = true;
        try
        {
            _host?.Get<DialogService>().Info("Erro inesperado", "Ocorreu um erro e a operação foi cancelada. Detalhes foram gravados no log.\n\n" + e.Exception.Message);
        }
        catch (Exception ex)
        {
            _host?.Logger.LogError(ex, "Falha ao exibir diálogo de erro");
        }
    }

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Hide();
        }

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _host is not null)
        {
            // Encerramento pelo sistema (logoff/desligamento) ou Application.Shutdown direto.
            _exiting = true;
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        base.OnExit(e);
    }
}
