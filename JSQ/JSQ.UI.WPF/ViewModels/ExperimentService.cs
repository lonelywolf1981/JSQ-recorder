using System;
using System.Threading;
using System.Threading.Tasks;
using JSQ.Capture;
using JSQ.Core.Models;
using JSQ.Decode;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Реальный сервис управления экспериментами
/// </summary>
public class ExperimentService : IExperimentService, IDisposable
{
    private readonly TcpCaptureService _captureService;
    private readonly ChannelDecoder _decoder = new ChannelDecoder();
    private Experiment? _currentExperiment;
    private bool _isRunning;

    // Параметры подключения (обновляются через Configure)
    private string _host = "192.168.0.214";
    private int _port = 55555;
    private int _timeoutMs = 5000;

    public event Action<SystemHealth>? HealthUpdated;
    public event Action<LogEntry>? LogReceived;
    public event Action<int, double>? ChannelValueReceived;

    private CancellationTokenSource? _healthUpdateCts;

    public ExperimentService()
    {
        _captureService = new TcpCaptureService();
        _captureService.DataReceived += OnDataReceived;
        _captureService.StatusChanged += OnStatusChanged;
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

    private void OnDataReceived(object sender, byte[] data)
    {
        // Декодируем значения каналов из сырых байт
        var values = _decoder.Feed(data, data.Length);
        foreach (var cv in values)
            ChannelValueReceived?.Invoke(cv.Index, cv.Value);

        // В лог пишем только если данные не поддались декодированию (бинарный мусор)
        // или если декодер ничего не нашёл — показываем кол-во байт
        if (values.Count == 0)
        {
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = "Info",
                Source = "TCP",
                Message = $"Получено {data.Length} байт (не декодировано)"
            });
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
                // Если уже подключены — отключаемся (например, при смене IP в настройках)
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

        _currentExperiment = experiment;
        _isRunning = true;

        var host = _host;
        var port = _port;

        Task.Run(async () =>
        {
            try
            {
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
                    Message = "Подключено к передатчику"
                });
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Error",
                    Source = "System",
                    Message = $"Ошибка подключения: {ex.Message}"
                });
            }
        });
    }

    public void PauseExperiment()
    {
        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Warning",
            Source = "System",
            Message = "Пауза эксперимента"
        });
    }

    public void ResumeExperiment() { }

    public void StopExperiment()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        Task.Run(async () =>
        {
            await _captureService.DisconnectAsync();
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = "Info",
                Source = "System",
                Message = "Эксперимент остановлен"
            });
        });
    }

    public SystemHealth GetCurrentHealth() => new SystemHealth { TotalChannels = 134 };

    public void Dispose()
    {
        _healthUpdateCts?.Cancel();
        _captureService.Dispose();
        _healthUpdateCts?.Dispose();
    }
}
