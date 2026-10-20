using Framex.Core.Domain;

namespace Framex.Core.Monitoring;

/// <summary>Rejeita valores fisicamente implausíveis antes que cheguem à interface ou ao histórico.</summary>
public static class SensorValidator
{
    public static (double Min, double Max) RangeFor(MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => (0, 100.5),
        MetricUnit.Celsius => (-40, 150),
        MetricUnit.Megahertz => (0, 15_000),
        MetricUnit.Watt => (0, 3_000),
        MetricUnit.Rpm => (0, 25_000),
        MetricUnit.Megabyte => (0, 64_000_000),
        MetricUnit.Fps => (0, 10_000),
        MetricUnit.Milliseconds => (0, 60_000),
        _ => (double.MinValue, double.MaxValue),
    };

    public static MetricValue Validate(double? raw, MetricUnit unit, string? sourceId = null)
    {
        if (raw is null)
        {
            return new MetricValue(null, SensorAvailability.Unavailable, "O sensor não retornou valor.", sourceId);
        }

        var value = raw.Value;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return new MetricValue(null, SensorAvailability.Invalid, "Valor inválido (NaN/infinito).", sourceId);
        }

        var (min, max) = RangeFor(unit);
        if (value < min || value > max)
        {
            return new MetricValue(null, SensorAvailability.Invalid, $"Valor fora da faixa plausível ({value:0.##}).", sourceId);
        }

        if (unit == MetricUnit.Percent)
        {
            value = Math.Clamp(value, 0, 100);
        }

        return MetricValue.Of(value, sourceId);
    }
}
