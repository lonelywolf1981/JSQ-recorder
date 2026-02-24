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
    private readonly string _presetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "channel_presets.json");

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
    /// assignments: channelIndex → HashSet постов ("A"/"B"/"C"), владеющих каналом.
    /// Common-каналы никогда не блокируются.
    /// </summary>
    public void InitForPost(string postId, IReadOnlyDictionary<int, HashSet<string>> assignments)
    {
        CurrentPostId = postId;
        OnPropertyChanged(nameof(WindowTitle));

        foreach (var ch in AllChannels)
        {
            bool isShared = ch.Group == ChannelGroup.Common || ch.Group == ChannelGroup.System;

            if (isShared)
            {
                // Common/System каналы — всегда доступны, выбраны если этот пост уже имеет их
                ch.IsSelected = assignments.TryGetValue(ch.Index, out var owners) && owners.Contains(postId);
                ch.IsTakenByOtherPost = false;
                ch.TakenByPost = string.Empty;
                ch.CanSelect = true;
            }
            else if (assignments.TryGetValue(ch.Index, out var assignedTo))
            {
                if (assignedTo.Contains(postId))
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
                    ch.TakenByPost = string.Join(",", assignedTo);
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
            CreatedAt = JsqClock.Now
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

        // Применяем лимиты к реестру каналов и сохраняем для следующего сеанса
        ApplyLimitsToRegistry();
        SaveLimitsToFile();

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

    /// <summary>Загружает каналы из ChannelRegistry с наложением сохранённых лимитов.</summary>
    private void LoadChannelsFromRegistry()
    {
        var savedLimits = LoadLimitsFromFile();

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

            double? minLimit = def.MinLimit;
            double? maxLimit = def.MaxLimit;
            if (savedLimits.TryGetValue(def.Index, out var lim))
            {
                minLimit = lim.Min;
                maxLimit = lim.Max;
            }

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
                CanSelect = true,
                MinLimit = minLimit,
                MaxLimit = maxLimit
            });
        }

        // Строим lookup для живых данных
        foreach (var ch in AllChannels)
            _channelByIndex[ch.Index] = ch;

        ApplyFilters();
    }

    // ─── Лимиты: сохранение/загрузка ────────────────────────────────────────

    private static readonly string LimitsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "channel_limits.json");

    private class ChannelLimitEntry
    {
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    private Dictionary<int, ChannelLimitEntry> LoadLimitsFromFile()
    {
        if (!File.Exists(LimitsPath)) return new Dictionary<int, ChannelLimitEntry>();
        try
        {
            var json = File.ReadAllText(LimitsPath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, ChannelLimitEntry>>(json);
            if (raw == null) return new Dictionary<int, ChannelLimitEntry>();
            var result = new Dictionary<int, ChannelLimitEntry>();
            foreach (var kvp in raw)
                if (int.TryParse(kvp.Key, out var idx))
                    result[idx] = kvp.Value;
            return result;
        }
        catch { return new Dictionary<int, ChannelLimitEntry>(); }
    }

    private void SaveLimitsToFile()
    {
        try
        {
            var dict = new Dictionary<string, ChannelLimitEntry>();
            foreach (var ch in AllChannels)
                if (ch.MinLimit.HasValue || ch.MaxLimit.HasValue)
                    dict[ch.Index.ToString()] = new ChannelLimitEntry { Min = ch.MinLimit, Max = ch.MaxLimit };
            File.WriteAllText(LimitsPath,
                JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void ApplyLimitsToRegistry()
    {
        foreach (var ch in AllChannels)
        {
            if (ChannelRegistry.All.TryGetValue(ch.Index, out var def))
            {
                def.MinLimit = ch.MinLimit;
                def.MaxLimit = ch.MaxLimit;
            }
        }
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
