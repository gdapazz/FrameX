using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Framex.App.Controls;

/// <summary>
/// Gráfico de linhas ao vivo, leve (renderização direta). Até três séries, valores NaN quebram a linha (lacuna real de dados).
/// Redesenha quando <see cref="Revision"/> muda.
/// </summary>
public sealed class LiveChart : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty Series1Property = DependencyProperty.Register(nameof(Series1), typeof(IReadOnlyList<double>), typeof(LiveChart), Render(null!));
    public static readonly DependencyProperty Series2Property = DependencyProperty.Register(nameof(Series2), typeof(IReadOnlyList<double>), typeof(LiveChart), Render(null!));
    public static readonly DependencyProperty Series3Property = DependencyProperty.Register(nameof(Series3), typeof(IReadOnlyList<double>), typeof(LiveChart), Render(null!));
    public static readonly DependencyProperty Brush1Property = DependencyProperty.Register(nameof(Brush1), typeof(Brush), typeof(LiveChart), Render(Brushes.Cyan));
    public static readonly DependencyProperty Brush2Property = DependencyProperty.Register(nameof(Brush2), typeof(Brush), typeof(LiveChart), Render(Brushes.MediumPurple));
    public static readonly DependencyProperty Brush3Property = DependencyProperty.Register(nameof(Brush3), typeof(Brush), typeof(LiveChart), Render(Brushes.HotPink));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(LiveChart), Render(Brushes.DimGray));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(LiveChart), Render(Brushes.Gray));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LiveChart), Render(0.0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LiveChart), Render(double.NaN));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(LiveChart), Render(120));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(nameof(Revision), typeof(int), typeof(LiveChart), Render(0));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(bool), typeof(LiveChart), Render(true));
    public static readonly DependencyProperty ShowAxisProperty = DependencyProperty.Register(nameof(ShowAxis), typeof(bool), typeof(LiveChart), Render(true));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LiveChart), Render(""));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(LiveChart), Render(1.8));

    public IReadOnlyList<double>? Series1 { get => (IReadOnlyList<double>?)GetValue(Series1Property); set => SetValue(Series1Property, value); }
    public IReadOnlyList<double>? Series2 { get => (IReadOnlyList<double>?)GetValue(Series2Property); set => SetValue(Series2Property, value); }
    public IReadOnlyList<double>? Series3 { get => (IReadOnlyList<double>?)GetValue(Series3Property); set => SetValue(Series3Property, value); }
    public Brush Brush1 { get => (Brush)GetValue(Brush1Property); set => SetValue(Brush1Property, value); }
    public Brush Brush2 { get => (Brush)GetValue(Brush2Property); set => SetValue(Brush2Property, value); }
    public Brush Brush3 { get => (Brush)GetValue(Brush3Property); set => SetValue(Brush3Property, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <summary>Máximo do eixo Y; NaN = automático a partir dos dados visíveis.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public bool Fill { get => (bool)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool ShowAxis { get => (bool)GetValue(ShowAxisProperty); set => SetValue(ShowAxisProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var axisW = ShowAxis ? 34.0 : 0;
        var plot = new Rect(axisW, 4, Math.Max(1, w - axisW), Math.Max(1, h - 8));

        var max = Maximum;
        if (double.IsNaN(max))
        {
            max = AutoMax(Series1, Series2, Series3);
        }

        var min = Minimum;
        if (max <= min)
        {
            max = min + 1;
        }

        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        gridPen.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = ChartText.Typeface(this);
        var hasData = HasFinite(Series1) || HasFinite(Series2) || HasFinite(Series3);
        for (var i = 0; i <= 2; i++)
        {
            var y = plot.Top + (plot.Height * i / 2.0);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (ShowAxis && hasData)
            {
                var v = max - ((max - min) * i / 2.0);
                var ft = new FormattedText(v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + Unit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush, dpi);
                dc.DrawText(ft, new Point(axisW - ft.Width - 6, y - (ft.Height / 2)));
            }
        }

        DrawSeries(dc, Series3, Brush3, plot, min, max, false);
        DrawSeries(dc, Series2, Brush2, plot, min, max, false);
        DrawSeries(dc, Series1, Brush1, plot, min, max, Fill);
    }

    private static bool HasFinite(IReadOnlyList<double>? series)
    {
        if (series is null)
        {
            return false;
        }

        for (var i = 0; i < series.Count; i++)
        {
            if (double.IsFinite(series[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static double AutoMax(params IReadOnlyList<double>?[] series)
    {
        var max = double.NaN;
        foreach (var s in series)
        {
            if (s is null)
            {
                continue;
            }

            for (var i = 0; i < s.Count; i++)
            {
                if (double.IsFinite(s[i]) && (double.IsNaN(max) || s[i] > max))
                {
                    max = s[i];
                }
            }
        }

        if (double.IsNaN(max) || max <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        return Math.Ceiling(max * 1.15 / magnitude * 2) / 2 * magnitude;
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double>? s, Brush brush, Rect plot, double min, double max, bool fill)
    {
        if (s is null || s.Count < 2)
        {
            return;
        }

        var capacity = Math.Max(Capacity, s.Count);
        var step = plot.Width / (capacity - 1);
        var offset = capacity - s.Count;
        double X(int i) => plot.Left + ((i + offset) * step);
        double Y(double v) => plot.Bottom - (Math.Clamp((v - min) / (max - min), 0, 1) * plot.Height);

        var line = new StreamGeometry();
        var area = fill ? new StreamGeometry() : null;
        using (var ctx = line.Open())
        using (var actx = area?.Open())
        {
            var open = false;
            var segmentStart = 0.0;
            var lastX = 0.0;
            for (var i = 0; i < s.Count; i++)
            {
                var v = s[i];
                if (!double.IsFinite(v))
                {
                    if (open && actx is not null)
                    {
                        actx.LineTo(new Point(lastX, plot.Bottom), false, false);
                        actx.LineTo(new Point(segmentStart, plot.Bottom), false, false);
                    }

                    open = false;
                    continue;
                }

                var p = new Point(X(i), Y(v));
                if (!open)
                {
                    ctx.BeginFigure(p, false, false);
                    actx?.BeginFigure(new Point(p.X, plot.Bottom), true, true);
                    actx?.LineTo(p, false, false);
                    segmentStart = p.X;
                    open = true;
                }
                else
                {
                    ctx.LineTo(p, true, true);
                    actx?.LineTo(p, false, false);
                }

                lastX = p.X;
            }

            if (open && actx is not null)
            {
                actx.LineTo(new Point(lastX, plot.Bottom), false, false);
            }
        }

        line.Freeze();
        if (area is not null && brush is SolidColorBrush solid)
        {
            area.Freeze();
            var c = solid.Color;
            var gradient = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        var pen = new Pen(brush, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
