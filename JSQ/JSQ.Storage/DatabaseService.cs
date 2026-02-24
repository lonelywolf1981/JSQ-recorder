using System.Data;
using System.Linq;
using Dapper;
using JSQ.Core.Models;
using Microsoft.Data.Sqlite;

namespace JSQ.Storage;

/// <summary>
/// Сервис работы с SQLite БД
/// </summary>
public interface IDatabaseService : IDisposable
{
    /// <summary>
    /// Путь к файлу БД
    /// </summary>
    string DbPath { get; }
    
    /// <summary>
    /// Инициализировать БД (создать схему)
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Получить подключение
    /// </summary>
    IDbConnection GetConnection();
    
    /// <summary>
    /// Выполнить WAL checkpoint
    /// </summary>
    Task CheckpointAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Получить размер БД в байтах
    /// </summary>
    long GetDatabaseSize();
}

/// <summary>
/// Реализация сервиса SQLite
/// </summary>
public class SqliteDatabaseService : IDatabaseService
{
    private readonly string _connectionString;
    private bool _initialized;
    
    public string DbPath { get; }
    
    public SqliteDatabaseService(string dbPath)
    {
        DbPath = dbPath;
        
        // Создаем директорию если не существует
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        // Connection string с WAL mode (Shared cache несовместим с WAL)
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        };
        
