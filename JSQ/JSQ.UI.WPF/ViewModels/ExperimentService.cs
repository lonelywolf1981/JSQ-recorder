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
using System.Globalization;

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
    // Роутинг: channelIndex → HashSet<postId> (один канал может идти в несколько постов)
    private readonly Dictionary<int, HashSet<string>> _channelPostMap = new();
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _monitoringLock = new(1, 1);
    private int _suppressAutoReconnect;

    // Параметры подключения
    private string _host = "192.168.0.214";
    private int _port = 55555;

    // Бинарный протокол передатчика (подтверждено ETL):
    // - init-пакет словаря управлений
    // - команды GO start/stop (0x0015)
    // - команды DOxx ON/OFF (20 байт)
    private static readonly byte[] ProtocolInitPacket = HexToBytes(
        "0000012400000018000000094469674F757453657400000007436F6D616E6469000000084C6F6F7073536574000000084C6F6F70734465660000000A496E7075744C6F6F70730000000553657455690000000850617373614461410000000944495374616E6462790000000B4C6F6F705374616E64427900000008536574444F2D4F4E00000009536574444F2D4F4646000000035265670000000B43616E63656C6C615265670000000B496E697A696F526567545800000006436F6E6669670000000C43616C696272617A696F6E650000000653656C6563740000000A737461746F43616C6962000000064F66667365740000000754696D6572444F00000008536574706F696E7400000007737461746F474F00000009446174652D54696D650000000444617461");

    private static readonly byte[] GoStartPacket = HexToBytes("0000000400150101");
    private static readonly byte[] GoStopPacket = HexToBytes("0000000400150000");

    // Соответствие постов бинарным выходам управления (подтверждено двумя ETL).
    private static readonly Dictionary<string, int> PostDoIndexMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, // DO01
        ["B"] = 2, // DO02
        ["C"] = 3  // DO03
    };

    public event Action<SystemHealth>? HealthUpdated;
    public event Action<LogEntry>? LogReceived;
    public event Action<int, double>? ChannelValueReceived;
    public event Action<string, AnomalyEvent>? PostAnomalyDetected;

    private CancellationTokenSource? _healthUpdateCts;
    private Task _initTask = Task.CompletedTask;
    private volatile bool _recoveryDone;

    // Счётчики для SamplesPerSecond
    private long _samplesThisWindow = 0;
    private long _lastSamplesPerSecond = 0;

    public ExperimentService(IDatabaseService dbService, IBatchWriter batchWriter, IExperimentRepository experimentRepo)
    {
        _dbService = dbService;
        _batchWriter = batchWriter;
        _experimentRepo = experimentRepo;

        _captureService = new TcpCaptureService();
        _captureService.DataReceived += OnDataReceived;
        _captureService.StatusChanged += OnStatusChanged;

        _initTask = Task.Run(async () => { try { await _dbService.InitializeAsync(); } catch { } });

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
        _captureService.ConnectionTimeoutMs = timeoutMs;
    }

    public Task<bool> SetPowerOnAsync(CancellationToken ct = default)
    {
        return SetAllPowerAsync(enable: true, ct);
    }

    public Task<bool> SetPowerOffAsync(CancellationToken ct = default)
    {
        return SetAllPowerAsync(enable: false, ct);
    }

    public Task<bool> SetPostPowerOnAsync(string postId, CancellationToken ct = default)
    {
        return SendPostPowerCommandAsync(postId, enable: true, ct);
    }

    public Task<bool> SetPostPowerOffAsync(string postId, CancellationToken ct = default)
    {
        return SendPostPowerCommandAsync(postId, enable: false, ct);
    }

    public async Task<bool> SetAllPowerOffAsync(CancellationToken ct = default)
    {
        return await SetAllPowerAsync(enable: false, ct);
    }

    public async Task<Dictionary<string, List<int>>> LoadPostChannelAssignmentsAsync(CancellationToken ct = default)
    {
        await _initTask;
        return await _experimentRepo.GetPostChannelAssignmentsAsync(ct);
    }

    public async Task SavePostChannelAssignmentsAsync(Dictionary<string, List<int>> assignments, CancellationToken ct = default)
    {
        await _initTask;
        await _experimentRepo.SavePostChannelAssignmentsAsync(assignments, ct);
    }

    public async Task<Dictionary<string, List<int>>> LoadPostChannelSelectionsAsync(CancellationToken ct = default)
    {
        await _initTask;
        return await _experimentRepo.GetPostChannelSelectionsAsync(ct);
    }

    public async Task SavePostChannelSelectionsAsync(Dictionary<string, List<int>> selections, CancellationToken ct = default)
    {
        await _initTask;
        await _experimentRepo.SavePostChannelSelectionsAsync(selections, ct);
    }

    public async Task<Dictionary<int, UiChannelConfigRecord>> LoadUiChannelConfigsAsync(CancellationToken ct = default)
    {
        await _initTask;
        return await _experimentRepo.GetUiChannelConfigsAsync(ct);
    }

    public async Task SaveUiChannelConfigsAsync(Dictionary<int, UiChannelConfigRecord> configs, CancellationToken ct = default)
    {
        await _initTask;
        await _experimentRepo.SaveUiChannelConfigsAsync(configs, ct);
    }

    public (string host, int port, ConnectionStatus status, DateTime lastPacketTime) GetConnectionSnapshot()
    {
        var stats = _captureService.Statistics;
        return (_host, _port, _captureService.Status, stats.LastPacketTime);
    }

    public void BeginMonitoring()
    {
        var host = _host;
        var port = _port;

        Task.Run(async () =>
        {
            // Защита от параллельных переподключений (например, повторный Save в настройках)
            if (!await _monitoringLock.WaitAsync(0))
                return;

            try
            {
                // Crash recovery: выполняется только один раз при первом BeginMonitoring.
                // Ищем эксперименты, прерванные предыдущим сбоем, и помечаем их как RECOVERED.
                if (!_recoveryDone)
                {
                    _recoveryDone = true;
                    try
                    {
                        await _initTask;
                        var orphaned = await _experimentRepo.RecoverOrphanedExperimentsAsync();
                        foreach (var exp in orphaned)
                        {
                            LogReceived?.Invoke(new LogEntry
                            {
                                Timestamp = JsqClock.Now,
                                Level = "Warning",
                                Source = "Recovery",
                                Message = $"Эксперимент '{exp.Name}' был прерван сбоем — помечен как RECOVERED"
                            });
                        }
                    }
                    catch { }
                }

                if (_captureService.Status == ConnectionStatus.Connected ||
                    _captureService.Status == ConnectionStatus.Connecting)
                {
                    // Намеренный disconnect во время переподключения не должен
                    // запускать авто-reconnect из OnStatusChanged.
                    Interlocked.Exchange(ref _suppressAutoReconnect, 1);
                    try
                    {
                        await _captureService.DisconnectAsync();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _suppressAutoReconnect, 0);
                    }
                }

                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = JsqClock.Now,
                    Level = "Info",
                    Source = "System",
                    Message = $"Подключение к {host}:{port}..."
                });

                // Стартуем новый цикл чтения с чистым декодером,
                // чтобы остатки неполного пакета не мешали после реконнекта.
                _decoder.Reset();

                await _captureService.ConnectAsync(host, port);

                // После подключения передатчик ожидает бинарный init-пакет протокола.
                // Без этого пакета приложение может видеть "Connected", но не получать каналов.
                _ = Task.Run(async () =>
                {
                    try { await SendInitializationSequenceAsync(); }
                    catch { }
                });

                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = JsqClock.Now,
                    Level = "Info",
                    Source = "System",
                    Message = $"Подключено. Получение данных от {host}:{port}."
                });
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = JsqClock.Now,
                    Level = "Warning",
                    Source = "System",
                    Message = $"Не удалось подключиться к {host}:{port}: {ex.Message}"
                });
            }
            finally
            {
                _monitoringLock.Release();
            }
        });
    }

    public void StartPost(string postId, Experiment experiment, IReadOnlyList<int> channelIndices)
    {
        bool shouldSendGoStart;

        lock (_stateLock)
        {
            if (_postStates.ContainsKey(postId))
                return; // уже запущен

            shouldSendGoStart = !_postStates.Values.Any(s => s.IsRunning && !s.IsPaused);

            if (string.IsNullOrEmpty(experiment.Id))
                experiment.Id = Guid.NewGuid().ToString("N");

            experiment.StartTime = JsqClock.Now;
            experiment.State = ExperimentState.Running;
            experiment.PostId = postId;

            var highPrecisionChannels = channelIndices
                .Where(idx => ChannelRegistry.All.TryGetValue(idx, out var def) && def.HighPrecision)
                .ToList();

            var anomalyDetector = new AnomalyDetector(experiment.Id);
            var aggregationService = new AggregationService(
                experiment.AggregationIntervalSec,
                highPrecisionChannels,
                highPrecisionIntervalSeconds: 10);

            // Правила для всех каналов поста: лимиты из реестра + таймаут NoData
            var rules = new List<AnomalyRule>();
            foreach (var idx in channelIndices)
            {
                ChannelRegistry.All.TryGetValue(idx, out var def);
                rules.Add(new AnomalyRule
                {
                    ChannelIndex = idx,
                    ChannelName = def?.Name ?? $"v{idx:D3}",
                    MinLimit = def?.MinLimit,
                    MaxLimit = def?.MaxLimit,
                    Enabled = true,
                    DebounceCount = 3,
                    NoDataTimeoutSec = 10
                });
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

            // Регистрируем каналы в роутинге (один канал может идти в несколько постов)
            foreach (var idx in channelIndices)
            {
                if (!_channelPostMap.TryGetValue(idx, out var set))
                    _channelPostMap[idx] = set = new HashSet<string>();
                set.Add(postId);
            }
        }

        if (shouldSendGoStart)
        {
            _ = Task.Run(async () =>
            {
                await SendBinaryPacketAsync(
                    GoStartPacket,
                    source: "Record",
                    postId: null,
                    successMessage: "Отправлена бинарная команда старта записи (0x0015=0101)",
                    failureMessage: "Не удалось отправить команду старта записи");
            });
        }

        // Сохраняем в БД асинхронно
        Task.Run(async () =>
        {
            try
            {
                await _initTask;
                await _experimentRepo.CreateAsync(experiment);
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = JsqClock.Now,
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
                    Timestamp = JsqClock.Now,
                    Level = "Warning",
                    Source = "Storage",
                    Post = postId,
                    Message = $"Не удалось сохранить в БД: {ex.Message}"
                });
            }
        });

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = JsqClock.Now,
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
            Timestamp = JsqClock.Now,
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
            Timestamp = JsqClock.Now,
            Level = "Info",
            Source = "System",
            Post = postId,
            Message = $"Пост {postId}: запись возобновлена"
        });
    }

    public void StopPost(string postId)
    {
        PostState? state;
        bool shouldSendGoStop;
        lock (_stateLock)
        {
            if (!_postStates.TryGetValue(postId, out state))
                return;

            state.IsRunning = false;
            _postStates.Remove(postId);

            // Удаляем только этот пост из роутинга (канал может остаться в других постах)
            foreach (var idx in state.ChannelIndices)
            {
                if (_channelPostMap.TryGetValue(idx, out var set))
                {
                    set.Remove(postId);
                    if (set.Count == 0) _channelPostMap.Remove(idx);
                }
            }

            shouldSendGoStop = !_postStates.Values.Any(s => s.IsRunning && !s.IsPaused);
        }

        if (shouldSendGoStop)
        {
            _ = Task.Run(async () =>
            {
                await SendBinaryPacketAsync(
                    GoStopPacket,
                    source: "Record",
                    postId: null,
                    successMessage: "Отправлена бинарная команда остановки записи (0x0015=0000)",
                    failureMessage: "Не удалось отправить команду остановки записи");
            });
        }

        var experiment = state.Experiment;
        var tailAggregates = state.AggregationService.Flush().ToList();
        Task.Run(async () =>
        {
            try
            {
                if (tailAggregates.Count > 0)
                    await _experimentRepo.SaveAggregatesAsync(experiment.Id, tailAggregates);

                await _experimentRepo.FinalizeAsync(experiment.Id);
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = JsqClock.Now,
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
                    Timestamp = JsqClock.Now,
                    Level = "Error",
                    Source = "Storage",
                    Post = postId,
                    Message = $"Ошибка при завершении поста {postId}: {ex.Message}"
                });
            }
        });

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = JsqClock.Now,
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

        // Считаем декодированные значения для SamplesPerSecond
        Interlocked.Add(ref _samplesThisWindow, values.Count);

        foreach (var cv in values)
        {
            ChannelValueReceived?.Invoke(cv.Index, cv.Value);
        }

        // Роутим данные по постам — под локом читаем snapshot, потом работаем
        List<(string postId, PostState state, List<Sample> samples)>? routes = null;

        lock (_stateLock)
        {
            if (_postStates.Count == 0) return;

            // Группируем значения по постам (один канал может идти в несколько постов)
            Dictionary<string, List<Sample>>? bySt = null;
            foreach (var cv in values)
            {
                if (!_channelPostMap.TryGetValue(cv.Index, out var postSet)) continue;
                double storageValue = double.IsNaN(cv.Value) ? -99.0 : cv.Value;
                var sample = new Sample(cv.Index, storageValue, cv.Timestamp);

                foreach (var pid in postSet)
                {
                    if (!_postStates.TryGetValue(pid, out var ps)) continue;
                    if (!ps.IsRunning || ps.IsPaused) continue;

                    bySt ??= new Dictionary<string, List<Sample>>();
                    if (!bySt.TryGetValue(pid, out var list))
                    {
                        list = new List<Sample>();
                        bySt[pid] = list;
                    }
                    list.Add(sample);
                }
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
            foreach (var sample in samples)
            {
                state.AggregationService.AddSample(sample);

                if (sample.IsValid)
                {
                    foreach (var evt in state.AnomalyDetector.CheckValue(sample.ChannelIndex, sample.Value, sample.Timestamp))
                        FirePostAnomaly(postId, state, evt);
                }
            }
        }
    }

    private void OnStatusChanged(object sender, ConnectionStatus status)
    {
        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = JsqClock.Now,
            Level = status == ConnectionStatus.Error ? "Error" : "Info",
            Source = "TCP",
            Message = $"Статус подключения: {status}"
        });

        // Авто-переподключение: если соединение потеряно — пробуем снова через 5 секунд.
        // _monitoringLock в BeginMonitoring защищает от параллельных попыток.
        if ((status == ConnectionStatus.Disconnected || status == ConnectionStatus.Error) &&
            Interlocked.CompareExchange(ref _suppressAutoReconnect, 0, 0) == 0)
        {
            var cts = _healthUpdateCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000).ConfigureAwait(false);
                    if (!(cts?.Token.IsCancellationRequested ?? true))
                        BeginMonitoring();
                }
                catch { }
            });
        }
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
                try { await ProcessAggregatesAsync(); } catch { }

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

        // Точный счётчик: сбрасываем накопленное за 1 секунду
        var samples = Interlocked.Exchange(ref _samplesThisWindow, 0);
        _lastSamplesPerSecond = samples;

        var health = new SystemHealth
        {
            TotalChannels = 134,
            TotalSamplesReceived = stats.TotalPacketsReceived,
            SamplesPerSecond = (ulong)_lastSamplesPerSecond,
            OverallStatus = stats.Status == ConnectionStatus.Connected
                ? HealthStatus.OK
                : HealthStatus.NoData
        };
        HealthUpdated?.Invoke(health);
    }

    private async Task ProcessAggregatesAsync()
    {
        List<PostState> active;
        lock (_stateLock)
            active = _postStates.Values.Where(s => s.IsRunning && !s.IsPaused).ToList();

        foreach (var state in active)
        {
            state.AggregationTick++;
            if (state.AggregationTick < 5) continue;
            state.AggregationTick = 0;

            var readyAggregates = state.AggregationService.GetReadyAggregates().ToList();

            foreach (var agg in readyAggregates)
            {
                foreach (var evt in state.AnomalyDetector.CheckAggregate(agg))
                    FirePostAnomaly(state.PostId, state, evt);
            }

            if (readyAggregates.Count > 0)
            {
                try { await _experimentRepo.SaveAggregatesAsync(state.Experiment.Id, readyAggregates); } catch { }
            }

            foreach (var evt in state.AnomalyDetector.CheckTimeouts(JsqClock.Now))
                FirePostAnomaly(state.PostId, state, evt);
        }
    }

    private async Task SaveCheckpointAsync(PostState state)
    {
        var checkpoint = new CheckpointData
        {
            CheckpointTime = JsqClock.NowIso(),
            LastSampleTimestamp = JsqClock.NowIso()
        };
        await _experimentRepo.SaveCheckpointAsync(state.Experiment.Id, checkpoint);
    }

    // ─── Вспомогательные методы ─────────────────────────────────────────────

    /// <summary>
    /// Пробрасывает событие аномалии в UI и сохраняет в БД.
    /// </summary>
    private void FirePostAnomaly(string postId, PostState state, AnomalyEvent evt)
    {
        PostAnomalyDetected?.Invoke(postId, evt);
        _ = Task.Run(async () =>
        {
            try { await _experimentRepo.SaveAnomalyEventAsync(evt); }
            catch { }
        });
    }

    private async Task<bool> SetAllPowerAsync(bool enable, CancellationToken ct = default)
    {
        var ok = true;

        foreach (var postId in new[] { "A", "B", "C" })
        {
            var postOk = await SendPostPowerCommandAsync(postId, enable, ct);
            ok = ok && postOk;

            if (!ct.IsCancellationRequested)
                await Task.Delay(30, ct);
        }

        return ok;
    }

    private async Task SendInitializationSequenceAsync(CancellationToken ct = default)
    {
        await SendBinaryPacketAsync(
            ProtocolInitPacket,
            source: "Protocol",
            postId: null,
            successMessage: "Отправлен init-пакет протокола (словарь команд/полей)",
            failureMessage: "Не удалось отправить init-пакет протокола",
            ct: ct);
    }

    private async Task<bool> SendPostPowerCommandAsync(string postId, bool enable, CancellationToken ct = default)
    {
        if (!PostDoIndexMap.TryGetValue(postId, out var doIndex))
        {
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = JsqClock.Now,
                Level = "Warning",
                Source = "Power",
                Post = postId,
                Message = $"Неизвестный пост для управления питанием: {postId}"
            });
            return false;
        }

        var packet = BuildDoPacket(doIndex, enable);
        var stateLabel = enable ? "ON" : "OFF";

        return await SendBinaryPacketAsync(
            packet,
            source: "Power",
            postId: postId,
            successMessage: $"Отправлена команда розетки поста {postId}: DO{doIndex:D2} {stateLabel}",
            failureMessage: $"Не удалось переключить розетку поста {postId}",
            ct: ct);
    }

    private async Task<bool> SendBinaryPacketAsync(
        byte[] packet,
        string source,
        string? postId,
        string successMessage,
        string failureMessage,
        CancellationToken ct = default)
    {
        try
        {
            if (_captureService.Status != ConnectionStatus.Connected)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = JsqClock.Now,
                    Level = "Warning",
                    Source = source,
                    Post = postId,
                    Message = $"{failureMessage}: нет активного подключения к передатчику"
                });
                return false;
            }

            await _captureService.SendAsync(packet, ct);

            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = JsqClock.Now,
                Level = "Info",
                Source = source,
                Post = postId,
                Message = successMessage
            });
            return true;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = JsqClock.Now,
                Level = "Warning",
                Source = source,
                Post = postId,
                Message = $"{failureMessage}: {ex.Message}"
            });
            return false;
        }
    }

    private static byte[] BuildDoPacket(int doIndex, bool enable)
    {
        if (doIndex < 1 || doIndex > 3)
            throw new ArgumentOutOfRangeException(nameof(doIndex), "Поддерживаются только DO01..DO03");

        var stateByte = enable ? (byte)0x01 : (byte)0x00;

        byte trailer = doIndex switch
        {
            1 => enable ? (byte)0x0E : (byte)0x0F,
            2 => enable ? (byte)0x0D : (byte)0x0C,
            3 => enable ? (byte)0x0C : (byte)0x0D,
            _ => (byte)0x00
        };

        return new byte[]
        {
            0x00, 0x00, 0x00, 0x10,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00,
            0x00, 0x04,
            0x44, 0x4F, 0x30, (byte)('0' + doIndex),
            stateByte,
            trailer
        };
    }

    private static byte[] HexToBytes(string hex)
    {
        if ((hex.Length & 1) != 0)
            throw new ArgumentException("Hex string length must be even", nameof(hex));

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(
                hex.Substring(i * 2, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    public async Task<List<ChannelEventRecord>> GetExperimentEventsAsync(string experimentId)
    {
        try
        {
            var records = await _experimentRepo.GetAnomalyEventsAsync(experimentId);
            return records.Select(r =>
            {
                DateTime.TryParse(r.Timestamp, null, DateTimeStyles.RoundtripKind, out var ts);
                return new ChannelEventRecord
                {
                    Timestamp = ts,
                    ChannelName = r.ChannelName,
                    EventType = r.AnomalyType,
                    Value = r.Value,
                    Threshold = r.Threshold
                };
            }).ToList();
        }
        catch { return new List<ChannelEventRecord>(); }
    }

    public async Task<List<PostExperimentRecord>> GetPostExperimentsAsync(
        string postId,
        DateTime? startFrom = null,
        DateTime? startTo = null,
        string? searchText = null)
    {
        try
        {
            var rows = await _experimentRepo.GetByPostAsync(postId, startFrom, startTo, searchText);
            return rows
                .OrderByDescending(r => r.StartTime)
                .Select(r => new PostExperimentRecord
                {
                    Id = r.Id,
                    PostId = r.PostId,
                    Name = r.Name,
                    Operator = r.Operator,
                    PartNumber = r.PartNumber,
                    Refrigerant = r.Refrigerant,
                    State = r.State,
                    StartTime = r.StartTime,
                    EndTime = r.EndTime
                })
                .ToList();
        }
        catch
        {
            return new List<PostExperimentRecord>();
        }
    }

    public async Task<List<ExperimentChannelInfo>> GetExperimentChannelsAsync(string experimentId)
    {
        try
        {
            var indices = await _experimentRepo.GetExperimentChannelIndicesAsync(experimentId);
            return indices
                .Select(idx =>
                {
                    ChannelRegistry.All.TryGetValue(idx, out var def);
                    return new ExperimentChannelInfo
                    {
                        ChannelIndex = idx,
                        ChannelName = def?.Name ?? $"v{idx:D3}",
                        Unit = def?.Unit ?? string.Empty
                    };
                })
                .OrderBy(c => c.ChannelName)
                .ToList();
        }
        catch
        {
            return new List<ExperimentChannelInfo>();
        }
    }

    public async Task<(DateTime? start, DateTime? end)> GetExperimentDataRangeAsync(string experimentId)
    {
        try
        {
            return await _experimentRepo.GetExperimentDataRangeAsync(experimentId);
        }
        catch
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        _healthUpdateCts?.Cancel();
        _captureService.Dispose();
        _batchWriter.Dispose();
        _dbService.Dispose();
        _healthUpdateCts?.Dispose();
        _monitoringLock.Dispose();
    }
    
    // ─── Загрузка истории для графиков ──────────────────────────────────────
    
    /// <summary>
    /// Загрузить исторические данные канала из БД
    /// </summary>
    public async Task<List<(DateTime time, double value)>> LoadChannelHistoryAsync(
        int channelIndex, DateTime startTime, DateTime endTime)
    {
        try
        {
            // Ищем по всем экспериментам в диапазоне дат — корректно при нескольких постах
            return await _experimentRepo.GetChannelHistoryAnyAsync(channelIndex, startTime, endTime);
        }
        catch
        {
            return new List<(DateTime, double)>();
        }
    }

    public async Task<List<(DateTime time, double value)>> LoadExperimentChannelHistoryAsync(
        string experimentId,
        int channelIndex,
        DateTime startTime,
        DateTime endTime)
    {
        try
        {
            return await _experimentRepo.GetChannelHistoryAsync(experimentId, channelIndex, startTime, endTime);
        }
        catch
        {
            return new List<(DateTime, double)>();
        }
    }
}
