using System;
using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.SaveCompleted += OnSaveCompleted;
        _viewModel.CancelRequested += OnCancelRequested;

        Closed += OnClosed;
    }

    private void OnSaveCompleted()
    {
        CloseWithDialogResult(true);
    }

    private void OnCancelRequested()
    {
        CloseWithDialogResult(false);
    }

    private void CloseWithDialogResult(bool result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => CloseWithDialogResult(result));
            return;
        }

        if (!IsLoaded)
            return;

        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        _viewModel.SaveCompleted -= OnSaveCompleted;
        _viewModel.CancelRequested -= OnCancelRequested;
        Closed -= OnClosed;
    }
}
