using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JSQ.Capture;
using JSQ.Core.Models;
using JSQ.Decode;
using JSQ.Rules;
using JSQ.Storage;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Реальный сервис управления экспериментами с поддержкой независимых постов A/B/C
/// </summary>
public class ExperimentService : IExperimentService, IDisposable
{
    private readonly TcpCaptureService _captureService;
    private readonly ChannelDecoder _decoder = new ChannelDecoder();
    private readonly IDatabaseService _dbService;
    private readonly IBatchWriter _batchWriter;
    private readonly IExperimentRepository _experimentRepo;

    // Состояние каждого поста
    private readonly Dictionary<string, PostState> _postStates = new();
    // Быстрый роутинг: channelIndex → postId
    private readonly Dictionary<int, string> _channelPostMap = new();
    private readonly object _stateLock = new();

    // Параметры подключения
    private string _host = "192.168.0.214";
    private int _port = 55555;

    public event Action<SystemHealth>? HealthUpdated;
    public event Action<LogEntry>? LogReceived;
    public event Action<int, double>? ChannelValueReceived;
    public event Action<string, AnomalyEvent>? PostAnomalyDetected;

    private CancellationTokenSource? _healthUpdateCts;

    public ExperimentService(IDatabaseService dbService, IBatchWriter batchWriter, IExperimentRepository experimentRepo)
    {
        _dbService = dbService;
        _batchWriter = batchWriter;
        _experimentRepo = experimentRepo;

        _captureService = new TcpCaptureService();
        _captureService.DataReceived += OnDataReceived;
        _captureService.StatusChanged += OnStatusChanged;

        Task.Run(async () => { try { await _dbService.InitializeAsync(); } catch { } });

        StartHealthUpdates();
    }

    // ─── Внутреннее состояние поста ─────────────────────────────────────────

    private class PostState
    {
        public string PostId { get; set; } = string.Empty;
        public Experiment Experiment { get; set; } = new();
        public IAnomalyDetector AnomalyDetector { get; set; } = null!;
        public IAggregationService AggregationService { get; set; } = null!;
        public HashSet<int> ChannelIndices { get; set; } = new();
        public bool IsRunning { get; set; }
        public bool IsPaused { get; set; }
        public int AggregationTick { get; set; }
        public int CheckpointTick { get; set; }
    }

    // ─── Публичный API ──────────────────────────────────────────────────────

    public void Configure(string host, int port, int timeoutMs)
    {
        _host = host;
        _port = port;
    }

