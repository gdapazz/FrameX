using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.GameDetection;
using Framex.Core.Monitoring;
using Framex.Core.Persistence;
using Framex.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests;

/// <summary>Relógio controlado manualmente para testes determinísticos.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "framex-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path_ = System.IO.Path.Combine(Directory, "framex.db");
        Database = new SqliteDatabase(Path_, NullLogger<SqliteDatabase>.Instance);
        Database.Initialize();
    }

    public string Directory { get; }
    public string Path_ { get; }
    public SqliteDatabase Database { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Arquivos temporários; o SO limpa depois.
        }
    }
}

internal sealed class FakeProcesses : IProcessSnapshotProvider
{
    public List<ProcessInfo> Processes { get; } = [];

    public IReadOnlyList<ProcessInfo> GetProcesses() => Processes.ToList();

    public static ProcessInfo Proc(int pid, string exe, string? path = null, bool window = true, long ws = 500_000_000) =>
        new(pid, exe, path ?? $@"C:\Games\{Path.GetFileNameWithoutExtension(exe)}\{exe}", window, window ? "Janela" : null, null, ws);
}

internal sealed class InMemoryGames : IGameRepository
{
    public List<Game> Games { get; } = [];
    public HashSet<string> Ignored { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Game> GetAll(bool includeRemoved = false) => Games.Where(g => includeRemoved || g.Status != GameStatus.Removed).ToList();
    public Game? Get(Guid id) => Games.FirstOrDefault(g => g.Id == id);

    public void Upsert(Game game)
    {
        Games.RemoveAll(g => g.Id == game.Id);
        Games.Add(game);
    }

    public IReadOnlyDictionary<Guid, GameStatistics> GetStatistics() => new Dictionary<Guid, GameStatistics>();
    public IReadOnlySet<string> GetIgnoredExecutables() => Ignored;

    public void SetIgnored(string executableName, bool ignored)
    {
        if (ignored)
        {
            Ignored.Add(executableName);
        }
        else
        {
            Ignored.Remove(executableName);
        }
    }
}

/// <summary>Monitor de hardware controlado pelo teste (dados simulados, isolados da produção).</summary>
internal sealed class FakeHardwareMonitor : IHardwareMonitor
{
    public HardwareSnapshot Latest { get; private set; } = HardwareSnapshot.Empty;
    public HardwareInventory Inventory { get; set; } = new(DateTimeOffset.UtcNow, [new HardwareComponentInfo("cpu", ComponentKind.Cpu, "CPU de teste", [])]);
    public bool IsRunning => true;
    public bool IsPaused => false;
    public MonitoringOptions Options { get; } = new();
    public IReadOnlyList<ProviderHealth> ProviderHealth => [];
    public event EventHandler<HardwareSnapshot>? SnapshotAvailable;

    public void Publish(DateTimeOffset at, params (MetricKind Kind, double Value)[] values)
    {
        var metrics = values.ToDictionary(v => v.Kind, v => MetricValue.Of(v.Value));
        Latest = new HardwareSnapshot(at, metrics, [], [], null);
        SnapshotAvailable?.Invoke(this, Latest);
    }

    public void Start() { }
    public Task StopAsync() => Task.CompletedTask;
    public void Pause() { }
    public void Resume() { }
    public void RefreshInventory() { }
}

internal sealed class FakeCapture : IFrameCaptureProvider
{
    public string Name => "Fake";
    public CaptureStatus Status { get; private set; } = CaptureStatus.Idle;
    public int StartCount { get; private set; }
    public int? LastPid { get; private set; }
    public event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;
    public event EventHandler<CaptureStatus>? StatusChanged;

    public CaptureStatus Probe() => new(CaptureState.Stopped, "Pronto");

    public Task StartAsync(int processId, string executableName, FrameCaptureOptions options, CancellationToken cancellationToken)
    {
        StartCount++;
        LastPid = processId;
        SetStatus(new CaptureStatus(CaptureState.Capturing, "Capturando"));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        SetStatus(new CaptureStatus(CaptureState.Stopped, "Parado"));
        return Task.CompletedTask;
    }

    public void SetStatus(CaptureStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(this, s);
    }

    public void Emit(IReadOnlyList<FrameSample> frames) => FramesReceived?.Invoke(this, frames);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
