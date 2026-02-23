using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSQ.Core.Models;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Главная ViewModel приложения
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IExperimentService _experimentService;
    
    [ObservableProperty]
    private SystemHealth _systemHealth = new();
    
    [ObservableProperty]
    private Experiment _currentExperiment;
    
    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _channels = new();
    
    [ObservableProperty]
    private ObservableCollection<LogEntry> _logEntries = new();
    
    [ObservableProperty]
    private string _statusMessage = "Готов";
    
    [ObservableProperty]
    private bool _isExperimentRunning;
    
    public MainViewModel(IExperimentService experimentService)
    {
        _experimentService = experimentService;
        
        // Подписка на события
        _experimentService.HealthUpdated += OnHealthUpdated;
        _experimentService.LogReceived += OnLogReceived;
    }
    
    [RelayCommand]
    private void NewExperiment()
    {
        // Открыть диалог создания эксперимента
    }
    
    [RelayCommand]
    private void StartExperiment()
    {
        if (_currentExperiment != null)
        {
            _experimentService.StartExperiment(_currentExperiment);
            IsExperimentRunning = true;
            StatusMessage = "Эксперимент запущен";
        }
    }
    
    [RelayCommand]
    private void PauseExperiment()
    {
        _experimentService.PauseExperiment();
        StatusMessage = "Эксперимент приостановлен";
    }
    
    [RelayCommand]
    private void StopExperiment()
    {
        _experimentService.StopExperiment();
        IsExperimentRunning = false;
        StatusMessage = "Эксперимент остановлен";
    }
    
    [RelayCommand]
    private void OpenSettings()
    {
        // Открыть настройки
    }
    
    [RelayCommand]
    private void OpenChannels()
    {
        // Открыть окно выбора каналов
        var viewModel = new ChannelSelectionViewModel();
        var window = new Views.ChannelSelectionWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }
    
    [RelayCommand]
    private void ExportData()
    {
        // Экспорт данных
    }
    
    private void OnHealthUpdated(SystemHealth health)
    {
        SystemHealth = health;
        
        // Обновление статуса
        StatusMessage = health.OverallStatus switch
        {
            HealthStatus.OK => "Все системы в норме",
            HealthStatus.Warning => $"Внимание: {health.ChannelsWarning} каналов с предупреждениями",
            HealthStatus.Alarm => $"Тревога: {health.ChannelsAlarm} каналов в аварии",
            HealthStatus.NoData => "Нет данных",
            _ => StatusMessage
        };
    }
    
    private void OnLogReceived(LogEntry entry)
    {
        LogEntries.Insert(0, entry);
        
        // Ограничение размера лога
        while (LogEntries.Count > 1000)
        {
            LogEntries.RemoveAt(LogEntries.Count - 1);
        }
    }
}

/// <summary>
/// Запись лога
/// </summary>
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "Info"; // Info, Warning, Error, Critical
    public string Message { get; set; } = string.Empty;
    public string? Source { get; set; }
}

/// <summary>
/// Сервис управления экспериментами (интерфейс для UI)
/// </summary>
public interface IExperimentService
{
    event Action<SystemHealth> HealthUpdated;
    event Action<LogEntry> LogReceived;
    
    void StartExperiment(Experiment experiment);
    void PauseExperiment();
    void ResumeExperiment();
    void StopExperiment();
    
    SystemHealth GetCurrentHealth();
}