    public void BeginMonitoring()
    {
        var host = _host;
        var port = _port;

        Task.Run(async () =>
        {
            try
            {
                if (_captureService.Status == ConnectionStatus.Connected)
                    await _captureService.DisconnectAsync();

                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Info",
                    Source = "System",
                    Message = $"Подключение к {host}:{port}..."
                });

                await _captureService.ConnectAsync(host, port);

                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Info",
                    Source = "System",
                    Message = $"Подключено. Получение данных от {host}:{port}."
                });
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Warning",
                    Source = "System",
                    Message = $"Не удалось подключиться к {host}:{port}: {ex.Message}"
                });
            }
        });
    }

    public void StartPost(string postId, Experiment experiment, IReadOnlyList<int> channelIndices)
    {
        lock (_stateLock)
        {
            if (_postStates.ContainsKey(postId))
                return; // уже запущен

            if (string.IsNullOrEmpty(experiment.Id))
                experiment.Id = Guid.NewGuid().ToString("N");

            experiment.StartTime = DateTime.Now;
            experiment.State = ExperimentState.Running;

            var anomalyDetector = new AnomalyDetector(experiment.Id);
            var aggregationService = new AggregationService(experiment.AggregationIntervalSec);

            // Правила только для каналов этого поста
            var rules = new List<AnomalyRule>();
            foreach (var idx in channelIndices)
            {
                if (!ChannelRegistry.All.TryGetValue(idx, out var def)) continue;
                if (def.MinLimit.HasValue || def.MaxLimit.HasValue)
                {
                    rules.Add(new AnomalyRule
                    {
                        ChannelIndex = idx,
                        ChannelName = def.Name,
                        MinLimit = def.MinLimit,
                        MaxLimit = def.MaxLimit,
                        Enabled = true,
                        DebounceCount = 3
                    });
                }
            }
            anomalyDetector.LoadRules(rules);

            var state = new PostState
            {
                PostId = postId,
                Experiment = experiment,
                AnomalyDetector = anomalyDetector,
                AggregationService = aggregationService,
                ChannelIndices = new HashSet<int>(channelIndices),
                IsRunning = true,
                IsPaused = false
            };

            _postStates[postId] = state;

            // Регистрируем каналы в роутинге
            foreach (var idx in channelIndices)
                _channelPostMap[idx] = postId;
        }

        // Сохраняем в БД асинхронно
        Task.Run(async () =>
        {
            try
            {
                await _experimentRepo.CreateAsync(experiment);
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Info",
                    Source = "Storage",
                    Post = postId,
                    Message = $"Эксперимент '{experiment.Name}' (Пост {postId}) создан в БД"
                });
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Warning",
                    Source = "Storage",
                    Post = postId,
                    Message = $"Не удалось сохранить в БД: {ex.Message}"
                });
            }
        });

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "System",
            Post = postId,
            Message = $"Пост {postId}: запись '{experiment.Name}' запущена ({channelIndices.Count} каналов)"
        });
    }

    public void PausePost(string postId)
    {
        lock (_stateLock)
        {
            if (!_postStates.TryGetValue(postId, out var state) || !state.IsRunning)
                return;

            state.IsPaused = true;
            state.Experiment.State = ExperimentState.Paused;

            Task.Run(() => _experimentRepo.UpdateStateAsync(state.Experiment.Id, ExperimentState.Paused));
        }

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "System",
            Post = postId,
            Message = $"Пост {postId}: запись приостановлена"
        });
    }

    public void ResumePost(string postId)
    {
        lock (_stateLock)
        {
            if (!_postStates.TryGetValue(postId, out var state) || !state.IsPaused)
                return;

            state.IsPaused = false;
            state.Experiment.State = ExperimentState.Running;

            Task.Run(() => _experimentRepo.UpdateStateAsync(state.Experiment.Id, ExperimentState.Running));
        }

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "System",
            Post = postId,
            Message = $"Пост {postId}: запись возобновлена"
        });
    }

    public void StopPost(string postId)
    {
        PostState? state;
        lock (_stateLock)
        {
            if (!_postStates.TryGetValue(postId, out state))
                return;

            state.IsRunning = false;
            _postStates.Remove(postId);

            // Удаляем каналы из роутинга
            foreach (var idx in state.ChannelIndices)
                _channelPostMap.Remove(idx);
        }

        var experiment = state.Experiment;
        Task.Run(async () =>
        {
            try
            {
                await _batchWriter.FlushAsync();
                await _experimentRepo.FinalizeAsync(experiment.Id);
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Info",
                    Source = "Storage",
                    Post = postId,
                    Message = $"Пост {postId}: '{experiment.Name}' завершён и сохранён в БД"
                });
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Error",
                    Source = "Storage",
                    Post = postId,
                    Message = $"Ошибка при завершении поста {postId}: {ex.Message}"
                });
            }
        });

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "System",
            Post = postId,
            Message = $"Пост {postId}: запись остановлена"
        });
    }

    public ExperimentState GetPostState(string postId)
    {
        lock (_stateLock)
        {
            if (!_postStates.TryGetValue(postId, out var state))
                return ExperimentState.Idle;
            if (state.IsPaused) return ExperimentState.Paused;
            if (state.IsRunning) return ExperimentState.Running;
            return ExperimentState.Stopped;
        }
    }

    public SystemHealth GetCurrentHealth() => new SystemHealth { TotalChannels = 134 };

    // ─── Приём данных и роутинг ─────────────────────────────────────────────

    private void OnDataReceived(object sender, byte[] data)
    {
        var values = _decoder.Feed(data, data.Length);

        if (values.Count == 0) return;

        foreach (var cv in values)
        {
            ChannelValueReceived?.Invoke(cv.Index, cv.Value);
        }

        // Роутим данные по постам — под локом читаем snapshot, потом работаем
        List<(string postId, PostState state, List<Sample> samples)>? routes = null;

        lock (_stateLock)
        {
            if (_postStates.Count == 0) return;

            // Группируем значения по постам
            Dictionary<string, List<Sample>>? bySt = null;
            foreach (var cv in values)
            {
                if (!_channelPostMap.TryGetValue(cv.Index, out var pid)) continue;
                if (!_postStates.TryGetValue(pid, out var ps)) continue;
                if (!ps.IsRunning || ps.IsPaused) continue;

                bySt ??= new Dictionary<string, List<Sample>>();
                if (!bySt.TryGetValue(pid, out var list))
                {
                    list = new List<Sample>();
                    bySt[pid] = list;
                }

                double storageValue = double.IsNaN(cv.Value) ? -99.0 : cv.Value;
                list.Add(new Sample(cv.Index, storageValue, cv.Timestamp));
            }

            if (bySt == null) return;

            routes = new List<(string, PostState, List<Sample>)>();
            foreach (var kvp in bySt)
            {
                if (_postStates.TryGetValue(kvp.Key, out var ps))
                    routes.Add((kvp.Key, ps, kvp.Value));
            }
        }

        if (routes == null) return;

        foreach (var (postId, state, samples) in routes)
        {
            _batchWriter.AddSamples(state.Experiment.Id, samples);

            foreach (var sample in samples)
            {
                state.AggregationService.AddSample(sample);

                if (sample.IsValid)
                {
                    foreach (var evt in state.AnomalyDetector.CheckValue(sample.ChannelIndex, sample.Value, sample.Timestamp))
                        PostAnomalyDetected?.Invoke(postId, evt);
                }
            }
        }
    }

    private void OnStatusChanged(object sender, ConnectionStatus status)
    {
        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = status == ConnectionStatus.Error ? "Error" : "Info",
            Source = "TCP",
            Message = $"Статус подключения: {status}"
        });
    }

    // ─── Фоновый цикл здоровья ──────────────────────────────────────────────

    private void StartHealthUpdates()
    {
        _healthUpdateCts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!_healthUpdateCts.Token.IsCancellationRequested)
            {
                try { UpdateHealth(); } catch { }
                try { ProcessAggregates(); } catch { }

                await Task.Delay(1000, _healthUpdateCts.Token).ConfigureAwait(false);

                // Чекпоинт каждые 30 секунд
                List<PostState> running;
                lock (_stateLock)
                    running = _postStates.Values.Where(s => s.IsRunning && !s.IsPaused).ToList();

                foreach (var state in running)
                {
                    state.CheckpointTick++;
                    if (state.CheckpointTick >= 30)
                    {
                        state.CheckpointTick = 0;
                        try { await SaveCheckpointAsync(state); } catch { }
                    }
                }
            }
        }, _healthUpdateCts.Token);
    }

    private void UpdateHealth()
    {
        var stats = _captureService.Statistics;
        int runningPosts;
        lock (_stateLock)
            runningPosts = _postStates.Count(s => s.Value.IsRunning);

        var health = new SystemHealth
        {
            TotalChannels = 134,
            TotalSamplesReceived = stats.TotalPacketsReceived,
            SamplesPerSecond = stats.BytesPerSecond / 100,
            OverallStatus = stats.Status == ConnectionStatus.Connected
                ? HealthStatus.OK
                : HealthStatus.NoData
        };
        HealthUpdated?.Invoke(health);
    }

    private void ProcessAggregates()
    {
        List<PostState> active;
        lock (_stateLock)
            active = _postStates.Values.Where(s => s.IsRunning && !s.IsPaused).ToList();

        foreach (var state in active)
        {
            state.AggregationTick++;
            if (state.AggregationTick < 5) continue;
            state.AggregationTick = 0;

            foreach (var agg in state.AggregationService.GetReadyAggregates())
            {
                foreach (var evt in state.AnomalyDetector.CheckAggregate(agg))
                    PostAnomalyDetected?.Invoke(state.PostId, evt);
            }

            foreach (var evt in state.AnomalyDetector.CheckTimeouts(DateTime.Now))
                PostAnomalyDetected?.Invoke(state.PostId, evt);
        }
    }

    private async Task SaveCheckpointAsync(PostState state)
    {
        var checkpoint = new CheckpointData
        {
            CheckpointTime = DateTime.Now.ToString("O"),
            LastSampleTimestamp = DateTime.Now.ToString("O")
        };
        await _experimentRepo.SaveCheckpointAsync(state.Experiment.Id, checkpoint);
    }

    public void Dispose()
    {
        _healthUpdateCts?.Cancel();
        _captureService.Dispose();
        _batchWriter.Dispose();
        _dbService.Dispose();
        _healthUpdateCts?.Dispose();
    }
}
