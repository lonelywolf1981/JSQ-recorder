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
                id, name, part_number, operator, refrigerant, state,
                start_time, post_a_enabled, post_b_enabled, post_c_enabled,
                batch_size, aggregation_interval_sec, checkpoint_interval_sec,
                created_at, updated_at
            ) VALUES (
                @Id, @Name, @PartNumber, @Operator, @Refrigerant, @State,
                @StartTime, @PostAEnabled, @PostBEnabled, @PostCEnabled,
                @BatchSize, @AggregationIntervalSec, @CheckpointIntervalSec,
                datetime('now'), datetime('now')
            );
        ";
        
        await conn.ExecuteAsync(sql, new
        {
            experiment.Id,
            experiment.Name,
            PartNumber = experiment.PartNumber,
            Operator = experiment.Operator,
            Refrigerant = experiment.Refrigerant,
            State = experiment.State.ToString(),
            StartTime = experiment.StartTime.ToString("O"),
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
                channel_type, min_limit, max_limit, enabled
            ) VALUES (
                @ExperimentId, @ChannelIndex, @ChannelName, @ChannelGroup,
                @ChannelType, @MinLimit, @MaxLimit, @Enabled
            );
        ";
        
        // Генерируем конфигурацию для всех 134 каналов
        var channels = GenerateDefaultChannelConfig();
        
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
                Enabled = ch.Enabled ? 1 : 0
            });
        }
    }
    
    private List<ChannelDefinition> GenerateDefaultChannelConfig()
    {
        // Генерируем дефолтную конфигурацию каналов
        var channels = new List<ChannelDefinition>();
        
        // Пост A (0-45)
        for (int i = 0; i <= 45; i++)
        {
            channels.Add(new ChannelDefinition
            {
                Index = i,
                Name = i switch
                {
                    0 => "A-Pc",
                    1 => "A-Pe",
                    <= 32 => $"A-T{i}",
                    <= 36 => i switch { 33 => "A-I", 34 => "A-F", 35 => "A-V", 36 => "A-W" },
                    _ => $"A-{i}"
                },
                Group = ChannelGroup.PostA,
                Type = i switch
                {
                    0 or 1 => ChannelType.Pressure,
                    <= 32 => ChannelType.Temperature,
                    _ => ChannelType.Electrical
                },
                Enabled = true
            });
        }
        
        // Пост B (46-91)
        for (int i = 46; i <= 91; i++)
        {
            channels.Add(new ChannelDefinition
            {
                Index = i,
                Name = $"B-T{i - 46}",
                Group = ChannelGroup.PostB,
                Type = ChannelType.Temperature,
                Enabled = true
            });
        }
        
        // Пост C (92-137)
        for (int i = 92; i <= 137; i++)
        {
            channels.Add(new ChannelDefinition
            {
                Index = i,
                Name = $"C-T{i - 92}",
                Group = ChannelGroup.PostC,
                Type = ChannelType.Temperature,
                Enabled = true
            });
        }
        
        return channels;
    }
    
    public async Task UpdateStateAsync(string experimentId, ExperimentState state, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            UPDATE experiments 
            SET state = @State, updated_at = datetime('now')
            WHERE id = @Id;
        ";
        
        await conn.ExecuteAsync(sql, new { Id = experimentId, State = state.ToString() });
    }
    
    public async Task<Experiment?> GetByIdAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            SELECT * FROM experiments WHERE id = @Id;
        ";
        
        var entity = await conn.QueryFirstOrDefaultAsync<ExperimentEntity>(sql, new { Id = experimentId });
        
        return entity?.ToExperiment();
    }
    
    public async Task<Experiment?> GetActiveAsync(CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        
        const string sql = @"
            SELECT * FROM experiments 
            WHERE state IN ('Running', 'Paused') 
            ORDER BY created_at DESC 
            LIMIT 1;
        ";
        
        var entity = await conn.QueryFirstOrDefaultAsync<ExperimentEntity>(sql);
        
        return entity?.ToExperiment();
    }
    
    public async Task FinalizeAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        using var transaction = conn.BeginTransaction();
        
        const string updateSql = @"
            UPDATE experiments 
            SET state = 'Finalized', end_time = datetime('now'), updated_at = datetime('now')
            WHERE id = @Id;
        ";
        
        await conn.ExecuteAsync(updateSql, new { Id = experimentId }, transaction);
        
        // Логируем событие
        const string eventSql = @"
            INSERT INTO system_events (experiment_id, event_type, severity, message)
            VALUES (@Id, 'ExperimentStop', 'Info', 'Experiment finalized');
        ";
        
        await conn.ExecuteAsync(eventSql, new { Id = experimentId }, transaction);
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

        const string sql = @"
            SELECT timestamp AS Timestamp, value AS Value
            FROM raw_samples
            WHERE experiment_id = @ExperimentId
              AND channel_index = @ChannelIndex
              AND timestamp >= @StartTime
              AND timestamp <= @EndTime
              AND is_valid = 1
            ORDER BY timestamp ASC;
        ";

        var rows = await conn.QueryAsync<SampleRow>(sql, new
        {
            ExperimentId = experimentId,
            ChannelIndex = channelIndex,
            StartTime = startTime.ToString("O"),
            EndTime = endTime.ToString("O")
        });

        return ParseSampleRows(rows);
    }

    public async Task<List<(DateTime time, double value)>> GetChannelHistoryAnyAsync(
        int channelIndex, DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();

        const string sql = @"
            SELECT timestamp AS Timestamp, value AS Value
            FROM raw_samples
            WHERE channel_index = @ChannelIndex
              AND timestamp >= @StartTime
              AND timestamp <= @EndTime
              AND is_valid = 1
            ORDER BY timestamp ASC;
        ";

        var rows = await conn.QueryAsync<SampleRow>(sql, new
        {
            ChannelIndex = channelIndex,
            StartTime = startTime.ToString("O"),
            EndTime = endTime.ToString("O")
        });

        return ParseSampleRows(rows);
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
            SELECT DISTINCT channel_index
            FROM raw_samples
            WHERE experiment_id = @ExperimentId
            ORDER BY channel_index ASC;
        ";

        var rows = await conn.QueryAsync<int>(sql, new { ExperimentId = experimentId });
        return rows.ToList();
    }

    public async Task<(DateTime? start, DateTime? end)> GetExperimentDataRangeAsync(string experimentId, CancellationToken ct = default)
    {
        using var conn = _dbService.GetConnection();
        const string sql = @"
            SELECT MIN(timestamp) AS StartTimestamp, MAX(timestamp) AS EndTimestamp
            FROM raw_samples
            WHERE experiment_id = @ExperimentId;
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

/// <summary>
/// Extension methods для конвертации
/// </summary>
public static class ExperimentEntityExtensions
{
    public static Experiment ToExperiment(this ExperimentEntity entity)
    {
        return new Experiment
        {
            Id = entity.Id,
            Name = entity.Name,
            PartNumber = entity.PartNumber,
            Operator = entity.Operator,
            Refrigerant = entity.Refrigerant,
            State = (ExperimentState)Enum.Parse(typeof(ExperimentState), entity.State),
            StartTime = string.IsNullOrEmpty(entity.StartTime) ? DateTime.MinValue : DateTime.Parse(entity.StartTime),
            EndTime = string.IsNullOrEmpty(entity.EndTime) ? null : DateTime.Parse(entity.EndTime),
            PostAEnabled = entity.PostAEnabled,
            PostBEnabled = entity.PostBEnabled,
            PostCEnabled = entity.PostCEnabled,
            BatchSize = entity.BatchSize,
            AggregationIntervalSec = entity.AggregationIntervalSec,
            CheckpointIntervalSec = entity.CheckpointIntervalSec
        };
    }
}
