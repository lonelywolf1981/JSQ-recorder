namespace JSQ.Core.Configuration;

/// <summary>
/// Настройки приложения
/// </summary>
public class AppConfig
{
    public string AppName { get; set; } = "JSQ Experiment Controller";
    public string Version { get; set; } = "0.1.0";
    
    // Настройки подключения к передатчику
    public TransmitterConfig Transmitter { get; set; } = new();
    
    // Настройки БД
    public DatabaseConfig Database { get; set; } = new();
    
    // Настройки логирования
    public LoggingConfig Logging { get; set; } = new();
    
    // Настройки экспорта
    public ExportConfig Export { get; set; } = new();
}

/// <summary>
/// Настройки передатчика
/// </summary>
public class TransmitterConfig
{
    public string IpAddress { get; set; } = "192.168.0.214";
    public int Port { get; set; } = 55555;
    public int ConnectionTimeoutMs { get; set; } = 5000;
    public int ReadTimeoutMs { get; set; } = 1000;
}

/// <summary>
/// Настройки БД
/// </summary>
public class DatabaseConfig
{
    public string DbPath { get; set; } = "data\\experiments.db";
    public int BatchSize { get; set; } = 500;
    public int FlushIntervalSec { get; set; } = 1;
}

/// <summary>
/// Настройки логирования
/// </summary>
public class LoggingConfig
{
    public string LogPath { get; set; } = "logs\\jsq-.log";
    public string MinimumLevel { get; set; } = "Information";
    public int RetainDays { get; set; } = 30;
}

/// <summary>
/// Настройки экспорта
/// </summary>
public class ExportConfig
{
    public string ExportPath { get; set; } = "export";
    public bool AtomicExport { get; set; } = true;
    public string DateFormat { get; set; } = "yyyyMMdd_HHmmss";
}
