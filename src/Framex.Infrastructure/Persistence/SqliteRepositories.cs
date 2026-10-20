using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Framex.Core.Alerts;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Framex.Infrastructure.Persistence;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

internal static class Db
{
    public static long ToUnixMs(this DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static SqliteCommand Command(this SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    public static SqliteCommand With(this SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static double? NullableDouble(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static string? NullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

public sealed class SqliteGameRepository : IGameRepository
{
    private readonly SqliteDatabase _db;

    public SqliteGameRepository(SqliteDatabase db) => _db = db;

    public IReadOnlyList<Game> GetAll(bool includeRemoved = false)
    {
        using var c = _db.Open();
        var games = new Dictionary<string, Game>();
        using (var cmd = c.Command("SELECT id, name, status, created_at, source, profile_json FROM games" + (includeRemoved ? "" : " WHERE status <> 2") + " ORDER BY name COLLATE NOCASE"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var game = new Game
                {
                    Id = Guid.Parse(r.GetString(0)),
                    Name = r.GetString(1),
                    Status = (GameStatus)r.GetInt32(2),
                    CreatedAt = Db.FromUnixMs(r.GetInt64(3)),
                    Source = r.NullableString(4),
                    Profile = JsonSerializer.Deserialize<GameProfile>(r.GetString(5), Json.Options) ?? new GameProfile(),
                };
                games[r.GetString(0)] = game;
            }
        }

        using (var cmd = c.Command("SELECT game_id, exe_name, last_path FROM game_executables"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (games.TryGetValue(r.GetString(0), out var g))
                {
                    g.Executables.Add(new GameExecutable(r.GetString(1), r.NullableString(2)));
                }
            }
        }

        return games.Values.ToList();
    }

    public Game? Get(Guid id) => GetAll(includeRemoved: true).FirstOrDefault(g => g.Id == id);

    public void Upsert(Game game)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.Command("""
            INSERT INTO games(id, name, status, created_at, source, profile_json) VALUES($id, $name, $status, $created, $source, $profile)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, status=excluded.status, source=excluded.source, profile_json=excluded.profile_json
            """, tx))
        {
            cmd.With("$id", game.Id.ToString()).With("$name", game.Name).With("$status", (int)game.Status)
               .With("$created", game.CreatedAt.ToUnixMs()).With("$source", game.Source)
               .With("$profile", JsonSerializer.Serialize(game.Profile, Json.Options));
            cmd.ExecuteNonQuery();
        }

        using (var del = c.Command("DELETE FROM game_executables WHERE game_id=$id", tx))
        {
            del.With("$id", game.Id.ToString()).ExecuteNonQuery();
        }

        foreach (var exe in game.Executables.DistinctBy(e => e.ExecutableName, StringComparer.OrdinalIgnoreCase))
        {
            using var ins = c.Command("INSERT INTO game_executables(game_id, exe_name, last_path) VALUES($id, $exe, $path)", tx);
            ins.With("$id", game.Id.ToString()).With("$exe", exe.ExecutableName).With("$path", exe.LastKnownPath).ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>FPS médio histórico ponderado pela duração das sessões com captura de quadros.</summary>
    public IReadOnlyDictionary<Guid, GameStatistics> GetStatistics()
    {
        using var c = _db.Open();
        using var cmd = c.Command("""
            SELECT game_id, COUNT(*), COALESCE(SUM(duration_s), 0), MAX(started_at),
                   SUM(CASE WHEN avg_fps IS NOT NULL THEN avg_fps * duration_s END) / NULLIF(SUM(CASE WHEN avg_fps IS NOT NULL THEN duration_s END), 0)
            FROM sessions WHERE state <> 0 AND game_id IS NOT NULL GROUP BY game_id
            """);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<Guid, GameStatistics>();
        while (r.Read())
        {
            var id = Guid.Parse(r.GetString(0));
            result[id] = new GameStatistics(id, r.GetInt32(1), TimeSpan.FromSeconds(r.GetDouble(2)),
                r.IsDBNull(3) ? null : Db.FromUnixMs(r.GetInt64(3)), r.NullableDouble(4));
        }

        return result;
    }

    public IReadOnlySet<string> GetIgnoredExecutables()
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT exe_name FROM ignored_processes");
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            set.Add(r.GetString(0));
        }

        return set;
    }

    public void SetIgnored(string executableName, bool ignored)
    {
        using var c = _db.Open();
        using var cmd = ignored
            ? c.Command("INSERT OR IGNORE INTO ignored_processes(exe_name) VALUES($e)")
            : c.Command("DELETE FROM ignored_processes WHERE exe_name=$e");
        cmd.With("$e", executableName).ExecuteNonQuery();
    }
}

public sealed class SqliteSessionRepository : ISessionRepository
{
    private readonly SqliteDatabase _db;
    private readonly Dictionary<Guid, long> _sidCache = new();
    private readonly Lock _cacheGate = new();

    public SqliteSessionRepository(SqliteDatabase db) => _db = db;

    public void Create(Session s)
    {
        using var c = _db.Open();
        using var cmd = c.Command("""
            INSERT INTO sessions(uid, game_id, game_name, exe_name, game_confirmed, started_at, ended_at, state, app_version, hardware, resolution, profile, capture_state)
            VALUES($uid, $gid, $gname, $exe, $conf, $start, NULL, $state, $ver, $hw, $res, $profile, $cap);
            SELECT last_insert_rowid();
            """);
        cmd.With("$uid", s.Id.ToString()).With("$gid", s.GameId?.ToString()).With("$gname", s.GameName).With("$exe", s.ExecutableName)
           .With("$conf", s.GameConfirmed ? 1 : 0).With("$start", s.StartedAt.ToUnixMs()).With("$state", (int)s.State)
           .With("$ver", s.AppVersion).With("$hw", s.HardwareFingerprint).With("$res", s.Resolution).With("$profile", s.MonitoringProfile)
           .With("$cap", (int)s.InitialCaptureState);
        var sid = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        lock (_cacheGate)
        {
            _sidCache[s.Id] = sid;
        }
    }

    private long Sid(SqliteConnection c, Guid id, SqliteTransaction? tx = null)
    {
        lock (_cacheGate)
        {
            if (_sidCache.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }

        using var cmd = c.Command("SELECT sid FROM sessions WHERE uid=$u", tx).With("$u", id.ToString());
        var result = cmd.ExecuteScalar() ?? throw new KeyNotFoundException($"Sessão {id} não encontrada.");
        var sid = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        lock (_cacheGate)
        {
            _sidCache[id] = sid;
        }

        return sid;
    }

    public void Append(Guid sessionId, IReadOnlyList<MetricSample> samples, IReadOnlyList<FrameSample> frames, IReadOnlyList<SessionEvent> events)
    {
        if (samples.Count == 0 && frames.Count == 0 && events.Count == 0)
        {
            return;
        }

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var sid = Sid(c, sessionId, tx);

        if (samples.Count > 0)
        {
            using var cmd = c.Command("INSERT OR REPLACE INTO metric_samples(sid, metric, t, value) VALUES($sid, $m, $t, $v)", tx);
            var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
            var pM = cmd.Parameters.Add("$m", SqliteType.Integer);
            var pT = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pV = cmd.Parameters.Add("$v", SqliteType.Real);
            cmd.Prepare();
            pSid.Value = sid;
            foreach (var s in samples)
            {
                if (!double.IsFinite(s.Value))
                {
                    continue;
                }

                pM.Value = (int)s.Metric;
                pT.Value = s.Timestamp.ToUnixMs();
                pV.Value = s.Value;
                cmd.ExecuteNonQuery();
            }
        }

        if (frames.Count > 0)
        {
            using var seqCmd = c.Command("SELECT COALESCE(MAX(seq), -1) + 1 FROM frame_chunks WHERE sid=$sid", tx).With("$sid", sid);
            var seq = Convert.ToInt64(seqCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            var data = new float[frames.Count];
            for (var i = 0; i < frames.Count; i++)
            {
                data[i] = (float)frames[i].FrameTimeMs;
            }

            using var cmd = c.Command("INSERT INTO frame_chunks(sid, seq, start_t, end_t, count, data) VALUES($sid, $seq, $st, $et, $n, $d)", tx);
            cmd.With("$sid", sid).With("$seq", seq).With("$st", frames[0].Timestamp.ToUnixMs()).With("$et", frames[^1].Timestamp.ToUnixMs())
               .With("$n", frames.Count).With("$d", MemoryMarshal.AsBytes(data.AsSpan()).ToArray());
            cmd.ExecuteNonQuery();
        }

        foreach (var e in events)
        {
            using var cmd = c.Command("INSERT INTO session_events(sid, t, type, severity, message) VALUES($sid, $t, $ty, $sev, $msg)", tx);
            cmd.With("$sid", sid).With("$t", e.Timestamp.ToUnixMs()).With("$ty", (int)e.Type).With("$sev", (int)e.Severity).With("$msg", e.Message);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Complete(Guid sessionId, DateTimeOffset endedAt, SessionState state, SessionSummary summary)
    {
        using var c = _db.Open();
        using var cmd = c.Command("""
            UPDATE sessions SET ended_at=$end, state=$state, summary_json=$sum, duration_s=$dur, avg_fps=$fps, low1_fps=$low,
                max_cpu_temp=$cpu, max_gpu_temp=$gpu WHERE uid=$uid
            """);
        cmd.With("$end", endedAt.ToUnixMs()).With("$state", (int)state).With("$sum", JsonSerializer.Serialize(summary, Json.Options))
           .With("$dur", summary.Duration.TotalSeconds).With("$fps", summary.Frames?.AverageFps).With("$low", summary.Frames?.Low1PercentFps)
           .With("$cpu", summary.Get(MetricKind.CpuTemperaturePackage)?.Maximum).With("$gpu", summary.Get(MetricKind.GpuTemperature)?.Maximum)
           .With("$uid", sessionId.ToString());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SessionListItem> Query(SessionQuery q)
    {
        using var c = _db.Open();
        var where = new List<string> { "state <> 0" };
        using var cmd = c.CreateCommand();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            where.Add("game_name LIKE $search ESCAPE '\\'");
            cmd.With("$search", "%" + q.Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }

        if (q.GameId is { } gid)
        {
            where.Add("game_id = $gid");
            cmd.With("$gid", gid.ToString());
        }

        if (q.From is { } from)
        {
            where.Add("started_at >= $from");
            cmd.With("$from", from.ToUnixMs());
        }

        if (q.To is { } to)
        {
            where.Add("started_at <= $to");
            cmd.With("$to", to.ToUnixMs());
        }

        if (q.MinDuration is { } md)
        {
            where.Add("duration_s >= $md");
            cmd.With("$md", md.TotalSeconds);
        }

        if (q.MinAverageFps is { } mf)
        {
            where.Add("avg_fps >= $mf");
            cmd.With("$mf", mf);
        }

        var order = q.Sort switch
        {
            SessionSort.OldestFirst => "started_at ASC",
            SessionSort.LongestFirst => "duration_s DESC",
            SessionSort.HighestFps => "avg_fps IS NULL, avg_fps DESC",
            SessionSort.LowestFps => "avg_fps IS NULL, avg_fps ASC",
            SessionSort.HottestGpu => "max_gpu_temp IS NULL, max_gpu_temp DESC",
            _ => "started_at DESC",
        };

        cmd.CommandText = $"""
            SELECT uid, game_id, game_name, started_at, ended_at, state, avg_fps, low1_fps, max_cpu_temp, max_gpu_temp, game_confirmed
            FROM sessions WHERE {string.Join(" AND ", where)} ORDER BY {order} LIMIT $limit
            """;
        cmd.With("$limit", Math.Clamp(q.Limit, 1, 10_000));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionListItem>();
        while (r.Read())
        {
            list.Add(new SessionListItem(
                Guid.Parse(r.GetString(0)),
                r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
                r.GetString(2),
                Db.FromUnixMs(r.GetInt64(3)),
                r.IsDBNull(4) ? null : Db.FromUnixMs(r.GetInt64(4)),
                (SessionState)r.GetInt32(5),
                r.NullableDouble(6),
                r.NullableDouble(7),
                r.NullableDouble(8),
                r.NullableDouble(9),
                r.GetInt32(10) == 1));
        }

        return list;
    }

    public Session? Get(Guid sessionId)
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT uid FROM sessions WHERE uid=$u").With("$u", sessionId.ToString());
        return cmd.ExecuteScalar() is null ? null : ReadSessions(c, "uid=$u", ("$u", sessionId.ToString())).FirstOrDefault();
    }

    public IReadOnlyList<Session> GetActive()
    {
        using var c = _db.Open();
        return ReadSessions(c, "state=0");
    }

    private static List<Session> ReadSessions(SqliteConnection c, string where, params (string Name, object Value)[] args)
    {
        using var cmd = c.Command($"""
            SELECT uid, game_id, game_name, exe_name, game_confirmed, started_at, ended_at, state, app_version, hardware, resolution, profile, capture_state, summary_json
            FROM sessions WHERE {where}
            """);
        foreach (var (n, v) in args)
        {
            cmd.With(n, v);
        }

        using var r = cmd.ExecuteReader();
        var list = new List<Session>();
        while (r.Read())
        {
            list.Add(new Session
            {
                Id = Guid.Parse(r.GetString(0)),
                GameId = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
                GameName = r.GetString(2),
                ExecutableName = r.GetString(3),
                GameConfirmed = r.GetInt32(4) == 1,
                StartedAt = Db.FromUnixMs(r.GetInt64(5)),
                EndedAt = r.IsDBNull(6) ? null : Db.FromUnixMs(r.GetInt64(6)),
                State = (SessionState)r.GetInt32(7),
                AppVersion = r.GetString(8),
                HardwareFingerprint = r.GetString(9),
                Resolution = r.NullableString(10),
                MonitoringProfile = r.GetString(11),
                InitialCaptureState = (CaptureState)r.GetInt32(12),
                Summary = r.IsDBNull(13) ? null : JsonSerializer.Deserialize<SessionSummary>(r.GetString(13), Json.Options),
            });
        }

        return list;
    }

    public IReadOnlyList<MetricSample> GetSamples(Guid sessionId)
    {
        using var c = _db.Open();
        var sid = Sid(c, sessionId);
        using var cmd = c.Command("SELECT metric, t, value FROM metric_samples WHERE sid=$sid ORDER BY t").With("$sid", sid);
        using var r = cmd.ExecuteReader();
        var list = new List<MetricSample>(4096);
        while (r.Read())
        {
            var metric = (MetricKind)r.GetInt32(0);
            if (Enum.IsDefined(metric))
            {
                list.Add(new MetricSample(Db.FromUnixMs(r.GetInt64(1)), metric, r.GetDouble(2)));
            }
        }

        return list;
    }

    public IReadOnlyList<double> GetFrameTimes(Guid sessionId)
    {
        using var c = _db.Open();
        var sid = Sid(c, sessionId);
        using var cmd = c.Command("SELECT data FROM frame_chunks WHERE sid=$sid ORDER BY seq").With("$sid", sid);
        using var r = cmd.ExecuteReader();
        var list = new List<double>(16_384);
        while (r.Read())
        {
            var bytes = (byte[])r.GetValue(0);
            foreach (var f in MemoryMarshal.Cast<byte, float>(bytes))
            {
                list.Add(f);
            }
        }

        return list;
    }

    public IReadOnlyList<SessionEvent> GetEvents(Guid sessionId)
    {
        using var c = _db.Open();
        var sid = Sid(c, sessionId);
        using var cmd = c.Command("SELECT t, type, severity, message FROM session_events WHERE sid=$sid ORDER BY t, id").With("$sid", sid);
        using var r = cmd.ExecuteReader();
        var list = new List<SessionEvent>();
        while (r.Read())
        {
            list.Add(new SessionEvent(Db.FromUnixMs(r.GetInt64(0)), (SessionEventType)r.GetInt32(1), (EventSeverity)r.GetInt32(2), r.GetString(3)));
        }

        return list;
    }

    public DateTimeOffset? GetLastDataTimestamp(Guid sessionId)
    {
        using var c = _db.Open();
        var sid = Sid(c, sessionId);
        using var cmd = c.Command("""
            SELECT MAX(x) FROM (
                SELECT MAX(t) AS x FROM metric_samples WHERE sid=$sid
                UNION ALL SELECT MAX(end_t) FROM frame_chunks WHERE sid=$sid
                UNION ALL SELECT MAX(t) FROM session_events WHERE sid=$sid)
            """).With("$sid", sid);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Db.FromUnixMs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
    }

    public void Delete(IEnumerable<Guid> sessionIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var id in sessionIds)
        {
            using var cmd = c.Command("DELETE FROM sessions WHERE uid=$u", tx).With("$u", id.ToString());
            cmd.ExecuteNonQuery();
            lock (_cacheGate)
            {
                _sidCache.Remove(id);
            }
        }

        tx.Commit();
    }

    public int PurgeDetailedData(DateTimeOffset olderThan)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var ids = c.Command("SELECT sid FROM sessions WHERE state <> 0 AND detail_purged = 0 AND ended_at < $t", tx).With("$t", olderThan.ToUnixMs());
        var sids = new List<long>();
        using (var r = ids.ExecuteReader())
        {
            while (r.Read())
            {
                sids.Add(r.GetInt64(0));
            }
        }

        foreach (var sid in sids)
        {
            foreach (var sql in new[] { "DELETE FROM metric_samples WHERE sid=$s", "DELETE FROM frame_chunks WHERE sid=$s", "UPDATE sessions SET detail_purged=1 WHERE sid=$s" })
            {
                using var cmd = c.Command(sql, tx).With("$s", sid);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return sids.Count;
    }
}

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteDatabase _db;

    public SqliteSettingsStore(SqliteDatabase db) => _db = db;

    public T? Load<T>(string key) where T : class
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT json FROM settings WHERE key=$k").With("$k", key);
        if (cmd.ExecuteScalar() is not string json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save<T>(string key, T value) where T : class
    {
        using var c = _db.Open();
        using var cmd = c.Command("INSERT INTO settings(key, json, updated_at) VALUES($k, $j, $u) ON CONFLICT(key) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at");
        cmd.With("$k", key).With("$j", JsonSerializer.Serialize(value, Json.Options)).With("$u", DateTimeOffset.UtcNow.ToUnixMs());
        cmd.ExecuteNonQuery();
    }
}

public sealed class SqliteAlertHistoryRepository : IAlertHistoryRepository
{
    private readonly SqliteDatabase _db;

    public SqliteAlertHistoryRepository(SqliteDatabase db) => _db = db;

    public void Add(AlertRecord record)
    {
        using var c = _db.Open();
        using var cmd = c.Command("INSERT INTO alert_history(t, rule_id, title, message, session_uid, game_name) VALUES($t, $r, $ti, $m, $s, $g)");
        cmd.With("$t", record.Timestamp.ToUnixMs()).With("$r", record.RuleId).With("$ti", record.Title).With("$m", record.Message)
           .With("$s", record.SessionId?.ToString()).With("$g", record.GameName);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AlertRecord> GetRecent(int limit)
    {
        using var c = _db.Open();
        using var cmd = c.Command("SELECT t, rule_id, title, message, session_uid, game_name FROM alert_history ORDER BY t DESC LIMIT $l").With("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AlertRecord>();
        while (r.Read())
        {
            list.Add(new AlertRecord(Db.FromUnixMs(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)), r.NullableString(5)));
        }

        return list;
    }
}
