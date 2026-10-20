using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Framex.App.Controls;

/// <summary>Série temporal: X em segundos desde o início da sessão (ordenado), Y no valor da métrica (NaN = lacuna).</summary>
public sealed class ChartSeries(string name, Brush brush, double[] x, double[] y, string unit, string format = "0.#")
{
    public string Name { get; } = name;
    public Brush Brush { get; } = brush;
    public double[] X { get; } = x;
    public double[] Y { get; } = y;
    public string Unit { get; } = unit;
    public string Format { get; } = format;
}

public sealed record ChartMarker(double X, string Label, Brush Brush);

/// <summary>
/// Gráfico de séries temporais para relatórios: zoom (roda do mouse), arrastar para mover, Shift+arrastar para selecionar intervalo,
/// duplo clique para restaurar. Intervalo visível e posição do cursor são propriedades bidirecionais para sincronizar vários gráficos.
/// Séries longas são reduzidas por coluna de pixel (mínimo/máximo), preservando picos.
/// </summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    private const double AxisLeft = 48;
    private const double AxisBottom = 22;
    private const double MinSpanSeconds = 2;

    private static FrameworkPropertyMetadata Render(object? value, bool twoWay = false) =>
        new(value, FrameworkPropertyMetadataOptions.AffectsRender | (twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0));

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(TimeSeriesChart), Render(null));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<ChartMarker>), typeof(TimeSeriesChart), Render(null));
    public static readonly DependencyProperty TotalDurationProperty = DependencyProperty.Register(nameof(TotalDuration), typeof(double), typeof(TimeSeriesChart), Render(0.0));
    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(nameof(ViewStart), typeof(double), typeof(TimeSeriesChart), Render(0.0, true));
    public static readonly DependencyProperty ViewEndProperty = DependencyProperty.Register(nameof(ViewEnd), typeof(double), typeof(TimeSeriesChart), Render(0.0, true));
    public static readonly DependencyProperty HoverXProperty = DependencyProperty.Register(nameof(HoverX), typeof(double), typeof(TimeSeriesChart), Render(double.NaN, true));
    public static readonly DependencyProperty YMinProperty = DependencyProperty.Register(nameof(YMin), typeof(double), typeof(TimeSeriesChart), Render(0.0));
    public static readonly DependencyProperty YMaxProperty = DependencyProperty.Register(nameof(YMax), typeof(double), typeof(TimeSeriesChart), Render(double.NaN));
    public static readonly DependencyProperty GapSecondsProperty = DependencyProperty.Register(nameof(GapSeconds), typeof(double), typeof(TimeSeriesChart), Render(5.0));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(TimeSeriesChart), Render(Brushes.DimGray));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(TimeSeriesChart), Render(Brushes.Gray));
    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(nameof(TooltipBackground), typeof(Brush), typeof(TimeSeriesChart), Render(Brushes.Black));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(TimeSeriesChart), Render(Brushes.White));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(TimeSeriesChart), Render(Brushes.Cyan));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<ChartMarker>? Markers { get => (IReadOnlyList<ChartMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public double TotalDuration { get => (double)GetValue(TotalDurationProperty); set => SetValue(TotalDurationProperty, value); }
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => (double)GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public double HoverX { get => (double)GetValue(HoverXProperty); set => SetValue(HoverXProperty, value); }
    public double YMin { get => (double)GetValue(YMinProperty); set => SetValue(YMinProperty, value); }

    /// <summary>Máximo do eixo Y; NaN = automático.</summary>
    public double YMax { get => (double)GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }

    /// <summary>Intervalo entre amostras acima do qual a linha é interrompida (lacuna de dados).</summary>
    public double GapSeconds { get => (double)GetValue(GapSecondsProperty); set => SetValue(GapSecondsProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush TooltipBackground { get => (Brush)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    private Point? _dragStart;
    private double _dragViewStart;
    private double _dragViewEnd;
    private bool _selecting;
    private double _selectionX;

    public TimeSeriesChart()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    private (double Start, double End) View()
    {
        var total = Math.Max(TotalDuration, 1);
        var s = ViewStart;
        var e = ViewEnd;
        if (e <= s)
        {
            return (0, total);
        }

        return (Math.Max(0, s), Math.Min(total, e));
    }

    private Rect Plot => new(AxisLeft, 8, Math.Max(1, ActualWidth - AxisLeft - 8), Math.Max(1, ActualHeight - AxisBottom - 8));

    private double ToX(double seconds, Rect plot, (double Start, double End) v) => plot.Left + ((seconds - v.Start) / (v.End - v.Start) * plot.Width);

    private double FromX(double px, Rect plot, (double Start, double End) v) => v.Start + ((px - plot.Left) / plot.Width * (v.End - v.Start));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 60 || h < 40)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var plot = Plot;
        var view = View();
        var series = Series ?? [];

        var (yMin, yMax) = YRange(series, view);
        double Y(double value) => plot.Bottom - (Math.Clamp((value - yMin) / (yMax - yMin), -0.05, 1.05) * plot.Height);

        var grid = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        grid.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var value = yMin + ((yMax - yMin) * i / 4);
            var y = Y(value);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = ChartText.Create(this, value.ToString(Math.Abs(yMax - yMin) >= 20 ? "0" : "0.#", CultureInfo.CurrentCulture), 10, LabelBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - (label.Height / 2)));
        }

        var span = view.End - view.Start;
        var tick = NiceStep(span / 6);
        for (var t = Math.Ceiling(view.Start / tick) * tick; t <= view.End; t += tick)
        {
            var x = ToX(t, plot, view);
            dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = ChartText.Create(this, ChartText.Time(t), 10, LabelBrush);
            dc.DrawText(label, new Point(Math.Clamp(x - (label.Width / 2), plot.Left, plot.Right - label.Width), plot.Bottom + 5));
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var marker in Markers ?? [])
        {
            if (marker.X < view.Start || marker.X > view.End)
            {
                continue;
            }

            var x = ToX(marker.X, plot, view);
            var pen = new Pen(marker.Brush, 1) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            dc.PushOpacity(0.7);
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.Pop();
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(new Point(x - 4, plot.Top), true, true);
                ctx.LineTo(new Point(x + 4, plot.Top), false, false);
                ctx.LineTo(new Point(x, plot.Top + 6), false, false);
            }

            tri.Freeze();
            dc.DrawGeometry(marker.Brush, null, tri);
        }

        foreach (var s in series)
        {
            DrawSeries(dc, s, plot, view, Y);
        }

        dc.Pop();

        if (_selecting && _dragStart is { } start)
        {
            var x1 = Math.Clamp(Math.Min(start.X, _selectionX), plot.Left, plot.Right);
            var x2 = Math.Clamp(Math.Max(start.X, _selectionX), plot.Left, plot.Right);
            var fill = AccentBrush.Clone();
            fill.Opacity = 0.15;
            fill.Freeze();
            dc.DrawRectangle(fill, new Pen(AccentBrush, 1), new Rect(x1, plot.Top, x2 - x1, plot.Height));
        }

        DrawHover(dc, series, plot, view, Y);
    }

    private (double Min, double Max) YRange(IReadOnlyList<ChartSeries> series, (double Start, double End) view)
    {
        var min = YMin;
        var max = YMax;
        if (double.IsNaN(max))
        {
            max = double.NaN;
            foreach (var s in series)
            {
                var (from, to) = IndexRange(s.X, view);
                for (var i = from; i < to; i++)
                {
                    var v = s.Y[i];
                    if (double.IsFinite(v) && (double.IsNaN(max) || v > max))
                    {
                        max = v;
                    }
                }
            }

            if (double.IsNaN(max) || max <= min)
            {
                max = min + 1;
            }
            else
            {
                var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(max - min, 1e-6))));
                max = min + (Math.Ceiling((max - min) * 1.1 / magnitude * 2) / 2 * magnitude);
            }
        }

        if (double.IsNaN(min) || min >= max)
        {
            min = max - 1;
        }

        return (min, max);
    }

    private static double NiceStep(double raw)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
        foreach (var s in steps)
        {
            if (s >= raw)
            {
                return s;
            }
        }

        return Math.Ceiling(raw / 3600) * 3600;
    }

    private static (int From, int To) IndexRange(double[] x, (double Start, double End) view)
    {
        var from = Array.BinarySearch(x, view.Start);
        if (from < 0)
        {
            from = ~from;
        }

        var to = Array.BinarySearch(x, view.End);
        to = to < 0 ? ~to : to + 1;
        return (Math.Max(0, from - 1), Math.Min(x.Length, to + 1));
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s, Rect plot, (double Start, double End) view, Func<double, double> y)
    {
        if (s.X.Length == 0)
        {
            return;
        }

        var (from, to) = IndexRange(s.X, view);
        var pen = new Pen(s.Brush, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var geometry = new StreamGeometry();
        var columns = (int)Math.Max(1, plot.Width);
        using (var ctx = geometry.Open())
        {
            if (to - from > columns * 2)
            {
                // Redução por coluna de pixel preservando mínimo e máximo de cada coluna.
                var open = false;
                var lastCol = -1;
                double colMin = 0, colMax = 0, lastX = double.NaN;
                for (var i = from; i < to; i++)
                {
                    var v = s.Y[i];
                    var gap = !double.IsNaN(lastX) && s.X[i] - lastX > GapSeconds;
                    lastX = s.X[i];
                    if (!double.IsFinite(v) || gap)
                    {
                        if (lastCol >= 0)
                        {
                            Emit(ctx, ref open, plot.Left + lastCol, y(colMin), y(colMax));
                        }

                        lastCol = -1;
                        open = false;
                        if (!double.IsFinite(v))
                        {
                            continue;
                        }
                    }

                    var col = (int)((ToX(s.X[i], plot, view) - plot.Left));
                    if (col != lastCol)
                    {
                        if (lastCol >= 0)
                        {
                            Emit(ctx, ref open, plot.Left + lastCol, y(colMin), y(colMax));
                        }

                        lastCol = col;
                        colMin = colMax = v;
                    }
                    else
                    {
                        colMin = Math.Min(colMin, v);
                        colMax = Math.Max(colMax, v);
                    }
                }

                if (lastCol >= 0)
                {
                    Emit(ctx, ref open, plot.Left + lastCol, y(colMin), y(colMax));
                }
            }
            else
            {
                var open = false;
                var lastX = double.NaN;
                for (var i = from; i < to; i++)
                {
                    var v = s.Y[i];
                    var gap = !double.IsNaN(lastX) && s.X[i] - lastX > GapSeconds;
                    lastX = s.X[i];
                    if (!double.IsFinite(v))
                    {
                        open = false;
                        continue;
                    }

                    var p = new Point(ToX(s.X[i], plot, view), y(v));
                    if (!open || gap)
                    {
                        ctx.BeginFigure(p, false, false);
                        open = true;
                    }
                    else
                    {
                        ctx.LineTo(p, true, true);
                    }
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void Emit(StreamGeometryContext ctx, ref bool open, double x, double yA, double yB)
    {
        if (!open)
        {
            ctx.BeginFigure(new Point(x, yA), false, false);
            open = true;
        }
        else
        {
            ctx.LineTo(new Point(x, yA), true, true);
        }

        if (Math.Abs(yA - yB) > 0.5)
        {
            ctx.LineTo(new Point(x, yB), true, true);
        }
    }

    private void DrawHover(DrawingContext dc, IReadOnlyList<ChartSeries> series, Rect plot, (double Start, double End) view, Func<double, double> y)
    {
        var hover = HoverX;
        if (double.IsNaN(hover) || hover < view.Start || hover > view.End)
        {
            return;
        }

        var x = ToX(hover, plot, view);
        var pen = new Pen(TextBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
        pen.Freeze();
        dc.PushOpacity(0.5);
        dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        dc.Pop();

        var lines = new List<(string Text, Brush Brush)> { (ChartText.Time(hover), LabelBrush) };
        foreach (var s in series)
        {
            var idx = Nearest(s.X, hover);
            if (idx < 0 || Math.Abs(s.X[idx] - hover) > GapSeconds)
            {
                lines.Add(($"{s.Name}: sem dados", s.Brush));
                continue;
            }

            var v = s.Y[idx];
            if (!double.IsFinite(v))
            {
                lines.Add(($"{s.Name}: sem dados", s.Brush));
                continue;
            }

            var py = y(v);
            if (py >= plot.Top && py <= plot.Bottom)
            {
                dc.DrawEllipse(s.Brush, new Pen(TooltipBackground, 2), new Point(ToX(s.X[idx], plot, view), py), 4, 4);
            }

            lines.Add(($"{s.Name}: {v.ToString(s.Format, CultureInfo.CurrentCulture)} {s.Unit}".TrimEnd(), s.Brush));
        }

        foreach (var m in Markers ?? [])
        {
            if (Math.Abs(ToX(m.X, plot, view) - x) <= 4)
            {
                lines.Add(("● " + m.Label, m.Brush));
            }
        }

        var texts = lines.Select(l => ChartText.Create(this, l.Text, 11, l.Brush == LabelBrush ? LabelBrush : TextBrush)).ToList();
        var boxW = texts.Max(t => t.Width) + 28;
        var boxH = texts.Sum(t => t.Height + 2) + 14;
        var bx = x + 12 + boxW > plot.Right ? x - 12 - boxW : x + 12;
        var by = plot.Top + 6;
        dc.DrawRoundedRectangle(TooltipBackground, new Pen(GridBrush, 1), new Rect(bx, by, boxW, boxH), 8, 8);
        var ty = by + 7;
        for (var i = 0; i < texts.Count; i++)
        {
            if (i > 0)
            {
                dc.DrawEllipse(lines[i].Brush, null, new Point(bx + 12, ty + (texts[i].Height / 2)), 3.5, 3.5);
            }

            dc.DrawText(texts[i], new Point(bx + (i > 0 ? 22 : 10), ty));
            ty += texts[i].Height + 2;
        }
    }

    private static int Nearest(double[] x, double value)
    {
        if (x.Length == 0)
        {
            return -1;
        }

        var i = Array.BinarySearch(x, value);
        if (i >= 0)
        {
            return i;
        }

        i = ~i;
        if (i <= 0)
        {
            return 0;
        }

        if (i >= x.Length)
        {
            return x.Length - 1;
        }

        return value - x[i - 1] <= x[i] - value ? i - 1 : i;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = Plot;
        var view = View();
        var p = e.GetPosition(this);
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_selecting)
            {
                _selectionX = p.X;
                InvalidateVisual();
            }
            else
            {
                var dt = (p.X - start.X) / plot.Width * (_dragViewEnd - _dragViewStart);
                SetView(_dragViewStart - dt, _dragViewEnd - dt);
            }
        }

        HoverX = p.X >= plot.Left && p.X <= plot.Right ? FromX(p.X, plot, view) : double.NaN;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverX = double.NaN;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            SetView(0, 0);
            return;
        }

        var view = View();
        _dragStart = e.GetPosition(this);
        _selectionX = _dragStart.Value.X;
        _dragViewStart = view.Start;
        _dragViewEnd = view.End;
        _selecting = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        Cursor = _selecting ? Cursors.IBeam : Cursors.SizeWE;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_selecting && _dragStart is { } start)
        {
            var plot = Plot;
            var view = View();
            var x1 = Math.Min(start.X, _selectionX);
            var x2 = Math.Max(start.X, _selectionX);
            if (x2 - x1 > 6)
            {
                SetView(FromX(x1, plot, view), FromX(x2, plot, view));
            }
        }

        _dragStart = null;
        _selecting = false;
        Cursor = Cursors.Cross;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var plot = Plot;
        var view = View();
        var anchor = FromX(e.GetPosition(this).X, plot, view);
        var factor = e.Delta > 0 ? 0.8 : 1.25;
        SetView(anchor - ((anchor - view.Start) * factor), anchor + ((view.End - anchor) * factor));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var view = View();
        var span = view.End - view.Start;
        switch (e.Key)
        {
            case Key.Left:
                SetView(view.Start - (span * 0.1), view.End - (span * 0.1));
                e.Handled = true;
                break;
            case Key.Right:
                SetView(view.Start + (span * 0.1), view.End + (span * 0.1));
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                SetView(view.Start + (span * 0.1), view.End - (span * 0.1));
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                SetView(view.Start - (span * 0.125), view.End + (span * 0.125));
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Home:
                SetView(0, 0);
                e.Handled = true;
                break;
        }
    }

    private void SetView(double start, double end)
    {
        var total = Math.Max(TotalDuration, 1);
        if (end <= start || (start <= 0 && end >= total))
        {
            ViewStart = 0;
            ViewEnd = 0;
            return;
        }

        var span = Math.Clamp(end - start, Math.Min(MinSpanSeconds, total), total);
        if (start < 0)
        {
            start = 0;
        }

        if (start + span > total)
        {
            start = total - span;
        }

        ViewStart = start;
        ViewEnd = start + span;
    }
}
