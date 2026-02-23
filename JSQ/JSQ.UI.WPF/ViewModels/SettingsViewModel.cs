using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JSQ.UI.WPF.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
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
        Saved = true;
        SaveCompleted?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Saved = false;
        SaveCompleted?.Invoke();
    }
}
