using System.Globalization;
using Framex.Core.Domain;

namespace Framex.Core.Monitoring;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

/// <summary>Internamente todas as memórias são armazenadas em MiB (2^20 bytes).</summary>
public enum MemoryUnitConvention
{
    /// <summary>GiB = 1024 MiB (convenção usada pelo Windows, embora rotulada como "GB").</summary>
    Binary,
    /// <summary>GB = 10^9 bytes (convenção dos fabricantes de armazenamento).</summary>
    Decimal,
}

public static class UnitFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static double CelsiusToFahrenheit(double celsius) => (celsius * 9 / 5) + 32;

    public static double ConvertTemperature(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? CelsiusToFahrenheit(celsius) : celsius;

    public static string TemperatureSuffix(TemperatureUnit unit) => unit == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    public static double MebibytesToGigabytes(double mib, MemoryUnitConvention convention) =>
        convention == MemoryUnitConvention.Binary ? mib / 1024.0 : mib * 1_048_576.0 / 1_000_000_000.0;

    public static double BytesToMebibytes(double bytes) => bytes / 1_048_576.0;

    public static string MemorySuffix(MemoryUnitConvention convention) => convention == MemoryUnitConvention.Binary ? "GiB" : "GB";

    public static string FormatMemory(double mib, MemoryUnitConvention convention, int decimals = 1)
    {
        if (mib < 1024 && convention == MemoryUnitConvention.Binary)
        {
            return mib.ToString("0", Culture) + " MiB";
        }

        return MebibytesToGigabytes(mib, convention).ToString("N" + decimals, Culture) + " " + MemorySuffix(convention);
    }

    public static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1_048_576 => (bytesPerSecond / 1_048_576).ToString("0.0", Culture) + " MiB/s",
        >= 1024 => (bytesPerSecond / 1024).ToString("0", Culture) + " KiB/s",
        _ => bytesPerSecond.ToString("0", Culture) + " B/s",
    };

    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1)
        {
            return $"{(int)d.TotalHours}h {d.Minutes:00}min";
        }

        return d.TotalMinutes >= 1 ? $"{d.Minutes}min {d.Seconds:00}s" : $"{d.Seconds}s";
    }

    public static string UnitSuffix(MetricUnit unit, TemperatureUnit temp = TemperatureUnit.Celsius) => unit switch
    {
        MetricUnit.Percent => "%",
        MetricUnit.Celsius => TemperatureSuffix(temp),
        MetricUnit.Megahertz => "MHz",
        MetricUnit.Watt => "W",
        MetricUnit.Rpm => "RPM",
        MetricUnit.Megabyte => "MiB",
        MetricUnit.Fps => "FPS",
        MetricUnit.Milliseconds => "ms",
        MetricUnit.Volt => "V",
        MetricUnit.Ampere => "A",
        MetricUnit.Gigabyte => "GiB",
        MetricUnit.BytesPerSecond => "B/s",
        _ => "",
    };

    /// <summary>Formata um valor na unidade de exibição escolhida (temperatura convertida quando necessário).</summary>
    public static string Format(double? value, MetricUnit unit, TemperatureUnit temp = TemperatureUnit.Celsius, MemoryUnitConvention mem = MemoryUnitConvention.Binary)
    {
        if (value is null)
        {
            return "—";
        }

        var v = value.Value;
        return unit switch
        {
            MetricUnit.Celsius => ConvertTemperature(v, temp).ToString("0", Culture) + " " + TemperatureSuffix(temp),
            MetricUnit.Megabyte => FormatMemory(v, mem),
            MetricUnit.Percent => v.ToString("0", Culture) + " %",
            MetricUnit.Milliseconds => v.ToString("0.0", Culture) + " ms",
            MetricUnit.Fps => v.ToString("0", Culture),
            MetricUnit.Watt => v.ToString("0.0", Culture) + " W",
            MetricUnit.Megahertz => v.ToString("0", Culture) + " MHz",
            MetricUnit.Rpm => v.ToString("0", Culture) + " RPM",
            MetricUnit.Volt => v.ToString("0.000", Culture) + " V",
            MetricUnit.Ampere => v.ToString("0.00", Culture) + " A",
            MetricUnit.Gigabyte => v.ToString("0.00", Culture) + " GiB",
            MetricUnit.BytesPerSecond => FormatRate(v),
            _ => v.ToString("0.##", Culture),
        };
    }
}
