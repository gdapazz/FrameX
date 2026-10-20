using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Framex.App.Controls;

internal static class ChartText
{
    public static Typeface Typeface(FrameworkElement owner, bool bold = false) =>
        new(owner.TryFindResource("F.Body") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);

    public static FormattedText Create(FrameworkElement owner, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface(owner, bold), size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip);

    public static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
