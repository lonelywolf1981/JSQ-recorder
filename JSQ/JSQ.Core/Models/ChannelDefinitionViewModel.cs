using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JSQ.Core.Models;

/// <summary>
/// Модель канала для UI (с расширенными свойствами для выбора)
/// </summary>
public partial class ChannelDefinitionViewModel : ObservableObject
{
    [ObservableProperty]
    private int _index;
    
    [ObservableProperty]
    private string _name = string.Empty;
    
    [ObservableProperty]
    private string _description = string.Empty;
    
    [ObservableProperty]
    private string _unit = string.Empty;
    
    [ObservableProperty]
    private ChannelGroup _group;
    
    [ObservableProperty]
    private ChannelType _type;
    
    [ObservableProperty]
    private bool _isEnabled;
    
    [ObservableProperty]
    private bool _isSelected;
    
    [ObservableProperty]
    private double? _minLimit;
    
    [ObservableProperty]
    private double? _maxLimit;
    
    [ObservableProperty]
    private string _postFix = string.Empty; // A, B, C

    /// <summary>Текущее значение от передатчика (null = нет данных)</summary>
    [ObservableProperty]
    private double? _currentValue;

    /// <summary>true если получены данные от передатчика</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>true если канал занят другим постом и недоступен для выбора.</summary>
    [ObservableProperty]
    private bool _isTakenByOtherPost;

    /// <summary>Идентификатор поста, которому занят канал: "A", "B", "C".</summary>
    [ObservableProperty]
    private string _takenByPost = string.Empty;

    /// <summary>Можно ли выбрать канал (false если занят другим постом).</summary>
    [ObservableProperty]
    private bool _canSelect = true;

    public ChannelDefinitionViewModel() { }
    
    public ChannelDefinitionViewModel(ChannelDefinition channel)
    {
        Index = channel.Index;
        Name = channel.Name;
        Description = channel.Description;
        Unit = channel.Unit;
        Group = channel.Group;
        Type = channel.Type;
        IsEnabled = channel.Enabled;
        IsSelected = channel.Enabled;
        MinLimit = channel.MinLimit;
        MaxLimit = channel.MaxLimit;
        
        // Извлекаем префикс поста из имени
        PostFix = Name.Split('-').Length > 1 ? Name.Split('-')[0] : string.Empty;
    }
    
    /// <summary>
    /// Обновление из модели
    /// </summary>
    public void UpdateFromModel(ChannelDefinition channel)
    {
        Index = channel.Index;
        Name = channel.Name;
        Description = channel.Description;
        Unit = channel.Unit;
        Group = channel.Group;
        Type = channel.Type;
        IsEnabled = channel.Enabled;
        MinLimit = channel.MinLimit;
        MaxLimit = channel.MaxLimit;
    }
    
    /// <summary>
    /// Преобразование в модель
    /// </summary>
    public ChannelDefinition ToModel()
    {
        return new ChannelDefinition
        {
            Index = Index,
            Name = Name,
            Description = Description,
            Unit = Unit,
            Group = Group,
            Type = Type,
            Enabled = IsEnabled,
            MinLimit = MinLimit,
            MaxLimit = MaxLimit
        };
    }
}
