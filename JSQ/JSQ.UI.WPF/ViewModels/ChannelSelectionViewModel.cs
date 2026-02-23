using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSQ.Core.Models;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// ViewModel для выбора каналов
/// </summary>
public partial class ChannelSelectionViewModel : ObservableObject
{
    private readonly string _presetsPath = "channel_presets.json";
    
    [ObservableProperty]
    private ObservableCollection<ChannelDefinitionViewModel> _allChannels = new();
    
    [ObservableProperty]
    private ObservableCollection<ChannelDefinitionViewModel> _filteredChannels = new();
    
    [ObservableProperty]
    private string _searchText = string.Empty;
    
    [ObservableProperty]
    private string _selectedGroup = "Все";
    
    [ObservableProperty]
    private string _selectedPost = "Все";
    
    [ObservableProperty]
    private ObservableCollection<string> _availableGroups = new();
    
    [ObservableProperty]
    private ObservableCollection<string> _availablePosts = new();
    
    [ObservableProperty]
    private ObservableCollection<PresetInfo> _availablePresets = new();
    
    [ObservableProperty]
    private string _selectedPresetName = string.Empty;
    
    [ObservableProperty]
    private double? _bulkMinLimit;
    
    [ObservableProperty]
    private double? _bulkMaxLimit;
    
