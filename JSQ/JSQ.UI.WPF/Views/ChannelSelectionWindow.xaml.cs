using System.Windows;

namespace JSQ.UI.WPF.Views;

/// <summary>
/// Interaction logic for ChannelSelectionWindow.xaml
/// </summary>
public partial class ChannelSelectionWindow : Window
{
    public ChannelSelectionWindow(ViewModels.ChannelSelectionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
