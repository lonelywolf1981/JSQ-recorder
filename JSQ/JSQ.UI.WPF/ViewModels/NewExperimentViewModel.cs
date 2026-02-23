using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSQ.Core.Models;

namespace JSQ.UI.WPF.ViewModels;

public partial class NewExperimentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = $"Эксперимент {DateTime.Now:yyyy-MM-dd HH:mm}";

    [ObservableProperty]
    private string _partNumber = string.Empty;

    [ObservableProperty]
    private string _operator = string.Empty;

    [ObservableProperty]
    private string _refrigerant = "R404a";

    public bool Confirmed { get; private set; }

    public event Action? DialogClosed;

    public Experiment BuildExperiment() => new Experiment
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        PartNumber = PartNumber,
        Operator = Operator,
        Refrigerant = Refrigerant,
        State = ExperimentState.Idle,
        StartTime = DateTime.Now
    };

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        Confirmed = true;
        DialogClosed?.Invoke();
    }

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(Name);

    partial void OnNameChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        DialogClosed?.Invoke();
    }
}
