using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Framex.App;
using Framex.App.Services;
using Framex.App.ViewModels;
using Framex.App.Views;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Persistence;
using Framex.Core.Reporting;
using Framex.Infrastructure;

namespace Framex.UiSmoke;

/// <summary>
/// Renderiza todas as telas com os ViewModels e provedores reais (dados do computador atual) em um diretório de dados
/// temporário. As únicas sessões presentes são SINTÉTICAS, geradas aqui e rotuladas como tal, para exercitar relatórios
/// e comparação. Resultado: PNGs + lista de erros de binding; código de saída 1 se houver erros.
/// </summary>
public static class Program
{
    private const int Width = 1360;

    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "ui-smoke"));
        Directory.CreateDirectory(outDir);
        var dataDir = Path.Combine(Path.GetTempPath(), "framex-ui-smoke-" + Guid.NewGuid().ToString("N")[..8]);

        var app = new Framex.App.App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));

        var bindingErrors = new BindingCollector();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        var host = new AppHost(new AppPaths(dataDir)) { ManageStartupRegistration = false };
        var exitCode = 0;
        try
        {
            host.InitializeData();
            var (older, newer) = SeedSyntheticSessions(host.Get<ISessionRepository>());

            var settings = host.Get<SettingsService>();
            var theme = host.Get<ThemeService>();
            theme.Apply(settings.ActiveTheme, settings.Current);
            host.Start();
            host.Get<LiveDataService>().Start();
            host.Get<OverlayService>().Initialize();
            Pump(TimeSpan.FromSeconds(4));

            var main = host.Get<MainViewModel>();
            var shots = new List<string>();

            void Shot(string name, double height = 1500)
            {
                Pump(TimeSpan.FromMilliseconds(900));
                var view = ViewFor(main.CurrentPage);
                view.DataContext = main.CurrentPage;
                shots.Add(Render(view, Path.Combine(outDir, name + ".png"), height));
            }

            foreach (var page in new[] { PageKey.Dashboard, PageKey.Games, PageKey.Hardware, PageKey.Reports, PageKey.About })
            {
                main.GoCommand.Execute(page);
                Shot(page.ToString().ToLowerInvariant(), page == PageKey.Hardware ? 2600 : 1500);
            }

            // Nota do Setup: inventário real + bases incluídas (nenhuma consulta à rede é feita aqui).
            main.GoCommand.Execute(PageKey.SetupScore);
            var setup = host.Get<SetupScoreViewModel>();
            WaitUntil(() => !setup.IsIdentifying, TimeSpan.FromSeconds(60));
            Shot("setup-preparation", 1500);
            // Sem bombear o dispatcher a avaliação fica parada em "Avaliando seu setup", o que permite capturar o estado.
            setup.EvaluateCommand.Execute(null);
            shots.Add(RenderNow(new SetupScoreView { DataContext = setup }, Path.Combine(outDir, "setup-running.png"), 1100));
            WaitUntil(() => !setup.IsRunning, TimeSpan.FromSeconds(150));
            if (!setup.IsResult)
            {
                throw new InvalidOperationException("A avaliação do setup não chegou ao resultado: " + setup.Message);
            }

            if (!setup.ShowIssues)
            {
                setup.ToggleIssuesCommand.Execute(null);
            }

            if (!setup.ShowMath)
            {
                setup.ToggleMathCommand.Execute(null);
            }

            Shot("setup-result", 4200);
            Console.WriteLine($"Nota do Setup (hardware real): {setup.OverallText} · {setup.TierText} · {setup.CompletenessText}");

            main.GoCommand.Execute(PageKey.Customization);
            var custom = host.Get<CustomizationViewModel>();
            foreach (var tab in Enum.GetValues<CustomizationTab>())
            {
                custom.Tab = tab;
                Shot("customization-" + tab.ToString().ToLowerInvariant(), 1900);
            }

            // Tema personalizado: duplica o ativo para exibir o editor.
            custom.Tab = CustomizationTab.Themes;
            custom.DuplicateThemeCommand.Execute(custom.Themes[0]);
            Shot("customization-theme-editor", 2600);

            main.GoCommand.Execute(PageKey.Settings);
            var settingsVm = host.Get<SettingsViewModel>();
            foreach (var section in Enum.GetValues<SettingsSection>())
            {
                settingsVm.Section = section;
                Shot("settings-" + section.ToString().ToLowerInvariant(), 1700);
            }

            var navigator = host.Get<Navigator>();
            navigator.OpenReport(newer);
            Pump(TimeSpan.FromSeconds(2));
            Shot("report-detail", 3600);

            navigator.Compare(older, newer);
            Pump(TimeSpan.FromSeconds(1));
            Shot("compare", 1400);

            shots.Add(RenderOverlay(host, Path.Combine(outDir, "overlay.png")));
            shots.Add(RenderShell(host, main, Path.Combine(outDir, "shell.png")));

            File.WriteAllLines(Path.Combine(outDir, "binding-errors.txt"), bindingErrors.Messages);
            Console.WriteLine($"Telas renderizadas: {shots.Count}");
            foreach (var s in shots)
            {
                Console.WriteLine("  " + s);
            }

            Console.WriteLine($"Erros de binding: {bindingErrors.Messages.Count}");
            foreach (var m in bindingErrors.Messages.Take(40))
            {
                Console.WriteLine("  " + m);
            }

            exitCode = bindingErrors.Messages.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Falha no smoke test: " + ex);
            exitCode = 2;
        }
        finally
        {
            var dispose = host.DisposeAsync().AsTask();
            while (!dispose.IsCompleted)
            {
                Pump(TimeSpan.FromMilliseconds(100));
            }

            app.Shutdown();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDelete(dataDir);
        }

        return exitCode;
    }

    private static FrameworkElement ViewFor(object page) => page switch
    {
        DashboardViewModel => new DashboardView(),
        GamesViewModel => new GamesView(),
        HardwareViewModel => new HardwareView(),
        SetupScoreViewModel => new SetupScoreView(),
        ReportsViewModel => new ReportsView(),
        ReportDetailViewModel => new ReportDetailView(),
        CompareViewModel => new CompareView(),
        CustomizationViewModel => new CustomizationView(),
        SettingsViewModel => new SettingsView(),
        AboutViewModel => new AboutView(),
        _ => throw new InvalidOperationException("Página sem view: " + page.GetType().Name),
    };

    private static string Render(FrameworkElement view, string path, double height)
    {
        var root = new Grid { Width = Width, Height = height, Background = ThemeService.GetBrush("B.Background") };
        TextElement.SetForeground(root, ThemeService.GetBrush("B.Text"));
        root.Children.Add(view);
        return RenderElement(root, path);
    }

    private static string RenderNow(FrameworkElement view, string path, double height)
    {
        var root = new Grid { Width = Width, Height = height, Background = ThemeService.GetBrush("B.Background") };
        TextElement.SetForeground(root, ThemeService.GetBrush("B.Text"));
        root.Children.Add(view);
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static string RenderElement(FrameworkElement root, string path)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        Pump(TimeSpan.FromMilliseconds(600));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>Renderiza o conteúdo do overlay sem exibir a janela (nada aparece na área de trabalho).</summary>
    private static string RenderOverlay(AppHost host, string path)
    {
        var vm = host.Get<OverlayViewModel>();
        vm.ApplyLayout(host.Get<SettingsService>().ActiveOverlay, host.Get<ThemeService>().Active);
        vm.GameName = "Sessão sintética";
        vm.Start();
        Pump(TimeSpan.FromSeconds(1.5));
        var window = new OverlayWindow(vm);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = window.DataContext;
        var root = new Grid { Width = 520, Height = 360, Background = new SolidColorBrush(Color.FromRgb(40, 44, 52)) };
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        content.Margin = new Thickness(20);
        root.Children.Add(content);
        var result = RenderElement(root, path);
        vm.Stop();
        window.Close();
        return result;
    }

    /// <summary>Renderiza o conteúdo da janela principal (barra lateral, barra superior e página) sem exibi-la.</summary>
    private static string RenderShell(AppHost host, MainViewModel main, string path)
    {
        main.GoCommand.Execute(PageKey.Dashboard);
        var window = new MainWindow(main, host.Get<SettingsService>(), host.Get<ThemeService>(), host.Get<DialogService>()) { AllowClose = true };
        var content = (FrameworkElement)window.Content;
        var resources = window.Resources;
        window.Content = null;
        var root = new Grid { Width = 1440, Height = 900, Background = ThemeService.GetBrush("B.Background"), DataContext = window.DataContext };
        TextElement.SetForeground(root, ThemeService.GetBrush("B.Text"));
        root.Resources.MergedDictionaries.Add(resources);
        root.Children.Add(content);
        var result = RenderElement(root, path);
        window.Close();
        return result;
    }

    /// <summary>Duas sessões sintéticas (rotuladas) com lacuna, eventos e condições diferentes para exercitar relatório e comparação.</summary>
    private static (Guid Older, Guid Newer) SeedSyntheticSessions(ISessionRepository repo)
    {
        Guid Seed(DateTimeOffset start, int seconds, double baseFps, string resolution, int seed)
        {
            var rnd = new Random(seed);
            var session = new Session
            {
                GameName = "Sessão sintética (teste de UI)",
                ExecutableName = "synthetic-ui-test.exe",
                GameConfirmed = true,
                StartedAt = start,
                State = SessionState.Active,
                AppVersion = "ui-smoke",
                HardwareFingerprint = "SINTÉTICO",
                Resolution = resolution,
                MonitoringProfile = "Equilibrado",
                InitialCaptureState = CaptureState.Capturing,
            };
            repo.Create(session);

            var samples = new List<MetricSample>();
            var frames = new List<FrameSample>();
            var frameTimes = new List<double>();
            for (var t = 0; t < seconds; t++)
            {
                if (t is >= 300 and < 330)
                {
                    continue;
                }

                var ts = start.AddSeconds(t);
                var dip = t is >= 420 and < 440 ? 0.55 : 1.0;
                var fps = baseFps * dip * (0.92 + (0.16 * rnd.NextDouble()));
                var fpsWindow = new List<double>();
                for (var f = 0; f < (int)fps; f++)
                {
                    var ft = 1000 / fps * (0.85 + (0.3 * rnd.NextDouble())) + (rnd.NextDouble() < 0.002 ? 40 : 0);
                    fpsWindow.Add(ft);
                }

                frameTimes.AddRange(fpsWindow);
                frames.AddRange(fpsWindow.Select((ft, i) => new FrameSample(ts.AddMilliseconds(i * 1000.0 / fpsWindow.Count), ft)));
                samples.Add(new MetricSample(ts, MetricKind.Fps, fpsWindow.Count / (fpsWindow.Sum() / 1000)));
                samples.Add(new MetricSample(ts, MetricKind.FrameTimeAvg, fpsWindow.Average()));
                samples.Add(new MetricSample(ts, MetricKind.FrameTimeMax, fpsWindow.Max()));
                samples.Add(new MetricSample(ts, MetricKind.CpuLoad, 45 + (20 * Math.Sin(t / 40.0)) + (5 * rnd.NextDouble())));
                samples.Add(new MetricSample(ts, MetricKind.GpuLoad, (90 * dip) + (8 * rnd.NextDouble())));
                samples.Add(new MetricSample(ts, MetricKind.GpuTemperature, 55 + (t / (double)seconds * 18) + rnd.NextDouble()));
                samples.Add(new MetricSample(ts, MetricKind.GpuHotspot, 68 + (t / (double)seconds * 20) + rnd.NextDouble()));
                samples.Add(new MetricSample(ts, MetricKind.GpuCoreClock, 1830 + (40 * rnd.NextDouble())));
                samples.Add(new MetricSample(ts, MetricKind.GpuPower, 110 + (15 * rnd.NextDouble())));
                samples.Add(new MetricSample(ts, MetricKind.RamUsed, 9200 + (t * 2.0)));
                samples.Add(new MetricSample(ts, MetricKind.VramUsed, 4100 + (t * 1.5)));
            }

            var events = new List<SessionEvent>
            {
                new(start, SessionEventType.SessionStarted, EventSeverity.Info, "Sessão sintética iniciada"),
                new(start.AddSeconds(300), SessionEventType.DataGap, EventSeverity.Warning, "Lacuna sintética de 30 s"),
                new(start.AddSeconds(421), SessionEventType.PerformanceDrop, EventSeverity.Warning, "Queda sintética de FPS"),
                new(start.AddSeconds(seconds), SessionEventType.SessionEnded, EventSeverity.Info, "Sessão sintética encerrada"),
            };
            repo.Append(session.Id, samples, frames, events);
            var end = start.AddSeconds(seconds);
            var analysis = SessionStatisticsCalculator.Analyze(start, end, samples, frameTimes, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
            repo.Complete(session.Id, end, SessionState.Completed, analysis.Summary);
            return session.Id;
        }

        var now = DateTimeOffset.UtcNow;
        var older = Seed(now.AddHours(-26), 600, 118, "1920×1080 @ 144 Hz", 7);
        var newer = Seed(now.AddHours(-2), 600, 104, "2560×1440 @ 144 Hz", 42);
        return (older, newer);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            Pump(TimeSpan.FromMilliseconds(100));
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Aviso: diretório temporário não removido ({ex.Message}): {dir}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Aviso: diretório temporário não removido ({ex.Message}): {dir}");
        }
    }

    private sealed class BindingCollector : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message) && !Messages.Contains(message))
            {
                Messages.Add(message);
            }
        }
    }
}
