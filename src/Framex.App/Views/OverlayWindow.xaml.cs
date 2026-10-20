using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Framex.App.Interop;
using Framex.App.ViewModels;
using Forms = System.Windows.Forms;

namespace Framex.App.Views;

/// <summary>
/// HUD independente: janela transparente, sempre no topo, fora da barra de tarefas. Não injeta nada no jogo.
/// Modo passivo: atravessa cliques e nunca recebe foco (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE). Modo edição: arrastável.
/// Observação: jogos em tela cheia exclusiva podem cobrir janelas comuns; o modo janela sem bordas é recomendado.
/// </summary>
public partial class OverlayWindow : Window
{
    private IntPtr _hwnd;
    private bool _editMode;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        MouseLeftButtonDown += OnMouseDown;
    }

    public event EventHandler<(double X, double Y, string? Monitor)>? PositionCommitted;

    public double RelativeX { get; set; }

    public double RelativeY { get; set; }

    public string? MonitorDevice { get; set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Reposition();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    public void SetEditMode(bool edit)
    {
        _editMode = edit;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (edit)
        {
            Win32.RemoveExStyle(_hwnd, Win32.WS_EX_TRANSPARENT);
            Cursor = Cursors.SizeAll;
        }
        else
        {
            Win32.AddExStyle(_hwnd, Win32.WS_EX_TRANSPARENT);
            Cursor = null;
        }
    }

    private static Forms.Screen TargetScreen(string? device) =>
        Forms.Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, device, StringComparison.OrdinalIgnoreCase)) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    /// <summary>Posiciona em pixels físicos dentro dos limites do monitor escolhido (posição relativa 0–1).</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero || !Win32.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var bounds = TargetScreen(MonitorDevice).Bounds;
        var x = bounds.Left + (int)Math.Round(Math.Clamp(RelativeX, 0, 1) * Math.Max(0, bounds.Width - rect.Width));
        var y = bounds.Top + (int)Math.Round(Math.Clamp(RelativeY, 0, 1) * Math.Max(0, bounds.Height - rect.Height));
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        if (!Win32.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var screen = Forms.Screen.FromHandle(_hwnd);
        var b = screen.Bounds;
        RelativeX = b.Width > rect.Width ? Math.Clamp((rect.Left - b.Left) / (double)(b.Width - rect.Width), 0, 1) : 0;
        RelativeY = b.Height > rect.Height ? Math.Clamp((rect.Top - b.Top) / (double)(b.Height - rect.Height), 0, 1) : 0;
        MonitorDevice = screen.Primary ? null : screen.DeviceName;
        Reposition();
        PositionCommitted?.Invoke(this, (RelativeX, RelativeY, MonitorDevice));
    }
}
