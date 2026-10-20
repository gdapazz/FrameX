using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Framex.Core.Customization;

namespace Framex.App.Controls;

/// <summary>Medidor circular/arco/barra desenhado diretamente (sem dados próprios). Valor nulo = sensor indisponível (trilha tracejada).</summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(ArcGauge), new FrameworkPropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(GaugeStyle), typeof(ArcGauge), new FrameworkPropertyMetadata(GaugeStyle.Ring, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(ArcGauge), new PropertyMetadata(true));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public GaugeStyle Mode
    {
        get => (GaugeStyle)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (ArcGauge)d;
        var target = g.Value is { } v && double.IsFinite(v) && g.Maximum > 0 ? Math.Clamp(v / g.Maximum, 0, 1) : 0;
        if (g.Animate && g.IsLoaded)
        {
            g.BeginAnimation(DisplayValueProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            g.BeginAnimation(DisplayValueProperty, null);
            g.SetValue(DisplayValueProperty, target);
        }

        g.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4)
        {
            return;
        }

        var fraction = (double)GetValue(DisplayValueProperty);
        var missing = Value is null;
        var t = Math.Min(Thickness, Math.Min(w, h) / 3);
        var trackPen = new Pen(Track, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (missing)
        {
            trackPen.DashStyle = new DashStyle([0.2, 1.6], 0);
            trackPen.DashCap = PenLineCap.Round;
        }

        var pen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (Mode == GaugeStyle.Bar)
        {
            var y = h / 2;
            dc.DrawLine(trackPen, new Point(t / 2, y), new Point(w - (t / 2), y));
            if (!missing && fraction > 0.001)
            {
                dc.DrawLine(pen, new Point(t / 2, y), new Point((t / 2) + ((w - t) * fraction), y));
            }

            return;
        }

        double start, sweep;
        if (Mode == GaugeStyle.Arc)
        {
            start = 135;
            sweep = 270;
        }
        else
        {
            start = -90;
            sweep = 359.9;
        }

        var r = (Math.Min(w, h) / 2) - (t / 2);
        var c = new Point(w / 2, h / 2);
        dc.DrawGeometry(null, trackPen, Arc(c, r, start, sweep));
        if (!missing && fraction > 0.001)
        {
            dc.DrawGeometry(null, pen, Arc(c, r, start, sweep * fraction));
        }
    }

    private static StreamGeometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        static Point At(Point c, double r, double deg)
        {
            var a = deg * Math.PI / 180;
            return new Point(c.X + (r * Math.Cos(a)), c.Y + (r * Math.Sin(a)));
        }

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(c, r, startDeg), false, false);
            ctx.ArcTo(At(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }

        g.Freeze();
        return g;
    }
}
