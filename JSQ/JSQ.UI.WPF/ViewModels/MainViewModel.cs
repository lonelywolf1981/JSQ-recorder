using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSQ.Core.Models;
using JSQ.Export;
using JSQ.UI.WPF.Views;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Главная ViewModel приложения
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ExperimentService _experimentService;
    private readonly ILegacyExportService _exportService;
    private readonly SettingsViewModel _settings;
    private readonly DispatcherTimer _staleChannelTimer;
    private static readonly TimeSpan StaleDataThreshold = TimeSpan.FromSeconds(5);
    private string _appliedHost = string.Empty;
    private int _appliedPort;
    private int _appliedTimeoutMs;

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
    [NotifyPropertyChangedFor(nameof(SystemHealthStatusText))]
    private SystemHealth _systemHealth = new();

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logEntries = new();

    /// <summary>Последние предупреждения/аномалии для панели Статус.</summary>
    public ObservableCollection<LogEntry> RecentAlerts { get; } = new();

    [ObservableProperty]
    private string _statusMessage = "Готов";

    /// <summary>True если хотя бы один пост ведёт запись.</summary>
    public bool IsAnyPostRunning => PostA.IsRunning || PostB.IsRunning || PostC.IsRunning;

    /// <summary>Читаемый текст для OverallStatus в блоке "Здоровье системы".</summary>
    public string SystemHealthStatusText => SystemHealth.OverallStatus switch
    {
        HealthStatus.OK      => "НОРМА",
        HealthStatus.Warning => "ВНИМАНИЕ",
        HealthStatus.Alarm   => "ТРЕВОГА",
        HealthStatus.NoData  => "НЕТ ДАННЫХ",
        _                    => "—"
    };

    // Количество каналов на каждом посту (для заголовка вкладки)
    public int PostACount => PostAChannels.Count;
    public int PostBCount => PostBChannels.Count;
    public int PostCCount => PostCChannels.Count;

    public MainViewModel(
        ExperimentService experimentService,
        ILegacyExportService exportService,
        SettingsViewModel settings)
    {
        _experimentService = experimentService;
        _exportService = exportService;
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
            NormalizeHost(_settings.TransmitterHost),
            _settings.TransmitterPort,
            _settings.ConnectionTimeoutMs);
        _appliedHost = NormalizeHost(_settings.TransmitterHost);
        _appliedPort = _settings.TransmitterPort;
        _appliedTimeoutMs = _settings.ConnectionTimeoutMs;
        _experimentService.BeginMonitoring();

        _settings.SaveCompleted += OnSettingsSaved;
        _settings.NonIntrusiveConnectionProbe = ProbeCurrentConnectionAsync;
        // CancelRequested — просто закрывает окно, BeginMonitoring не вызываем
    }

    private void OnSettingsSaved()
    {
        var host = NormalizeHost(_settings.TransmitterHost);
        var connectionChanged =
            !string.Equals(_appliedHost, host, StringComparison.OrdinalIgnoreCase) ||
            _appliedPort != _settings.TransmitterPort ||
            _appliedTimeoutMs != _settings.ConnectionTimeoutMs;

        // Не переподключаемся если идёт запись
        if (!IsAnyPostRunning && connectionChanged)
        {
            _experimentService.Configure(
                host,
                _settings.TransmitterPort,
                _settings.ConnectionTimeoutMs);
            _experimentService.BeginMonitoring();

            _appliedHost = host;
            _appliedPort = _settings.TransmitterPort;
            _appliedTimeoutMs = _settings.ConnectionTimeoutMs;
            StatusMessage = $"Настройки сохранены. Переподключение к {host}:{_settings.TransmitterPort}";
            return;
        }

        if (IsAnyPostRunning)
        {
            StatusMessage = "Настройки сохранены. Переподключение отложено до остановки записи";
            return;
        }

        StatusMessage = "Настройки сохранены (без переподключения)";
    }

    private static string NormalizeHost(string? host)
        => (host ?? string.Empty).Trim();

    private Task<(bool handled, bool success, string message)> ProbeCurrentConnectionAsync(
        string host,
        int port,
        int timeoutMs)
    {
        _ = timeoutMs;

        var normalizedHost = NormalizeHost(host);
        var snapshot = _experimentService.GetConnectionSnapshot();

        if (!string.Equals(snapshot.host, normalizedHost, StringComparison.OrdinalIgnoreCase) ||
            snapshot.port != port)
        {
            return Task.FromResult((false, false, string.Empty));
        }

        if (snapshot.status == ConnectionStatus.Connected)
        {
            var tail = snapshot.lastPacketTime == default
                ? string.Empty
                : $"; последний пакет {(JsqClock.Now - snapshot.lastPacketTime).TotalSeconds:F1} с назад";

            return Task.FromResult((
                true,
                true,
                $"Соединение уже активно: {normalizedHost}:{port} (без открытия нового сокета){tail}"));
        }

        if (snapshot.status == ConnectionStatus.Connecting || snapshot.status == ConnectionStatus.Reconnecting)
        {
            return Task.FromResult((
                true,
                true,
                $"Идёт подключение к {normalizedHost}:{port} (без открытия нового сокета)"));
        }

        return Task.FromResult((
            true,
            false,
            $"Текущее соединение для {normalizedHost}:{port} не активно: {snapshot.status}"));
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
            MinLimit = def.MinLimit,
            MaxLimit = def.MaxLimit,
            HighPrecision = def.HighPrecision,
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
                MinLimit = def.MinLimit,
                MaxLimit = def.MaxLimit,
                HighPrecision = def.HighPrecision,
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
        monitor.LastExperimentId = experiment.Id;
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
        monitor.AnomalyCount = 0;
        monitor.CurrentExperiment = null;

        // После остановки записи снимаем «боевые» статусы канала.
        // Если данных нет (или они устарели) — канал уходит в серый NoData.
        // Если поток живой — канал становится OK.
        NormalizePostStatusesAfterStop(postId);

        NotifyRunningChanged();
        StatusMessage = $"Пост {postId}: запись остановлена";
    }

    private void NormalizePostStatusesAfterStop(string postId)
    {
        var now = JsqClock.Now;
        foreach (var ch in GetPostChannels(postId))
        {
            ch.IsRecording = false;  // сбрасываем флаг записи

            ch.Status = AlertStatusPolicy.ResolveAfterStop(
                ch.LastUpdateTime,
                ch.CurrentValue,
                now,
                StaleDataThreshold);
        }
    }

    [RelayCommand]
    private async Task ExportPost(string postId)
    {
        var monitor = GetPostMonitor(postId);
        var experimentId = monitor.IsRunning
            ? monitor.CurrentExperiment?.Id
            : monitor.LastExperimentId;

        if (string.IsNullOrWhiteSpace(experimentId))
        {
            StatusMessage = $"Пост {postId}: нечего экспортировать";
            return;
        }

        var safeExperimentId = experimentId!;

        try
        {
            var outputRoot = string.IsNullOrWhiteSpace(_settings.ExportPath)
                ? "export"
                : _settings.ExportPath;

            var defaultPath = BuildDefaultExportDbfPath(outputRoot);
            var dlg = new SaveFileDialog
            {
                Title = "Экспорт в legacy-пакет",
                Filter = "DBF файл (*.dbf)|*.dbf",
                FileName = Path.GetFileName(defaultPath),
                InitialDirectory = Path.GetDirectoryName(defaultPath),
                AddExtension = true,
                DefaultExt = ".dbf",
                OverwritePrompt = false,
                CheckPathExists = true
            };

            var accepted = dlg.ShowDialog(Application.Current.MainWindow) == true;
            if (!accepted)
            {
                StatusMessage = $"Пост {postId}: экспорт отменён";
                return;
            }

            var targetDirectory = Path.GetDirectoryName(dlg.FileName);
            var packageName = Path.GetFileNameWithoutExtension(dlg.FileName);
            if (string.IsNullOrWhiteSpace(targetDirectory) || string.IsNullOrWhiteSpace(packageName))
            {
                StatusMessage = $"Пост {postId}: некорректный путь экспорта";
                return;
            }

            _settings.ExportPath = targetDirectory;

            var result = await _exportService.ExportExperimentAsync(
                safeExperimentId,
                targetDirectory,
                packageName,
                "Prova001");
            StatusMessage = $"Пост {postId}: экспорт завершён ({result.PackageName}, {result.RecordCount} строк)";

            LogEntries.Insert(0, new LogEntry
            {
                Timestamp = JsqClock.Now,
                Level = "Info",
                Source = "Export",
                Post = postId,
                Message = $"Экспорт: {result.PackageDirectory}"
            });
            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);

            MessageBox.Show(
                $"Экспорт завершён.\nПапка: {result.PackageDirectory}",
                "JSQ Export",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Пост {postId}: ошибка экспорта — {ex.Message}";
            MessageBox.Show(
                ex.Message,
                "Ошибка экспорта",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string BuildDefaultExportDbfPath(string outputRoot)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(outputRoot) ? "export" : outputRoot);
        Directory.CreateDirectory(root);

        var max = 0;
        foreach (var dir in Directory.GetDirectories(root, "Prova*"))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("Prova", StringComparison.OrdinalIgnoreCase))
            {
                var numeric = new string(name.Skip(5).TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(numeric, out var value) && value > max)
                    max = value;
            }
        }

        return Path.Combine(root, $"Prova{max + 1:D3}.dbf");
    }

    [RelayCommand]
    private void OpenEventHistory(string postId)
    {
        var monitor = GetPostMonitor(postId);
        var initialExperimentId = monitor.IsRunning
            ? monitor.CurrentExperiment?.Id
            : monitor.LastExperimentId;

        var window = new Views.EventHistoryWindow(postId, _experimentService, _exportService, _settings, initialExperimentId)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }

    [RelayCommand]
    private void OpenChannelChart(ChannelStatus? channel)
    {
        if (channel == null) return;

        // Берём время старта записи для поста этого канала (если запись активна)
        DateTime? experimentStart = null;
        if (!string.IsNullOrEmpty(channel.Post))
        {
            var monitor = GetPostMonitor(channel.Post);
            experimentStart = monitor.CurrentExperiment?.StartTime;
        }

        var vm = new ChannelChartViewModel(channel, _experimentService, experimentStart);
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
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_channelMap.TryGetValue(index, out var statuses)) return;

            ChannelRegistry.All.TryGetValue(index, out var def);

            foreach (var ch in statuses)
            {
                ch.CurrentValue = double.IsNaN(value) ? (double?)null : value;
                ch.LastUpdateTime = JsqClock.Now;

                if (double.IsNaN(value))
                {
                    // Некорректное значение в потоке — нет данных
                    ch.Status = HealthStatus.NoData;
                }
                else
                {
                    // Данные пришли — канал живой
                    var monitor = GetPostMonitor(ch.Post);
                    ch.IsRecording = monitor.IsRunning;

                    if (monitor.IsRunning &&
                        def != null &&
                        ((def.MinLimit.HasValue && value < def.MinLimit.Value) ||
                         (def.MaxLimit.HasValue && value > def.MaxLimit.Value)))
                    {
                        ch.Status = HealthStatus.Warning;
                    }
                    else if (ch.Status != HealthStatus.Alarm)
                    {
                        // Не сбрасываем Alarm — его снимает только DataRestored
                        ch.Status = HealthStatus.OK;  // данные идут → канал живой
                    }
                }
            }
        });
    }

    private void OnPostAnomalyDetected(string postId, AnomalyEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var monitor = GetPostMonitor(postId);
            string level;

            if (evt.AnomalyType == AnomalyType.DataRestored)
            {
                // Восстановление — не аномалия, логируем Info/зелёный
                level = "Info";
                // Явно снимаем Alarm: OnChannelValueReceived не трогает Alarm-статус
                if (_channelMap.TryGetValue(evt.ChannelIndex, out var restoredStatuses))
                {
                    var ch = restoredStatuses.FirstOrDefault(s => s.Post == postId) ?? restoredStatuses.FirstOrDefault();
                    if (ch != null)
                        ch.Status = HealthStatus.OK;
                }
            }
            else if (evt.AnomalyType == AnomalyType.LimitsRestored)
            {
                // Возврат в пределы — не аномалия, логируем Info/зелёный, канал → OK
                level = "Info";
                if (_channelMap.TryGetValue(evt.ChannelIndex, out var limRestoredStatuses))
                {
                    var ch = limRestoredStatuses.FirstOrDefault(s => s.Post == postId) ?? limRestoredStatuses.FirstOrDefault();
                    if (ch != null)
                        ch.Status = HealthStatus.OK;
                }
            }
            else if (evt.AnomalyType == AnomalyType.MinViolation ||
                     evt.AnomalyType == AnomalyType.MaxViolation)
            {
                // Выход за пределы — предупреждение, но НЕ аномалия:
                // счётчик ⚠ не увеличиваем, в здоровье идёт в "Предупреждения"
                level = "Warning";
                if (_channelMap.TryGetValue(evt.ChannelIndex, out var statuses))
                {
                    var ch = statuses.FirstOrDefault(s => s.Post == postId) ?? statuses.FirstOrDefault();
                    if (ch != null)
                        ch.Status = HealthStatus.Warning;
                }
            }
            else
            {
                // Настоящая аномалия (NoData — отключение канала, DeltaSpike, QualityBad):
                // счётчик ⚠ увеличиваем, в здоровье идёт в "Отключены"
                monitor.AnomalyCount++;
                level = evt.Severity == "Critical" ? "Error" : "Warning";

                if (_channelMap.TryGetValue(evt.ChannelIndex, out var statuses))
                {
                    var ch = statuses.FirstOrDefault(s => s.Post == postId) ?? statuses.FirstOrDefault();
                    if (ch != null)
                        ch.Status = evt.Severity == "Critical" ? HealthStatus.Alarm : HealthStatus.Warning;
                }
            }

            var entry = new LogEntry
            {
                Timestamp = evt.Timestamp,
                Level = level,
                Source = evt.ChannelName,
                Post = postId,
                Message = evt.Message
            };

            LogEntries.Insert(0, entry);
            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);

            RecentAlerts.Insert(0, entry);
            while (RecentAlerts.Count > 8)
                RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
        });
    }

    private void MarkStaleChannels()
    {
        // Во время записи: OK/Warning → NoData если данные молчат > 5 сек.
        // Вне записи: любой устаревший статус → NoData (серый), включая бывший Alarm.
        // Сами оповещения генерирует AnomalyDetector.CheckTimeouts (через 10 сек),
        // а не этот таймер — чтобы исключить ложные срабатывания при паузах потока.
        var now = JsqClock.Now;
        foreach (var statuses in _channelMap.Values)
        {
            foreach (var ch in statuses)
            {
                var isRunning = !string.IsNullOrEmpty(ch.Post) && GetPostMonitor(ch.Post).IsRunning;
                var isStale = AlertStatusPolicy.IsStale(ch.LastUpdateTime, now, StaleDataThreshold);

                if (!isRunning)
                {
                    if (isStale)
                    {
                        ch.Status = HealthStatus.NoData;
                        ch.IsRecording = false;
                    }
                    continue;
                }

                if ((ch.Status == HealthStatus.OK || ch.Status == HealthStatus.Warning) &&
                    isStale)
                {
                    ch.Status = HealthStatus.NoData;
                }
            }
        }
    }

    private void OnHealthUpdated(SystemHealth health)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Считаем реальные статусы каналов из UI-модели
            int ok = 0, warning = 0, alarm = 0, noData = 0;
            foreach (var statuses in _channelMap.Values)
            {
                foreach (var ch in statuses)
                {
                    switch (ch.Status)
                    {
                        case HealthStatus.OK:      ok++;      break;
                        case HealthStatus.Warning:  warning++; break;
                        case HealthStatus.Alarm:    alarm++;   break;
                        default:                   noData++;  break;
                    }
                }
            }
            health.ChannelsOK = ok;
            health.ChannelsWarning = warning;
            health.ChannelsAlarm = alarm;
            health.ChannelsNoData = noData;
            health.TotalChannels = ok + warning + alarm + noData;

            // OverallStatus по реальному состоянию каналов
            if (alarm > 0) health.OverallStatus = HealthStatus.Alarm;
            else if (warning > 0) health.OverallStatus = HealthStatus.Warning;
            else if (ok > 0) health.OverallStatus = HealthStatus.OK;
            else health.OverallStatus = HealthStatus.NoData;

            SystemHealth = health;
            if (!IsAnyPostRunning)
            {
                StatusMessage = health.OverallStatus switch
                {
                    HealthStatus.OK => "Все системы в норме",
                    HealthStatus.Warning => $"Внимание: {health.ChannelsWarning} каналов с предупреждениями",
                    HealthStatus.Alarm => $"Тревога: {health.ChannelsAlarm} каналов в аварии",
                    HealthStatus.NoData => "Нет данных от источника",
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

    Task<List<(DateTime time, double value)>> LoadChannelHistoryAsync(
        int channelIndex, DateTime startTime, DateTime endTime);

    Task<List<(DateTime time, double value)>> LoadExperimentChannelHistoryAsync(
        string experimentId,
        int channelIndex,
        DateTime startTime,
        DateTime endTime);

    Task<List<ExperimentChannelInfo>> GetExperimentChannelsAsync(string experimentId);

    Task<List<PostExperimentRecord>> GetPostExperimentsAsync(
        string postId,
        DateTime? startFrom = null,
        DateTime? startTo = null,
        string? searchText = null);

    Task<(DateTime? start, DateTime? end)> GetExperimentDataRangeAsync(string experimentId);

    Task<List<ChannelEventRecord>> GetExperimentEventsAsync(string experimentId);
}

/// <summary>
/// Запись события канала для отображения в истории эксперимента
/// </summary>
public class ChannelEventRecord
{
    public DateTime Timestamp { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public double? Value { get; set; }
    public double? Threshold { get; set; }

    public string EventTypeDisplay => EventType switch
    {
        "NoData"          => "Нет данных",
        "DataRestored"    => "Данные восстановлены",
        "MinViolation"    => "Ниже минимума",
        "MaxViolation"    => "Выше максимума",
        "LimitsRestored"  => "Возврат в пределы",
        "DeltaSpike"      => "Резкий скачок",
        "QualityDegraded" => "Ухудшение качества",
        "QualityBad"      => "Плохое качество",
        _                 => EventType
    };
}

public class ExperimentChannelInfo
{
    public int ChannelIndex { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Unit)
        ? $"{ChannelName} (v{ChannelIndex:D3})"
        : $"{ChannelName} [{Unit}] (v{ChannelIndex:D3})";
}

public class PostExperimentRecord
{
    public string Id { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Refrigerant { get; set; } = string.Empty;
    public ExperimentState State { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public string StateDisplay => State.ToString();
    public string StartDisplay => StartTime == default ? "—" : StartTime.ToString("dd.MM.yyyy HH:mm:ss");
}
