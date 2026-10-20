using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.GameDetection;
using Framex.Core.SessionTracking;
using Framex.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests;

/// <summary>Integração: detecção → sessão → persistência SQLite → resumo, com provedores simulados.</summary>
public class SessionRecorderTests
{
    private sealed class Env : ISessionEnvironment
    {
        public string AppVersion => "0.1.0-test";
        public string? PrimaryResolution() => "1920×1080";
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TempDatabase Db { get; } = new();
        public ManualTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeSeconds(1_750_000_000));
        public FakeProcesses Processes { get; } = new();
        public InMemoryGames Games { get; } = new();
        public FakeHardwareMonitor Hardware { get; } = new();
        public FakeCapture Capture { get; } = new();
        public SqliteSessionRepository Sessions { get; }
        public GameDetectionService Detection { get; }
        public SessionRecorder Recorder { get; }
        public Game Game { get; } = new() { Name = "Jogo de Teste", Executables = [new GameExecutable("teste.exe", null)] };

        public Rig(TimeSpan? minDuration = null)
        {
            Games.Games.Add(Game);
            Sessions = new SqliteSessionRepository(Db.Database);
            Detection = new GameDetectionService(Processes, Games, new GameDetectionOptions { ExitGracePeriod = TimeSpan.Zero }, Time, NullLogger<GameDetectionService>.Instance);
            Recorder = new SessionRecorder(Hardware, Capture, Detection, Sessions, new LiveFrameAggregator(Time), new Env(),
                new SessionRecorderOptions { MinSessionDuration = minDuration ?? TimeSpan.FromSeconds(5), FlushInterval = TimeSpan.FromHours(1) },
                Time, NullLogger<SessionRecorder>.Instance);
            Recorder.Attach();
        }

