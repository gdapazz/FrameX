using System.Globalization;
using Framex.Core.Customization;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Monitoring;

namespace Framex.App.Services;

/// <summary>Formatação para exibição respeitando as unidades escolhidas. Ausência de valor é sempre "—", nunca zero.</summary>
public static class Fmt
{
    public const string Missing = "—";

    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static string Number(double? v, string format = "0") => v is { } x && double.IsFinite(x) ? x.ToString(format, C) : Missing;

    public static string Percent(double? v) => v is null ? Missing : Number(v) + "%";

    public static string Temperature(double? celsius, AppSettings s) =>
        celsius is null ? Missing : Number(UnitFormatter.ConvertTemperature(celsius.Value, s.TemperatureUnit)) + UnitFormatter.TemperatureSuffix(s.TemperatureUnit);

    public static string Memory(double? mib, AppSettings s, int decimals = 1) => mib is null ? Missing : UnitFormatter.FormatMemory(mib.Value, s.MemoryUnits, decimals);

    public static string Clock(double? mhz) => mhz is null ? Missing : mhz >= 1000 ? Number(mhz / 1000, "0.00") + " GHz" : Number(mhz) + " MHz";

    public static string Power(double? w) => w is null ? Missing : Number(w, w < 10 ? "0.0" : "0") + " W";

    public static string Fps(double? fps) => Number(fps);

    public static string Ms(double? ms) => ms is null ? Missing : Number(ms, "0.0") + " ms";

    public static string Duration(TimeSpan d) => UnitFormatter.FormatDuration(d);

    public static string Metric(MetricKind kind, double? value, AppSettings s)
    {
        if (value is null)
        {
            return Missing;
        }

        var d = MetricCatalog.Get(kind);
        return d.Unit switch
        {
            MetricUnit.Celsius => Temperature(value, s),
            MetricUnit.Megabyte => Memory(value, s),
            MetricUnit.Megahertz => Clock(value),
            MetricUnit.Watt => Power(value),
            MetricUnit.Percent => Percent(value),
            MetricUnit.Milliseconds => Ms(value),
            MetricUnit.Fps => Number(value, "0.0") + " FPS",
            MetricUnit.Rpm => Number(value) + " RPM",
            _ => UnitFormatter.Format(value, d.Unit, s.TemperatureUnit, s.MemoryUnits),
        };
    }

    public static string Availability(SensorAvailability a) => a switch
    {
        SensorAvailability.Available => "Disponível",
        SensorAvailability.Unavailable => "Indisponível",
        SensorAvailability.Unsupported => "Não suportado",
        SensorAvailability.Stale => "Desatualizado",
        SensorAvailability.Invalid => "Leitura inválida",
        _ => a.ToString(),
    };

    public static string Capture(CaptureState state) => state switch
    {
        CaptureState.NotConfigured => "PresentMon não configurado",
        CaptureState.Unavailable => "Captura indisponível",
        CaptureState.Starting => "Iniciando captura",
        CaptureState.Capturing => "Capturando quadros",
        CaptureState.WaitingForFrames => "Aguardando quadros",
        CaptureState.AccessDenied => "Sem permissão para captura",
        CaptureState.Stopped => "Captura parada",
        CaptureState.Error => "Erro na captura",
        CaptureState.Disabled => "Captura desativada",
        _ => state.ToString(),
    };

    public static string LocalTime(DateTimeOffset t) => t.ToLocalTime().ToString("dd/MM/yyyy HH:mm", C);

