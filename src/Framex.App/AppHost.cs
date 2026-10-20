using System.IO;
using Framex.App.Services;
using Framex.App.ViewModels;
using Framex.Core.Alerts;
using Framex.Core.Customization;
using Framex.Core.FrameCapture;
using Framex.Core.GameDetection;
using Framex.Core.Monitoring;
using Framex.Core.Persistence;
using Framex.Core.SessionTracking;
using Framex.Core.SetupScore;
using Framex.Infrastructure;
using Framex.Infrastructure.FrameCapture;
using Framex.Infrastructure.Hardware;
using Framex.Infrastructure.Persistence;
using Framex.Infrastructure.Processes;
using Framex.Infrastructure.Service;
using Framex.Infrastructure.SetupScore;
using Framex.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace Framex.App;

/// <summary>Composição de dependências, inicialização ordenada e encerramento limpo de todos os serviços.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private bool _started;

    public AppHost(AppPaths paths)
    {
        Paths = paths;
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("App", "FRAMEX")
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.LogsDirectory, "framex-.jsonl"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: false)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Information).AddSerilog(serilog, dispose: true));
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);

        // Persistência
        services.AddSingleton(sp => new SqliteDatabase(paths.DatabasePath, sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        services.AddSingleton<IGameRepository, SqliteGameRepository>();
        services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IAlertHistoryRepository, SqliteAlertHistoryRepository>();

        // Núcleo de monitoramento
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IAlertRuleSource>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton(new MonitoringOptions());
        // Os provedores pertencem ao serviço de monitoramento (ele os libera); não são registrados no contêiner.
        // O serviço FRAMEX (Setup.exe) fornece os sensores de CPU que exigem driver; sem ele, o provedor não contribui.
        services.AddSingleton(sp => new HardwareMonitoringService(
            [new ServiceSensorProvider(sp.GetRequiredService<ILogger<ServiceSensorProvider>>()),
             new WindowsSystemProvider(sp.GetRequiredService<ILogger<WindowsSystemProvider>>()), new LibreHardwareSensorProvider(sp.GetRequiredService<ILogger<LibreHardwareSensorProvider>>())],
            sp.GetRequiredService<MonitoringOptions>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<HardwareMonitoringService>>()));
        services.AddSingleton<IHardwareMonitor>(sp => sp.GetRequiredService<HardwareMonitoringService>());
        services.AddSingleton<IProcessSnapshotProvider, Win32ProcessSnapshotProvider>();
        services.AddSingleton(new GameDetectionOptions());
        services.AddSingleton<GameDetectionService>();
        services.AddSingleton(sp => new LiveFrameAggregator(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new PresentMonCaptureProvider(() => settings.Current.PresentMonPath, paths, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PresentMonCaptureProvider>>());
        });
        services.AddSingleton(sp => new ServiceFrameCaptureProvider(sp.GetRequiredService<ILogger<ServiceFrameCaptureProvider>>()));
        services.AddSingleton<HybridFrameCaptureProvider>();
        services.AddSingleton<IFrameCaptureProvider>(sp => sp.GetRequiredService<HybridFrameCaptureProvider>());
        services.AddSingleton<ISessionEnvironment, SessionEnvironment>();
        services.AddSingleton(new SessionRecorderOptions());
        services.AddSingleton<SessionRecorder>();
        services.AddSingleton<AlertService>();

        // Nota do Setup (inventário local, bases CC0 em cache, histórico)
        services.AddSingleton<ISetupInventoryProvider, WindowsSetupInventoryProvider>();
        services.AddSingleton<BlenderOpenDataStore>();
        services.AddSingleton<IBenchmarkDatasetSource>(sp => sp.GetRequiredService<BlenderOpenDataStore>());
        services.AddSingleton<ISetupEvaluationRepository, SqliteSetupEvaluationRepository>();
        services.AddSingleton(new SetupEvaluationOptions());
        services.AddSingleton(sp => new SetupEvaluationService(
            sp.GetRequiredService<ISetupInventoryProvider>(), sp.GetRequiredService<IBenchmarkDatasetSource>(), sp.GetRequiredService<ISetupEvaluationRepository>(),
            () => SessionEnvironment.Version, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<SetupEvaluationOptions>(),
            sp.GetRequiredService<ILogger<SetupEvaluationService>>()));

        // Interface
        services.AddSingleton<ThemeService>();
        services.AddSingleton<LiveDataService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<Navigator>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<OverlayService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<HardwareViewModel>();
        services.AddSingleton<SetupScoreViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddTransient<ReportDetailViewModel>();
        services.AddTransient<CompareViewModel>();
        services.AddSingleton<CustomizationViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("FRAMEX");
    }

    public AppPaths Paths { get; }

    public IServiceProvider Services => _services;

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public Microsoft.Extensions.Logging.ILogger Logger => _logger;

    /// <summary>Se falso, a entrada de inicialização com o Windows (HKCU\…\Run) nunca é alterada (ferramentas de teste).</summary>
    public bool ManageStartupRegistration { get; init; } = true;

    /// <summary>Avisos de inicialização para exibir ao usuário (recuperação de banco, sessões recuperadas etc.).</summary>
    public List<string> StartupNotices { get; } = [];

    public void InitializeData()
    {
        var db = Get<SqliteDatabase>();
        db.Initialize();
        if (db.RecoveryNotice is not null)
        {
            StartupNotices.Add(db.RecoveryNotice);
        }

        Get<SettingsService>().Load();
    }

    public void Start()
    {
        var settings = Get<SettingsService>();
        ApplyRuntimeSettings();

        var monitor = Get<HardwareMonitoringService>();
        var detection = Get<GameDetectionService>();
        var recorder = Get<SessionRecorder>();
        var alerts = Get<AlertService>();

        var recovered = recorder.RecoverInterruptedSessions();
        if (recovered > 0)
        {
            StartupNotices.Add(recovered == 1
                ? "1 sessão interrompida (falha ou desligamento) foi recuperada e marcada como incompleta."
                : $"{recovered} sessões interrompidas foram recuperadas e marcadas como incompletas.");
        }

        monitor.Start();
        recorder.Attach();
        alerts.Attach();
        detection.Start();
        _started = true;

        _ = Task.Run(() => PurgeOldDetail(settings.Current.DetailedRetentionDays));
        _logger.LogInformation("FRAMEX {Version} iniciado; perfil {Profile}", SessionEnvironment.Version, settings.Current.Profile);
    }

    private void PurgeOldDetail(int days)
    {
        if (days <= 0)
        {
            return;
        }

        try
        {
            var purged = Get<ISessionRepository>().PurgeDetailedData(DateTimeOffset.UtcNow.AddDays(-days));
            if (purged > 0)
            {
                _logger.LogInformation("Dados detalhados de {Count} sessões antigas removidos (retenção de {Days} dias)", purged, days);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao aplicar a retenção de dados");
        }
    }

    /// <summary>Propaga as preferências para as opções dos serviços (lidas por eles a cada ciclo).</summary>
    public void ApplyRuntimeSettings()
    {
        var s = Get<SettingsService>().Current;
        var tuning = PerformanceTuning.For(s.Profile);

        var monitoring = Get<MonitoringOptions>();
        monitoring.PollInterval = tuning.SensorInterval;
        monitoring.PreferredGpuId = s.PreferredGpuId;

        var detection = Get<GameDetectionOptions>();
        if (!_started)
        {
            detection.PollInterval = tuning.ProcessPollInterval;
        }

        detection.ExitGracePeriod = TimeSpan.FromSeconds(s.ExitGraceSeconds);
        detection.AutoDetectStoreGames = s.AutoDetectStoreGames;
        detection.RecordUnconfirmedGames = s.RecordUnconfirmedGames;

        var recorder = Get<SessionRecorderOptions>();
        recorder.CaptureFrames = s.CaptureFrames;
        recorder.MinSessionDuration = TimeSpan.FromSeconds(s.MinSessionSeconds);
        recorder.MonitoringProfileName = PerformanceTuning.DisplayName(s.Profile);
        recorder.Capture = new FrameCaptureOptions { TrackGpu = tuning.TrackGpuInCapture, TrackDisplay = true };

        Get<AlertService>().GloballyEnabled = s.AlertsEnabled;
        Get<LiveDataService>().Interval = tuning.UiInterval;
        if (!ManageStartupRegistration)
        {
            return;
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && StartupRegistration.IsEnabled() != s.StartWithWindows)
            {
                StartupRegistration.Set(s.StartWithWindows, exe, s.StartMinimized);
            }
            else if (exe is not null && s.StartWithWindows)
            {
                StartupRegistration.Set(true, exe, s.StartMinimized);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível atualizar a inicialização com o Windows");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Encerrando FRAMEX");
        try
        {
            // Objetos de janela (bandeja, overlay, atalhos) precisam ser liberados na thread da interface, antes de qualquer await.
            Get<LiveDataService>().Stop();
            Get<SetupScoreViewModel>().CancelAll();
            Get<TrayService>().Dispose();
            Get<OverlayService>().Dispose();
            Get<HotkeyService>().Dispose();
            Get<AlertService>().Dispose();

            // Ordem importa: gravador finaliza a sessão (precisa de captura e banco), depois detecção, captura e sensores.
            var timeout = Task.Delay(TimeSpan.FromSeconds(8));
            var shutdown = ShutdownCoreAsync();
            if (await Task.WhenAny(shutdown, timeout).ConfigureAwait(false) == timeout)
            {
                _logger.LogWarning("Encerramento excedeu o tempo limite; finalizando mesmo assim");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha durante o encerramento");
        }
        finally
        {
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ShutdownCoreAsync()
    {
        await Get<SessionRecorder>().DisposeAsync().ConfigureAwait(false);
        await Get<GameDetectionService>().StopAsync().ConfigureAwait(false);
        await Get<HybridFrameCaptureProvider>().DisposeAsync().ConfigureAwait(false);
        await Get<HardwareMonitoringService>().StopAsync().ConfigureAwait(false);
    }
}
