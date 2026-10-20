using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Framex.App.Services;
using Framex.Core.Customization;

namespace Framex.App.Controls;

/// <summary>Seletor de cor: amostra + campo hexadecimal + painel HSV (saturação/brilho e matiz).</summary>
public sealed class ColorPicker : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorPicker), new FrameworkPropertyMetadata("#00E5FF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    private readonly Border _swatch;
    private readonly TextBox _text;
    private readonly Popup _popup;
    private readonly Border _svArea;
    private readonly Ellipse _svThumb;
    private readonly Slider _hue;
    private readonly Canvas _svCanvas;
    private double _h;
    private double _s;
    private double _v;
    private bool _updating;

    public ColorPicker()
    {
        _swatch = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "Abrir seletor de cor" };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        _swatch.MouseLeftButtonUp += (_, _) => _popup!.IsOpen = !_popup.IsOpen;

        _text = new TextBox { Width = 96, Margin = new Thickness(8, 0, 0, 0), MaxLength = 9, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        _text.LostKeyboardFocus += (_, _) => CommitText();
        _text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
            }
        };

        _svCanvas = new Canvas { Width = 200, Height = 140, ClipToBounds = true };
        _svArea = new Border { Width = 200, Height = 140, CornerRadius = new CornerRadius(6) };
        var white = new Rectangle { Width = 200, Height = 140, RadiusX = 6, RadiusY = 6, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Rectangle { Width = 200, Height = 140, RadiusX = 6, RadiusY = 6, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        _svThumb = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        _svCanvas.Children.Add(_svArea);
        _svCanvas.Children.Add(white);
        _svCanvas.Children.Add(black);
        _svCanvas.Children.Add(_svThumb);
        _svCanvas.MouseLeftButtonDown += (_, e) =>
        {
            _svCanvas.CaptureMouse();
            PickSv(e.GetPosition(_svCanvas));
        };
        _svCanvas.MouseMove += (_, e) =>
        {
            if (_svCanvas.IsMouseCaptured)
            {
                PickSv(e.GetPosition(_svCanvas));
            }
        };
        _svCanvas.MouseLeftButtonUp += (_, _) => _svCanvas.ReleaseMouseCapture();

        var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++)
        {
            hueBrush.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        }

        _hue = new Slider { Minimum = 0, Maximum = 359, Width = 200, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Matiz" };
        _hue.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            _h = e.NewValue;
            Commit();
        };
        var hueTrack = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = hueBrush, Width = 200, Margin = new Thickness(0, 10, 0, 0) };

        var panel = new StackPanel();
        panel.Children.Add(_svCanvas);
        panel.Children.Add(hueTrack);
        panel.Children.Add(_hue);
        var popupBorder = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "B.SurfaceHigh");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        _popup = new Popup { Child = popupBorder, PlacementTarget = _swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 6 };

        var root = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(_swatch);
        root.Children.Add(_text);
        root.Children.Add(_popup);
        Content = root;
        Sync();
    }

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColorPicker)d).Sync();

    private void Sync()
    {
        var c = ThemeService.ParseColor(Hex ?? "", Colors.Black);
        _swatch.Background = new SolidColorBrush(c);
        _text.Text = Hex;
        if (_updating)
        {
            return;
        }

        (_h, _s, _v) = ToHsv(c);
        _updating = true;
        _hue.Value = _h;
        _updating = false;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        _svArea.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_svThumb, (_s * _svCanvas.Width) - 7);
        Canvas.SetTop(_svThumb, ((1 - _v) * _svCanvas.Height) - 7);
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / _svCanvas.Width, 0, 1);
        _v = Math.Clamp(1 - (p.Y / _svCanvas.Height), 0, 1);
        Commit();
    }

    private void Commit()
    {
        _updating = true;
        Hex = ThemeService.ToHex(FromHsv(_h, _s, _v));
        _updating = false;
        UpdateVisuals();
    }

    private void CommitText()
    {
        var t = _text.Text.Trim();
        if (!t.StartsWith('#'))
        {
            t = "#" + t;
        }

        if (ThemeDefinition.IsValidColor(t))
        {
            Hex = t.ToUpperInvariant();
        }
        else
        {
            _text.Text = Hex;
        }
    }

    public static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r)
            {
                h = 60 * (((g - b) / d) % 6);
            }
            else if (max == g)
            {
                h = 60 * (((b - r) / d) + 2);
            }
            else
            {
                h = 60 * (((r - g) / d) + 4);
            }
        }

        if (h < 0)
        {
            h += 360;
        }

        return (h, max == 0 ? 0 : d / max, max);
    }
}
