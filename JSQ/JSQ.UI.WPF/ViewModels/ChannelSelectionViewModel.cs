using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
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

    // Быстрый lookup для обновления живых значений: index → VM
    private readonly Dictionary<int, ChannelDefinitionViewModel> _channelByIndex = new();
    private IExperimentService? _liveDataService;
    
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
    private string _newPresetName = string.Empty;
    
    [ObservableProperty]
    private int _selectedCount;
    
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

        // Подписка на добавление каналов — для реактивного обновления SelectedCount
        AllChannels.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
                foreach (ChannelDefinitionViewModel ch in e.NewItems)
                    ch.PropertyChanged += OnChannelIsSelectedChanged;
            if (e.OldItems != null)
                foreach (ChannelDefinitionViewModel ch in e.OldItems)
                    ch.PropertyChanged -= OnChannelIsSelectedChanged;
        };

        LoadPresets();
        LoadTestChannels();
    }

    private void OnChannelIsSelectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelDefinitionViewModel.IsSelected))
            UpdateSelectedCount();
    }

    /// <summary>Подписаться на живые данные от передатчика.</summary>
    public void SubscribeToLiveData(IExperimentService service)
    {
        _liveDataService = service;
        // Строим индексный словарь
        _channelByIndex.Clear();
        foreach (var ch in AllChannels)
            _channelByIndex[ch.Index] = ch;
        service.ChannelValueReceived += OnLiveChannelValue;
    }

    /// <summary>Отписаться от живых данных (вызывается при закрытии окна).</summary>
    public void UnsubscribeFromLiveData()
    {
        if (_liveDataService != null)
        {
            _liveDataService.ChannelValueReceived -= OnLiveChannelValue;
            _liveDataService = null;
        }
    }

    private void OnLiveChannelValue(int index, double value)
    {
        if (!_channelByIndex.TryGetValue(index, out var ch))
            return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ch.CurrentValue = double.IsNaN(value) ? (double?)null : value;
            ch.IsActive = !double.IsNaN(value);
        });
    }
    
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }
    
    partial void OnSelectedGroupChanged(string value)
    {
        // Если выбрана группа по посту — сбрасываем фильтр "Пост", чтобы не было конфликта AND
        if (value is "Пост A" or "Пост B" or "Пост C")
            SelectedPost = "Все";
        ApplyFilters();
    }

    partial void OnSelectedPostChanged(string value)
    {
        // Если выбран конкретный пост — сбрасываем группу-пост, чтобы не было конфликта AND
        if (value != "Все" && SelectedGroup is "Пост A" or "Пост B" or "Пост C")
            SelectedGroup = "Все";
        ApplyFilters();
    }
    
    partial void OnSelectedCountChanged(int value)
    {
        // Обновление статуса при изменении количества выбранных
    }
    
    /// <summary>
    /// Пересчет количества выбранных каналов
    /// </summary>
    private void UpdateSelectedCount()
    {
        SelectedCount = AllChannels.Count(c => c.IsSelected);
    }
    
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var channel in FilteredChannels)
        {
            channel.IsSelected = true;
        }
        UpdateSelectedCount();
    }
    
    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var channel in FilteredChannels)
        {
            channel.IsSelected = false;
        }
        UpdateSelectedCount();
    }
    
    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var channel in FilteredChannels)
        {
            channel.IsSelected = !channel.IsSelected;
        }
        UpdateSelectedCount();
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
        if (string.IsNullOrWhiteSpace(NewPresetName))
            return;

        var selectedChannels = AllChannels
            .Where(c => c.IsSelected)
            .Select(c => c.Index)
            .ToList();

        var preset = new ChannelPreset
        {
            Name = NewPresetName,
            SelectedChannelIndices = selectedChannels,
            CreatedAt = DateTime.Now
        };

        var presets = LoadAllPresets();
        var existing = presets.FirstOrDefault(p => p.Name == NewPresetName);
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

        UpdateSelectedCount();
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
        
        // Обновляем счетчик
        UpdateSelectedCount();
        
        // TODO: Интеграция с ExperimentService
        Console.WriteLine($"Выбрано каналов: {selectedIndices.Count}");
        
        // Закрыть окно с результатом
        SaveSelectionCompleted?.Invoke(selectedIndices);
    }
    
    /// <summary>
    /// Событие завершения сохранения выбора
    /// </summary>
    public event Action<System.Collections.Generic.List<int>> SaveSelectionCompleted;
    
    private void ApplyFilters()
    {
        FilteredChannels.Clear();
        
        var query = AllChannels.AsEnumerable();
        
        // Фильтр по поиску
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(c => c.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    c.Description.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0);
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
        // Генерация всех 134 каналов согласно спецификации
        
        // Пост A - Давление (2 канала)
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 0, Name = "A-Pc", Description = "Discharge Pressure", Unit = "bara", Group = ChannelGroup.PostA, Type = ChannelType.Pressure, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 1, Name = "A-Pe", Description = "Suction Pressure", Unit = "bara", Group = ChannelGroup.PostA, Type = ChannelType.Pressure, PostFix = "A", IsEnabled = true, IsSelected = false });
        
        // Пост B - Давление (2 канала)
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 2, Name = "B-Pc", Description = "Discharge Pressure", Unit = "bara", Group = ChannelGroup.PostB, Type = ChannelType.Pressure, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 3, Name = "B-Pe", Description = "Suction Pressure", Unit = "bara", Group = ChannelGroup.PostB, Type = ChannelType.Pressure, PostFix = "B", IsEnabled = true, IsSelected = false });
        
        // Пост C - Давление (2 канала)
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 4, Name = "C-Pc", Description = "Discharge Pressure", Unit = "bara", Group = ChannelGroup.PostC, Type = ChannelType.Pressure, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 5, Name = "C-Pe", Description = "Suction Pressure", Unit = "bara", Group = ChannelGroup.PostC, Type = ChannelType.Pressure, PostFix = "C", IsEnabled = true, IsSelected = false });
        
        // Общие каналы (8 каналов)
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 6, Name = "VEL", Description = "Velocity", Unit = "m/s", Group = ChannelGroup.Common, Type = ChannelType.Flow, PostFix = "", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 7, Name = "UR", Description = "Relative Humidity", Unit = "%", Group = ChannelGroup.Common, Type = ChannelType.Humidity, PostFix = "", IsEnabled = true, IsSelected = false });
        for (int i = 0; i < 5; i++)
        {
            AllChannels.Add(new ChannelDefinitionViewModel { Index = 8 + i, Name = $"mA{i + 1}", Description = $"Current loop {i + 1}", Unit = "mA", Group = ChannelGroup.Common, Type = ChannelType.CurrentLoop, PostFix = "", IsEnabled = true, IsSelected = false });
        }
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 13, Name = "Flux", Description = "Flow rate", Unit = "l/m", Group = ChannelGroup.Common, Type = ChannelType.Flow, PostFix = "", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 14, Name = "UR-sie", Description = "Humidity Siemens", Unit = "%", Group = ChannelGroup.Common, Type = ChannelType.Humidity, PostFix = "", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 15, Name = "T-sie", Description = "Temperature Siemens", Unit = "°C", Group = ChannelGroup.Common, Type = ChannelType.Temperature, PostFix = "", IsEnabled = true, IsSelected = false });
        
        // Пост A - Температуры (32 канала: Tc, Te, T1..T30)
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 16, Name = "A-Tc", Description = "Condensing temperature", Unit = "°C", Group = ChannelGroup.PostA, Type = ChannelType.Temperature, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = 17, Name = "A-Te", Description = "Evaporation temperature", Unit = "°C", Group = ChannelGroup.PostA, Type = ChannelType.Temperature, PostFix = "A", IsEnabled = true, IsSelected = false });
        for (int i = 1; i <= 30; i++)
        {
            AllChannels.Add(new ChannelDefinitionViewModel { Index = 17 + i, Name = $"A-T{i}", Description = $"Temperature {i}", Unit = "°C", Group = ChannelGroup.PostA, Type = ChannelType.Temperature, PostFix = "A", IsEnabled = true, IsSelected = false });
        }
        
        // Пост A - Электрические (6 каналов: I, F, V, W, PF, MaxI)
        int aElecBase = 48;
        AllChannels.Add(new ChannelDefinitionViewModel { Index = aElecBase++, Name = "A-I", Description = "Current", Unit = "A", Group = ChannelGroup.PostA, Type = ChannelType.Electrical, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = aElecBase++, Name = "A-F", Description = "Frequency", Unit = "Hz", Group = ChannelGroup.PostA, Type = ChannelType.Electrical, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = aElecBase++, Name = "A-V", Description = "Voltage", Unit = "V", Group = ChannelGroup.PostA, Type = ChannelType.Electrical, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = aElecBase++, Name = "A-W", Description = "Power", Unit = "W", Group = ChannelGroup.PostA, Type = ChannelType.Electrical, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = aElecBase++, Name = "A-PF", Description = "Power Factor", Unit = "", Group = ChannelGroup.PostA, Type = ChannelType.Electrical, PostFix = "A", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = aElecBase++, Name = "A-MaxI", Description = "Max Current", Unit = "A", Group = ChannelGroup.PostA, Type = ChannelType.Electrical, PostFix = "A", IsEnabled = true, IsSelected = false });
        
        // Пост B - Температуры (32 канала: Tc, Te, T1..T30)
        int bTempBase = 54;
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bTempBase++, Name = "B-Tc", Description = "Condensing temperature", Unit = "°C", Group = ChannelGroup.PostB, Type = ChannelType.Temperature, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bTempBase++, Name = "B-Te", Description = "Evaporation temperature", Unit = "°C", Group = ChannelGroup.PostB, Type = ChannelType.Temperature, PostFix = "B", IsEnabled = true, IsSelected = false });
        for (int i = 1; i <= 30; i++)
        {
            AllChannels.Add(new ChannelDefinitionViewModel { Index = bTempBase++, Name = $"B-T{i}", Description = $"Temperature {i}", Unit = "°C", Group = ChannelGroup.PostB, Type = ChannelType.Temperature, PostFix = "B", IsEnabled = true, IsSelected = false });
        }
        
        // Пост B - Электрические (6 каналов)
        int bElecBase = 86;
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bElecBase++, Name = "B-I", Description = "Current", Unit = "A", Group = ChannelGroup.PostB, Type = ChannelType.Electrical, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bElecBase++, Name = "B-F", Description = "Frequency", Unit = "Hz", Group = ChannelGroup.PostB, Type = ChannelType.Electrical, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bElecBase++, Name = "B-V", Description = "Voltage", Unit = "V", Group = ChannelGroup.PostB, Type = ChannelType.Electrical, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bElecBase++, Name = "B-W", Description = "Power", Unit = "W", Group = ChannelGroup.PostB, Type = ChannelType.Electrical, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bElecBase++, Name = "B-PF", Description = "Power Factor", Unit = "", Group = ChannelGroup.PostB, Type = ChannelType.Electrical, PostFix = "B", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = bElecBase++, Name = "B-MaxI", Description = "Max Current", Unit = "A", Group = ChannelGroup.PostB, Type = ChannelType.Electrical, PostFix = "B", IsEnabled = true, IsSelected = false });
        
        // Пост C - Температуры (32 канала: Tc, Te, T1..T30)
        int cTempBase = 92;
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cTempBase++, Name = "C-Tc", Description = "Condensing temperature", Unit = "°C", Group = ChannelGroup.PostC, Type = ChannelType.Temperature, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cTempBase++, Name = "C-Te", Description = "Evaporation temperature", Unit = "°C", Group = ChannelGroup.PostC, Type = ChannelType.Temperature, PostFix = "C", IsEnabled = true, IsSelected = false });
        for (int i = 1; i <= 30; i++)
        {
            AllChannels.Add(new ChannelDefinitionViewModel { Index = cTempBase++, Name = $"C-T{i}", Description = $"Temperature {i}", Unit = "°C", Group = ChannelGroup.PostC, Type = ChannelType.Temperature, PostFix = "C", IsEnabled = true, IsSelected = false });
        }
        
        // Пост C - Электрические (6 каналов)
        int cElecBase = 124;
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cElecBase++, Name = "C-I", Description = "Current", Unit = "A", Group = ChannelGroup.PostC, Type = ChannelType.Electrical, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cElecBase++, Name = "C-F", Description = "Frequency", Unit = "Hz", Group = ChannelGroup.PostC, Type = ChannelType.Electrical, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cElecBase++, Name = "C-V", Description = "Voltage", Unit = "V", Group = ChannelGroup.PostC, Type = ChannelType.Electrical, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cElecBase++, Name = "C-W", Description = "Power", Unit = "W", Group = ChannelGroup.PostC, Type = ChannelType.Electrical, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cElecBase++, Name = "C-PF", Description = "Power Factor", Unit = "", Group = ChannelGroup.PostC, Type = ChannelType.Electrical, PostFix = "C", IsEnabled = true, IsSelected = false });
        AllChannels.Add(new ChannelDefinitionViewModel { Index = cElecBase++, Name = "C-MaxI", Description = "Max Current", Unit = "A", Group = ChannelGroup.PostC, Type = ChannelType.Electrical, PostFix = "C", IsEnabled = true, IsSelected = false });
        
        // Системные каналы (4 канала)
        for (int i = 0; i < 4; i++)
        {
            AllChannels.Add(new ChannelDefinitionViewModel { Index = 130 + i, Name = $"SYS-{i + 1}", Description = $"System channel {i + 1}", Unit = "", Group = ChannelGroup.System, Type = ChannelType.System, PostFix = "", IsEnabled = true, IsSelected = false });
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
