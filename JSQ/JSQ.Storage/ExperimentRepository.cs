using System.Linq;
using Dapper;
using JSQ.Core.Models;
using JSQ.Storage.Entities;

namespace JSQ.Storage;

/// <summary>
/// Сервис управления экспериментами
/// </summary>
public interface IExperimentRepository
{
    /// <summary>
    /// Создать новый эксперимент
    /// </summary>
    Task CreateAsync(Experiment experiment, CancellationToken ct = default);
    
    /// <summary>
    /// Обновить состояние эксперимента
    /// </summary>
    Task UpdateStateAsync(string experimentId, ExperimentState state, CancellationToken ct = default);
    
    /// <summary>
    /// Получить эксперимент по ID
    /// </summary>
    Task<Experiment?> GetByIdAsync(string experimentId, CancellationToken ct = default);
    
    /// <summary>
    /// Получить активный эксперимент
    /// </summary>
    Task<Experiment?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить список экспериментов по посту (A/B/C) с фильтрами.
    /// </summary>
    Task<List<Experiment>> GetByPostAsync(
        string postId,
        DateTime? startFrom = null,
        DateTime? startTo = null,
        string? searchText = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Завершить эксперимент
    /// </summary>
    Task FinalizeAsync(string experimentId, CancellationToken ct = default);
    
    /// <summary>
    /// Сохранить чекпоинт
    /// </summary>
    Task SaveCheckpointAsync(string experimentId, CheckpointData checkpoint, CancellationToken ct = default);
    
    /// <summary>
    /// Получить последний чекпоинт
    /// </summary>
    Task<CheckpointData?> GetLastCheckpointAsync(string experimentId, CancellationToken ct = default);
    
    /// <summary>
    /// Получить исторические данные канала за период
    /// </summary>
    Task<List<(DateTime time, double value)>> GetChannelHistoryAsync(
        string experimentId, int channelIndex, DateTime startTime, DateTime endTime, CancellationToken ct = default);

    /// <summary>
    /// Получить исторические данные канала за период без фильтра по эксперименту
    /// </summary>
    Task<List<(DateTime time, double value)>> GetChannelHistoryAnyAsync(
        int channelIndex, DateTime startTime, DateTime endTime, CancellationToken ct = default);

    /// <summary>
    /// Сохранить событие аномалии
    /// </summary>
    Task SaveAnomalyEventAsync(AnomalyEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Сохранить агрегированные значения окон
    /// </summary>
    Task SaveAggregatesAsync(string experimentId, IEnumerable<AggregatedValue> aggregates, CancellationToken ct = default);

    /// <summary>
    /// Получить все события аномалий для эксперимента
    /// </summary>
    Task<List<AnomalyEventRecord>> GetAnomalyEventsAsync(string experimentId, CancellationToken ct = default);

    /// <summary>
    /// Получить список индексов каналов, для которых есть сырые данные эксперимента.
    /// </summary>
    Task<List<int>> GetExperimentChannelIndicesAsync(string experimentId, CancellationToken ct = default);

    /// <summary>
    /// Получить диапазон времени сырых данных эксперимента.
    /// </summary>
    Task<(DateTime? start, DateTime? end)> GetExperimentDataRangeAsync(string experimentId, CancellationToken ct = default);

    /// <summary>
    /// Найти все эксперименты, оставшиеся в состоянии Running/Paused после сбоя,
    /// пометить их как Recovered и вернуть список.
    /// </summary>
    Task<List<Experiment>> RecoverOrphanedExperimentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Сохранить текущее распределение каналов по постам для интерфейса.
    /// </summary>
    Task SavePostChannelAssignmentsAsync(Dictionary<string, List<int>> assignments, CancellationToken ct = default);

    /// <summary>
    /// Загрузить сохраненное распределение каналов по постам для интерфейса.
    /// </summary>
    Task<Dictionary<string, List<int>>> GetPostChannelAssignmentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Сохранить пометки выбранных каналов по постам для интерфейса.
    /// </summary>
    Task SavePostChannelSelectionsAsync(Dictionary<string, List<int>> selections, CancellationToken ct = default);

    /// <summary>
    /// Загрузить пометки выбранных каналов по постам для интерфейса.
    /// </summary>
    Task<Dictionary<string, List<int>>> GetPostChannelSelectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Сохранить конфигурацию каналов интерфейса (лимиты и режим 10с).
    /// </summary>
    Task SaveUiChannelConfigsAsync(Dictionary<int, UiChannelConfigRecord> configs, CancellationToken ct = default);

    /// <summary>
    /// Загрузить конфигурацию каналов интерфейса (лимиты и режим 10с).
    /// </summary>
    Task<Dictionary<int, UiChannelConfigRecord>> GetUiChannelConfigsAsync(CancellationToken ct = default);
}

/// <summary>
/// Данные чекпоинта
/// </summary>
public class CheckpointData
{
    public string CheckpointTime { get; set; } = string.Empty;
    public string? LastSampleTimestamp { get; set; }
    public long? LastSampleId { get; set; }
    public string? QueueStateJson { get; set; }
    public string? StatisticsJson { get; set; }
}

/// <summary>
/// Реализация репозитория экспериментов
/// </summary>
public class ExperimentRepository : IExperimentRepository
{
    private readonly IDatabaseService _dbService;
    
    public ExperimentRepository(IDatabaseService dbService)
    {
        _dbService = dbService;
    }
    
    public async Task CreateAsync(Experiment experiment, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            INSERT INTO experiments (
                id, post_id, name, part_number, operator, refrigerant, state,
                start_time, post_a_enabled, post_b_enabled, post_c_enabled,
                batch_size, aggregation_interval_sec, checkpoint_interval_sec,
                created_at, updated_at
            ) VALUES (
                @Id, @PostId, @Name, @PartNumber, @Operator, @Refrigerant, @State,
                @StartTime, @PostAEnabled, @PostBEnabled, @PostCEnabled,
                @BatchSize, @AggregationIntervalSec, @CheckpointIntervalSec,
                @Now, @Now
            );
        ";
        
        await conn.ExecuteAsync(sql, new
        {
            experiment.Id,
            PostId = string.IsNullOrWhiteSpace(experiment.PostId) ? null : experiment.PostId,
            experiment.Name,
            PartNumber = experiment.PartNumber,
            Operator = experiment.Operator,
            Refrigerant = experiment.Refrigerant,
            State = experiment.State.ToString(),
            StartTime = experiment.StartTime.ToString("O"),
            Now = JsqClock.NowIso(),
            PostAEnabled = experiment.PostAEnabled ? 1 : 0,
            PostBEnabled = experiment.PostBEnabled ? 1 : 0,
            PostCEnabled = experiment.PostCEnabled ? 1 : 0,
            BatchSize = experiment.BatchSize,
            AggregationIntervalSec = experiment.AggregationIntervalSec,
            CheckpointIntervalSec = experiment.CheckpointIntervalSec
        });
        
        // Сохраняем конфигурацию каналов
        await SaveChannelConfigAsync(conn, experiment);
    }
    
    private async Task SaveChannelConfigAsync(IDbConnection conn, Experiment experiment)
    {
        const string sql = @"
            INSERT INTO channel_config (
                experiment_id, channel_index, channel_name, channel_group,
                channel_type, min_limit, max_limit, enabled,
                high_precision, agg_interval_sec
            ) VALUES (
                @ExperimentId, @ChannelIndex, @ChannelName, @ChannelGroup,
                @ChannelType, @MinLimit, @MaxLimit, @Enabled,
                @HighPrecision, @AggIntervalSec
            );
        ";
        
        // Каноническая конфигурация — строго из ChannelRegistry
        var channels = ChannelRegistry.All.Values
            .OrderBy(ch => ch.Index)
            .ToList();
        
        foreach (var ch in channels)
        {
            await conn.ExecuteAsync(sql, new
            {
                ExperimentId = experiment.Id,
                ChannelIndex = ch.Index,
                ChannelName = ch.Name,
                ChannelGroup = ch.Group.ToString(),
                ChannelType = ch.Type.ToString(),
                MinLimit = ch.MinLimit,
                MaxLimit = ch.MaxLimit,
                Enabled = ch.Enabled ? 1 : 0,
                HighPrecision = ch.HighPrecision ? 1 : 0,
                AggIntervalSec = ch.HighPrecision ? 10 : 20
            });
        }
    }
    
    public async Task UpdateStateAsync(string experimentId, ExperimentState state, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            UPDATE experiments 
            SET state = @State, updated_at = @Now
            WHERE id = @Id;
        ";
        
        await conn.ExecuteAsync(sql, new { Id = experimentId, State = state.ToString(), Now = JsqClock.NowIso() });
    }
    
    public async Task<Experiment?> GetByIdAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            SELECT
                id AS Id,
                post_id AS PostId,
                name AS Name,
                part_number AS PartNumber,
                operator AS Operator,
                refrigerant AS Refrigerant,
                state AS State,
                start_time AS StartTime,
                end_time AS EndTime,
                post_a_enabled AS PostAEnabled,
                post_b_enabled AS PostBEnabled,
                post_c_enabled AS PostCEnabled,
                batch_size AS BatchSize,
                aggregation_interval_sec AS AggregationIntervalSec,
                checkpoint_interval_sec AS CheckpointIntervalSec,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM experiments
            WHERE id = @Id;
        ";
        
        var entity = await conn.QueryFirstOrDefaultAsync<ExperimentEntity>(sql, new { Id = experimentId });
        
        return entity?.ToExperiment();
    }
    
    public async Task<Experiment?> GetActiveAsync(CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            SELECT
                id AS Id,
                post_id AS PostId,
                name AS Name,
                part_number AS PartNumber,
                operator AS Operator,
                refrigerant AS Refrigerant,
                state AS State,
                start_time AS StartTime,
                end_time AS EndTime,
                post_a_enabled AS PostAEnabled,
                post_b_enabled AS PostBEnabled,
                post_c_enabled AS PostCEnabled,
                batch_size AS BatchSize,
                aggregation_interval_sec AS AggregationIntervalSec,
                checkpoint_interval_sec AS CheckpointIntervalSec,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM experiments
            WHERE state IN ('Running', 'Paused') 
            ORDER BY created_at DESC 
            LIMIT 1;
        ";
        
        var entity = await conn.QueryFirstOrDefaultAsync<ExperimentEntity>(sql);
        
        return entity?.ToExperiment();
    }

    public async Task<List<Experiment>> GetByPostAsync(
        string postId,
        DateTime? startFrom = null,
        DateTime? startTo = null,
        string? searchText = null,
        CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        var trimmedSearch = searchText?.Trim();
        var like = string.IsNullOrWhiteSpace(trimmedSearch)
            ? null
            : $"%{trimmedSearch}%";

        const string sql = @"
            SELECT
                id AS Id,
                post_id AS PostId,
                name AS Name,
                part_number AS PartNumber,
                operator AS Operator,
                refrigerant AS Refrigerant,
                state AS State,
                start_time AS StartTime,
                end_time AS EndTime,
                post_a_enabled AS PostAEnabled,
                post_b_enabled AS PostBEnabled,
                post_c_enabled AS PostCEnabled,
                batch_size AS BatchSize,
                aggregation_interval_sec AS AggregationIntervalSec,
                checkpoint_interval_sec AS CheckpointIntervalSec,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM experiments
            WHERE post_id = @PostId
              AND state <> 'Idle'
              AND (@StartFrom IS NULL OR start_time >= @StartFrom)
              AND (@StartTo IS NULL OR start_time <= @StartTo)
              AND (
                    @Like IS NULL OR
                    name LIKE @Like OR
                    part_number LIKE @Like OR
                    operator LIKE @Like OR
                    refrigerant LIKE @Like
                  )
            ORDER BY start_time DESC, created_at DESC;
        ";

        var rows = await conn.QueryAsync<ExperimentEntity>(sql, new
        {
            PostId = postId,
            StartFrom = startFrom?.ToString("O"),
            StartTo = startTo?.ToString("O"),
            Like = like
        });

        return rows.Select(r => r.ToExperiment()).ToList();
    }
    
    public async Task FinalizeAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        using var transaction = conn.BeginTransaction();
        
        const string updateSql = @"
            UPDATE experiments 
            SET state = 'Finalized', end_time = @Now, updated_at = @Now
            WHERE id = @Id;
        ";
        
        var now = JsqClock.NowIso();
        await conn.ExecuteAsync(updateSql, new { Id = experimentId, Now = now }, transaction);
        
        // Логируем событие
        const string eventSql = @"
            INSERT INTO system_events (experiment_id, timestamp, event_type, severity, message)
            VALUES (@Id, @Now, 'ExperimentStop', 'Info', 'Experiment finalized');
        ";
        
        await conn.ExecuteAsync(eventSql, new { Id = experimentId, Now = now }, transaction);
        transaction.Commit();
    }
    
