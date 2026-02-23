using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JSQ.Capture;
using JSQ.Core.Models;
using JSQ.Decode;
using JSQ.Rules;
using JSQ.Storage;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Реальный сервис управления экспериментами
/// </summary>
public class ExperimentService : IExperimentService, IDisposable
{
    private readonly TcpCaptureService _captureService;
    private readonly ChannelDecoder _decoder = new ChannelDecoder();
    private readonly IDatabaseService _dbService;
    private readonly IBatchWriter _batchWriter;
    private readonly IExperimentRepository _experimentRepo;

    private Experiment? _currentExperiment;
    private bool _isRunning;
    private IAnomalyDetector? _anomalyDetector;
    private IAggregationService? _aggregationService;

    // Параметры подключения (обновляются через Configure)
    private string _host = "192.168.0.214";
    private int _port = 55555;
    private int _timeoutMs = 5000;

    public event Action<SystemHealth>? HealthUpdated;
    public event Action<LogEntry>? LogReceived;
    public event Action<int, double>? ChannelValueReceived;
    public event Action<AnomalyEvent>? AnomalyDetected;

    private CancellationTokenSource? _healthUpdateCts;
    private int _aggregationTick;
    private int _checkpointTick;

    public ExperimentService(IDatabaseService dbService, IBatchWriter batchWriter, IExperimentRepository experimentRepo)
    {
        _dbService = dbService;
        _batchWriter = batchWriter;
        _experimentRepo = experimentRepo;

        _captureService = new TcpCaptureService();
        _captureService.DataReceived += OnDataReceived;
        _captureService.StatusChanged += OnStatusChanged;

        // Инициализируем БД асинхронно
        Task.Run(async () => { try { await _dbService.InitializeAsync(); } catch { } });

        StartHealthUpdates();
    }

