using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.SaveCompleted += () =>
        {
            DialogResult = true;
            Close();
        };
        viewModel.CancelRequested += () =>
        {
            DialogResult = false;
            Close();
        };
    }
}
