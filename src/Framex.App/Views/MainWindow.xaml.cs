using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using Framex.App.Services;
using Framex.App.ViewModels;
using Framex.Core.Customization;

namespace Framex.App.Views;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;

    public MainWindow(MainViewModel viewModel, SettingsService settings, ThemeService theme, DialogService dialogs)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;

        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        theme.ThemeApplied += (_, _) => ApplyScale();
        viewModel.PropertyChanged += OnViewModelChanged;
        ApplyScale();
    }

    /// <summary>Quando verdadeiro, fechar a janela encerra o aplicativo (sem perguntar).</summary>
    public bool AllowClose { get; set; }

    /// <summary>Disparado quando o usuário decide encerrar o FRAMEX pela janela.</summary>
    public event EventHandler? ExitRequested;

    private void ApplyScale()
    {
        var scale = Math.Clamp(_settings.Current.UiScale, 0.8, 1.5);
        UiScale.ScaleX = scale;
        UiScale.ScaleY = scale;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage) || _theme.Animations == AnimationLevel.Off)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(_theme.Animations == AnimationLevel.Reduced ? 120 : 220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        if (_theme.Animations == AnimationLevel.Full)
        {
            PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        var behavior = _settings.Current.CloseBehavior;
        if (behavior == CloseBehavior.Ask)
        {
            var choice = _dialogs.Show("Fechar o FRAMEX",
                "Minimizar para a bandeja mantém o monitoramento e a gravação de sessões ativos. Encerrar finaliza a sessão em andamento e para todo o monitoramento.",
                [new DialogButton("Cancelar"), new DialogButton("Encerrar", Danger: true), new DialogButton("Minimizar para a bandeja", Primary: true)],
                "Lembrar minha escolha", out var remember);
            if (choice <= 0)
            {
                return;
            }

            behavior = choice == 1 ? CloseBehavior.Exit : CloseBehavior.MinimizeToTray;
            if (remember)
            {
                _settings.Current.CloseBehavior = behavior;
                _settings.Save();
            }
        }

        if (behavior == CloseBehavior.Exit || !_settings.Current.ShowTrayIcon)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Hide();
        }
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
