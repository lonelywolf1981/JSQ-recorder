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
/// ViewModel для назначения каналов на пост
/// </summary>
public partial class ChannelSelectionViewModel : ObservableObject
{
    private readonly string _presetsPath = "channel_presets.json";

    // Текущий пост, для которого открыт диалог
    public string CurrentPostId { get; private set; } = string.Empty;

    // Заголовок окна
    public string WindowTitle => string.IsNullOrEmpty(CurrentPostId)
        ? "Назначение каналов"
        : $"Назначение каналов — Пост {CurrentPostId}";

    // Быстрый lookup для живых данных: index → VM
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
    private ObservableCollection<string> _availableGroups = new();

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

        AllChannels.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
                foreach (ChannelDefinitionViewModel ch in e.NewItems)
                    ch.PropertyChanged += OnChannelIsSelectedChanged;
            if (e.OldItems != null)
                foreach (ChannelDefinitionViewModel ch in e.OldItems)
                    ch.PropertyChanged -= OnChannelIsSelectedChanged;
        };

        LoadChannelsFromRegistry();
        LoadPresets();
    }

    /// <summary>
    /// Инициализировать диалог для конкретного поста.
    /// assignments: channelIndex → "A"/"B"/"C"
    /// </summary>
    public void InitForPost(string postId, IReadOnlyDictionary<int, string> assignments)
    {
        CurrentPostId = postId;
        OnPropertyChanged(nameof(WindowTitle));

        foreach (var ch in AllChannels)
        {
            if (assignments.TryGetValue(ch.Index, out var assignedTo))
            {
                if (assignedTo == postId)
                {
                    ch.IsSelected = true;
                    ch.IsTakenByOtherPost = false;
                    ch.TakenByPost = string.Empty;
                    ch.CanSelect = true;
                }
                else
                {
                    ch.IsSelected = false;
                    ch.IsTakenByOtherPost = true;
                    ch.TakenByPost = assignedTo;
                    ch.CanSelect = false;
                }
            }
            else
            {
                ch.IsSelected = false;
                ch.IsTakenByOtherPost = false;
                ch.TakenByPost = string.Empty;
                ch.CanSelect = true;
            }
        }

        UpdateSelectedCount();
        ApplyFilters();
    }

    /// <summary>Подписаться на живые данные от передатчика.</summary>
    public void SubscribeToLiveData(IExperimentService service)
    {
        _liveDataService = service;
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

    private void OnChannelIsSelectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelDefinitionViewModel.IsSelected))
            UpdateSelectedCount();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedGroupChanged(string value) => ApplyFilters();

    private void UpdateSelectedCount()
    {
        SelectedCount = AllChannels.Count(c => c.IsSelected);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var ch in FilteredChannels.Where(c => c.CanSelect))
            ch.IsSelected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var ch in FilteredChannels.Where(c => c.CanSelect))
            ch.IsSelected = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var ch in FilteredChannels.Where(c => c.CanSelect))
            ch.IsSelected = !ch.IsSelected;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void ApplyBulkLimits()
    {
        foreach (var ch in FilteredChannels.Where(c => c.IsSelected && c.CanSelect))
        {
            if (BulkMinLimit.HasValue) ch.MinLimit = BulkMinLimit;
            if (BulkMaxLimit.HasValue) ch.MaxLimit = BulkMaxLimit;
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
        if (existing != null) presets.Remove(existing);
        presets.Add(preset);

        SaveAllPresets(presets);
        LoadPresets();
    }

    [RelayCommand]
    private void LoadPreset()
    {
        var fullPreset = LoadFullPreset(SelectedPresetName);
        if (fullPreset == null) return;

        DeselectAllCommand.Execute(null);
        foreach (var ch in AllChannels.Where(c => c.CanSelect && fullPreset.SelectedChannelIndices.Contains(c.Index)))
            ch.IsSelected = true;

        UpdateSelectedCount();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedPresetName)) return;

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
        var selectedIndices = AllChannels
            .Where(c => c.IsSelected && c.CanSelect)
            .Select(c => c.Index)
            .ToList();

        UpdateSelectedCount();
        SaveSelectionCompleted?.Invoke(selectedIndices);
    }

    public event Action<List<int>> SaveSelectionCompleted = delegate { };

    private void ApplyFilters()
    {
        FilteredChannels.Clear();

        var query = AllChannels.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(c =>
                c.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.Description.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0);

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

        foreach (var ch in query)
            FilteredChannels.Add(ch);
    }

    /// <summary>Загружает каналы из ChannelRegistry (единственный источник истины).</summary>
    private void LoadChannelsFromRegistry()
    {
        foreach (var kvp in ChannelRegistry.All)
        {
            var def = kvp.Value;
            var postFix = def.Group switch
            {
                ChannelGroup.PostA => "A",
                ChannelGroup.PostB => "B",
                ChannelGroup.PostC => "C",
                _ => string.Empty
            };

            AllChannels.Add(new ChannelDefinitionViewModel
            {
                Index = def.Index,
                Name = def.Name,
                Description = def.Description,
                Unit = def.Unit,
                Group = def.Group,
                Type = def.Type,
                PostFix = postFix,
                IsEnabled = true,
                IsSelected = false,
                CanSelect = true
            });
        }

        // Строим lookup для живых данных
        foreach (var ch in AllChannels)
            _channelByIndex[ch.Index] = ch;

        ApplyFilters();
    }

    private void LoadPresets()
    {
        AvailablePresets.Clear();
        var presets = LoadAllPresets();
        foreach (var preset in presets)
            AvailablePresets.Add(new PresetInfo { Name = preset.Name, CreatedAt = preset.CreatedAt });
    }

    private List<ChannelPreset> LoadAllPresets()
    {
        if (!File.Exists(_presetsPath)) return new List<ChannelPreset>();
        try
        {
            var json = File.ReadAllText(_presetsPath);
            return JsonSerializer.Deserialize<List<ChannelPreset>>(json) ?? new List<ChannelPreset>();
        }
        catch { return new List<ChannelPreset>(); }
    }

    private void SaveAllPresets(List<ChannelPreset> presets)
    {
        var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_presetsPath, json);
    }

    private ChannelPreset? LoadFullPreset(string name) =>
        LoadAllPresets().FirstOrDefault(p => p.Name == name);
}

public class PresetInfo
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ChannelPreset
{
    public string Name { get; set; } = string.Empty;
    public List<int> SelectedChannelIndices { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
