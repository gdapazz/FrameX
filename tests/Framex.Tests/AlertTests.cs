using Framex.Core.Alerts;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;

namespace Framex.Tests;

public class AlertTests
{
    private static AlertContext Ctx(DateTimeOffset now, double gpuTemp, bool inSession = true, Guid? game = null, double? fps = null) =>
        new(new HardwareSnapshot(now, new Dictionary<MetricKind, MetricValue> { [MetricKind.GpuTemperature] = MetricValue.Of(gpuTemp) }, [], [], null),
            new LiveFrameMetrics(fps, null, null, null, 0, null), inSession, game, now);

    private static AlertRule TempRule() => new()
    {
        Id = "t", Name = "GPU quente", Subject = AlertSubject.Metric, Metric = MetricKind.GpuTemperature, Threshold = 80,
        MinDuration = TimeSpan.FromSeconds(10), Cooldown = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public void FiresOnlyAfterMinimumDuration()
    {
        var engine = new AlertEngine();
        var rule = TempRule();
        var t = DateTimeOffset.UtcNow;
        Assert.Empty(engine.Evaluate([rule], Ctx(t, 85)));
        Assert.Empty(engine.Evaluate([rule], Ctx(t.AddSeconds(5), 85)));
        Assert.Single(engine.Evaluate([rule], Ctx(t.AddSeconds(10), 86)));
    }

    [Fact]
    public void ConditionInterruption_ResetsTimer()
    {
        var engine = new AlertEngine();
        var rule = TempRule();
        var t = DateTimeOffset.UtcNow;
        engine.Evaluate([rule], Ctx(t, 85));
        engine.Evaluate([rule], Ctx(t.AddSeconds(6), 70));
        Assert.Empty(engine.Evaluate([rule], Ctx(t.AddSeconds(12), 85)));
    }

    [Fact]
    public void Cooldown_SuppressesRepeatedNotifications()
    {
        var engine = new AlertEngine();
        var rule = TempRule();
        rule.MinDuration = TimeSpan.Zero;
        var t = DateTimeOffset.UtcNow;
        Assert.Single(engine.Evaluate([rule], Ctx(t, 90)));
        Assert.Empty(engine.Evaluate([rule], Ctx(t.AddSeconds(30), 90)));
        Assert.Single(engine.Evaluate([rule], Ctx(t.AddSeconds(61), 90)));
    }

    [Fact]
    public void DisabledOrOutOfSession_DoesNotFire()
    {
        var engine = new AlertEngine();
        var rule = TempRule();
        rule.MinDuration = TimeSpan.Zero;
        Assert.Empty(engine.Evaluate([rule], Ctx(DateTimeOffset.UtcNow, 95, inSession: false)));
        rule.Enabled = false;
        Assert.Empty(engine.Evaluate([rule], Ctx(DateTimeOffset.UtcNow, 95)));
    }

    [Fact]
    public void GameSpecificRule_OnlyAppliesToThatGame()
    {
        var engine = new AlertEngine();
        var rule = TempRule();
        rule.MinDuration = TimeSpan.Zero;
        rule.GameId = Guid.NewGuid();
        Assert.Empty(engine.Evaluate([rule], Ctx(DateTimeOffset.UtcNow, 95, game: Guid.NewGuid())));
        Assert.Single(engine.Evaluate([rule], Ctx(DateTimeOffset.UtcNow, 95, game: rule.GameId)));
    }

    [Fact]
    public void FpsBelow_UsesLiveFrames_AndIgnoresMissingFps()
    {
        var engine = new AlertEngine();
        var rule = new AlertRule { Id = "f", Name = "FPS", Subject = AlertSubject.LiveFps, Comparison = AlertComparison.Below, Threshold = 30, MinDuration = TimeSpan.Zero };
        Assert.Empty(engine.Evaluate([rule], Ctx(DateTimeOffset.UtcNow, 50, fps: null)));
        Assert.Single(engine.Evaluate([rule], Ctx(DateTimeOffset.UtcNow, 50, fps: 20)));
    }

    [Fact]
    public void VramPercent_ComputedFromUsedAndTotal()
    {
        var engine = new AlertEngine();
        var rule = new AlertRule { Id = "v", Name = "VRAM", Subject = AlertSubject.VramPercent, Threshold = 90, MinDuration = TimeSpan.Zero };
        var now = DateTimeOffset.UtcNow;
        var snap = new HardwareSnapshot(now, new Dictionary<MetricKind, MetricValue>
        {
            [MetricKind.VramUsed] = MetricValue.Of(5800),
            [MetricKind.VramTotal] = MetricValue.Of(6144),
        }, [], [], null);
        var fired = engine.Evaluate([rule], new AlertContext(snap, default, true, null, now));
        Assert.InRange(Assert.Single(fired).ObservedValue!.Value, 94, 95);
    }

    [Fact]
    public void EventAlerts_RespectCooldown()
    {
        var engine = new AlertEngine();
        var rules = DefaultAlertRules.Create();
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(engine.RaiseEvent(rules, AlertSubject.CaptureInterrupted, "x", null, now));
        Assert.Null(engine.RaiseEvent(rules, AlertSubject.CaptureInterrupted, "x", null, now.AddMinutes(1)));
    }

    [Fact]
    public void DefaultTemperatureRules_AreDisabled_UserMustChooseLimits()
    {
        var rules = DefaultAlertRules.Create();
        Assert.All(rules.Where(r => r.Metric is MetricKind.GpuTemperature or MetricKind.CpuTemperaturePackage && r.Subject == AlertSubject.Metric),
            r => Assert.False(r.Enabled));
    }
}
