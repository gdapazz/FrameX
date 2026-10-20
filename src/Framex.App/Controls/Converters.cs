using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Framex.App.Services;

namespace Framex.App.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is not null && (value is not string s || s.Length > 0);
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Compara um enum com o parâmetro (nome do valor). Usado em RadioButtons/segmentos.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>Visível quando o enum é igual ao parâmetro (nome do valor).</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public static EnumToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Verdadeiro quando os dois valores são o mesmo objeto (ou iguais).</summary>
public sealed class ReferenceEqualsConverter : IMultiValueConverter
{
    public static ReferenceEqualsConverter Instance { get; } = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>false → 0, true → 1 (ComboBox de duas opções).</summary>
public sealed class BoolToIndexConverter : IValueConverter
{
    public static BoolToIndexConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 1;
}

public sealed class LocalTimeConverter : IValueConverter
{
    public static LocalTimeConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset d ? Fmt.LocalTime(d) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Valores de retenção usados como Tag (int) nos itens do ComboBox.</summary>
public static class RetentionChoices
{
    public const int Forever = 0;
    public const int Days30 = 30;
    public const int Days90 = 90;
    public const int Days180 = 180;
    public const int Days365 = 365;
}

public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "";
    public string FalseText { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? TrueText : FalseText;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Converte "#RRGGBB" em pincel.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = new SolidColorBrush(ThemeService.ParseColor(value as string ?? "", Colors.Transparent));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToOrientationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? System.Windows.Controls.Orientation.Horizontal : System.Windows.Controls.Orientation.Vertical;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Multiplica um número pelo parâmetro.</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? d * f : value ?? 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Rótulo em português para valores de enum exibidos em listas de seleção.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static EnumLabelConverter Instance { get; } = new();

    public static string Label(object? value) => value switch
    {
        Framex.Core.Customization.OverlayMetric m => Fmt.OverlayMetricName(m),
        Framex.Core.Customization.OverlayStyle s => s switch
        {
            Framex.Core.Customization.OverlayStyle.Minimal => "Minimalista",
            Framex.Core.Customization.OverlayStyle.Compact => "Compacto",
            Framex.Core.Customization.OverlayStyle.Detailed => "Detalhado",
            Framex.Core.Customization.OverlayStyle.PerformancePanel => "Painel de desempenho",
            Framex.Core.Customization.OverlayStyle.FpsOnly => "Somente FPS",
            _ => "Personalizado",
        },
        Framex.Core.Customization.OverlayDisplay d => d switch
        {
            Framex.Core.Customization.OverlayDisplay.Bar => "Barra",
            Framex.Core.Customization.OverlayDisplay.Graph => "Gráfico",
            Framex.Core.Customization.OverlayDisplay.Ring => "Anel",
            _ => "Texto",
        },
        Framex.Core.Customization.GaugeStyle g => g switch
        {
            Framex.Core.Customization.GaugeStyle.Arc => "Arco",
            Framex.Core.Customization.GaugeStyle.Bar => "Barra",
            _ => "Anel",
        },
        Framex.Core.Customization.AnimationLevel a => a switch
        {
            Framex.Core.Customization.AnimationLevel.Off => "Desativadas",
            Framex.Core.Customization.AnimationLevel.Reduced => "Reduzidas",
            _ => "Completas",
        },
        Framex.Core.Customization.PanelDensity p => p == Framex.Core.Customization.PanelDensity.Compact ? "Compacta" : "Confortável",
        Framex.Core.Customization.PerformanceProfile p => Framex.Core.Customization.PerformanceTuning.DisplayName(p),
        Framex.Core.Customization.CloseBehavior c => c switch
        {
            Framex.Core.Customization.CloseBehavior.MinimizeToTray => "Minimizar para a bandeja",
            Framex.Core.Customization.CloseBehavior.Exit => "Encerrar o FRAMEX",
            _ => "Perguntar sempre",
        },
        Framex.Core.Monitoring.TemperatureUnit t => t == Framex.Core.Monitoring.TemperatureUnit.Fahrenheit ? "Fahrenheit (°F)" : "Celsius (°C)",
        Framex.Core.Monitoring.MemoryUnitConvention m => m == Framex.Core.Monitoring.MemoryUnitConvention.Decimal ? "Decimal (1 GB = 10⁹ bytes)" : "Binária (1 GB = 1024 MiB, como o Windows)",
        Framex.Core.Alerts.AlertChannel c => c switch
        {
            Framex.Core.Alerts.AlertChannel.None => "Somente registrar no relatório",
            Framex.Core.Alerts.AlertChannel.Overlay => "Overlay",
            Framex.Core.Alerts.AlertChannel.WindowsAndOverlay => "Windows e overlay",
            _ => "Notificação do Windows",
        },
        Framex.Core.Alerts.AlertComparison c => c == Framex.Core.Alerts.AlertComparison.Below ? "abaixo de" : "acima de",
        null => "",
        _ => value.ToString() ?? "",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Label(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visível quando a contagem é maior que zero (parâmetro "Invert" inverte).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public static CountToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int n && n > 0) ^ (parameter as string == "Invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Resolve uma chave de recurso (ex.: "I.Hardware") para o recurso da aplicação.</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static ResourceKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formata uma fração/escala (1.0) como percentual ("100%").</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? $"{d * 100:0}%" : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
