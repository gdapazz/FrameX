using System.Windows;
using System.Windows.Controls;

namespace Framex.App.Controls;

/// <summary>Grade responsiva do painel: colunas conforme a largura disponível; itens "largos" ocupam duas colunas.</summary>
public sealed class DashboardPanel : Panel
{
    public static readonly DependencyProperty WideProperty = DependencyProperty.RegisterAttached(
        "Wide", typeof(bool), typeof(DashboardPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(DashboardPanel), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(DashboardPanel), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static bool GetWide(DependencyObject d) => (bool)d.GetValue(WideProperty);

    public static void SetWide(DependencyObject d, bool value) => d.SetValue(WideProperty, value);

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private readonly List<Rect> _slots = [];

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * 4 : available.Width;
        var columns = Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));
        // Itens largos ocupam duas colunas: com 3 colunas sobraria um vão ao lado de cada um.
        columns = columns >= 4 ? 4 : columns == 3 ? 2 : columns;
        var colW = (width - (Spacing * (columns - 1))) / columns;

        _slots.Clear();
        var col = 0;
        var y = 0.0;
        var rowH = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                _slots.Add(Rect.Empty);
                continue;
            }

            var span = GetWide(child) ? Math.Min(2, columns) : 1;
            if (col + span > columns)
            {
                y += rowH + Spacing;
                col = 0;
                rowH = 0;
            }

            var w = (colW * span) + (Spacing * (span - 1));
            child.Measure(new Size(w, double.PositiveInfinity));
            _slots.Add(new Rect(col * (colW + Spacing), y, w, child.DesiredSize.Height));
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            col += span;
        }

        // Itens da mesma linha recebem a mesma altura.
        for (var i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].IsEmpty)
            {
                continue;
            }

            var top = _slots[i].Top;
            var maxH = _slots.Where(s => !s.IsEmpty && Math.Abs(s.Top - top) < 0.5).Max(s => s.Height);
            _slots[i] = new Rect(_slots[i].X, top, _slots[i].Width, maxH);
        }

        return new Size(width, _slots.Count == 0 ? 0 : y + rowH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            var slot = i < _slots.Count ? _slots[i] : Rect.Empty;
            child.Arrange(slot.IsEmpty ? new Rect(0, 0, 0, 0) : slot);
            i++;
        }

        return finalSize;
    }
}
