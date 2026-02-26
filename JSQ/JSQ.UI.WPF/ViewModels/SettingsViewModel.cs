using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JSQ.UI.WPF.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private static readonly string SettingsFile =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.json");

    [ObservableProperty]
    private string _transmitterHost = "192.168.0.214";

    [ObservableProperty]
    private int _transmitterPort = 55555;

    [ObservableProperty]
    private int _connectionTimeoutMs = 5000;

    [ObservableProperty]
    private string _databasePath = @"data\experiments.db";

    [ObservableProperty]
    private string _exportPath = @"export\";

    [ObservableProperty]
    private bool _autoUpdateEnabled = true;

    [ObservableProperty]
    private string _updateFeedPath = @"\\srv-updates\JSQ\stable";

    [ObservableProperty]
    private int _updateCheckIntervalMinutes = 10;

    [ObservableProperty]
    private bool _isCheckingUpdateFeed;

    [ObservableProperty]
    private string _updateFeedCheckMessage = string.Empty;

    [ObservableProperty]
    private bool _updateFeedCheckSuccess;

    [ObservableProperty]
    private bool _updateFeedCheckVisible;

    // --- Тест подключения ---

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testResultMessage = string.Empty;

    [ObservableProperty]
    private bool _testResultSuccess;

    [ObservableProperty]
    private bool _testResultVisible;

    /// <summary>
    /// Неинвазивная проверка соединения через уже работающий мониторинг (если доступна).
    /// Возвращает: handled/success/message.
    /// </summary>
    public Func<string, int, int, Task<(bool handled, bool success, string message)>>? NonIntrusiveConnectionProbe { get; set; }

    /// <summary>
    /// Абсолютный путь к БД.
    /// Относительные пути разрешаются от BaseDirectory приложения.
    /// </summary>
    public string ResolvedDatabasePath => Path.IsPathRooted(DatabasePath)
        ? DatabasePath
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DatabasePath);

    public bool Saved { get; private set; }

    public event Action? SaveCompleted;
    public event Action? CancelRequested;

    public SettingsViewModel()
    {
        LoadFromFile();
    }

    // --- Персистентность ---

    private void LoadFromFile()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return;

            var json = File.ReadAllText(SettingsFile);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            if (dto == null)
                return;

            if (!string.IsNullOrWhiteSpace(dto.TransmitterHost))
                TransmitterHost = dto.TransmitterHost;
            if (dto.TransmitterPort > 0)
                TransmitterPort = dto.TransmitterPort;
            if (dto.ConnectionTimeoutMs > 0)
                ConnectionTimeoutMs = dto.ConnectionTimeoutMs;
            if (!string.IsNullOrWhiteSpace(dto.DatabasePath))
                DatabasePath = dto.DatabasePath;
            if (!string.IsNullOrWhiteSpace(dto.ExportPath))
                ExportPath = dto.ExportPath;
            AutoUpdateEnabled = dto.AutoUpdateEnabled;
            if (!string.IsNullOrWhiteSpace(dto.UpdateFeedPath))
                UpdateFeedPath = dto.UpdateFeedPath;
            if (dto.UpdateCheckIntervalMinutes > 0)
                UpdateCheckIntervalMinutes = dto.UpdateCheckIntervalMinutes;
        }
        catch
        {
            // Повреждённый файл — используем дефолты, не падаем
        }
    }

    private void SaveToFile()
    {
        try
        {
            var dto = new SettingsDto
            {
                TransmitterHost = TransmitterHost,
                TransmitterPort = TransmitterPort,
                ConnectionTimeoutMs = ConnectionTimeoutMs,
                DatabasePath = DatabasePath,
                ExportPath = ExportPath,
                AutoUpdateEnabled = AutoUpdateEnabled,
                UpdateFeedPath = UpdateFeedPath,
                UpdateCheckIntervalMinutes = UpdateCheckIntervalMinutes
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Не удалось сохранить — не критично
        }
    }

    // --- Тест подключения ---

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResultVisible = false;
        TestResultMessage = string.Empty;

        var host = (TransmitterHost ?? string.Empty).Trim();
        var port = TransmitterPort;
        var timeoutMs = ConnectionTimeoutMs;

        try
        {
            var probe = NonIntrusiveConnectionProbe;
            if (probe != null)
            {
                var (handled, success, message) = await probe(host, port, timeoutMs);
                if (handled)
                {
                    TestResultSuccess = success;
                    TestResultMessage = message;
                    return;
                }
            }

            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);

            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeoutMs, cts.Token);

            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            {
                TestResultSuccess = false;
                TestResultMessage = $"Таймаут: нет ответа от {host}:{port} за {timeoutMs} мс";
            }
            else
            {
                await connectTask;
                TestResultSuccess = true;
                TestResultMessage = $"Подключение успешно: {host}:{port}";
            }
        }
        catch (Exception ex)
        {
            TestResultSuccess = false;
            TestResultMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
            TestResultVisible = true;
        }
    }

    private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(TransmitterHost) && TransmitterPort > 0;

    [RelayCommand(CanExecute = nameof(CanCheckUpdateFeed))]
    private async Task CheckUpdateFeedAsync()
    {
        IsCheckingUpdateFeed = true;
        UpdateFeedCheckVisible = false;
        UpdateFeedCheckMessage = string.Empty;

        var feedPath = (UpdateFeedPath ?? string.Empty).Trim();

        try
        {
            var (success, message) = await Task.Run(() => CheckUpdateFeedPath(feedPath));
            UpdateFeedCheckSuccess = success;
            UpdateFeedCheckMessage = message;
        }
        catch (Exception ex)
        {
            UpdateFeedCheckSuccess = false;
            UpdateFeedCheckMessage = $"Ошибка проверки: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdateFeed = false;
            UpdateFeedCheckVisible = true;
        }
    }

    private static (bool success, string message) CheckUpdateFeedPath(string feedPath)
    {
        if (string.IsNullOrWhiteSpace(feedPath))
            return (false, "Путь к обновлениям не задан");

        if (!Directory.Exists(feedPath))
            return (false, $"Папка недоступна: {feedPath}");

        _ = Directory.GetFileSystemEntries(feedPath);

        var manifestPath = Path.Combine(feedPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return (true, $"Папка доступна: {feedPath}. manifest.json не найден");
        }

        return (true, $"Папка доступна: {feedPath}. manifest.json найден");
    }

    private bool CanCheckUpdateFeed() => !IsTesting && !IsCheckingUpdateFeed;

    partial void OnIsTestingChanged(bool value)
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        CheckUpdateFeedCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingUpdateFeedChanged(bool value)
    {
        CheckUpdateFeedCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveOrCancel() => !IsTesting && !IsCheckingUpdateFeed;

    [RelayCommand(CanExecute = nameof(CanSaveOrCancel))]
    private void Save()
    {
        // Санитизация пользовательского ввода перед сохранением/применением
        TransmitterHost = (TransmitterHost ?? string.Empty).Trim();
        DatabasePath = (DatabasePath ?? string.Empty).Trim();
        ExportPath = (ExportPath ?? string.Empty).Trim();
        UpdateFeedPath = (UpdateFeedPath ?? string.Empty).Trim();

        SaveToFile();
        Saved = true;
        SaveCompleted?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanSaveOrCancel))]
    private void Cancel()
    {
        LoadFromFile();
        Saved = false;
        CancelRequested?.Invoke();
    }

    // DTO для сериализации
    private class SettingsDto
    {
        public string TransmitterHost { get; set; } = string.Empty;
        public int TransmitterPort { get; set; }
        public int ConnectionTimeoutMs { get; set; }
        public string DatabasePath { get; set; } = string.Empty;
        public string ExportPath { get; set; } = string.Empty;
        public bool AutoUpdateEnabled { get; set; } = true;
        public string UpdateFeedPath { get; set; } = string.Empty;
        public int UpdateCheckIntervalMinutes { get; set; } = 10;
    }
}
