using Framex.Core.Domain;

namespace Framex.Core.Alerts;

public enum AlertSubject
{
    /// <summary>Uma métrica canônica de hardware (usa <see cref="AlertRule.Metric"/>).</summary>
    Metric,
    /// <summary>VRAM utilizada / VRAM total, em %.</summary>
    VramPercent,
    /// <summary>FPS ao vivo (janela de 1 s).</summary>
    LiveFps,
    /// <summary>Maior frametime da última janela de 1 s, em ms.</summary>
    FrameTimeSpike,
    /// <summary>Sensor da métrica ficou indisponível.</summary>
    SensorUnavailable,
    /// <summary>Captura de quadros interrompida durante uma sessão.</summary>
    CaptureInterrupted,
    /// <summary>Jogo detectado por heurística, sem associação confirmada.</summary>
    UnconfirmedGame,
}

public enum AlertComparison
{
    Above,
    Below,
}

public enum AlertChannel
{
    None,
    Windows,
    Overlay,
    WindowsAndOverlay,
}

public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public AlertSubject Subject { get; set; }
    public MetricKind? Metric { get; set; }
    public AlertComparison Comparison { get; set; } = AlertComparison.Above;
    public double Threshold { get; set; }

    /// <summary>A condição precisa se manter por esse tempo antes de disparar.</summary>
    public TimeSpan MinDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Intervalo mínimo entre duas notificações da mesma regra.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Se definido, a regra só vale para este jogo.</summary>
    public Guid? GameId { get; set; }

    /// <summary>Se verdadeiro, só é avaliada durante sessões de jogo.</summary>
    public bool OnlyDuringSessions { get; set; } = true;
}

public sealed record AlertNotification(string RuleId, string Title, string Message, EventSeverity Severity, DateTimeOffset Timestamp, double? ObservedValue);

public sealed record AlertRecord(DateTimeOffset Timestamp, string RuleId, string Title, string Message, Guid? SessionId, string? GameName);

public static class DefaultAlertRules
{
    /// <summary>
    /// Regras iniciais propositalmente conservadoras e DESATIVADAS para temperatura: limites seguros dependem
    /// do componente e das especificações do fabricante, então o usuário deve definir os seus.
    /// </summary>
    public static List<AlertRule> Create() =>
    [
        new() { Id = "gpu-temp", Name = "Temperatura da GPU alta", Subject = AlertSubject.Metric, Metric = MetricKind.GpuTemperature, Threshold = 85, MinDuration = TimeSpan.FromSeconds(15), Enabled = false },
        new() { Id = "cpu-temp", Name = "Temperatura da CPU alta", Subject = AlertSubject.Metric, Metric = MetricKind.CpuTemperaturePackage, Threshold = 90, MinDuration = TimeSpan.FromSeconds(15), Enabled = false },
        new() { Id = "ram-high", Name = "RAM quase esgotada", Subject = AlertSubject.Metric, Metric = MetricKind.RamLoad, Threshold = 90, MinDuration = TimeSpan.FromSeconds(20) },
        new() { Id = "vram-high", Name = "VRAM próxima do limite", Subject = AlertSubject.VramPercent, Threshold = 95, MinDuration = TimeSpan.FromSeconds(20) },
        new() { Id = "fps-low", Name = "FPS abaixo do limite", Subject = AlertSubject.LiveFps, Comparison = AlertComparison.Below, Threshold = 30, MinDuration = TimeSpan.FromSeconds(10), Enabled = false },
        new() { Id = "ft-spikes", Name = "Picos de frametime", Subject = AlertSubject.FrameTimeSpike, Threshold = 100, MinDuration = TimeSpan.Zero, Cooldown = TimeSpan.FromMinutes(2), Enabled = false },
        new() { Id = "gpu-sensor", Name = "Sensor de temperatura da GPU indisponível", Subject = AlertSubject.SensorUnavailable, Metric = MetricKind.GpuTemperature, MinDuration = TimeSpan.FromSeconds(30), Cooldown = TimeSpan.FromMinutes(30) },
        new() { Id = "capture-lost", Name = "Captura de quadros interrompida", Subject = AlertSubject.CaptureInterrupted, MinDuration = TimeSpan.Zero, Cooldown = TimeSpan.FromMinutes(10) },
        new() { Id = "unconfirmed", Name = "Jogo detectado sem associação confirmada", Subject = AlertSubject.UnconfirmedGame, MinDuration = TimeSpan.Zero, Cooldown = TimeSpan.FromHours(1) },
    ];
}
