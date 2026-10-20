using Framex.Core.Alerts;
using Framex.Core.Customization;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framex.Tests;

public class PersistenceTests
{
    private static Session NewSession(DateTimeOffset start, string game = "Jogo", Guid? gameId = null) => new()
    {
        GameId = gameId ?? Guid.NewGuid(),
        GameName = game,
        ExecutableName = "jogo.exe",
        StartedAt = start,
        State = SessionState.Active,
        AppVersion = "0.1.0",
        HardwareFingerprint = "CPU | GPU | 16 GiB",
        MonitoringProfile = "Equilibrado",
        InitialCaptureState = CaptureState.Stopped,
    };

    private static SessionSummary Summary(double avgFps, double gpuMax) => new(
        TimeSpan.FromMinutes(10),
        new FrameSummary(1000, 0, avgFps, null, null, null, null, null, null, null, null, null, null, [], []),
        [new MetricStatistic(MetricKind.GpuTemperature, 60, 50, gpuMax, 600, 100, "teste")],
        [], [], DateTimeOffset.UtcNow);

    [Fact]
    public void FreshDatabase_IsMigratedToLatestVersion()
    {
        using var tmp = new TempDatabase();
        using var c = tmp.Database.Open();
        Assert.Equal(SqliteDatabase.LatestVersion, tmp.Database.GetVersion(c));
    }

