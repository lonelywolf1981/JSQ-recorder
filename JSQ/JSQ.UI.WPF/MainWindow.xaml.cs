using System.ComponentModel;
using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
