using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JSQ.Core.Models;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// Состояние одного поста (A/B/C) — для отображения в UI
/// </summary>
public partial class PostMonitorViewModel : ObservableObject
{
    public string PostId { get; }

    private readonly DispatcherTimer _durationTimer;

    public PostMonitorViewModel(string postId)
    {
        PostId = postId;
        _durationTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => OnPropertyChanged(nameof(DurationDisplay));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private ExperimentState _state = ExperimentState.Idle;

    partial void OnStateChanged(ExperimentState value)
    {
        if (value == ExperimentState.Running)
            _durationTimer.Start();
        else
        {
            _durationTimer.Stop();
            OnPropertyChanged(nameof(DurationDisplay));
        }
    }

    [ObservableProperty]
    private Experiment? _currentExperiment;

    /// <summary>ID последнего запущенного эксперимента — сохраняется и после остановки.</summary>
    [ObservableProperty]
    private string _lastExperimentId = string.Empty;

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

    public string DurationDisplay => IsRunning && CurrentExperiment != null
        ? CurrentExperiment.Duration.ToString(@"hh\:mm\:ss")
        : "—";

    public string AnomalyBadge => AnomalyCount > 0 ? $"⚠ {AnomalyCount}" : string.Empty;
    public bool HasAnomalies => AnomalyCount > 0;

    public bool IsRunning => State == ExperimentState.Running;
    public bool IsIdle    => !IsRunning;

    public bool CanStart => State == ExperimentState.Idle;
    public bool CanStop  => State == ExperimentState.Running || State == ExperimentState.Paused;
}
