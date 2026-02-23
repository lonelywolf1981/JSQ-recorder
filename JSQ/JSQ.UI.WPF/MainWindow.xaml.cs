using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF;

/// <summary>
/// Main Window of the application
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