        _connectionString = builder.ToString();
        _initialized = false;
    }
    
    public IDbConnection GetConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
    
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;
        
        using var conn = GetConnection();
        
        // Включаем WAL mode
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        
        // Включаем synchronous=FULL для гарантированной записи
        await conn.ExecuteAsync("PRAGMA synchronous=FULL;");
        
        // Включаем foreign keys
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
        
        // Увеличиваем размер кэша страниц (10000 страниц * 4KB = 40MB)
        await conn.ExecuteAsync("PRAGMA cache_size=-10000;");
        
        // Включаем busy timeout (5 секунд)
        await conn.ExecuteAsync("PRAGMA busy_timeout=5000;");
        
        // Загружаем и выполняем схему
        var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Schema", "schema.sql");
        if (!File.Exists(schemaPath))
        {
            // Пробуем альтернативный путь
            schemaPath = Path.Combine(Path.GetDirectoryName(DbPath) ?? "", "Schema", "schema.sql");
        }
        
        if (File.Exists(schemaPath))
        {
            var schema = File.ReadAllText(schemaPath);
            await conn.ExecuteAsync(schema);
        }
        else
        {
            // Создаем минимальную схему если файл не найден
            await CreateMinimalSchemaAsync(conn);
        }

        await EnsurePostIdSchemaAsync(conn);
        
        _initialized = true;
    }

    private async Task EnsurePostIdSchemaAsync(IDbConnection conn)
    {
        var columns = (await conn.QueryAsync<TableInfoRow>("PRAGMA table_info(experiments);")).ToList();
        if (!columns.Any(c => string.Equals(c.Name, "post_id", StringComparison.OrdinalIgnoreCase)))
        {
            await conn.ExecuteAsync("ALTER TABLE experiments ADD COLUMN post_id TEXT;");
        }

        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_experiments_post_start ON experiments(post_id, start_time DESC);");
        await BackfillExperimentPostIdsAsync(conn);
    }

    private async Task BackfillExperimentPostIdsAsync(IDbConnection conn)
    {
        var experimentIds = await conn.QueryAsync<string>(@"
            SELECT id
            FROM experiments
            WHERE post_id IS NULL OR TRIM(post_id) = '';
        ");

        foreach (var experimentId in experimentIds)
        {
            var postId = await InferPostIdAsync(conn, experimentId);
            if (string.IsNullOrWhiteSpace(postId))
                continue;

            await conn.ExecuteAsync(@"
                UPDATE experiments
                SET post_id = @PostId,
                    updated_at = datetime('now')
                WHERE id = @Id;
            ", new { Id = experimentId, PostId = postId });
        }
    }

    private async Task<string?> InferPostIdAsync(IDbConnection conn, string experimentId)
    {
        var channelIndices = (await conn.QueryAsync<int>(@"
            SELECT DISTINCT channel_index
            FROM raw_samples
            WHERE experiment_id = @Id
            LIMIT 1000;
        ", new { Id = experimentId })).ToList();

        var postBySamples = ResolvePostFromChannelIndices(channelIndices);
        if (!string.IsNullOrWhiteSpace(postBySamples))
            return postBySamples;

        var groups = (await conn.QueryAsync<string>(@"
            SELECT channel_group
            FROM channel_config
            WHERE experiment_id = @Id
              AND enabled = 1;
        ", new { Id = experimentId })).ToList();

        var postByConfig = ResolvePostFromGroups(groups);
        if (!string.IsNullOrWhiteSpace(postByConfig))
            return postByConfig;

        var expName = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT name FROM experiments WHERE id = @Id;", new { Id = experimentId });

        return ResolvePostFromName(expName);
    }

    private static string? ResolvePostFromChannelIndices(IEnumerable<int> channelIndices)
    {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 0,
            ["B"] = 0,
            ["C"] = 0
        };

        foreach (var index in channelIndices)
        {
            var def = ChannelRegistry.GetByIndex(index);
            if (def == null)
                continue;

            switch (def.Group)
            {
                case ChannelGroup.PostA:
                    counters["A"]++;
                    break;
                case ChannelGroup.PostB:
                    counters["B"]++;
                    break;
                case ChannelGroup.PostC:
                    counters["C"]++;
                    break;
            }
        }

        return ResolveDominantPost(counters);
    }

    private static string? ResolvePostFromGroups(IEnumerable<string> groups)
    {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 0,
            ["B"] = 0,
            ["C"] = 0
        };

        foreach (var group in groups)
        {
            if (string.Equals(group, "PostA", StringComparison.OrdinalIgnoreCase)) counters["A"]++;
            else if (string.Equals(group, "PostB", StringComparison.OrdinalIgnoreCase)) counters["B"]++;
            else if (string.Equals(group, "PostC", StringComparison.OrdinalIgnoreCase)) counters["C"]++;
        }

        return ResolveDominantPost(counters);
    }

    private static string? ResolveDominantPost(Dictionary<string, int> counters)
    {
        var ordered = counters.OrderByDescending(kvp => kvp.Value).ToList();
        if (ordered.Count == 0 || ordered[0].Value == 0)
            return null;

        if (ordered.Count > 1 && ordered[0].Value == ordered[1].Value)
            return null;

        return ordered[0].Key;
    }

    private static string? ResolvePostFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var safeName = name!;

        if (safeName.IndexOf("пост a", StringComparison.OrdinalIgnoreCase) >= 0 ||
            safeName.IndexOf("post a", StringComparison.OrdinalIgnoreCase) >= 0)
            return "A";

        if (safeName.IndexOf("пост b", StringComparison.OrdinalIgnoreCase) >= 0 ||
            safeName.IndexOf("post b", StringComparison.OrdinalIgnoreCase) >= 0)
            return "B";

        if (safeName.IndexOf("пост c", StringComparison.OrdinalIgnoreCase) >= 0 ||
            safeName.IndexOf("post c", StringComparison.OrdinalIgnoreCase) >= 0)
            return "C";

        return null;
    }

    private class TableInfoRow
    {
        public string Name { get; set; } = string.Empty;
    }
    
    private async Task CreateMinimalSchemaAsync(IDbConnection conn)
    {
        var sql = @"
            CREATE TABLE IF NOT EXISTS experiments (
                id TEXT PRIMARY KEY,
                post_id TEXT,
                name TEXT NOT NULL,
                part_number TEXT,
                operator TEXT,
                refrigerant TEXT,
                state TEXT NOT NULL DEFAULT 'Idle',
                start_time TEXT,
                end_time TEXT,
                post_a_enabled INTEGER DEFAULT 1,
                post_b_enabled INTEGER DEFAULT 1,
                post_c_enabled INTEGER DEFAULT 1,
                batch_size INTEGER DEFAULT 500,
                aggregation_interval_sec INTEGER DEFAULT 20,
                checkpoint_interval_sec INTEGER DEFAULT 30,
                created_at TEXT DEFAULT (datetime('now')),
                updated_at TEXT DEFAULT (datetime('now'))
            );
            
            CREATE TABLE IF NOT EXISTS raw_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                channel_index INTEGER NOT NULL,
                value REAL NOT NULL,
                is_valid INTEGER DEFAULT 1,
                created_at TEXT DEFAULT (datetime('now'))
            );
            
            CREATE INDEX IF NOT EXISTS idx_raw_samples_experiment_timestamp 
                ON raw_samples(experiment_id, timestamp);
            
            CREATE TABLE IF NOT EXISTS agg_samples_20s (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                channel_index INTEGER NOT NULL,
                value_min REAL,
                value_max REAL,
                value_avg REAL,
                sample_count INTEGER,
                invalid_count INTEGER,
                quality_flag INTEGER DEFAULT 1,
                created_at TEXT DEFAULT (datetime('now')),
                UNIQUE(experiment_id, timestamp, channel_index)
            );
            
            CREATE TABLE IF NOT EXISTS anomaly_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                channel_index INTEGER NOT NULL,
                channel_name TEXT NOT NULL,
                anomaly_type TEXT NOT NULL,
                value REAL,
                threshold REAL,
                duration_sec INTEGER,
                is_acknowledged INTEGER DEFAULT 0,
                acknowledged_at TEXT,
                acknowledged_by TEXT,
                context_json TEXT,
                created_at TEXT DEFAULT (datetime('now'))
            );
            
            CREATE TABLE IF NOT EXISTS system_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_id TEXT,
                timestamp TEXT DEFAULT (datetime('now')),
                event_type TEXT NOT NULL,
                severity TEXT DEFAULT 'Info',
                message TEXT NOT NULL,
                source TEXT,
                correlation_id TEXT,
                details_json TEXT
            );
            
            CREATE TABLE IF NOT EXISTS checkpoints (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_id TEXT NOT NULL,
                checkpoint_time TEXT NOT NULL,
                last_sample_timestamp TEXT,
                last_sample_id INTEGER,
                queue_state_json TEXT,
                statistics_json TEXT
            );

            CREATE TABLE IF NOT EXISTS channel_config (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_id TEXT NOT NULL,
                channel_index INTEGER NOT NULL,
                channel_name TEXT NOT NULL,
                channel_group TEXT,
                channel_type TEXT,
                min_limit REAL,
                max_limit REAL,
                enabled INTEGER DEFAULT 1
            );
        ";
        
        await conn.ExecuteAsync(sql);
    }
    
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        using var conn = GetConnection();
        // WAL checkpoint - PASSIVE (не блокирующий)
        await conn.ExecuteAsync("PRAGMA wal_checkpoint(PASSIVE);");
    }
    
    public long GetDatabaseSize()
    {
        try
        {
            var fileInfo = new FileInfo(DbPath);
            return fileInfo.Exists ? fileInfo.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
    
    public void Dispose()
    {
        // SQLite автоматически закрывает подключения
        GC.SuppressFinalize(this);
    }
}