    public ChannelSelectionViewModel()
    {
        AvailableGroups.Add("Все");
        AvailableGroups.Add("Пост A");
        AvailableGroups.Add("Пост B");
        AvailableGroups.Add("Пост C");
        AvailableGroups.Add("Общие");
        AvailableGroups.Add("Давление");
        AvailableGroups.Add("Температура");
        AvailableGroups.Add("Электрические");
        
        AvailablePosts.Add("Все");
        AvailablePosts.Add("A");
        AvailablePosts.Add("B");
        AvailablePosts.Add("C");
        
        LoadPresets();
        
        // Загрузка тестовых данных
        LoadTestChannels();
    }
    
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }
    
    partial void OnSelectedGroupChanged(string value)
    {
        ApplyFilters();
    }
    
    partial void OnSelectedPostChanged(string value)
    {
        ApplyFilters();
    }
    
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var channel in FilteredChannels)
        {
            channel.IsSelected = true;
        }
    }
    
    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var channel in FilteredChannels)
        {
            channel.IsSelected = false;
        }
    }
    
    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var channel in FilteredChannels)
        {
            channel.IsSelected = !channel.IsSelected;
        }
    }
    
    [RelayCommand]
    private void ApplyBulkLimits()
    {
        foreach (var channel in FilteredChannels.Where(c => c.IsSelected))
        {
            if (BulkMinLimit.HasValue)
                channel.MinLimit = BulkMinLimit;
            if (BulkMaxLimit.HasValue)
                channel.MaxLimit = BulkMaxLimit;
        }
    }
    
    [RelayCommand]
    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedPresetName))
            return;
        
        var selectedChannels = AllChannels
            .Where(c => c.IsSelected)
            .Select(c => c.Index)
            .ToList();
        
        var preset = new ChannelPreset
        {
            Name = SelectedPresetName,
            SelectedChannelIndices = selectedChannels,
            CreatedAt = DateTime.Now
        };
        
        var presets = LoadAllPresets();
        var existing = presets.FirstOrDefault(p => p.Name == SelectedPresetName);
        if (existing != null)
            presets.Remove(existing);
        presets.Add(preset);
        
        SaveAllPresets(presets);
        LoadPresets();
    }
    
    [RelayCommand]
    private void LoadPreset()
    {
        var preset = AvailablePresets.FirstOrDefault(p => p.Name == SelectedPresetName);
        if (preset == null)
            return;
        
        var fullPreset = LoadFullPreset(preset.Name);
        if (fullPreset == null)
            return;
        
        DeselectAllCommand.Execute(null);
        
        foreach (var channel in AllChannels.Where(c => fullPreset.SelectedChannelIndices.Contains(c.Index)))
        {
            channel.IsSelected = true;
        }
    }
    
    [RelayCommand]
    private void DeletePreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedPresetName))
            return;
        
        var presets = LoadAllPresets();
        var preset = presets.FirstOrDefault(p => p.Name == SelectedPresetName);
        if (preset != null)
        {
            presets.Remove(preset);
            SaveAllPresets(presets);
            LoadPresets();
        }
    }
    
    [RelayCommand]
    private void SaveSelection()
    {
        // Сохранение выбора каналов в эксперимент
        var selectedIndices = AllChannels
            .Where(c => c.IsSelected)
            .Select(c => c.Index)
            .ToList();
        
        // TODO: Интеграция с ExperimentService
        Console.WriteLine($"Выбрано каналов: {selectedIndices.Count}");
    }
    
    private void ApplyFilters()
    {
        FilteredChannels.Clear();
        
        var query = AllChannels.AsEnumerable();
        
        // Фильтр по поиску
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(c => c.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                    c.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }
        
        // Фильтр по группе
        if (SelectedGroup != "Все")
        {
            query = SelectedGroup switch
            {
                "Пост A" => query.Where(c => c.PostFix == "A"),
                "Пост B" => query.Where(c => c.PostFix == "B"),
                "Пост C" => query.Where(c => c.PostFix == "C"),
                "Общие" => query.Where(c => c.Group == ChannelGroup.Common),
                "Давление" => query.Where(c => c.Type == ChannelType.Pressure),
                "Температура" => query.Where(c => c.Type == ChannelType.Temperature),
                "Электрические" => query.Where(c => c.Type == ChannelType.Electrical),
                _ => query
            };
        }
        
        // Фильтр по посту
        if (SelectedPost != "Все")
        {
            query = query.Where(c => c.PostFix == SelectedPost);
        }
        
        foreach (var channel in query)
        {
            FilteredChannels.Add(channel);
        }
    }
    
    private void LoadTestChannels()
    {
        // Генерация тестовых каналов на основе спецификации
        var channels = new[]
        {
            // Пост A - Давление
            new { Index = 0, Name = "A-Pc", Unit = "bara", Group = ChannelGroup.PostA, Type = ChannelType.Pressure, Desc = "Discharge Pressure" },
            new { Index = 1, Name = "A-Pe", Unit = "bara", Group = ChannelGroup.PostA, Type = ChannelType.Pressure, Desc = "Suction Pressure" },
            
            // Пост A - Температуры
            new { Index = 16, Name = "A-Tc", Unit = "°C", Group = ChannelGroup.PostA, Type = ChannelType.Temperature, Desc = "Condensing temperature" },
            new { Index = 17, Name = "A-Te", Unit = "°C", Group = ChannelGroup.PostA, Type = ChannelType.Temperature, Desc = "Evaporation temperature" },
        };
        
        // Генерируем T1..T30 для каждого поста
        for (int i = 1; i <= 30; i++)
        {
            foreach (var post in new[] { "A", "B", "C" })
            {
                var index = post switch
                {
                    "A" => 17 + i,
                    "B" => 53 + i,
                    "C" => 99 + i,
                    _ => 0
                };
                
                AllChannels.Add(new ChannelDefinitionViewModel
                {
                    Index = index,
                    Name = $"{post}-T{i}",
                    Description = $"{post} Temperature {i}",
                    Unit = "°C",
                    Group = post switch { "A" => ChannelGroup.PostA, "B" => ChannelGroup.PostB, "C" => ChannelGroup.PostC },
                    Type = ChannelType.Temperature,
                    PostFix = post,
                    IsEnabled = true,
                    IsSelected = false
                });
            }
        }
        
        // Добавляем электрические каналы
        foreach (var post in new[] { "A", "B", "C" })
        {
            var baseIndex = post switch { "A" => 47, "B" => 84, "C" => 130 };
            
            AllChannels.Add(new ChannelDefinitionViewModel
            {
                Index = baseIndex,
                Name = $"{post}-I",
                Description = $"{post} Current",
                Unit = "A",
                Group = post switch { "A" => ChannelGroup.PostA, "B" => ChannelGroup.PostB, "C" => ChannelGroup.PostC },
                Type = ChannelType.Electrical,
                PostFix = post,
                IsEnabled = true,
                IsSelected = false
            });
        }
        
        ApplyFilters();
    }
    
    private void LoadPresets()
    {
        AvailablePresets.Clear();
        var presets = LoadAllPresets();
        foreach (var preset in presets)
        {
            AvailablePresets.Add(new PresetInfo { Name = preset.Name, CreatedAt = preset.CreatedAt });
        }
    }
    
    private System.Collections.Generic.List<ChannelPreset> LoadAllPresets()
    {
        if (!File.Exists(_presetsPath))
            return new System.Collections.Generic.List<ChannelPreset>();
        
        try
        {
            var json = File.ReadAllText(_presetsPath);
            return JsonSerializer.Deserialize<System.Collections.Generic.List<ChannelPreset>>(json) 
                ?? new System.Collections.Generic.List<ChannelPreset>();
        }
        catch
        {
            return new System.Collections.Generic.List<ChannelPreset>();
        }
    }
    
    private void SaveAllPresets(System.Collections.Generic.List<ChannelPreset> presets)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(presets, options);
        File.WriteAllText(_presetsPath, json);
    }
    
    private ChannelPreset? LoadFullPreset(string name)
    {
        var presets = LoadAllPresets();
        return presets.FirstOrDefault(p => p.Name == name);
    }
}

/// <summary>
/// Информация о пресете для UI
/// </summary>
public class PresetInfo
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Пресет выбора каналов
/// </summary>
public class ChannelPreset
{
    public string Name { get; set; } = string.Empty;
    public System.Collections.Generic.List<int> SelectedChannelIndices { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
