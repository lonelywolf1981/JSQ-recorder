using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JSQ.Core.Models;
using JSQ.Capture;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Реальный сервис управления экспериментами
/// </summary>
public class ExperimentService : IExperimentService, IDisposable
{
    private readonly TcpCaptureService _captureService;
    private Experiment? _currentExperiment;
    private bool _isRunning;
    
    public event Action<SystemHealth>? HealthUpdated;
    public event Action<LogEntry>? LogReceived;
    
    private readonly SystemHealth _health = new();
    private CancellationTokenSource? _healthUpdateCts;
    
    public ExperimentService()
    {
        _captureService = new TcpCaptureService();
        
        // Подписка на события захвата
        _captureService.DataReceived += OnDataReceived;
        _captureService.StatusChanged += OnStatusChanged;
        
        // Запуск обновления здоровья
        StartHealthUpdates();
    }
    
    private void StartHealthUpdates()
    {
        _healthUpdateCts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!_healthUpdateCts.Token.IsCancellationRequested)
            {
                UpdateHealth();
                await Task.Delay(1000);
            }
        });
    }
    
    private void UpdateHealth()
    {
        var stats = _captureService.Statistics;
        
        _health.TotalChannels = 134;
        _health.TotalSamplesReceived = stats.TotalPacketsReceived;
        _health.SamplesPerSecond = stats.BytesPerSecond / 100; // Примерно
        
        var (ingest, decode, persist) = _captureService.GetQueueSizes();
        
        HealthUpdated?.Invoke(_health);
    }
    
    private void OnDataReceived(object sender, byte[] data)
    {
        // Логируем полученные данные
        var message = System.Text.Encoding.UTF8.GetString(data);
        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Info",
            Source = "TCP",
            Message = message.Length > 100 ? message.Substring(0, 100) + "..." : message
        });
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
    
    public void StartExperiment(Experiment experiment)
    {
        if (_isRunning)
            return;
        
        _currentExperiment = experiment;
        _isRunning = true;
        
        // Подключение к передатчику
        Task.Run(async () =>
        {
            try
            {
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Info",
                    Source = "System",
                    Message = $"Подключение к передатчику {experiment.PartNumber}..."
                });
                
                await _captureService.ConnectAsync("192.168.0.214", 55555);
                
                LogReceived?.Invoke(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = "Success",
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
        // TODO: Реализовать паузу
        LogReceived?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = "Warning",
            Source = "System",
            Message = "Пауза эксперимента"
        });
    }
    
    public void ResumeExperiment()
    {
        // TODO: Реализовать возобновление
    }
    
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
    
    public SystemHealth GetCurrentHealth()
    {
        return _health;
    }
    
    public void Dispose()
    {
        _healthUpdateCts?.Cancel();
        _captureService.Dispose();
        _healthUpdateCts?.Dispose();
    }
}
