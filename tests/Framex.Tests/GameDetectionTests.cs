using Framex.Core.Domain;
using Framex.Core.GameDetection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests;

public class GameDetectionTests
{
    private static Game MakeGame(string name, params string[] exes) => new()
    {
        Name = name,
        Executables = exes.Select(e => new GameExecutable(e, null)).ToList(),
    };

    [Fact]
    public void KnownGame_IsDetected_AndPrimaryProcessPrefersWindow()
    {
        var game = MakeGame("Jogo", "jogo.exe", "jogo_helper.exe");
        var procs = new[]
        {
            FakeProcesses.Proc(10, "jogo_helper.exe", window: false, ws: 900_000_000),
            FakeProcesses.Proc(11, "jogo.exe", window: true, ws: 300_000_000),
        };
        var result = GameDetectionEngine.Detect(procs, [game], new HashSet<string>(), allowHeuristics: false);
        var d = Assert.Single(result);
        Assert.Equal(11, d.Process.ProcessId);
        Assert.Equal(2, d.InstanceCount);
        Assert.Equal(DetectionKind.Known, d.Kind);
    }

    [Fact]
    public void Launchers_AndHelpers_AreNotHeuristicGames()
    {
        var procs = new[]
        {
            FakeProcesses.Proc(1, "steam.exe", @"C:\Program Files (x86)\Steam\steamapps\common\x\steam.exe"),
            FakeProcesses.Proc(2, "UnityCrashHandler64.exe", @"C:\Program Files (x86)\Steam\steamapps\common\Hollow\UnityCrashHandler64.exe"),
            FakeProcesses.Proc(3, "GameLauncher.exe", @"D:\SteamLibrary\steamapps\common\Foo\GameLauncher.exe"),
        };
        Assert.Empty(GameDetectionEngine.Detect(procs, [], new HashSet<string>(), allowHeuristics: true));
    }

    [Fact]
    public void Heuristic_RequiresStoreLibraryAndVisibleWindow()
    {
        var inStore = FakeProcesses.Proc(1, "hollow_knight.exe", @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        var noWindow = FakeProcesses.Proc(2, "server.exe", @"D:\SteamLibrary\steamapps\common\Server\server.exe", window: false);
        var outside = FakeProcesses.Proc(3, "notepad.exe", @"C:\Windows\System32\notepad.exe");
        var result = GameDetectionEngine.Detect([inStore, noWindow, outside], [], new HashSet<string>(), allowHeuristics: true);
        var d = Assert.Single(result);
        Assert.Equal(DetectionKind.Heuristic, d.Kind);
        Assert.Equal("Hollow Knight", d.SuggestedName);
    }

    [Fact]
    public void IgnoredAndRemovedExecutables_AreSkipped()
    {
        var removed = MakeGame("Removido", "old.exe");
        removed.Status = GameStatus.Removed;
        var procs = new[]
        {
            FakeProcesses.Proc(1, "old.exe", @"D:\SteamLibrary\steamapps\common\Old\old.exe"),
            FakeProcesses.Proc(2, "ign.exe", @"D:\SteamLibrary\steamapps\common\Ign\ign.exe"),
        };
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IGN.EXE" };
        Assert.Empty(GameDetectionEngine.Detect(procs, [removed], ignored, allowHeuristics: true));
    }

    private static (GameDetectionService Svc, FakeProcesses Procs, InMemoryGames Games, ManualTimeProvider Time) Create(params Game[] games)
    {
        var procs = new FakeProcesses();
        var repo = new InMemoryGames();
        repo.Games.AddRange(games);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var svc = new GameDetectionService(procs, repo, new GameDetectionOptions { ExitGracePeriod = TimeSpan.FromSeconds(10) }, time, NullLogger<GameDetectionService>.Instance);
        return (svc, procs, repo, time);
    }

    [Fact]
    public void Session_StartsAndEndsAfterGracePeriod()
    {
        var (svc, procs, _, time) = Create(MakeGame("Jogo", "jogo.exe"));
        var started = 0;
        GameEndedEventArgs? ended = null;
        svc.GameStarted += (_, _) => started++;
        svc.GameEnded += (_, e) => ended = e;

        procs.Processes.Add(FakeProcesses.Proc(100, "jogo.exe"));
        svc.Tick();
        svc.Tick();
        Assert.Equal(1, started);

        procs.Processes.Clear();
        var exitAt = time.Now;
        svc.Tick();
        time.Advance(TimeSpan.FromSeconds(5));
        svc.Tick();
        Assert.Null(ended);

        time.Advance(TimeSpan.FromSeconds(6));
        svc.Tick();
        Assert.NotNull(ended);
        Assert.Equal(exitAt, ended!.EndedAt);
        Assert.Null(svc.Active);
    }

    [Fact]
    public void ProcessRestart_WithinGrace_ContinuesSameSession()
    {
        var (svc, procs, _, time) = Create(MakeGame("Jogo", "jogo.exe"));
        var started = 0;
        var ended = 0;
        var changes = new List<string>();
        svc.GameStarted += (_, _) => started++;
        svc.GameEnded += (_, _) => ended++;
        svc.GameProcessChanged += (_, e) => changes.Add(e.Message);

        procs.Processes.Add(FakeProcesses.Proc(100, "jogo.exe"));
        svc.Tick();
        procs.Processes.Clear();
        svc.Tick();
        time.Advance(TimeSpan.FromSeconds(3));
        procs.Processes.Add(FakeProcesses.Proc(200, "jogo.exe"));
        svc.Tick();

        Assert.Equal(1, started);
        Assert.Equal(0, ended);
        Assert.Equal(200, svc.Active!.Process.ProcessId);
        Assert.Contains(changes, m => m.StartsWith("Processo reiniciado", StringComparison.Ordinal));
    }

    [Fact]
    public void UnexpectedCrash_EndsSession()
    {
        var (svc, procs, _, time) = Create(MakeGame("Jogo", "jogo.exe"));
        var ended = 0;
        svc.GameEnded += (_, _) => ended++;
        procs.Processes.Add(FakeProcesses.Proc(100, "jogo.exe"));
        svc.Tick();
        procs.Processes.Clear();
        svc.Tick();
        time.Advance(TimeSpan.FromSeconds(11));
        svc.Tick();
        Assert.Equal(1, ended);
    }

    [Fact]
    public void MultipleInstances_AreReported()
    {
        var (svc, procs, _, _) = Create(MakeGame("Jogo", "jogo.exe"));
        var changes = new List<string>();
        svc.GameProcessChanged += (_, e) => changes.Add(e.Message);
        procs.Processes.Add(FakeProcesses.Proc(1, "jogo.exe"));
        svc.Tick();
        procs.Processes.Add(FakeProcesses.Proc(2, "jogo.exe"));
        svc.Tick();
        Assert.Contains(changes, m => m.Contains("Instância adicional", StringComparison.Ordinal));
        Assert.Equal(1, svc.Active!.Process.ProcessId);
    }

    [Fact]
    public void HeuristicGame_IsRegisteredAsPending()
    {
        var (svc, procs, games, _) = Create();
        Game? pending = null;
        svc.PendingGameDiscovered += (_, g) => pending = g;
        procs.Processes.Add(FakeProcesses.Proc(5, "celeste.exe", @"C:\Program Files (x86)\Steam\steamapps\common\Celeste\celeste.exe"));
        svc.Tick();
        Assert.NotNull(pending);
        Assert.Equal(GameStatus.Pending, games.Games.Single().Status);
        Assert.False(svc.Active!.Confirmed);
    }
}
