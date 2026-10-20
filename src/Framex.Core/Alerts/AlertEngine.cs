using System.Globalization;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Monitoring;

namespace Framex.Core.Alerts;

public sealed record AlertContext(HardwareSnapshot Snapshot, LiveFrameMetrics Frames, bool InSession, Guid? GameId, DateTimeOffset Now);

/// <summary>
/// Avalia regras de alerta com histerese temporal: a condição deve permanecer verdadeira por
/// <see cref="AlertRule.MinDuration"/> e respeitar o <see cref="AlertRule.Cooldown"/>.
/// </summary>
public sealed class AlertEngine
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly Dictionary<string, DateTimeOffset> _conditionSince = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new();

    public IReadOnlyList<AlertNotification> Evaluate(IEnumerable<AlertRule> rules, AlertContext ctx)
    {
        var fired = new List<AlertNotification>();
        foreach (var rule in rules)
        {
            if (!IsApplicable(rule, ctx))
            {
                _conditionSince.Remove(rule.Id);
                continue;
            }

            var (met, observed) = Check(rule, ctx);
            if (!met)
            {
                _conditionSince.Remove(rule.Id);
                continue;
            }

            if (!_conditionSince.TryGetValue(rule.Id, out var since))
            {
                since = ctx.Now;
                _conditionSince[rule.Id] = since;
            }

            if (ctx.Now - since < rule.MinDuration)
            {
                continue;
            }

            if (TryFire(rule, ctx.Now))
            {
                fired.Add(new AlertNotification(rule.Id, rule.Name, Describe(rule, observed), SeverityOf(rule), ctx.Now, observed));
            }
        }

        return fired;
    }

    /// <summary>Dispara um alerta de evento (captura interrompida, jogo não confirmado), respeitando o cooldown.</summary>
    public AlertNotification? RaiseEvent(IEnumerable<AlertRule> rules, AlertSubject subject, string message, Guid? gameId, DateTimeOffset now)
    {
        var rule = rules.FirstOrDefault(r => r.Enabled && r.Subject == subject && (r.GameId is null || r.GameId == gameId));
        if (rule is null || !TryFire(rule, now))
        {
            return null;
        }

        return new AlertNotification(rule.Id, rule.Name, message, EventSeverity.Warning, now, null);
    }

    public void Reset()
    {
        _conditionSince.Clear();
        _lastFired.Clear();
    }

    private bool TryFire(AlertRule rule, DateTimeOffset now)
    {
        if (_lastFired.TryGetValue(rule.Id, out var last) && now - last < rule.Cooldown)
        {
            return false;
        }

        _lastFired[rule.Id] = now;
        return true;
    }

    private static bool IsApplicable(AlertRule rule, AlertContext ctx) =>
        rule.Enabled
        && rule.Subject is not (AlertSubject.CaptureInterrupted or AlertSubject.UnconfirmedGame)
        && (!rule.OnlyDuringSessions || ctx.InSession)
        && (rule.GameId is null || rule.GameId == ctx.GameId);

    private static (bool Met, double? Observed) Check(AlertRule rule, AlertContext ctx)
    {
        switch (rule.Subject)
        {
            case AlertSubject.Metric when rule.Metric is { } metric:
            {
                var v = ctx.Snapshot[metric];
                return v.HasValue ? (Compare(v.Value!.Value, rule), v.Value) : (false, null);
            }

            case AlertSubject.VramPercent:
            {
                var used = ctx.Snapshot[MetricKind.VramUsed];
                var total = ctx.Snapshot[MetricKind.VramTotal];
                if (!used.HasValue || !total.HasValue || total.Value <= 0)
                {
                    return (false, null);
                }

                var pct = used.Value!.Value / total.Value!.Value * 100;
                return (Compare(pct, rule), pct);
            }

            case AlertSubject.LiveFps:
                return ctx.Frames.Fps is { } fps ? (Compare(fps, rule), fps) : (false, null);

            case AlertSubject.FrameTimeSpike:
                return ctx.Frames.MaxFrameTimeMs is { } ft ? (Compare(ft, rule), ft) : (false, null);

            case AlertSubject.SensorUnavailable when rule.Metric is { } metric:
            {
                var v = ctx.Snapshot[metric];
                return (v.Availability is SensorAvailability.Unavailable or SensorAvailability.Invalid, null);
            }

            default:
                return (false, null);
        }
    }

    private static bool Compare(double value, AlertRule rule) =>
        rule.Comparison == AlertComparison.Above ? value > rule.Threshold : value < rule.Threshold;

    private static EventSeverity SeverityOf(AlertRule rule) =>
        rule.Subject is AlertSubject.SensorUnavailable ? EventSeverity.Info : EventSeverity.Warning;

    private static string Describe(AlertRule rule, double? observed)
    {
        var obs = observed is { } o ? o.ToString("0.#", Pt) : "—";
        var dir = rule.Comparison == AlertComparison.Above ? "acima de" : "abaixo de";
        var dur = rule.MinDuration.TotalSeconds >= 1 ? $" por {rule.MinDuration.TotalSeconds:0} s" : "";
        return rule.Subject switch
        {
            AlertSubject.Metric when rule.Metric is { } m =>
                $"{MetricCatalog.Get(m).Label}: {obs} {UnitFormatter.UnitSuffix(MetricCatalog.Get(m).Unit)} ({dir} {rule.Threshold.ToString("0.#", Pt)}{dur}).",
            AlertSubject.VramPercent => $"VRAM em {obs}% ({dir} {rule.Threshold:0}%{dur}).",
            AlertSubject.LiveFps => $"FPS em {obs} ({dir} {rule.Threshold:0}{dur}).",
            AlertSubject.FrameTimeSpike => $"Pico de frametime de {obs} ms (limite {rule.Threshold:0} ms).",
            AlertSubject.SensorUnavailable when rule.Metric is { } m => $"{MetricCatalog.Get(m).Label} sem leitura{dur}.",
            _ => rule.Name,
        };
    }
}
