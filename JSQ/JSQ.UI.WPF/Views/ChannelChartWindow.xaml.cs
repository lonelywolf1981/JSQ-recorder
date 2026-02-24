using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF.Views;

public partial class ChannelChartWindow : Window
{
    public ChannelChartWindow(ChannelChartViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
