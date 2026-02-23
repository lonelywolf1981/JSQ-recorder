using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using JSQ.UI.WPF.ViewModels;
using JSQ.Core.Models;
using JSQ.Storage;

namespace JSQ.UI.WPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public IServiceProvider Services { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Перехватываем все необработанные исключения — не даём приложению молча закрыться
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"Ошибка UI:\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "JSQ - Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject?.ToString() ?? "неизвестная ошибка";
            MessageBox.Show($"Критическая ошибка:\n{ex}", "JSQ - Критическая ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка запуска: {ex.Message}\n\n{ex.StackTrace}",
                "JSQ - Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // ViewModels
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        // Storage — путь к БД берём из настроек
        services.AddSingleton<IDatabaseService>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsViewModel>();
            return new SqliteDatabaseService(settings.DatabasePath);
        });
        services.AddSingleton<IBatchWriter>(sp =>
            new BatchWriter(sp.GetRequiredService<IDatabaseService>()));
        services.AddSingleton<IExperimentRepository>(sp =>
            new ExperimentRepository(sp.GetRequiredService<IDatabaseService>()));

        // Services
        services.AddSingleton<IExperimentService>(sp =>
            new ExperimentService(
                sp.GetRequiredService<IDatabaseService>(),
                sp.GetRequiredService<IBatchWriter>(),
                sp.GetRequiredService<IExperimentRepository>()));

        // Views
        services.AddSingleton<MainWindow>();
    }
}

/// <summary>
/// Заглушка для сервиса экспериментов (для разработки UI)
/// </summary>
public class ExperimentServiceStub : IExperimentService
{
    public event Action<SystemHealth> HealthUpdated = delegate { };
    public event Action<LogEntry> LogReceived = delegate { };
    public event Action<int, double>? ChannelValueReceived;
    public event Action<AnomalyEvent>? AnomalyDetected;

    public SystemHealth GetCurrentHealth() => new SystemHealth();

    public void Configure(string host, int port, int timeoutMs) { }
    public void BeginMonitoring() { }
    public void StartExperiment(Experiment experiment) { }
    public void PauseExperiment() { }
    public void ResumeExperiment() { }
    public void StopExperiment() { }
}
