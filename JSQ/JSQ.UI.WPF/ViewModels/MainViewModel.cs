using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    // Единый lookup: channelIndex → ChannelStatus (O(1) обновления)
    private readonly Dictionary<int, ChannelStatus> _channelMap = new();

    // Назначение каналов на посты: channelIndex → "A"/"B"/"C"
    private readonly Dictionary<int, string> _channelPostAssignment = new();

    // Каналы с активными аномалиями (статус не сбрасывается)
    private readonly HashSet<int> _anomalousChannels = new();

    // --- Состояние постов ---

    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _postAChannels = new();
    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _postAFiltered = new();
    [ObservableProperty]
    private string _postASearch = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _postBChannels = new();
    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _postBFiltered = new();
    [ObservableProperty]
    private string _postBSearch = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _postCChannels = new();
    [ObservableProperty]
    private ObservableCollection<ChannelStatus> _postCFiltered = new();
    [ObservableProperty]
    private string _postCSearch = string.Empty;

    // --- Общие ---

    [ObservableProperty]
    private SystemHealth _systemHealth = new();

    [ObservableProperty]
    private Experiment? _currentExperiment;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logEntries = new();

    [ObservableProperty]
    private string _statusMessage = "Готов";

    [ObservableProperty]
    private bool _isExperimentRunning;

    // Количество каналов на каждом посту (для отображения в заголовке вкладки)
    public int PostACount => PostAChannels.Count;
    public int PostBCount => PostBChannels.Count;
    public int PostCCount => PostCChannels.Count;

    public MainViewModel(IExperimentService experimentService, SettingsViewModel settings)
    {
        _experimentService = experimentService;
        _settings = settings;

        _experimentService.HealthUpdated += OnHealthUpdated;
        _experimentService.LogReceived += OnLogReceived;
        _experimentService.ChannelValueReceived += OnChannelValueReceived;
        _experimentService.AnomalyDetected += OnAnomalyDetected;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => OnPropertyChanged(nameof(CurrentExperiment));

        _staleChannelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _staleChannelTimer.Tick += (_, _) => MarkStaleChannels();
        _staleChannelTimer.Start();

        // Отслеживаем изменения коллекций для обновления счётчиков
        PostAChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PostACount));
        PostBChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PostBCount));
        PostCChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PostCCount));

        // По умолчанию — каналы назначены по группам (A→постA, B→постB, C→постC)
        InitializeDefaultChannelAssignment();

        _experimentService.Configure(
            _settings.TransmitterHost,
            _settings.TransmitterPort,
            _settings.ConnectionTimeoutMs);
        _experimentService.BeginMonitoring();

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

    /// <summary>Назначает каналы по умолчанию: группа PostA → пост A, PostB → B, PostC → C.</summary>
    private void InitializeDefaultChannelAssignment()
    {
        foreach (var kvp in ChannelRegistry.All)
        {
            var def = kvp.Value;
            string? postId = def.Group switch
            {
                ChannelGroup.PostA => "A",
                ChannelGroup.PostB => "B",
                ChannelGroup.PostC => "C",
                _ => null
            };

            if (postId == null) continue; // Common/System — не назначаем по умолчанию

            var ch = new ChannelStatus
            {
                ChannelIndex = kvp.Key,
                ChannelName = def.Name,
                Unit = def.Unit,
                Post = postId,
                Status = HealthStatus.NoData
            };

            _channelMap[kvp.Key] = ch;
            _channelPostAssignment[kvp.Key] = postId;
            GetPostChannels(postId).Add(ch);
        }

        ApplyPostFilter("A");
        ApplyPostFilter("B");
        ApplyPostFilter("C");
    }

    // --- Поиск по постам ---

    partial void OnPostASearchChanged(string value) => ApplyPostFilter("A");
    partial void OnPostBSearchChanged(string value) => ApplyPostFilter("B");
    partial void OnPostCSearchChanged(string value) => ApplyPostFilter("C");

    private void ApplyPostFilter(string postId)
    {
        var source = GetPostChannels(postId);
        var filtered = GetPostFiltered(postId);
        var search = GetPostSearch(postId);

        filtered.Clear();
        foreach (var ch in source)
        {
            if (string.IsNullOrWhiteSpace(search) ||
                ch.ChannelName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                filtered.Add(ch);
        }
    }

    // --- Helpers ---

    private ObservableCollection<ChannelStatus> GetPostChannels(string postId) => postId switch
    {
        "A" => PostAChannels,
        "B" => PostBChannels,
        "C" => PostCChannels,
        _ => PostAChannels
    };

    private ObservableCollection<ChannelStatus> GetPostFiltered(string postId) => postId switch
    {
        "A" => PostAFiltered,
        "B" => PostBFiltered,
        "C" => PostCFiltered,
        _ => PostAFiltered
    };

    private string GetPostSearch(string postId) => postId switch
    {
        "A" => PostASearch,
        "B" => PostBSearch,
        "C" => PostCSearch,
        _ => string.Empty
    };

    // --- Назначение каналов на пост ---

    private void AssignChannelsToPost(string postId, IList<int> newIndices)
    {
        // Убираем старые каналы этого поста
        var toRemove = _channelPostAssignment
            .Where(kvp => kvp.Value == postId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var idx in toRemove)
        {
            _channelPostAssignment.Remove(idx);
            _channelMap.Remove(idx);
        }

        GetPostChannels(postId).Clear();

        // Добавляем новые
        foreach (var idx in newIndices)
        {
            var ch = new ChannelStatus
            {
                ChannelIndex = idx,
                ChannelName = ChannelRegistry.GetName(idx),
                Unit = ChannelRegistry.GetUnit(idx),
                Post = postId,
                Status = HealthStatus.NoData
            };
            _channelMap[idx] = ch;
            _channelPostAssignment[idx] = postId;
            GetPostChannels(postId).Add(ch);
        }

        ApplyPostFilter(postId);
        StatusMessage = $"Пост {postId}: назначено {newIndices.Count} каналов";
    }

    // --- Команды управления постами ---

    [RelayCommand]
    private void OpenChannelsForPost(string postId)
    {
        var viewModel = new ChannelSelectionViewModel();
        viewModel.InitForPost(postId, _channelPostAssignment);
        viewModel.SubscribeToLiveData(_experimentService);

        var window = new ChannelSelectionWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };

        window.Closed += (s, e) => viewModel.UnsubscribeFromLiveData();
        window.SelectionSaved += (selectedIndices) =>
        {
            AssignChannelsToPost(postId, selectedIndices);
        };

        window.ShowDialog();
    }

    // --- Управление экспериментом ---

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
        if (CurrentExperiment == null)
        {
            NewExperiment();
            if (CurrentExperiment == null) return;
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
        var window = new SettingsWindow(_settings)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private void ExportData()
    {
        // TODO: экспорт в DBF
    }

    // --- Обработчики событий от сервиса ---

    private void OnChannelValueReceived(int index, double value)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_channelMap.TryGetValue(index, out var ch))
                return;

            ch.CurrentValue = double.IsNaN(value) ? (double?)null : value;
            ch.LastUpdateTime = DateTime.Now;

            if (double.IsNaN(value))
                ch.Status = HealthStatus.NoData;
            else if (!_anomalousChannels.Contains(index))
                ch.Status = HealthStatus.OK;
        });
    }

    private void OnAnomalyDetected(AnomalyEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _anomalousChannels.Add(evt.ChannelIndex);

            if (_channelMap.TryGetValue(evt.ChannelIndex, out var ch))
                ch.Status = evt.Severity == "Critical" ? HealthStatus.Alarm : HealthStatus.Warning;

            var post = _channelPostAssignment.TryGetValue(evt.ChannelIndex, out var p) ? p : "?";

            LogEntries.Insert(0, new LogEntry
            {
                Timestamp = evt.Timestamp,
                Level = evt.Severity == "Critical" ? "Error" : "Warning",
                Source = evt.ChannelName,
                Post = post,
                Message = evt.Message
            });

            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    private void MarkStaleChannels()
    {
        var threshold = TimeSpan.FromSeconds(5);
        var now = DateTime.Now;
        foreach (var ch in _channelMap.Values)
        {
            if (ch.Status == HealthStatus.OK && (now - ch.LastUpdateTime) > threshold)
                ch.Status = HealthStatus.NoData;
        }
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
    public string? Post { get; set; }
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
