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
    private readonly DispatcherTimer _staleChannelTimer;

    // channelIndex → список ChannelStatus (один per post, для Common-каналов их может быть несколько)
    private readonly Dictionary<int, List<ChannelStatus>> _channelMap = new();

    // channelIndex → HashSet<postId> (Common-каналы могут принадлежать нескольким постам)
    private readonly Dictionary<int, HashSet<string>> _channelPostAssignment = new();

    // --- Состояние постов (мониторинг) ---

    public PostMonitorViewModel PostA { get; } = new("A");
    public PostMonitorViewModel PostB { get; } = new("B");
    public PostMonitorViewModel PostC { get; } = new("C");

    // --- Каналы по постам ---

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
    private ObservableCollection<LogEntry> _logEntries = new();

    /// <summary>Последние предупреждения/аномалии для панели Статус.</summary>
    public ObservableCollection<LogEntry> RecentAlerts { get; } = new();

    [ObservableProperty]
    private string _statusMessage = "Готов";

    /// <summary>True если хотя бы один пост ведёт запись.</summary>
    public bool IsAnyPostRunning => PostA.IsRunning || PostB.IsRunning || PostC.IsRunning;

    // Количество каналов на каждом посту (для заголовка вкладки)
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
        _experimentService.PostAnomalyDetected += OnPostAnomalyDetected;

        _staleChannelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _staleChannelTimer.Tick += (_, _) => MarkStaleChannels();
        _staleChannelTimer.Start();

        PostAChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PostACount));
        PostBChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PostBCount));
        PostCChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PostCCount));

        InitializeDefaultChannelAssignment();

        _experimentService.Configure(
            _settings.TransmitterHost,
            _settings.TransmitterPort,
            _settings.ConnectionTimeoutMs);
        _experimentService.BeginMonitoring();

        _settings.SaveCompleted += OnSettingsSaved;
        // CancelRequested — просто закрывает окно, BeginMonitoring не вызываем
    }

    private void OnSettingsSaved()
    {
        // Не переподключаемся если идёт запись
        if (!IsAnyPostRunning)
        {
            _experimentService.Configure(
                _settings.TransmitterHost,
                _settings.TransmitterPort,
                _settings.ConnectionTimeoutMs);
            _experimentService.BeginMonitoring();
        }
        StatusMessage = $"Настройки сохранены. Хост: {_settings.TransmitterHost}:{_settings.TransmitterPort}";
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

            var ch = CreateChannelStatus(kvp.Key, def, postId);
            AddChannelStatus(kvp.Key, postId, ch);
            GetPostChannels(postId).Add(ch);
        }

        ApplyPostFilter("A");
        ApplyPostFilter("B");
        ApplyPostFilter("C");
    }

    private ChannelStatus CreateChannelStatus(int idx, ChannelDefinition def, string postId) =>
        new ChannelStatus
        {
            ChannelIndex = idx,
            ChannelName = def.Name,
            Unit = def.Unit,
            Post = postId,
            Status = HealthStatus.NoData
        };

    private void AddChannelStatus(int idx, string postId, ChannelStatus ch)
    {
        if (!_channelMap.TryGetValue(idx, out var list))
            _channelMap[idx] = list = new List<ChannelStatus>();
        list.Add(ch);

        if (!_channelPostAssignment.TryGetValue(idx, out var posts))
            _channelPostAssignment[idx] = posts = new HashSet<string>();
        posts.Add(postId);
    }

    private void RemoveChannelStatus(int idx, string postId)
    {
        if (_channelMap.TryGetValue(idx, out var list))
        {
            list.RemoveAll(s => s.Post == postId);
            if (list.Count == 0) _channelMap.Remove(idx);
        }

        if (_channelPostAssignment.TryGetValue(idx, out var posts))
        {
            posts.Remove(postId);
            if (posts.Count == 0) _channelPostAssignment.Remove(idx);
        }
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

    private PostMonitorViewModel GetPostMonitor(string postId) => postId switch
    {
        "A" => PostA,
        "B" => PostB,
        "C" => PostC,
        _ => PostA
    };

    // --- Назначение каналов на пост ---

    private void AssignChannelsToPost(string postId, IList<int> newIndices)
    {
        // Убираем все текущие каналы этого поста
        var toRemove = _channelPostAssignment
            .Where(kvp => kvp.Value.Contains(postId))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var idx in toRemove)
            RemoveChannelStatus(idx, postId);

        GetPostChannels(postId).Clear();

        // Добавляем новые каналы
        foreach (var idx in newIndices)
        {
            if (!ChannelRegistry.All.TryGetValue(idx, out var def)) continue;
            var ch = new ChannelStatus
            {
                ChannelIndex = idx,
                ChannelName = def.Name,
                Unit = def.Unit,
                Post = postId,
                Status = HealthStatus.NoData
            };
            AddChannelStatus(idx, postId, ch);
            GetPostChannels(postId).Add(ch);
        }

        ApplyPostFilter(postId);
        StatusMessage = $"Пост {postId}: назначено {newIndices.Count} каналов";
    }

    // --- Команды ---

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private bool CanOpenSettings() => !IsAnyPostRunning;

    private void NotifyRunningChanged()
    {
        OnPropertyChanged(nameof(IsAnyPostRunning));
        OpenSettingsCommand.NotifyCanExecuteChanged();
    }

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

    [RelayCommand]
    private void StartPost(string postId)
    {
        var monitor = GetPostMonitor(postId);
        if (!monitor.CanStart) return;

        var viewModel = new NewExperimentViewModel();
        var window = new NewExperimentWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();

        if (!viewModel.Confirmed) return;

        var experiment = viewModel.BuildExperiment();
        var channelIndices = _channelPostAssignment
            .Where(kvp => kvp.Value.Contains(postId))
            .Select(kvp => kvp.Key)
            .ToList();

        if (channelIndices.Count == 0)
        {
            StatusMessage = $"Пост {postId}: не назначены каналы для записи";
            return;
        }

        _experimentService.StartPost(postId, experiment, channelIndices);

        monitor.CurrentExperiment = experiment;
        monitor.State = ExperimentState.Running;
        monitor.AnomalyCount = 0;

        NotifyRunningChanged();
        StatusMessage = $"Пост {postId}: запись '{experiment.Name}' активна ({channelIndices.Count} каналов)";
    }

    [RelayCommand]
    private void StopPost(string postId)
    {
        var monitor = GetPostMonitor(postId);
        if (!monitor.CanStop) return;

        _experimentService.StopPost(postId);
        monitor.State = ExperimentState.Idle;
        monitor.CurrentExperiment = null;

        NotifyRunningChanged();
        StatusMessage = $"Пост {postId}: запись остановлена";
    }

    [RelayCommand]
    private void ExportPost(string postId)
    {
        // TODO: экспорт в DBF для конкретного поста
        StatusMessage = $"Пост {postId}: экспорт...";
    }

    [RelayCommand]
    private void OpenChannelChart(ChannelStatus? channel)
    {
        if (channel == null) return;

        var vm = new ChannelChartViewModel(channel, _experimentService);
        var window = new Views.ChannelChartWindow(vm)
        {
            Owner = Application.Current.MainWindow
        };
        window.Closed += (_, _) => vm.Unsubscribe();
        window.Show();
    }

    // --- Обработчики событий от сервиса ---

    private void OnChannelValueReceived(int index, double value)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_channelMap.TryGetValue(index, out var statuses)) return;

            foreach (var ch in statuses)
            {
                ch.CurrentValue = double.IsNaN(value) ? (double?)null : value;
                ch.LastUpdateTime = DateTime.Now;

                var monitor = GetPostMonitor(ch.Post);
                if (double.IsNaN(value))
                    ch.Status = HealthStatus.NoData;
                else if (monitor.IsRunning)
                    ch.Status = HealthStatus.OK;
            }
        });
    }

    private void OnPostAnomalyDetected(string postId, AnomalyEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var monitor = GetPostMonitor(postId);
            monitor.AnomalyCount++;

            // Обновляем статус конкретного канала для конкретного поста
            if (_channelMap.TryGetValue(evt.ChannelIndex, out var statuses))
            {
                var ch = statuses.FirstOrDefault(s => s.Post == postId) ?? statuses.FirstOrDefault();
                if (ch != null)
                    ch.Status = evt.Severity == "Critical" ? HealthStatus.Alarm : HealthStatus.Warning;
            }

            var entry = new LogEntry
            {
                Timestamp = evt.Timestamp,
                Level = evt.Severity == "Critical" ? "Error" : "Warning",
                Source = evt.ChannelName,
                Post = postId,
                Message = evt.Message
            };

            LogEntries.Insert(0, entry);
            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);

            // Добавляем в RecentAlerts
            RecentAlerts.Insert(0, entry);
            while (RecentAlerts.Count > 8)
                RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
        });
    }

    private void MarkStaleChannels()
    {
        var threshold = TimeSpan.FromSeconds(5);
        var now = DateTime.Now;
        foreach (var statuses in _channelMap.Values)
        {
            foreach (var ch in statuses)
            {
                if (ch.Status == HealthStatus.OK && (now - ch.LastUpdateTime) > threshold)
                    ch.Status = HealthStatus.NoData;
            }
        }
    }

    private void OnHealthUpdated(SystemHealth health)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            SystemHealth = health;
            if (!IsAnyPostRunning)
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
    event Action<int, double>? ChannelValueReceived;
    event Action<string, AnomalyEvent>? PostAnomalyDetected;

    void Configure(string host, int port, int timeoutMs);
    void BeginMonitoring();

    void StartPost(string postId, Experiment experiment, IReadOnlyList<int> channelIndices);
    void PausePost(string postId);
    void ResumePost(string postId);
    void StopPost(string postId);

    ExperimentState GetPostState(string postId);
    SystemHealth GetCurrentHealth();
}