    public async Task SaveCheckpointAsync(string experimentId, CheckpointData checkpoint, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            INSERT INTO checkpoints (
                experiment_id, checkpoint_time, last_sample_timestamp,
                last_sample_id, queue_state_json, statistics_json
            ) VALUES (
                @ExperimentId, @CheckpointTime, @LastSampleTimestamp,
                @LastSampleId, @QueueStateJson, @StatisticsJson
            );
        ";
        
        await conn.ExecuteAsync(sql, new
        {
            ExperimentId = experimentId,
            CheckpointTime = checkpoint.CheckpointTime,
            checkpoint.LastSampleTimestamp,
            checkpoint.LastSampleId,
            checkpoint.QueueStateJson,
            checkpoint.StatisticsJson
        });
    }
    
    public async Task<CheckpointData?> GetLastCheckpointAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            SELECT * FROM checkpoints
            WHERE experiment_id = @Id
            ORDER BY checkpoint_time DESC
            LIMIT 1;
        ";

        return await conn.QueryFirstOrDefaultAsync<CheckpointData>(sql, new { Id = experimentId });
    }
    
    public async Task<List<(DateTime time, double value)>> GetChannelHistoryAsync(
        string experimentId, int channelIndex, DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string aggSql = @"
            SELECT timestamp AS Timestamp,
                   COALESCE(value_avg, value_max, value_min) AS Value
            FROM agg_samples_20s
            WHERE experiment_id = @ExperimentId
              AND channel_index = @ChannelIndex
              AND timestamp >= @StartTime
              AND timestamp <= @EndTime
            ORDER BY timestamp ASC;
        ";

        var rows = (await conn.QueryAsync<SampleRow>(aggSql, new
        {
            ExperimentId = experimentId,
            ChannelIndex = channelIndex,
            StartTime = startTime.ToString("O"),
            EndTime = endTime.ToString("O")
        })).ToList();

        if (rows.Count == 0)
        {
            const string rawSql = @"
                SELECT timestamp AS Timestamp, value AS Value
                FROM raw_samples
                WHERE experiment_id = @ExperimentId
                  AND channel_index = @ChannelIndex
                  AND timestamp >= @StartTime
                  AND timestamp <= @EndTime
                  AND is_valid = 1
                ORDER BY timestamp ASC;
            ";

            rows = (await conn.QueryAsync<SampleRow>(rawSql, new
            {
                ExperimentId = experimentId,
                ChannelIndex = channelIndex,
                StartTime = startTime.ToString("O"),
                EndTime = endTime.ToString("O")
            })).ToList();
        }

        return ParseSampleRows(rows);
    }

    public async Task<List<(DateTime time, double value)>> GetChannelHistoryAnyAsync(
        int channelIndex, DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string aggSql = @"
            SELECT timestamp AS Timestamp,
                   COALESCE(value_avg, value_max, value_min) AS Value
            FROM agg_samples_20s
            WHERE channel_index = @ChannelIndex
              AND timestamp >= @StartTime
              AND timestamp <= @EndTime
            ORDER BY timestamp ASC;
        ";

        var rows = (await conn.QueryAsync<SampleRow>(aggSql, new
        {
            ChannelIndex = channelIndex,
            StartTime = startTime.ToString("O"),
            EndTime = endTime.ToString("O")
        })).ToList();

        if (rows.Count == 0)
        {
            const string rawSql = @"
                SELECT timestamp AS Timestamp, value AS Value
                FROM raw_samples
                WHERE channel_index = @ChannelIndex
                  AND timestamp >= @StartTime
                  AND timestamp <= @EndTime
                  AND is_valid = 1
                ORDER BY timestamp ASC;
            ";

            rows = (await conn.QueryAsync<SampleRow>(rawSql, new
            {
                ChannelIndex = channelIndex,
                StartTime = startTime.ToString("O"),
                EndTime = endTime.ToString("O")
            })).ToList();
        }

        return ParseSampleRows(rows);
    }

    public async Task SaveAggregatesAsync(
        string experimentId,
        IEnumerable<AggregatedValue> aggregates,
        CancellationToken ct = default)
    {
        var rows = aggregates?.ToList() ?? new List<AggregatedValue>();
        if (rows.Count == 0)
            return;

        using var conn = _dbService.GetConnection();
        using var transaction = conn.BeginTransaction();

        const string sql = @"
            INSERT OR REPLACE INTO agg_samples_20s (
                experiment_id, timestamp, channel_index,
                value_min, value_max, value_avg,
                sample_count, invalid_count, quality_flag, agg_window_sec
            ) VALUES (
                @ExperimentId, @Timestamp, @ChannelIndex,
                @Min, @Max, @Avg,
                @SampleCount, @InvalidCount, @QualityFlag, @WindowSeconds
            );
        ";

        await conn.ExecuteAsync(sql, rows.Select(a => new
        {
            ExperimentId = experimentId,
            Timestamp = a.WindowStart.ToString("O"),
            a.ChannelIndex,
            Min = a.Min,
            Max = a.Max,
            Avg = a.Avg,
            a.SampleCount,
            a.InvalidCount,
            a.QualityFlag,
            WindowSeconds = a.WindowSeconds <= 0 ? 20 : a.WindowSeconds
        }), transaction);

        transaction.Commit();
    }

    private static List<(DateTime time, double value)> ParseSampleRows(IEnumerable<SampleRow> rows)
    {
        var result = new List<(DateTime, double)>();
        foreach (var r in rows)
        {
            if (DateTime.TryParse(r.Timestamp, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                result.Add((ts, r.Value));
        }
        return result;
    }

    private class SampleRow
    {
        public string Timestamp { get; set; } = string.Empty;
        public double Value { get; set; }
    }

    public async Task SaveAnomalyEventAsync(AnomalyEvent evt, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        const string sql = @"
            INSERT INTO anomaly_events (
                experiment_id, timestamp, channel_index, channel_name,
                anomaly_type, value, threshold
            ) VALUES (
                @ExperimentId, @Timestamp, @ChannelIndex, @ChannelName,
                @AnomalyType, @Value, @Threshold
            );
        ";
        await conn.ExecuteAsync(sql, new
        {
            ExperimentId = evt.ExperimentId,
            Timestamp = evt.Timestamp.ToString("O"),
            ChannelIndex = evt.ChannelIndex,
            ChannelName = evt.ChannelName,
            AnomalyType = evt.AnomalyType.ToString(),
            Value = evt.Value,
            Threshold = evt.Threshold
        });
    }

    public async Task<List<AnomalyEventRecord>> GetAnomalyEventsAsync(
        string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        const string sql = @"
            SELECT timestamp AS Timestamp, channel_index AS ChannelIndex,
                   channel_name AS ChannelName, anomaly_type AS AnomalyType,
                   value AS Value, threshold AS Threshold
            FROM anomaly_events
            WHERE experiment_id = @ExperimentId
            ORDER BY timestamp ASC;
        ";
        var rows = await conn.QueryAsync<AnomalyEventRecord>(sql, new { ExperimentId = experimentId });
        return rows.ToList();
    }

    public async Task<List<int>> GetExperimentChannelIndicesAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        const string sql = @"
            SELECT channel_index
            FROM (
                SELECT DISTINCT channel_index
                FROM agg_samples_20s
                WHERE experiment_id = @ExperimentId
                UNION
                SELECT DISTINCT channel_index
                FROM raw_samples
                WHERE experiment_id = @ExperimentId
            )
            ORDER BY channel_index ASC;
        ";

        var rows = await conn.QueryAsync<int>(sql, new { ExperimentId = experimentId });
        return rows.ToList();
    }

    public async Task<List<Experiment>> RecoverOrphanedExperimentsAsync(CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string selectSql = @"
            SELECT
                id AS Id,
                post_id AS PostId,
                name AS Name,
                part_number AS PartNumber,
                operator AS Operator,
                refrigerant AS Refrigerant,
                state AS State,
                start_time AS StartTime,
                end_time AS EndTime,
                post_a_enabled AS PostAEnabled,
                post_b_enabled AS PostBEnabled,
                post_c_enabled AS PostCEnabled,
                batch_size AS BatchSize,
                aggregation_interval_sec AS AggregationIntervalSec,
                checkpoint_interval_sec AS CheckpointIntervalSec,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM experiments
            WHERE state IN ('Running', 'Paused')
            ORDER BY created_at ASC;
        ";
        var entities = (await conn.QueryAsync<ExperimentEntity>(selectSql)).ToList();

        if (entities.Count == 0)
            return new List<Experiment>();

        const string updateSql = @"
            UPDATE experiments
            SET state = 'Recovered', updated_at = @Now
            WHERE state IN ('Running', 'Paused');
        ";
        await conn.ExecuteAsync(updateSql, new { Now = JsqClock.NowIso() });

        return entities.Select(e => e.ToExperiment()).ToList();
    }

    public async Task<(DateTime? start, DateTime? end)> GetExperimentDataRangeAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        const string sql = @"
            SELECT
                MIN(ts) AS StartTimestamp,
                MAX(ts) AS EndTimestamp
            FROM (
                SELECT timestamp AS ts
                FROM agg_samples_20s
                WHERE experiment_id = @ExperimentId
                UNION ALL
                SELECT timestamp AS ts
                FROM raw_samples
                WHERE experiment_id = @ExperimentId
            );
        ";

        var row = await conn.QueryFirstOrDefaultAsync<SampleRangeRow>(sql, new { ExperimentId = experimentId });
        if (row == null)
        {
            return (null, null);
        }

        DateTime? start = null;
        DateTime? end = null;

        if (!string.IsNullOrWhiteSpace(row.StartTimestamp) &&
            DateTime.TryParse(row.StartTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var s))
        {
            start = s;
        }

        if (!string.IsNullOrWhiteSpace(row.EndTimestamp) &&
            DateTime.TryParse(row.EndTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var e))
        {
            end = e;
        }

        return (start, end);
    }

    public async Task SavePostChannelAssignmentsAsync(Dictionary<string, List<int>> assignments, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("DELETE FROM post_channel_assignment;", transaction: tx);

        const string sql = @"
            INSERT INTO post_channel_assignment (post_id, channel_index, updated_at)
            VALUES (@PostId, @ChannelIndex, @UpdatedAt);
        ";

        var now = JsqClock.NowIso();
        foreach (var pair in assignments)
        {
            var postId = (pair.Key ?? string.Empty).Trim().ToUpperInvariant();
            if (postId is not ("A" or "B" or "C"))
                continue;

            foreach (var idx in pair.Value.Distinct().OrderBy(v => v))
            {
                await conn.ExecuteAsync(sql, new
                {
                    PostId = postId,
                    ChannelIndex = idx,
                    UpdatedAt = now
                }, tx);
            }
        }

        tx.Commit();
    }

    public async Task<Dictionary<string, List<int>>> GetPostChannelAssignmentsAsync(CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string sql = @"
            SELECT post_id AS PostId, channel_index AS ChannelIndex
            FROM post_channel_assignment
            ORDER BY post_id, channel_index;
        ";

        var rows = await conn.QueryAsync<PostChannelAssignmentRow>(sql);

        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new List<int>(),
            ["B"] = new List<int>(),
            ["C"] = new List<int>()
        };

        foreach (var row in rows)
        {
            var postId = (row.PostId ?? string.Empty).Trim().ToUpperInvariant();
            if (!result.TryGetValue(postId, out var list))
                continue;

            list.Add(row.ChannelIndex);
        }

        foreach (var post in result.Keys.ToList())
            result[post] = result[post].Distinct().OrderBy(v => v).ToList();

        return result;
    }

    public async Task SavePostChannelSelectionsAsync(Dictionary<string, List<int>> selections, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("DELETE FROM post_channel_selection;", transaction: tx);

        const string sql = @"
            INSERT INTO post_channel_selection (post_id, channel_index, is_selected, updated_at)
            VALUES (@PostId, @ChannelIndex, 1, @UpdatedAt);
        ";

        var now = JsqClock.NowIso();
        foreach (var pair in selections)
        {
            var postId = (pair.Key ?? string.Empty).Trim().ToUpperInvariant();
            if (postId is not ("A" or "B" or "C"))
                continue;

            foreach (var idx in pair.Value.Distinct().OrderBy(v => v))
            {
                await conn.ExecuteAsync(sql, new
                {
                    PostId = postId,
                    ChannelIndex = idx,
                    UpdatedAt = now
                }, tx);
            }
        }

        tx.Commit();
    }

    public async Task<Dictionary<string, List<int>>> GetPostChannelSelectionsAsync(CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string sql = @"
            SELECT post_id AS PostId, channel_index AS ChannelIndex
            FROM post_channel_selection
            WHERE is_selected = 1
            ORDER BY post_id, channel_index;
        ";

        var rows = await conn.QueryAsync<PostChannelAssignmentRow>(sql);

        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new List<int>(),
            ["B"] = new List<int>(),
            ["C"] = new List<int>()
        };

        foreach (var row in rows)
        {
            var postId = (row.PostId ?? string.Empty).Trim().ToUpperInvariant();
            if (!result.TryGetValue(postId, out var list))
                continue;

            list.Add(row.ChannelIndex);
        }

        foreach (var post in result.Keys.ToList())
            result[post] = result[post].Distinct().OrderBy(v => v).ToList();

        return result;
    }

    public async Task SaveUiChannelConfigsAsync(Dictionary<int, UiChannelConfigRecord> configs, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("DELETE FROM ui_channel_config;", transaction: tx);

        const string sql = @"
            INSERT INTO ui_channel_config (channel_index, min_limit, max_limit, high_precision, updated_at)
            VALUES (@ChannelIndex, @MinLimit, @MaxLimit, @HighPrecision, @UpdatedAt);
        ";

        var now = JsqClock.NowIso();
        foreach (var pair in configs.OrderBy(p => p.Key))
        {
            var idx = pair.Key;
            var cfg = pair.Value;
            await conn.ExecuteAsync(sql, new
            {
                ChannelIndex = idx,
                MinLimit = cfg.MinLimit,
                MaxLimit = cfg.MaxLimit,
                HighPrecision = cfg.HighPrecision ? 1 : 0,
                UpdatedAt = now
            }, tx);
        }

        tx.Commit();
    }

    public async Task<Dictionary<int, UiChannelConfigRecord>> GetUiChannelConfigsAsync(CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string sql = @"
            SELECT channel_index AS ChannelIndex,
                   min_limit AS MinLimit,
                   max_limit AS MaxLimit,
                   high_precision AS HighPrecision
            FROM ui_channel_config
            ORDER BY channel_index;
        ";

        var rows = await conn.QueryAsync<UiChannelConfigRow>(sql);
        return rows.ToDictionary(
            r => r.ChannelIndex,
            r => new UiChannelConfigRecord
            {
                MinLimit = r.MinLimit,
                MaxLimit = r.MaxLimit,
                HighPrecision = r.HighPrecision != 0
            });
    }
}

