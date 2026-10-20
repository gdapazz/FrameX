using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Framex.Core.Customization;

namespace Framex.App.Services;

/// <summary>Aplica tokens de tema aos recursos da aplicação. Toda a interface usa DynamicResource, então a troca é imediata.</summary>
public sealed class ThemeService
{
    public ThemeDefinition Active { get; private set; } = BuiltInThemes.Get(BuiltInThemes.DefaultId);

    public AnimationLevel Animations { get; private set; } = AnimationLevel.Full;

    public event EventHandler? ThemeApplied;

    public static Color ParseColor(string hex, Color fallback)
    {
        if (!ThemeDefinition.IsValidColor(hex))
        {
            return fallback;
        }

        var s = hex[1..];
        byte Read(int i) => byte.Parse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length == 8 ? Color.FromArgb(Read(0), Read(2), Read(4), Read(6)) : Color.FromRgb(Read(0), Read(2), Read(4));
    }

    public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    public void Apply(ThemeDefinition theme, AppSettings settings)
    {
        var t = theme.Clone();
        t.Validate();
        Active = t;
        Animations = settings.Animations == AnimationLevel.Off || t.Animations == AnimationLevel.Off
            ? AnimationLevel.Off
            : (AnimationLevel)Math.Min((int)settings.Animations, (int)t.Animations);

        var r = Application.Current.Resources;
        var fallback = Colors.Magenta;
        Color C(string hex) => ParseColor(hex, fallback);

        var background = C(t.Background);
        var primary = C(t.Primary);
        var accent = C(t.Accent);
        var accent2 = C(t.Accent2);
        var surface = WithAlpha(C(t.Surface), t.SurfaceOpacity);
        var surfaceAlt = WithAlpha(C(t.SurfaceAlt), t.SurfaceOpacity);

        r["C.Background"] = background;
        r["C.Primary"] = primary;
        r["C.Accent"] = accent;
        r["C.Accent2"] = accent2;

        Set(r, "B.Background", background);
        Set(r, "B.BackgroundAlt", C(t.BackgroundAlt));
        Set(r, "B.Surface", surface);
        Set(r, "B.SurfaceAlt", surfaceAlt);
        Set(r, "B.SurfaceHigh", C(t.SurfaceHigh));
        Set(r, "B.Border", C(t.Border));
        Set(r, "B.Primary", primary);
        Set(r, "B.Secondary", C(t.Secondary));
        Set(r, "B.Accent", accent);
        Set(r, "B.Accent2", accent2);
        Set(r, "B.Text", C(t.Text));
        Set(r, "B.TextMuted", C(t.TextMuted));
        Set(r, "B.Success", C(t.Success));
        Set(r, "B.Warning", C(t.Warning));
        Set(r, "B.Error", C(t.Error));
        Set(r, "B.PrimarySoft", WithAlpha(primary, 0.12));
        Set(r, "B.PrimaryHover", WithAlpha(primary, 0.2));

        var light = RelativeLuminance(C(t.Text)) < 0.3;
        Set(r, "B.Hover", light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x14, 255, 255, 255));
        Set(r, "B.Pressed", light ? Color.FromArgb(0x22, 0, 0, 0) : Color.FromArgb(0x24, 255, 255, 255));
        Set(r, "B.OnPrimary", RelativeLuminance(primary) > 0.35 ? Color.FromRgb(0x05, 0x0A, 0x12) : Colors.White);

        for (var i = 0; i < 6; i++)
        {
            var hex = t.ChartPalette.Count == 0 ? t.Primary : t.ChartPalette[i % t.ChartPalette.Count];
            Set(r, "B.Chart" + i, C(hex));
        }

        r["B.HeroGradient"] = Freeze(new LinearGradientBrush(primary, accent, new Point(0, 0), new Point(1, 1)));
        r["B.SurfaceGradient"] = Freeze(new LinearGradientBrush(surfaceAlt, surface, new Point(0, 0), new Point(0.4, 1)));
        var backdrop = new RadialGradientBrush(WithAlpha(primary, 0.1 * t.GlowIntensity / 0.55), Colors.Transparent)
        {
            Center = new Point(0.85, 0),
            GradientOrigin = new Point(0.85, 0),
            RadiusX = 0.9,
            RadiusY = 0.7,
        };
        r["B.Backdrop"] = Freeze(backdrop);

        r["R.Corner"] = new CornerRadius(t.CornerRadius);
        r["R.CornerSmall"] = new CornerRadius(Math.Max(0, t.CornerRadius * 0.66));
        r["D.Corner"] = t.CornerRadius;
        r["T.Border"] = new Thickness(t.BorderThickness);

        var compact = settings.Density == PanelDensity.Compact;
        var pad = (compact ? 12 : 18) * t.Spacing;
        var gap = (compact ? 10 : 14) * t.Spacing;
        r["T.Card"] = new Thickness(pad);
        r["T.CardCompact"] = new Thickness(pad * 0.66);
        r["T.Gap"] = new Thickness(0, 0, gap, gap);

        var fs = t.FontScale;
        r["S.Caption"] = 11 * fs;
        r["S.Body"] = 13 * fs;
        r["S.Subtitle"] = 15 * fs;
        r["S.Title"] = 22 * fs;
        r["S.Hero"] = 34 * fs;
        r["S.Display"] = 56 * fs;
        r["O.Glow"] = t.GlowIntensity;

        ThemeApplied?.Invoke(this, EventArgs.Empty);
    }

    private static void Set(ResourceDictionary r, string key, Color color) => r[key] = Freeze(new SolidColorBrush(color));

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    public static Brush GetBrush(string key) => Application.Current.Resources[key] as Brush ?? Brushes.Magenta;

    public static Color GetColor(string key) => GetBrush(key) is SolidColorBrush s ? s.Color : Colors.Magenta;
}
