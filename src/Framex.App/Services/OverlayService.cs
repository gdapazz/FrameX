using Framex.App.ViewModels;
using Framex.App.Views;
using Framex.Core.Customization;
using Framex.Core.GameDetection;
using Microsoft.Extensions.Logging;

namespace Framex.App.Services;

/// <summary>Controla quando o HUD aparece: ativado nas configurações, alternado pelo usuário e, opcionalmente, apenas durante jogos.</summary>
public sealed class OverlayService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly GameDetectionService _detection;
    private readonly OverlayViewModel _viewModel;
    private readonly ILogger<OverlayService> _logger;
    private OverlayWindow? _window;
    private bool _userHidden;
    private bool _gameActive;
    private bool _gameAllowsOverlay = true;
    private string? _gameLayoutId;
    private bool _disposed;

    public OverlayService(SettingsService settings, ThemeService theme, GameDetectionService detection, OverlayViewModel viewModel, ILogger<OverlayService> logger)
    {
        _settings = settings;
        _theme = theme;
        _detection = detection;
        _viewModel = viewModel;
        _logger = logger;
    }

    public bool IsVisible => _window?.IsVisible == true;

    public bool EditMode { get; private set; }

    /// <summary>Exibição forçada para pré-visualização na tela de personalização.</summary>
    public bool Preview { get; private set; }

    public event EventHandler? StateChanged;

    public void Initialize()
    {
        _detection.GameStarted += (_, e) => Ui.Post(() =>
        {
            _gameActive = true;
            _gameAllowsOverlay = e.Active.Game.Profile.ShowOverlay;
            _gameLayoutId = e.Active.Game.Profile.OverlayLayoutId;
            _viewModel.GameName = e.Active.Game.Name;
            ApplyLayout();
        });
        _detection.GameEnded += (_, _) => Ui.Post(() =>
        {
            _gameActive = false;
            _gameAllowsOverlay = true;
            _gameLayoutId = null;
            _viewModel.GameName = null;
            ApplyLayout();
        });
        _theme.ThemeApplied += (_, _) => ApplyLayout();
        _gameActive = _detection.Active is not null;
        ApplyLayout();
    }

    /// <summary>Layout em uso: o definido no perfil do jogo ativo, se existir; senão o layout ativo global.</summary>
    public OverlayLayout CurrentLayout =>
        (_gameLayoutId is null || Preview || EditMode ? null : _settings.OverlayLayouts.FirstOrDefault(l => l.Id == _gameLayoutId)) ?? _settings.ActiveOverlay;

    public void ApplyLayout()
    {
        var layout = CurrentLayout;
        _viewModel.ApplyLayout(layout, _theme.Active);
        if (_window is not null)
        {
            _window.RelativeX = layout.PositionX;
            _window.RelativeY = layout.PositionY;
            _window.MonitorDevice = layout.MonitorDevice;
            _window.Reposition();
        }

        Evaluate();
    }

    /// <summary>Atalho/bandeja: mostra ou oculta o HUD.</summary>
    public void Toggle()
    {
        if (!_settings.Current.OverlayEnabled)
        {
            _settings.Current.OverlayEnabled = true;
            _userHidden = false;
            _settings.Save();
        }
        else
        {
            _userHidden = IsVisible;
        }

        Evaluate();
    }

    public void SetPreview(bool preview)
    {
        Preview = preview;
        ApplyLayout();
    }

    public void ToggleEditMode() => SetEditMode(!EditMode);

    public void SetEditMode(bool edit)
    {
        EditMode = edit;
        _viewModel.EditMode = edit;
        ApplyLayout();
        _window?.SetEditMode(edit);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowAlert(string text) => Ui.Post(() => _viewModel.ShowAlert(text));

    public void Evaluate()
    {
        if (_disposed)
        {
            return;
        }

        var s = _settings.Current;
        var shouldShow = EditMode || Preview
                         || (s.OverlayEnabled && !_userHidden && (_gameActive ? _gameAllowsOverlay : !s.OverlayOnlyDuringGames));
        try
        {
            if (shouldShow)
            {
                EnsureWindow();
                if (!_window!.IsVisible)
                {
                    _window.Show();
                    _window.SetEditMode(EditMode);
                }

                _viewModel.Start();
            }
            else if (_window is { IsVisible: true })
            {
                _window.Hide();
                _viewModel.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao atualizar o overlay");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        var layout = CurrentLayout;
        _window = new OverlayWindow(_viewModel)
        {
            RelativeX = layout.PositionX,
            RelativeY = layout.PositionY,
            MonitorDevice = layout.MonitorDevice,
        };
        _window.PositionCommitted += (_, p) =>
        {
            var active = CurrentLayout;
            active.PositionX = p.X;
            active.PositionY = p.Y;
            active.MonitorDevice = p.Monitor;
            _settings.SaveOverlayLayouts();
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.Stop();
        _window?.Close();
        _window = null;
    }
}
