using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Framex.Core.Domain;

namespace Framex.App.Controls;

/// <summary>Distribuição de frametimes (contagem de quadros por faixa em ms), com destaque da faixa sob o cursor.</summary>
public sealed class HistogramChart : FrameworkElement
{
    public static readonly DependencyProperty BinsProperty = DependencyProperty.Register(nameof(Bins), typeof(IReadOnlyList<HistogramBin>), typeof(HistogramChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(nameof(TooltipBackground), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private int _hover = -1;

    public IReadOnlyList<HistogramBin>? Bins { get => (IReadOnlyList<HistogramBin>?)GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush TooltipBackground { get => (Brush)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var bins = Bins;
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        if (bins is null || bins.Count == 0 || w < 40 || h < 40)
        {
            return;
        }

        var plotH = h - 20;
        var max = Math.Max(1, bins.Max(b => b.Count));
        var total = Math.Max(1, bins.Sum(b => b.Count));
        var barW = w / bins.Count;
        for (var i = 0; i < bins.Count; i++)
        {
            var bh = bins[i].Count == 0 ? 0 : Math.Max(2, bins[i].Count / (double)max * (plotH - 4));
            var rect = new Rect((i * barW) + 1, plotH - bh, Math.Max(1, barW - 2), bh);
            dc.PushOpacity(i == _hover ? 1 : 0.75);
            dc.DrawRoundedRectangle(BarBrush, null, rect, 2, 2);
            dc.Pop();
        }

        var step = Math.Max(1, bins.Count / 6);
        for (var i = 0; i < bins.Count; i += step)
        {
            var label = ChartText.Create(this, bins[i].FromMs.ToString("0.#", CultureInfo.CurrentCulture), 10, LabelBrush);
            dc.DrawText(label, new Point(i * barW, plotH + 4));
        }

        var unit = ChartText.Create(this, "ms", 10, LabelBrush);
        dc.DrawText(unit, new Point(w - unit.Width, plotH + 4));

        if (_hover >= 0 && _hover < bins.Count)
        {
            var b = bins[_hover];
            var text = ChartText.Create(this,
                $"{b.FromMs.ToString("0.#", CultureInfo.CurrentCulture)}–{b.ToMs.ToString("0.#", CultureInfo.CurrentCulture)} ms\n{b.Count:N0} quadros ({b.Count * 100.0 / total:0.##}%)", 11, TextBrush);
            var x = Math.Clamp((_hover * barW) - (text.Width / 2), 0, Math.Max(0, w - text.Width - 16));
            dc.DrawRoundedRectangle(TooltipBackground, null, new Rect(x, 0, text.Width + 16, text.Height + 10), 6, 6);
            dc.DrawText(text, new Point(x + 8, 5));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var bins = Bins;
        var index = bins is { Count: > 0 } ? (int)(e.GetPosition(this).X / (ActualWidth / bins.Count)) : -1;
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }
}
