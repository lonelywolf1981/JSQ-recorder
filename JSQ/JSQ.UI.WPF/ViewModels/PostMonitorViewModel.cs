using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using JSQ.Core.Models;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Состояние одного поста (A/B/C) — для отображения в UI
/// </summary>
public partial class PostMonitorViewModel : ObservableObject
{
    public string PostId { get; }

    public PostMonitorViewModel(string postId)
    {
        PostId = postId;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private ExperimentState _state = ExperimentState.Idle;

    [ObservableProperty]
    private Experiment? _currentExperiment;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnomalyBadge))]
    [NotifyPropertyChangedFor(nameof(HasAnomalies))]
    private int _anomalyCount;

    public string StateBadge => State switch
    {
        ExperimentState.Running => "● ЗАПИСЬ",
        ExperimentState.Paused  => "⏸ ПАУЗА",
        _                       => "○ Свободен"
    };

    private static readonly SolidColorBrush _brushRunning = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush _brushPaused  = new(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly SolidColorBrush _brushIdle    = new(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public SolidColorBrush StateBrush => State switch
    {
        ExperimentState.Running => _brushRunning,
        ExperimentState.Paused  => _brushPaused,
        _                       => _brushIdle
    };

    public string AnomalyBadge => AnomalyCount > 0 ? $"⚠ {AnomalyCount}" : string.Empty;
    public bool HasAnomalies => AnomalyCount > 0;

    public bool IsRunning => State == ExperimentState.Running;
    public bool IsPaused  => State == ExperimentState.Paused;
    public bool IsIdle    => !IsRunning && !IsPaused;

    public bool CanStart => IsIdle;
    public bool CanPause => IsRunning;
    public bool CanStop  => IsRunning || IsPaused;
}
