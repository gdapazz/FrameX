using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Persistence;

/// <summary>
/// Banco SQLite local com WAL, migrações por PRAGMA user_version, cópia de segurança antes de migrar
/// e verificação de integridade na abertura (um banco corrompido é preservado e substituído por um novo).
/// </summary>
public sealed class SqliteDatabase
{
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly string _connectionString;

    public SqliteDatabase(string path, ILogger<SqliteDatabase> logger)
    {
        Path = path;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string Path { get; }

    public static int LatestVersion => Migrations.All[^1].Version;

    /// <summary>Mensagem para o usuário caso o banco anterior tenha sido isolado por corrupção.</summary>
    public string? RecoveryNotice { get; private set; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path) && !IsHealthy())
        {
            var quarantine = $"{Path}.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}";
            SqliteConnection.ClearAllPools();
            File.Move(Path, quarantine);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(Path + suffix))
                {
                    File.Move(Path + suffix, quarantine + suffix);
                }
            }

            RecoveryNotice = $"O banco de dados estava corrompido e foi preservado em \"{System.IO.Path.GetFileName(quarantine)}\". Um novo histórico foi iniciado.";
            _logger.LogError("Banco de dados corrompido isolado em {Path}", quarantine);
        }

        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        Migrate(connection);
    }

    private bool IsHealthy()
    {
        try
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Falha na verificação de integridade do banco");
            return false;
        }
    }

    public int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Migrate(SqliteConnection connection)
    {
        var current = GetVersion(connection);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"O banco de dados foi criado por uma versão mais nova do FRAMEX (esquema {current}). Atualize o aplicativo; o histórico não foi alterado.");
        }

        if (current == LatestVersion)
        {
            return;
        }

        if (current > 0)
        {
            Backup(connection, current);
        }

        foreach (var migration in Migrations.All.Where(m => m.Version > current))
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                cmd.ExecuteNonQuery();
            }

            using (var ver = connection.CreateCommand())
            {
                ver.Transaction = tx;
                ver.CommandText = $"PRAGMA user_version = {migration.Version};";
                ver.ExecuteNonQuery();
            }

            tx.Commit();
            _logger.LogInformation("Migração do banco aplicada: versão {Version} ({Description})", migration.Version, migration.Description);
        }
    }

    private void Backup(SqliteConnection connection, int version)
    {
        var target = $"{Path}.v{version}.bak";
        try
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            destination.Open();
            connection.BackupDatabase(destination);
            _logger.LogInformation("Cópia de segurança do banco criada antes da migração: {Path}", target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível criar a cópia de segurança antes da migração");
        }
    }
}

internal sealed record Migration(int Version, string Description, string Sql);

internal static class Migrations
{
    public static readonly Migration[] All =
    [
        new(1, "Esquema inicial", """
            CREATE TABLE games(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                status INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                source TEXT,
                profile_json TEXT NOT NULL
            );
            CREATE TABLE game_executables(
                game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                exe_name TEXT NOT NULL COLLATE NOCASE,
                last_path TEXT,
                PRIMARY KEY(game_id, exe_name)
            );
            CREATE INDEX ix_game_executables_exe ON game_executables(exe_name);
            CREATE TABLE ignored_processes(exe_name TEXT PRIMARY KEY COLLATE NOCASE);

            CREATE TABLE sessions(
                sid INTEGER PRIMARY KEY AUTOINCREMENT,
                uid TEXT NOT NULL UNIQUE,
                game_id TEXT,
                game_name TEXT NOT NULL,
                exe_name TEXT NOT NULL,
                game_confirmed INTEGER NOT NULL,
                started_at INTEGER NOT NULL,
                ended_at INTEGER,
                state INTEGER NOT NULL,
                app_version TEXT NOT NULL,
                hardware TEXT NOT NULL,
                resolution TEXT,
                profile TEXT NOT NULL,
                capture_state INTEGER NOT NULL,
                summary_json TEXT,
                duration_s REAL,
                avg_fps REAL,
                low1_fps REAL,
                max_cpu_temp REAL,
                max_gpu_temp REAL
            );
            CREATE INDEX ix_sessions_started ON sessions(started_at);
            CREATE INDEX ix_sessions_game ON sessions(game_id, started_at);
            CREATE INDEX ix_sessions_state ON sessions(state);

            CREATE TABLE metric_samples(
                sid INTEGER NOT NULL REFERENCES sessions(sid) ON DELETE CASCADE,
                metric INTEGER NOT NULL,
                t INTEGER NOT NULL,
                value REAL NOT NULL,
                PRIMARY KEY(sid, metric, t)
            ) WITHOUT ROWID;

            CREATE TABLE frame_chunks(
                sid INTEGER NOT NULL REFERENCES sessions(sid) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                start_t INTEGER NOT NULL,
                end_t INTEGER NOT NULL,
                count INTEGER NOT NULL,
                data BLOB NOT NULL,
                PRIMARY KEY(sid, seq)
            ) WITHOUT ROWID;

            CREATE TABLE session_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sid INTEGER NOT NULL REFERENCES sessions(sid) ON DELETE CASCADE,
                t INTEGER NOT NULL,
                type INTEGER NOT NULL,
                severity INTEGER NOT NULL,
                message TEXT NOT NULL
            );
            CREATE INDEX ix_session_events_sid ON session_events(sid, t);

            CREATE TABLE settings(
                key TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            );

            CREATE TABLE alert_history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                t INTEGER NOT NULL,
                rule_id TEXT NOT NULL,
                title TEXT NOT NULL,
                message TEXT NOT NULL,
                session_uid TEXT,
                game_name TEXT
            );
            CREATE INDEX ix_alert_history_t ON alert_history(t);
            """),
        new(2, "Dados detalhados purgados por retenção", """
            ALTER TABLE sessions ADD COLUMN detail_purged INTEGER NOT NULL DEFAULT 0;
            """),
        new(3, "Nota do Setup: histórico de avaliações e escolhas manuais", """
            CREATE TABLE setup_evaluations(
                id TEXT PRIMARY KEY,
                created_at INTEGER NOT NULL,
                methodology TEXT NOT NULL,
                include_storage INTEGER NOT NULL,
                completeness INTEGER NOT NULL,
                overall INTEGER,
                coverage REAL NOT NULL,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL
            );
            CREATE INDEX ix_setup_evaluations_created ON setup_evaluations(created_at);
            CREATE TABLE setup_overrides(
                category INTEGER NOT NULL,
                device_key TEXT NOT NULL,
                record_name TEXT NOT NULL,
                dataset_id TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                PRIMARY KEY(category, device_key)
            );
            """),
    ];
}
