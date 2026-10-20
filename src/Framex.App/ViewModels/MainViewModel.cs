using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.SessionTracking;
using Framex.Infrastructure.Service;
using Framex.Infrastructure.FrameCapture;
using Microsoft.Extensions.DependencyInjection;

namespace Framex.App.ViewModels;

public sealed record Notice(string Text, bool Warning);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly SessionRecorder _recorder;
    private readonly IFrameCaptureProvider _capture;
    private readonly OverlayService _overlay;
    private readonly LiveDataService _live;
    private readonly Navigator _navigator;

    public MainViewModel(IServiceProvider services, SessionRecorder recorder, IFrameCaptureProvider capture, OverlayService overlay, LiveDataService live, Navigator navigator)
    {
        _services = services;
        _recorder = recorder;
        _capture = capture;
        _overlay = overlay;
        _live = live;
        _navigator = navigator;
        IsElevated = FramexServiceStatus.CaptureAvailable();

        navigator.Requested += (_, r) => Ui.Post(() => Navigate(r));
        overlay.StateChanged += (_, _) => Ui.Post(UpdateStatus);
        live.Tick += (_, _) => UpdateStatus();
        _currentPage = services.GetRequiredService<DashboardViewModel>();
    }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private PageKey _selectedPage = PageKey.Dashboard;

    [ObservableProperty]
    private string _gameStatus = "Nenhum jogo em execução";

    [ObservableProperty]
    private bool _gameActive;

    [ObservableProperty]
    private string _captureStatus = "";

    [ObservableProperty]
    private string _captureDetail = "";

    [ObservableProperty]
    private string _captureTone = "Muted";

    [ObservableProperty]
    private bool _recordingPaused;

    [ObservableProperty]
    private bool _overlayVisible;

    [ObservableProperty]
    private string _fpsBadge = Fmt.Missing;

    public bool IsElevated { get; }

    public string PrivilegeText => IsElevated ? "Captura de FPS e sensores de CPU disponíveis" : "Serviço do FRAMEX indisponível (instale pelo Setup.exe)";

    public string Version => "v" + SessionEnvironment.Version;

    public ObservableCollection<Notice> Notices { get; } = [];

    public void AddNotice(string text, bool warning = true)
    {
        if (Notices.All(n => n.Text != text))
        {
            Notices.Add(new Notice(text, warning));
        }
    }

    [RelayCommand]
    private void DismissNotice(Notice notice) => Notices.Remove(notice);

    private bool _syncingSelection;

    partial void OnSelectedPageChanged(PageKey value)
    {
        if (!_syncingSelection)
        {
            CurrentPage = PageFor(value);
        }
    }

    partial void OnCurrentPageChanged(object? oldValue, object newValue)
    {
        if (oldValue is IPageActivated old && !ReferenceEquals(oldValue, newValue))
        {
            old.OnDeactivated();
        }
    }

    [RelayCommand]
    private void Go(PageKey page)
    {
        if (SelectedPage == page)
        {
            CurrentPage = PageFor(page);
        }

        SelectedPage = page;
    }

    private object PageFor(PageKey page)
    {
        object vm = page switch
        {
            PageKey.Dashboard => _services.GetRequiredService<DashboardViewModel>(),
            PageKey.Games => _services.GetRequiredService<GamesViewModel>(),
            PageKey.Hardware => _services.GetRequiredService<HardwareViewModel>(),
            PageKey.SetupScore => _services.GetRequiredService<SetupScoreViewModel>(),
            PageKey.Reports => _services.GetRequiredService<ReportsViewModel>(),
            PageKey.Customization => _services.GetRequiredService<CustomizationViewModel>(),
            PageKey.Settings => _services.GetRequiredService<SettingsViewModel>(),
            _ => _services.GetRequiredService<AboutViewModel>(),
        };
        if (vm is IPageActivated activated)
        {
            activated.OnActivated();
        }

        return vm;
    }

    private void Navigate(NavigationRequest request)
    {
        if (request.SessionId is { } id)
        {
            _syncingSelection = true;
            SelectedPage = PageKey.Reports;
            _syncingSelection = false;
            if (request.CompareWith is { } other)
            {
                var compare = _services.GetRequiredService<CompareViewModel>();
                compare.Load(id, other);
                CurrentPage = compare;
            }
            else
            {
                var detail = _services.GetRequiredService<ReportDetailViewModel>();
                detail.Load(id);
                CurrentPage = detail;
            }

            return;
        }

        Go(request.Page);
    }

    [RelayCommand]
    private void ToggleOverlay() => _overlay.Toggle();

    [RelayCommand]
    private void TogglePause()
    {
        if (_recorder.IsPaused)
        {
            _recorder.Resume();
        }
        else
        {
            _recorder.Pause();
        }

        UpdateStatus();
    }

    private CaptureStatus? _probe;
    private DateTime _probeAt;

    /// <summary>Força nova verificação do PresentMon (após mudar o caminho nas configurações).</summary>
    public void InvalidateProbe() => _probe = null;

    public void UpdateStatus()
    {
        var active = _recorder.ActiveGame;
        GameActive = active is not null;
        GameStatus = active is null
            ? "Nenhum jogo em execução"
            : active.Confirmed ? active.Game.Name : active.Game.Name + " (não confirmado)";
        RecordingPaused = _recorder.IsPaused;
        OverlayVisible = _overlay.IsVisible;
        FpsBadge = Fmt.Fps(_live.Frames.Fps);

        if (active is null && (_probe is null || DateTime.UtcNow - _probeAt > TimeSpan.FromSeconds(5)))
        {
            _probe = _capture.Probe();
            _probeAt = DateTime.UtcNow;
        }

        var status = active is null ? _probe! : _capture.Status;
        if (active is null && status.State is CaptureState.Stopped)
        {
            status = new CaptureStatus(CaptureState.Stopped, "Aguardando um jogo para capturar quadros.");
        }

        CaptureStatus = Fmt.Capture(status.State);
        CaptureDetail = status.Message + (status.Detail is null ? "" : "\n" + status.Detail);
        CaptureTone = status.State switch
        {
            CaptureState.Capturing => "Good",
            CaptureState.Starting or CaptureState.WaitingForFrames => "Info",
            CaptureState.NotConfigured or CaptureState.AccessDenied or CaptureState.Unavailable or CaptureState.Error => "Warn",
            _ => "Muted",
        };
    }
}

/// <summary>Telas que recarregam dados ao serem exibidas.</summary>
public interface IPageActivated
{
    void OnActivated();

    void OnDeactivated()
    {
    }
}
