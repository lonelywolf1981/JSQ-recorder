using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF.Views;

public partial class NewExperimentWindow : Window
{
    public NewExperimentWindow(NewExperimentViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.DialogClosed += () => Close();
    }
}
