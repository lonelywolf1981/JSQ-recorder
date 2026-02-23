using System.IO;
using System.Text.Json;

namespace JSQ.Core.Configuration;

/// <summary>
/// Сервис конфигурации
/// </summary>
public interface IConfigService
{
    AppConfig Load();
    void Save(AppConfig config);
}

/// <summary>
/// Сервис конфигурации на основе JSON
/// </summary>
public class JsonConfigService : IConfigService
{
    private readonly string _configPath;
    
    public JsonConfigService(string configPath = "jsq.config.json")
    {
        _configPath = configPath;
    }
    
    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            return new AppConfig();
        }
        
        var json = File.ReadAllText(_configPath);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }
    
    public void Save(AppConfig config)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        
        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(_configPath, json);
    }
}
