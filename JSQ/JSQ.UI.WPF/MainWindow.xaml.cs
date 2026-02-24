using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JSQ.Core.Models;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ChannelDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is ChannelStatus ch)
            ((MainViewModel)DataContext).OpenChannelChartCommand.Execute(ch);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (vm.IsAnyPostRunning)
        {
            MessageBox.Show(
                "Идёт запись. Остановите все посты перед закрытием программы.",
                "JSQ — Запись активна",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