    [Fact]
    public void MigrationFromV1_PreservesHistory_AndCreatesBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "framex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "framex.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = Migrations.All[0].Sql + "PRAGMA user_version=1;" +
                    "INSERT INTO sessions(uid, game_name, exe_name, game_confirmed, started_at, state, app_version, hardware, profile, capture_state) " +
                    "VALUES('11111111-1111-1111-1111-111111111111', 'Antigo', 'a.exe', 1, 0, 1, '0.0.1', 'hw', 'p', 0);";
                cmd.ExecuteNonQuery();
            }

            var db = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
            db.Initialize();
            var repo = new SqliteSessionRepository(db);
            Assert.Single(repo.Query(new SessionQuery()), s => s.GameName == "Antigo");
            Assert.True(File.Exists(path + ".v1.bak"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NewerSchema_IsRejected_WithoutTouchingData()
    {
        using var tmp = new TempDatabase();
        using (var c = tmp.Database.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version=999;";
            cmd.ExecuteNonQuery();
        }

        var db = new SqliteDatabase(tmp.Path_, NullLogger<SqliteDatabase>.Instance);
        Assert.Throws<InvalidOperationException>(db.Initialize);
    }

    [Fact]
    public void CorruptedDatabase_IsQuarantined()
    {
        var dir = Path.Combine(Path.GetTempPath(), "framex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "framex.db");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x42, 8192).ToArray());
        try
        {
            var db = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
            db.Initialize();
            Assert.NotNull(db.RecoveryNotice);
            Assert.Contains(Directory.GetFiles(dir), f => f.Contains("corrompido", StringComparison.Ordinal));
            using var c = db.Open();
            Assert.Equal(SqliteDatabase.LatestVersion, db.GetVersion(c));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Session_RoundTrip_SamplesFramesEventsAndSummary()
    {
        using var tmp = new TempDatabase();
        var repo = new SqliteSessionRepository(tmp.Database);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var s = NewSession(start);
        repo.Create(s);

        var samples = Enumerable.Range(0, 10).Select(i => new MetricSample(start.AddSeconds(i), MetricKind.CpuLoad, i * 10)).ToList();
        var frames = Enumerable.Range(0, 500).Select(i => new FrameSample(start.AddMilliseconds(i * 16), 16 + (i % 3))).ToList();
        repo.Append(s.Id, samples, frames, [new SessionEvent(start, SessionEventType.SessionStarted, EventSeverity.Info, "início")]);
        repo.Append(s.Id, [], frames.Take(100).ToList(), []);

        Assert.Equal(10, repo.GetSamples(s.Id).Count);
        var fts = repo.GetFrameTimes(s.Id);
        Assert.Equal(600, fts.Count);
        Assert.Equal(16, fts[0], 3);
        Assert.Single(repo.GetEvents(s.Id));
        Assert.Single(repo.GetActive());
        Assert.Equal(start.AddSeconds(9), repo.GetLastDataTimestamp(s.Id)); // última amostra (9 s) é posterior ao último quadro (7,98 s)

        repo.Complete(s.Id, start.AddMinutes(10), SessionState.Completed, Summary(144, 70));
        var loaded = repo.Get(s.Id)!;
        Assert.Equal(SessionState.Completed, loaded.State);
        Assert.Equal(144, loaded.Summary!.Frames!.AverageFps);
        Assert.Empty(repo.GetActive());
    }

    [Fact]
    public void Query_FiltersAndSorts()
    {
        using var tmp = new TempDatabase();
        var repo = new SqliteSessionRepository(tmp.Database);
        var t = DateTimeOffset.UtcNow.AddDays(-3);
        var a = NewSession(t, "Alpha");
        var b = NewSession(t.AddDays(1), "Beta");
        var c = NewSession(t.AddDays(2), "Alpha 2");
        foreach (var (s, fps) in new[] { (a, 60.0), (b, 120.0), (c, 90.0) })
        {
            repo.Create(s);
            repo.Complete(s.Id, s.StartedAt.AddMinutes(10), SessionState.Completed, Summary(fps, fps / 2));
        }

        Assert.Equal(2, repo.Query(new SessionQuery(Search: "alpha")).Count);
        Assert.Equal("Beta", repo.Query(new SessionQuery(Sort: SessionSort.HighestFps))[0].GameName);
        Assert.Equal("Alpha", repo.Query(new SessionQuery(Sort: SessionSort.OldestFirst))[0].GameName);
        Assert.Equal(2, repo.Query(new SessionQuery(MinAverageFps: 80)).Count);
        Assert.Single(repo.Query(new SessionQuery(From: t.AddDays(1.5))));
        Assert.Equal("Beta", repo.Query(new SessionQuery(Sort: SessionSort.HottestGpu))[0].GameName);
        Assert.Empty(repo.Query(new SessionQuery(Search: "%")));
    }

    [Fact]
    public void Delete_CascadesDetailedData()
    {
        using var tmp = new TempDatabase();
        var repo = new SqliteSessionRepository(tmp.Database);
        var s = NewSession(DateTimeOffset.UtcNow);
        repo.Create(s);
        repo.Append(s.Id, [new MetricSample(s.StartedAt, MetricKind.GpuLoad, 50)], [new FrameSample(s.StartedAt, 10)], []);
        repo.Delete([s.Id]);
        Assert.Null(repo.Get(s.Id));
        using var c = tmp.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM metric_samples) + (SELECT COUNT(*) FROM frame_chunks)";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Retention_PurgesSamplesButKeepsSummary()
    {
        using var tmp = new TempDatabase();
        var repo = new SqliteSessionRepository(tmp.Database);
        var old = NewSession(DateTimeOffset.UtcNow.AddDays(-100));
        repo.Create(old);
        repo.Append(old.Id, [new MetricSample(old.StartedAt, MetricKind.GpuLoad, 50)], [], []);
        repo.Complete(old.Id, old.StartedAt.AddHours(1), SessionState.Completed, Summary(100, 60));

        Assert.Equal(1, repo.PurgeDetailedData(DateTimeOffset.UtcNow.AddDays(-30)));
        Assert.Empty(repo.GetSamples(old.Id));
        Assert.NotNull(repo.Get(old.Id)!.Summary);
    }

    [Fact]
    public void Games_UpsertStatisticsAndIgnoreList()
    {
        using var tmp = new TempDatabase();
        var games = new SqliteGameRepository(tmp.Database);
        var sessions = new SqliteSessionRepository(tmp.Database);
        var g = new Game { Name = "Jogo", Executables = [new GameExecutable("jogo.exe", @"C:\x\jogo.exe")] };
        games.Upsert(g);
        g.Name = "Jogo Corrigido";
        g.Executables.Add(new GameExecutable("jogo-dx12.exe", null));
        games.Upsert(g);

        var loaded = Assert.Single(games.GetAll());
        Assert.Equal("Jogo Corrigido", loaded.Name);
        Assert.Equal(2, loaded.Executables.Count);

        foreach (var (minutes, fps) in new[] { (10, 100.0), (30, 60.0) })
        {
            var s = NewSession(DateTimeOffset.UtcNow, gameId: g.Id);
            sessions.Create(s);
            var sum = Summary(fps, 70) with { Duration = TimeSpan.FromMinutes(minutes) };
            sessions.Complete(s.Id, s.StartedAt.AddMinutes(minutes), SessionState.Completed, sum);
        }

        var stats = games.GetStatistics()[g.Id];
        Assert.Equal(2, stats.SessionCount);
        Assert.Equal(40, stats.TotalMonitored.TotalMinutes, 3);
        Assert.Equal(70, stats.AverageFps!.Value, 3); // (10×100 + 30×60) / 40

        g.Status = GameStatus.Removed;
        games.Upsert(g);
        Assert.Empty(games.GetAll());
        Assert.Single(games.GetAll(includeRemoved: true));
        Assert.Equal(2, sessions.Query(new SessionQuery(GameId: g.Id)).Count); // relatórios preservados

        games.SetIgnored("launcher.exe", true);
        Assert.Contains("LAUNCHER.EXE", games.GetIgnoredExecutables());
        games.SetIgnored("launcher.exe", false);
        Assert.Empty(games.GetIgnoredExecutables());
    }

    [Fact]
    public void Settings_AndAlertHistory_Persist()
    {
        using var tmp = new TempDatabase();
        var store = new SqliteSettingsStore(tmp.Database);
        var settings = new AppSettings { ThemeId = "toxic-green", Profile = PerformanceProfile.Economy, UiScale = 1.2 };
        store.Save("app", settings);
        var reopened = new SqliteSettingsStore(new SqliteDatabase(tmp.Path_, NullLogger<SqliteDatabase>.Instance));
        var loaded = reopened.Load<AppSettings>("app")!;
        Assert.Equal("toxic-green", loaded.ThemeId);
        Assert.Equal(PerformanceProfile.Economy, loaded.Profile);
        Assert.Null(store.Load<AppSettings>("inexistente"));

        var alerts = new SqliteAlertHistoryRepository(tmp.Database);
        alerts.Add(new AlertRecord(DateTimeOffset.UtcNow, "r", "Título", "Mensagem", null, "Jogo"));
        Assert.Single(alerts.GetRecent(10));
    }
}
