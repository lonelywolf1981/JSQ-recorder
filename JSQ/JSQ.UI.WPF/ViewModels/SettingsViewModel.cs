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
    private const string SettingsFile = "app_settings.json";

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

    // --- Тест подключения ---

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testResultMessage = string.Empty;

    [ObservableProperty]
    private bool _testResultSuccess;

    [ObservableProperty]
    private bool _testResultVisible;

    public bool Saved { get; private set; }

    public event Action? SaveCompleted;

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
                ExportPath = ExportPath
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

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(ConnectionTimeoutMs);

            var connectTask = client.ConnectAsync(TransmitterHost, TransmitterPort);
            var timeoutTask = Task.Delay(ConnectionTimeoutMs, cts.Token);

            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            {
                TestResultSuccess = false;
                TestResultMessage = $"Таймаут: нет ответа от {TransmitterHost}:{TransmitterPort} за {ConnectionTimeoutMs} мс";
            }
            else
            {
                await connectTask;
                TestResultSuccess = true;
                TestResultMessage = $"Подключение успешно: {TransmitterHost}:{TransmitterPort}";
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

    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Save()
    {
        SaveToFile();
        Saved = true;
        SaveCompleted?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        // Откат к последним сохранённым значениям
        LoadFromFile();
        Saved = false;
        SaveCompleted?.Invoke();
    }

    // DTO для сериализации
    private class SettingsDto
    {
        public string TransmitterHost { get; set; } = string.Empty;
        public int TransmitterPort { get; set; }
        public int ConnectionTimeoutMs { get; set; }
        public string DatabasePath { get; set; } = string.Empty;
        public string ExportPath { get; set; } = string.Empty;
    }
}