/// <summary>
/// Запись события аномалии для отображения в истории
/// </summary>
public class AnomalyEventRecord
{
    public string Timestamp { get; set; } = string.Empty;
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string AnomalyType { get; set; } = string.Empty;
    public double? Value { get; set; }
    public double? Threshold { get; set; }
}

internal class SampleRangeRow
{
    public string? StartTimestamp { get; set; }
    public string? EndTimestamp { get; set; }
}

internal class PostChannelAssignmentRow
{
    public string PostId { get; set; } = string.Empty;
    public int ChannelIndex { get; set; }
}

internal class UiChannelConfigRow
{
    public int ChannelIndex { get; set; }
    public double? MinLimit { get; set; }
    public double? MaxLimit { get; set; }
    public int HighPrecision { get; set; }
}

public class UiChannelConfigRecord
{
    public double? MinLimit { get; set; }
    public double? MaxLimit { get; set; }
    public bool HighPrecision { get; set; }
}

/// <summary>
/// Extension methods для конвертации
/// </summary>
public static class ExperimentEntityExtensions
{
    public static Experiment ToExperiment(this ExperimentEntity entity)
    {
        var startTime = ParseDateTime(entity.StartTime)
                        ?? ParseDateTime(entity.CreatedAt)
                        ?? DateTime.MinValue;

        var endTime = ParseDateTime(entity.EndTime);

        return new Experiment
        {
            Id = entity.Id,
            PostId = entity.PostId,
            Name = entity.Name,
            PartNumber = entity.PartNumber,
            Operator = entity.Operator,
            Refrigerant = entity.Refrigerant,
            State = (ExperimentState)Enum.Parse(typeof(ExperimentState), entity.State),
            StartTime = startTime,
            EndTime = endTime,
            PostAEnabled = entity.PostAEnabled,
            PostBEnabled = entity.PostBEnabled,
            PostCEnabled = entity.PostCEnabled,
            BatchSize = entity.BatchSize,
            AggregationIntervalSec = entity.AggregationIntervalSec,
            CheckpointIntervalSec = entity.CheckpointIntervalSec
        };
    }

    private static DateTime? ParseDateTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        if (DateTime.TryParse(text, out dt))
            return dt;

        return null;
    }
}
