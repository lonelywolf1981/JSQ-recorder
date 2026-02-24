using System.Data;
using Dapper;
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
        
        // Connection string с WAL mode
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
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
        
        _initialized = true;
    }
    
    private async Task CreateMinimalSchemaAsync(IDbConnection conn)
    {
        var sql = @"
            CREATE TABLE IF NOT EXISTS experiments (
                id TEXT PRIMARY KEY,
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