        public async ValueTask DisposeAsync()
        {
            await Recorder.DisposeAsync();
            Db.Dispose();
        }
    }

    private static Task<T> Next<T>(Action<EventHandler<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe((_, e) => tcs.TrySetResult(e));
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FullSession_IsRecordedProgressively_AndSummarized()
    {
        await using var rig = new Rig();
        var started = Next<Session>(h => rig.Recorder.SessionStarted += h);
        var completed = Next<Session>(h => rig.Recorder.SessionCompleted += h);

        rig.Processes.Processes.Add(FakeProcesses.Proc(4242, "teste.exe"));
        rig.Detection.Tick();
        var session = await started;
        Assert.Equal(4242, rig.Capture.LastPid);

        for (var sec = 0; sec < 120; sec++)
        {
            var t = rig.Time.Now;
            rig.Hardware.Publish(t, (MetricKind.CpuLoad, 40 + (sec % 10)), (MetricKind.GpuTemperature, 60 + (sec / 10.0)), (MetricKind.RamTotal, 16384));
            var frames = Enumerable.Range(0, 100).Select(i => new FrameSample(t.AddMilliseconds(i * 10), sec == 60 && i == 0 ? 100 : 10)).ToList();
            rig.Capture.Emit(frames);
            if (sec == 30)
            {
                rig.Recorder.Flush();
                Assert.NotEmpty(rig.Sessions.GetSamples(session.Id)); // gravação progressiva
            }

            rig.Time.Advance(TimeSpan.FromSeconds(1));
        }

        rig.Processes.Processes.Clear();
        rig.Detection.Tick();
        var done = await completed;

        var stored = rig.Sessions.Get(done.Id)!;
        Assert.Equal(SessionState.Completed, stored.State);
        Assert.Equal("1920×1080", stored.Resolution);
        var sum = stored.Summary!;
        Assert.Equal(12_000, sum.Frames!.FrameCount);
        Assert.InRange(sum.Frames.AverageFps!.Value, 99, 100);
        Assert.NotNull(sum.Frames.Low1PercentFps);
        Assert.Equal(1, rig.Sessions.GetSamples(done.Id).Count(s => s.Metric == MetricKind.RamTotal)); // métricas estáticas não se repetem
        Assert.Equal(71.9, sum.Get(MetricKind.GpuTemperature)!.Maximum!.Value, 3);
        Assert.Contains(sum.Limitations, l => l.Contains("Temperatura da CPU", StringComparison.Ordinal));
        var events = rig.Sessions.GetEvents(done.Id);
        Assert.Contains(events, e => e.Type == SessionEventType.SessionStarted);
        Assert.Contains(events, e => e.Type == SessionEventType.SessionEnded);
    }

    [Fact]
    public async Task ShortSession_IsDiscarded()
    {
        await using var rig = new Rig(TimeSpan.FromMinutes(1));
        var started = Next<Session>(h => rig.Recorder.SessionStarted += h);
        var discarded = Next<Session>(h => rig.Recorder.SessionDiscarded += h);
        rig.Processes.Processes.Add(FakeProcesses.Proc(1, "teste.exe"));
        rig.Detection.Tick();
        var s = await started;
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Processes.Processes.Clear();
        rig.Detection.Tick();
        await discarded;
        Assert.Null(rig.Sessions.Get(s.Id));
    }

    [Fact]
    public async Task InterruptedSession_IsRecoveredAsIncomplete()
    {
        await using var rig = new Rig();
        var started = Next<Session>(h => rig.Recorder.SessionStarted += h);
        rig.Processes.Processes.Add(FakeProcesses.Proc(1, "teste.exe"));
        rig.Detection.Tick();
        var s = await started;
        for (var i = 0; i < 40; i++)
        {
            rig.Hardware.Publish(rig.Time.Now, (MetricKind.CpuLoad, 50));
            rig.Time.Advance(TimeSpan.FromSeconds(1));
        }

        rig.Recorder.Flush();
        var lastData = rig.Sessions.GetLastDataTimestamp(s.Id);

        // Simula falha: um novo processo do FRAMEX abre o mesmo banco e encontra a sessão ainda ativa.
        var recorderAfterCrash = new SessionRecorder(rig.Hardware, rig.Capture, rig.Detection, rig.Sessions, new LiveFrameAggregator(rig.Time), new Env(),
            new SessionRecorderOptions(), rig.Time, NullLogger<SessionRecorder>.Instance);
        Assert.Equal(1, recorderAfterCrash.RecoverInterruptedSessions());

        var recovered = rig.Sessions.Get(s.Id)!;
        Assert.Equal(SessionState.Incomplete, recovered.State);
        Assert.Equal(lastData, recovered.EndedAt);
        Assert.NotNull(recovered.Summary);
        Assert.Contains(rig.Sessions.GetEvents(s.Id), e => e.Type == SessionEventType.Recovered);
        Assert.Contains(recovered.Summary!.Limitations, l => l.Contains("quadros", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CaptureFailure_DoesNotStopHardwareRecording()
    {
        await using var rig = new Rig();
        var started = Next<Session>(h => rig.Recorder.SessionStarted += h);
        var interrupted = Next<string>(h => rig.Recorder.CaptureInterrupted += h);
        rig.Processes.Processes.Add(FakeProcesses.Proc(1, "teste.exe"));
        rig.Detection.Tick();
        var s = await started;
        rig.Capture.SetStatus(new CaptureStatus(CaptureState.Error, "PresentMon caiu"));
        await interrupted;
        rig.Hardware.Publish(rig.Time.Now, (MetricKind.GpuLoad, 99));
        rig.Recorder.Flush();
        Assert.Contains(rig.Sessions.GetSamples(s.Id), x => x.Metric == MetricKind.GpuLoad);
        Assert.Contains(rig.Sessions.GetEvents(s.Id), e => e.Type == SessionEventType.CaptureStateChanged && e.Message == "PresentMon caiu");
    }

    [Fact]
    public async Task LongSession_KeepsBuffersBounded()
    {
        await using var rig = new Rig();
        rig.Recorder.Options.MaxBufferedFrames = 1000;
        var started = Next<Session>(h => rig.Recorder.SessionStarted += h);
        rig.Processes.Processes.Add(FakeProcesses.Proc(1, "teste.exe"));
        rig.Detection.Tick();
        var s = await started;
        for (var i = 0; i < 50; i++)
        {
            rig.Capture.Emit(Enumerable.Range(0, 100).Select(k => new FrameSample(rig.Time.Now.AddMilliseconds(k), 5)).ToList());
            rig.Time.Advance(TimeSpan.FromSeconds(1));
        }

        rig.Recorder.Flush();
        Assert.Equal(1000, rig.Sessions.GetFrameTimes(s.Id).Count);
        Assert.Contains(rig.Sessions.GetEvents(s.Id), e => e.Type == SessionEventType.SamplesDropped);
    }
}