    private void StartHealthUpdates()
    {
        _healthUpdateCts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!_healthUpdateCts.Token.IsCancellationRequested)
            {
                try { UpdateHealth(); }
                catch { /* не даём фоновому циклу упасть */ }

                // Каждые 5 сек — проверяем агрегацию и аномалии
                _aggregationTick++;
                if (_aggregationTick >= 5)
                {
                    _aggregationTick = 0;
                    try { ProcessAggregates(); } catch { }
                }

                // Каждые 30 сек — сохраняем чекпоинт
                _checkpointTick++;
                if (_checkpointTick >= 30)
                {
                    _checkpointTick = 0;
                    try { await SaveCheckpointAsync(); } catch { }
                }

                await Task.Delay(1000, _healthUpdateCts.Token).ConfigureAwait(false);
            }
        }, _healthUpdateCts.Token);
    }

    private void UpdateHealth()
    {
        var stats = _captureService.Statistics;
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
        if (_aggregationService == null || _anomalyDetector == null)
            return;

        foreach (var agg in _aggregationService.GetReadyAggregates())
        {
            foreach (var evt in _anomalyDetector.CheckAggregate(agg))
                AnomalyDetected?.Invoke(evt);
        }

        foreach (var evt in _anomalyDetector.CheckTimeouts(DateTime.Now))
            AnomalyDetected?.Invoke(evt);
    }

    private async Task SaveCheckpointAsync()
    {
        if (_currentExperiment == null || !_isRunning)
            return;

        var checkpoint = new CheckpointData
        {
            CheckpointTime = DateTime.Now.ToString("O"),
            LastSampleTimestamp = DateTime.Now.ToString("O")
        };
        await _experimentRepo.SaveCheckpointAsync(_currentExperiment.Id, checkpoint);
    }

    private void OnDataReceived(object sender, byte[] data)
    {
        var values = _decoder.Feed(data, data.Length);

        if (values.Count == 0)
        {
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = "Info",
                Source = "TCP",
                Message = $"Получено {data.Length} байт (не декодировано)"
            });
            return;
        }

        // Конвертируем ChannelValue → Sample и отправляем в UI
        var samples = new List<Sample>(values.Count);
        foreach (var cv in values)
        {
            // double.NaN → -99.0 (маркер «нет данных» для хранилища)
            double storageValue = double.IsNaN(cv.Value) ? -99.0 : cv.Value;
            samples.Add(new Sample(cv.Index, storageValue, cv.Timestamp));

            ChannelValueReceived?.Invoke(cv.Index, cv.Value);
        }

        // Запись и анализ — только если эксперимент активен
        if (!_isRunning || _currentExperiment == null)
            return;

        _batchWriter.AddSamples(_currentExperiment.Id, samples);

        if (_aggregationService != null && _anomalyDetector != null)
        {
            foreach (var sample in samples)
            {
                _aggregationService.AddSample(sample);

                if (sample.IsValid)
                {
                    foreach (var evt in _anomalyDetector.CheckValue(sample.ChannelIndex, sample.Value, sample.Timestamp))
                        AnomalyDetected?.Invoke(evt);
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

    public void Configure(string host, int port, int timeoutMs)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
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

    public void StartExperiment(Experiment experiment)
    {
        if (_isRunning)
            return;

        if (string.IsNullOrEmpty(experiment.Id))
            experiment.Id = Guid.NewGuid().ToString("N");

        experiment.StartTime = DateTime.Now;
        experiment.State = ExperimentState.Running;

        _currentExperiment = experiment;
        _isRunning = true;

        // Создаём детектор аномалий и агрегатор для нового эксперимента
        _anomalyDetector = new AnomalyDetector(experiment.Id);
        _aggregationService = new AggregationService(experiment.AggregationIntervalSec);

        // Загружаем правила из ChannelRegistry (MinLimit/MaxLimit из определений каналов)
        var rules = new List<AnomalyRule>();
        foreach (var kvp in ChannelRegistry.All)
        {
            var def = kvp.Value;
            if (def.MinLimit.HasValue || def.MaxLimit.HasValue)
            {
                rules.Add(new AnomalyRule
                {
                    ChannelIndex = kvp.Key,
                    ChannelName = def.Name,
                    MinLimit = def.MinLimit,
                    MaxLimit = def.MaxLimit,
                    Enabled = true,
                    DebounceCount = 3
                });
            }
        }
        _anomalyDetector.LoadRules(rules);

        // Сохраняем эксперимент в БД (игнорируем ошибку — запись данных продолжается)
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
                    Message = $"Эксперимент '{experiment.Name}' создан в БД (ID: {experiment.Id})"
                });
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Warning",
                    Source = "Storage",
                    Message = $"Не удалось сохранить эксперимент в БД: {ex.Message}"
                });
            }
        });

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "System",
            Message = $"Эксперимент '{experiment.Name}' запущен. Запись данных активна."
        });
    }

    public void PauseExperiment()
    {
        if (_currentExperiment != null)
            Task.Run(() => _experimentRepo.UpdateStateAsync(_currentExperiment.Id, ExperimentState.Paused));

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Warning",
            Source = "System",
            Message = "Эксперимент приостановлен"
        });
    }

    public void ResumeExperiment()
    {
        if (_currentExperiment != null)
            Task.Run(() => _experimentRepo.UpdateStateAsync(_currentExperiment.Id, ExperimentState.Running));
    }

    public void StopExperiment()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        var experiment = _currentExperiment;
        _anomalyDetector = null;
        _aggregationService = null;
        _currentExperiment = null;

        Task.Run(async () =>
        {
            try
            {
                // Финальный flush буфера записи
                await _batchWriter.FlushAsync();

                if (experiment != null)
                {
                    await _experimentRepo.FinalizeAsync(experiment.Id);
                    LogReceived?.Invoke(new LogEntry
                    {
                        Timestamp = DateTime.Now,
                        Level = "Info",
                        Source = "Storage",
                        Message = $"Эксперимент '{experiment.Name}' завершён и сохранён в БД"
                    });
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Error",
                    Source = "Storage",
                    Message = $"Ошибка при завершении эксперимента: {ex.Message}"
                });
            }
        });

        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "System",
            Message = "Эксперимент остановлен"
        });
    }

    public SystemHealth GetCurrentHealth() => new SystemHealth { TotalChannels = 134 };

    public void Dispose()
    {
        _healthUpdateCts?.Cancel();
        _captureService.Dispose();
        _batchWriter.Dispose();
        _dbService.Dispose();
        _healthUpdateCts?.Dispose();
    }
}