    /// <summary>Valor de exibição para um item do overlay ou do painel.</summary>
    public static (string Title, double? Value, string Text, double Fraction) Overlay(OverlayMetric metric, HardwareSnapshot snap, LiveFrameMetrics frames, AppSettings s)
    {
        double? V(MetricKind k) => LiveDataService.Val(snap, k);
        static double Frac(double? v, double max) => v is { } x && max > 0 ? Math.Clamp(x / max, 0, 1) : 0;
        switch (metric)
        {
            case OverlayMetric.Fps:
                return ("FPS", frames.Fps, Number(frames.Fps), Frac(frames.Fps, 240));
            case OverlayMetric.FrameTime:
                var ft = frames.Fps is > 0 ? 1000 / frames.Fps : null;
                return ("Frametime", ft, Ms(ft), Frac(ft, 50));
            case OverlayMetric.Low1Percent:
                return ("1% low", frames.Low1PercentFps, Number(frames.Low1PercentFps), Frac(frames.Low1PercentFps, 240));
            case OverlayMetric.FrameTimeGraph:
                return ("Frametime", frames.MaxFrameTimeMs, "máx " + Ms(frames.MaxFrameTimeMs), 0);
            case OverlayMetric.CpuLoad:
                return ("CPU", V(MetricKind.CpuLoad), Percent(V(MetricKind.CpuLoad)), Frac(V(MetricKind.CpuLoad), 100));
            case OverlayMetric.CpuTemperature:
                var ct = LiveDataService.CpuTemperatureOf(snap);
                return ("CPU °", ct, Temperature(ct, s), Frac(ct, 100));
            case OverlayMetric.CpuClock:
                return ("CPU clock", V(MetricKind.CpuClockAverage), Clock(V(MetricKind.CpuClockAverage)), 0);
            case OverlayMetric.CpuPower:
                return ("CPU W", V(MetricKind.CpuPower), Power(V(MetricKind.CpuPower)), 0);
            case OverlayMetric.GpuLoad:
                return ("GPU", V(MetricKind.GpuLoad), Percent(V(MetricKind.GpuLoad)), Frac(V(MetricKind.GpuLoad), 100));
            case OverlayMetric.GpuTemperature:
                return ("GPU °", V(MetricKind.GpuTemperature), Temperature(V(MetricKind.GpuTemperature), s), Frac(V(MetricKind.GpuTemperature), 100));
            case OverlayMetric.GpuHotspot:
                return ("Hotspot", V(MetricKind.GpuHotspot), Temperature(V(MetricKind.GpuHotspot), s), Frac(V(MetricKind.GpuHotspot), 110));
            case OverlayMetric.GpuClock:
                return ("GPU clock", V(MetricKind.GpuCoreClock), Clock(V(MetricKind.GpuCoreClock)), 0);
            case OverlayMetric.GpuPower:
                return ("GPU W", V(MetricKind.GpuPower), Power(V(MetricKind.GpuPower)), 0);
            case OverlayMetric.Ram:
                return ("RAM", V(MetricKind.RamUsed), Memory(V(MetricKind.RamUsed), s), Frac(V(MetricKind.RamUsed), V(MetricKind.RamTotal) ?? 0));
            case OverlayMetric.Vram:
                return ("VRAM", V(MetricKind.VramUsed), Memory(V(MetricKind.VramUsed), s), Frac(V(MetricKind.VramUsed), V(MetricKind.VramTotal) ?? 0));
            case OverlayMetric.Clock:
                var now = DateTime.Now;
                return ("Hora", now.Hour, now.ToString("HH:mm", C), 0);
            default:
                return (metric.ToString(), null, Missing, 0);
        }
    }

    public static string OverlayMetricName(OverlayMetric m) => m switch
    {
        OverlayMetric.Fps => "FPS",
        OverlayMetric.FrameTime => "Frametime médio",
        OverlayMetric.Low1Percent => "1% low (30 s)",
        OverlayMetric.FrameTimeGraph => "Gráfico de frametime",
        OverlayMetric.CpuLoad => "Uso da CPU",
        OverlayMetric.CpuTemperature => "Temperatura da CPU",
        OverlayMetric.CpuClock => "Clock da CPU",
        OverlayMetric.CpuPower => "Consumo da CPU",
        OverlayMetric.GpuLoad => "Uso da GPU",
        OverlayMetric.GpuTemperature => "Temperatura da GPU",
        OverlayMetric.GpuHotspot => "Hotspot da GPU",
        OverlayMetric.GpuClock => "Clock da GPU",
        OverlayMetric.GpuPower => "Consumo da GPU",
        OverlayMetric.Ram => "RAM em uso",
        OverlayMetric.Vram => "VRAM em uso",
        OverlayMetric.Clock => "Relógio",
        _ => m.ToString(),
    };
}
