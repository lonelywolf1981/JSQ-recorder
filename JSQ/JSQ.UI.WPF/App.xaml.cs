using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using JSQ.UI.WPF.ViewModels;
using JSQ.Core.Models;

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
        
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
    
    private void ConfigureServices(IServiceCollection services)
    {
        // ViewModels
        services.AddSingleton<MainViewModel>();
        
        // Services
        // TODO: Добавить реальные сервисы
        services.AddSingleton<IExperimentService, ExperimentServiceStub>();
        
        // Views
        services.AddSingleton<MainWindow>();
    }
}

/// <summary>
/// Заглушка для сервиса экспериментов (для разработки UI)
/// </summary>
public class ExperimentServiceStub : IExperimentService
{
    public event Action<SystemHealth> HealthUpdated;
    public event Action<LogEntry> LogReceived;
    
    public SystemHealth GetCurrentHealth() => new SystemHealth();
    
    public void StartExperiment(Experiment experiment) { }
    public void PauseExperiment() { }
    public void ResumeExperiment() { }
    public void StopExperiment() { }
}
