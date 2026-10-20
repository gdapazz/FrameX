using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Framex.Core.Customization;

namespace Framex.App.Controls;

/// <summary>
/// Ilustração vetorial da avaliação: CPU (encapsulamento com pinos), GPU (placa com ventoinhas) e módulo de RAM
/// (chips e contatos) flutuando com pequenas diferenças de fase, mais pontos de luz e uma barra de varredura indeterminada.
/// Desenho direto em <see cref="OnRender"/> (sem árvore visual extra), limitado a ~30 quadros/s.
/// Respeita o nível de animação do FRAMEX e a preferência do Windows; pausa quando oculta ou com a janela minimizada.
/// </summary>
public sealed class SetupScanAnimation : FrameworkElement
{
    private const double DesignWidth = 520;
    private const double DesignHeight = 230;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SetupScanAnimation), new PropertyMetadata(false, (d, _) => ((SetupScanAnimation)d).UpdateRunning()));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(AnimationLevel), typeof(SetupScanAnimation), new FrameworkPropertyMetadata(AnimationLevel.Full, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SetupScanAnimation)d).UpdateRunning()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SetupScanAnimation), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(SetupScanAnimation), new FrameworkPropertyMetadata(Brushes.Magenta, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SetupScanAnimation), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(Brush), typeof(SetupScanAnimation), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Stopwatch _clock = new();
    private TimeSpan _lastFrame;
    private bool _subscribed;
    private Window? _window;

    public SetupScanAnimation()
    {
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.StateChanged += OnWindowStateChanged;
            }

            UpdateRunning();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
            {
                _window.StateChanged -= OnWindowStateChanged;
                _window = null;
            }

            Stop();
        };
        IsVisibleChanged += (_, _) => UpdateRunning();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public AnimationLevel Level
    {
        get => (AnimationLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Detail
    {
        get => (Brush)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Nível efetivo: o menor entre a preferência do FRAMEX e a do Windows ("Mostrar animações").</summary>
    public AnimationLevel EffectiveLevel => SystemParameters.ClientAreaAnimation ? Level : AnimationLevel.Off;

    /// <summary>Verdadeiro enquanto o quadro está sendo redesenhado (exposto para testes de interface).</summary>
    public bool IsAnimating => _subscribed;

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateRunning();

    private void UpdateRunning()
    {
        var shouldRun = IsActive && IsLoaded && IsVisible && EffectiveLevel != AnimationLevel.Off && _window?.WindowState != WindowState.Minimized;
        if (shouldRun && !_subscribed)
        {
            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
        else if (!shouldRun && _subscribed)
        {
            Stop();
        }

        InvalidateVisual();
    }

    private void Stop()
    {
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }

        _clock.Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        if (now - _lastFrame < FrameInterval)
        {
            return;
        }

        _lastFrame = now;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? DesignWidth : Math.Min(availableSize.Width, DesignWidth);
        return new Size(w, w * DesignHeight / DesignWidth);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 20)
        {
            return;
        }

        var level = EffectiveLevel;
        var t = level == AnimationLevel.Off ? 0 : _clock.Elapsed.TotalSeconds;
        var full = level == AnimationLevel.Full;
        var amplitude = full ? 6.0 : level == AnimationLevel.Reduced ? 2.0 : 0;
        var tilt = full ? 2.2 : 0;

        var scale = Math.Min(w / DesignWidth, h / DesignHeight);
        dc.PushTransform(new TranslateTransform((w - (DesignWidth * scale)) / 2, (h - (DesignHeight * scale)) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));

        var stroke = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var accent = new Pen(Accent, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var detail = new Pen(Detail, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        stroke.Freeze();
        accent.Freeze();
        detail.Freeze();

        if (full)
        {
            DrawParticles(dc, t);
        }

        Float(dc, 95, 100, t, 0.0, amplitude, tilt, () => DrawCpu(dc, stroke, accent, detail));
        Float(dc, 262, 92, t, 1.1, amplitude, tilt * 0.6, () => DrawGpu(dc, stroke, accent, detail, full ? t : 0));
        Float(dc, 430, 104, t, 2.2, amplitude, tilt, () => DrawRam(dc, stroke, accent, detail));

        if (level != AnimationLevel.Off)
        {
            DrawScanBar(dc, t, full ? 1.0 : 0.5);
        }

        dc.Pop();
        dc.Pop();
    }

    private static void Float(DrawingContext dc, double cx, double cy, double t, double phase, double amplitude, double tilt, Action draw)
    {
        var dy = Math.Sin((t * 1.6) + phase) * amplitude;
        var angle = Math.Sin((t * 1.1) + phase) * tilt;
        dc.PushTransform(new TranslateTransform(cx, cy + dy));
        dc.PushTransform(new RotateTransform(angle));
        draw();
        dc.Pop();
        dc.Pop();
    }

    private void DrawCpu(DrawingContext dc, Pen stroke, Pen accent, Pen detail)
    {
        const double s = 74;
        var body = new Rect(-s / 2, -s / 2, s, s);
        for (var i = 0; i < 6; i++)
        {
            var o = -s / 2 + 10 + (i * ((s - 20) / 5));
            dc.DrawLine(detail, new Point(o, -s / 2), new Point(o, -s / 2 - 9));
            dc.DrawLine(detail, new Point(o, s / 2), new Point(o, s / 2 + 9));
            dc.DrawLine(detail, new Point(-s / 2, o), new Point(-s / 2 - 9, o));
            dc.DrawLine(detail, new Point(s / 2, o), new Point(s / 2 + 9, o));
        }

        dc.DrawRoundedRectangle(Fill, stroke, body, 8, 8);
        dc.DrawRoundedRectangle(null, accent, new Rect(-18, -18, 36, 36), 4, 4);
        dc.DrawEllipse(Accent, null, new Point(-s / 2 + 9, -s / 2 + 9), 2.4, 2.4);
        dc.DrawLine(detail, new Point(-9, 0), new Point(9, 0));
        dc.DrawLine(detail, new Point(0, -9), new Point(0, 9));
    }

    private void DrawGpu(DrawingContext dc, Pen stroke, Pen accent, Pen detail, double t)
    {
        const double cw = 168;
        const double ch = 78;
        var card = new Rect(-cw / 2, -ch / 2, cw, ch);
        dc.DrawRectangle(Detail, null, new Rect(-cw / 2 - 8, -ch / 2 - 4, 5, ch + 14));
        for (var i = 0; i < 9; i++)
        {
            dc.DrawRectangle(Accent, null, new Rect(-36 + (i * 9), ch / 2, 5, 7));
        }

        dc.DrawRoundedRectangle(Fill, stroke, card, 10, 10);
        foreach (var fx in new[] { -38.0, 38.0 })
        {
            var c = new Point(fx, 0);
            dc.DrawEllipse(null, accent, c, 27, 27);
            dc.DrawEllipse(Accent, null, c, 4, 4);
            var spin = t * 140;
            for (var b = 0; b < 6; b++)
            {
                var a = (spin + (b * 60)) * Math.PI / 180;
                var p1 = new Point(c.X + (Math.Cos(a) * 7), c.Y + (Math.Sin(a) * 7));
                var p2 = new Point(c.X + (Math.Cos(a + 0.55) * 22), c.Y + (Math.Sin(a + 0.55) * 22));
                dc.DrawLine(detail, p1, p2);
            }
        }

        dc.DrawLine(detail, new Point(-cw / 2 + 10, -ch / 2 + 8), new Point(cw / 2 - 10, -ch / 2 + 8));
    }

    private void DrawRam(DrawingContext dc, Pen stroke, Pen accent, Pen detail)
    {
        const double mw = 132;
        const double mh = 44;
        var module = new Rect(-mw / 2, -mh / 2, mw, mh);
        dc.DrawRoundedRectangle(Fill, stroke, module, 4, 4);
        for (var i = 0; i < 5; i++)
        {
            dc.DrawRoundedRectangle(null, accent, new Rect(-mw / 2 + 9 + (i * 24), -mh / 2 + 7, 18, 16), 2, 2);
        }

        for (var i = 0; i < 22; i++)
        {
            var x = -mw / 2 + 6 + (i * 5.6);
            if (Math.Abs(x - 8) < 3)
            {
                continue;
            }

            dc.DrawLine(detail, new Point(x, mh / 2 - 9), new Point(x, mh / 2 - 3));
        }

        dc.DrawLine(stroke, new Point(5, mh / 2), new Point(11, mh / 2));
    }

    private void DrawParticles(DrawingContext dc, double t)
    {
        for (var i = 0; i < 14; i++)
        {
            var seed = i * 37.17;
            var period = 3.2 + (i % 5 * 0.45);
            var phase = ((t + (seed % period)) % period) / period;
            var x = 40 + ((seed * 13) % 440);
            var y = 210 - (phase * 190);
            var alpha = Math.Sin(phase * Math.PI);
            dc.PushOpacity(alpha * 0.8);
            dc.DrawEllipse(i % 3 == 0 ? Accent : Stroke, null, new Point(x, y), 1.6 + (i % 3 * 0.5), 1.6 + (i % 3 * 0.5));
            dc.Pop();
        }

        var pulse = (t % 2.4) / 2.4;
        dc.PushOpacity(1 - pulse);
        dc.DrawEllipse(null, new Pen(Stroke, 1.2), new Point(262, 92), 60 + (pulse * 70), 34 + (pulse * 40));
        dc.Pop();
    }

    private void DrawScanBar(DrawingContext dc, double t, double speed)
    {
        const double left = 60;
        const double width = 400;
        const double y = 205;
        dc.DrawRoundedRectangle(Detail, null, new Rect(left, y, width, 4), 2, 2);
        var seg = 90.0;
        var p = ((t * speed * 0.55) % 1.0) * (width + seg) - seg;
        var x0 = Math.Max(left, left + p);
        var x1 = Math.Min(left + width, left + p + seg);
        if (x1 > x0)
        {
            dc.DrawRoundedRectangle(Stroke, null, new Rect(x0, y, x1 - x0, 4), 2, 2);
        }
    }
}

/// <summary>Reticências animadas ("…") que respeitam o nível de animação; ficam estáticas quando desativadas.</summary>
public sealed class AnimatedEllipsis : TextBlock
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(AnimationLevel), typeof(AnimatedEllipsis), new PropertyMetadata(AnimationLevel.Full, (d, _) => ((AnimatedEllipsis)d).Update()));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private int _step;

    public AnimatedEllipsis()
    {
        Text = "...";
        _timer.Tick += (_, _) =>
        {
            _step = (_step + 1) % 4;
            Text = new string('.', _step);
        };
        IsVisibleChanged += (_, _) => Update();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => Update();
    }

    public AnimationLevel Level
    {
        get => (AnimationLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private void Update()
    {
        if (IsVisible && IsLoaded && Level != AnimationLevel.Off && SystemParameters.ClientAreaAnimation)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            Text = "...";
        }
    }
}
