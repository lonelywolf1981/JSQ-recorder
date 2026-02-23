using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSQ.Core.Models;
using JSQ.UI.WPF.Views;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Главная ViewModel приложения
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IExperimentService _experimentService;
    private readonly SettingsViewModel _settings;
    private readonly DispatcherTimer _durationTimer;
    private readonly DispatcherTimer _staleChannelTimer;

    // Быстрый lookup: индекс канала → ChannelStatus (для O(1) обновлений)
    private readonly Dictionary<int, ChannelStatus> _channelMap = new();

    // Каналы с активными аномалиями (статус не сбрасывается до OK пока аномалия активна)
    private readonly HashSet<int> _anomalousChannels = new();

    [ObservableProperty]
    private SystemHealth _systemHealth = new();

    [ObservableProperty]
    private Experiment? _currentExperiment;

    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _channels = new();

    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _filteredChannels = new();

    [ObservableProperty]
    private string _channelSearchText = string.Empty;

    [ObservableProperty]
    private string _channelGroupFilter = "Все";

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logEntries = new();

    [ObservableProperty]
    private string _statusMessage = "Готов";

    [ObservableProperty]
    private bool _isExperimentRunning;

    public MainViewModel(IExperimentService experimentService, SettingsViewModel settings)
    {
        _experimentService = experimentService;
        _settings = settings;

        _experimentService.HealthUpdated += OnHealthUpdated;
        _experimentService.LogReceived += OnLogReceived;
        _experimentService.ChannelValueReceived += OnChannelValueReceived;
        _experimentService.AnomalyDetected += OnAnomalyDetected;

        // Обновление таймера длительности эксперимента
        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => OnPropertyChanged(nameof(CurrentExperiment));

        // Проверка устаревших каналов (нет данных >5 секунд → NoData)
        _staleChannelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _staleChannelTimer.Tick += (_, _) => MarkStaleChannels();
        _staleChannelTimer.Start();

        // Загружаем все каналы из реестра — данные будут обновляться по мере поступления
        InitializeChannels();

        // Автоподключение при старте
        _experimentService.Configure(
            _settings.TransmitterHost,
            _settings.TransmitterPort,
            _settings.ConnectionTimeoutMs);
        _experimentService.BeginMonitoring();

        // При сохранении настроек — переподключаемся с новым IP/портом
        _settings.SaveCompleted += OnSettingsSaved;
    }

    private void OnSettingsSaved()
    {
        _experimentService.Configure(
            _settings.TransmitterHost,
            _settings.TransmitterPort,
            _settings.ConnectionTimeoutMs);
        _experimentService.BeginMonitoring();
        StatusMessage = $"Настройки сохранены. Подключение к {_settings.TransmitterHost}:{_settings.TransmitterPort}...";
    }

    private void InitializeChannels()
    {
        Channels.Clear();
        _channelMap.Clear();
        foreach (var kvp in ChannelRegistry.All)
        {
            var ch = new ChannelStatus
            {
                ChannelIndex = kvp.Key,
                ChannelName = kvp.Value.Name,
                Unit = kvp.Value.Unit,
                Status = HealthStatus.NoData
            };
            Channels.Add(ch);
            _channelMap[kvp.Key] = ch;
        }
        ApplyChannelFilter();
    }

    partial void OnChannelSearchTextChanged(string value) => ApplyChannelFilter();
    partial void OnChannelGroupFilterChanged(string value) => ApplyChannelFilter();

    private void ApplyChannelFilter()
    {
        FilteredChannels.Clear();
        foreach (var ch in Channels)
        {
            var matchesSearch = string.IsNullOrWhiteSpace(ChannelSearchText) ||
                                ch.ChannelName.IndexOf(ChannelSearchText, StringComparison.OrdinalIgnoreCase) >= 0;
            var matchesGroup = ChannelGroupFilter == "Все" ||
                               ch.ChannelName.StartsWith(ChannelGroupFilter + "-", StringComparison.OrdinalIgnoreCase);
            if (matchesSearch && matchesGroup)
                FilteredChannels.Add(ch);
        }
    }

    private void MarkStaleChannels()
    {
        var staleThreshold = TimeSpan.FromSeconds(5);
        var now = DateTime.Now;
        foreach (var ch in Channels)
        {
            if (ch.Status == HealthStatus.OK && (now - ch.LastUpdateTime) > staleThreshold)
                ch.Status = HealthStatus.NoData;
        }
    }

    private void OnChannelValueReceived(int index, double value)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_channelMap.TryGetValue(index, out var ch))
                return;

            ch.CurrentValue = double.IsNaN(value) ? (double?)null : value;
            ch.LastUpdateTime = DateTime.Now;

            if (double.IsNaN(value))
            {
                ch.Status = HealthStatus.NoData;
            }
            else if (!_anomalousChannels.Contains(index))
            {
                // Сбрасываем в OK только если нет активной аномалии
                ch.Status = HealthStatus.OK;
            }
        });
    }

    private void OnAnomalyDetected(AnomalyEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _anomalousChannels.Add(evt.ChannelIndex);

            if (_channelMap.TryGetValue(evt.ChannelIndex, out var ch))
                ch.Status = evt.Severity == "Critical" ? HealthStatus.Alarm : HealthStatus.Warning;

            LogEntries.Insert(0, new LogEntry
            {
                Timestamp = evt.Timestamp,
                Level = evt.Severity == "Critical" ? "Error" : "Warning",
                Source = evt.ChannelName,
                Message = evt.Message
            });

            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    [RelayCommand]
    private void NewExperiment()
    {
        var viewModel = new NewExperimentViewModel();
        var window = new NewExperimentWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();

        if (viewModel.Confirmed)
        {
            CurrentExperiment = viewModel.BuildExperiment();
            StatusMessage = $"Эксперимент '{CurrentExperiment.Name}' создан. Нажмите Запуск.";
        }
    }

    [RelayCommand]
    private void StartExperiment()
    {
        // Если эксперимент не создан — открываем диалог
        if (CurrentExperiment == null)
        {
            NewExperiment();
            if (CurrentExperiment == null)
                return; // пользователь отменил
        }

        _experimentService.Configure(
            _settings.TransmitterHost,
            _settings.TransmitterPort,
            _settings.ConnectionTimeoutMs);

        _experimentService.StartExperiment(CurrentExperiment);
        _anomalousChannels.Clear();

        IsExperimentRunning = true;
        StatusMessage = $"Запись активна: '{CurrentExperiment.Name}' ({_settings.TransmitterHost}:{_settings.TransmitterPort})";
        _durationTimer.Start();
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
        _anomalousChannels.Clear();
        IsExperimentRunning = false;
        StatusMessage = "Эксперимент остановлен";
        _durationTimer.Stop();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenChannels()
    {
        var viewModel = new ChannelSelectionViewModel();
        viewModel.SubscribeToLiveData(_experimentService);

        var window = new Views.ChannelSelectionWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };

        window.Closed += (s, e) => viewModel.UnsubscribeFromLiveData();

        window.SelectionSaved += (selectedIndices) =>
        {
            Channels.Clear();
            _channelMap.Clear();

            foreach (var index in selectedIndices)
            {
                var ch = new ChannelStatus
                {
                    ChannelIndex = index,
                    ChannelName = ChannelRegistry.GetName(index),
                    Unit = ChannelRegistry.GetUnit(index),
                    Status = HealthStatus.NoData
                };
                Channels.Add(ch);
                _channelMap[index] = ch;
            }

            ApplyChannelFilter();
            StatusMessage = $"Выбрано {selectedIndices.Count} каналов";
        };

        window.ShowDialog();
    }

    [RelayCommand]
    private void ExportData()
    {
        // TODO: экспорт в DBF
    }

    private void OnHealthUpdated(SystemHealth health)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            SystemHealth = health;
            if (!IsExperimentRunning)
            {
                StatusMessage = health.OverallStatus switch
                {
                    HealthStatus.OK => "Все системы в норме",
                    HealthStatus.Warning => $"Внимание: {health.ChannelsWarning} каналов с предупреждениями",
                    HealthStatus.Alarm => $"Тревога: {health.ChannelsAlarm} каналов в аварии",
                    HealthStatus.NoData => "Нет данных",
                    _ => StatusMessage
                };
            }
        });
    }

    private void OnLogReceived(LogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Insert(0, entry);
            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }
}

/// <summary>
/// Запись лога
/// </summary>
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "Info";
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

    /// <summary>Канал index получил новое значение value (double.NaN = нет данных).</summary>
    event Action<int, double>? ChannelValueReceived;

    /// <summary>Обнаружена аномалия на одном из каналов.</summary>
    event Action<AnomalyEvent>? AnomalyDetected;

    /// <summary>Задать параметры подключения.</summary>
    void Configure(string host, int port, int timeoutMs);

    /// <summary>Подключиться к передатчику и начать приём данных (без создания эксперимента).</summary>
    void BeginMonitoring();

    void StartExperiment(Experiment experiment);
    void PauseExperiment();
    void ResumeExperiment();
    void StopExperiment();

    SystemHealth GetCurrentHealth();
}
