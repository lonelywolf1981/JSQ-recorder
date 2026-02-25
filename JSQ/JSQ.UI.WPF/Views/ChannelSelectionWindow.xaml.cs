using System;
using System.Collections.Generic;
using System.Windows;

namespace JSQ.UI.WPF.Views;

/// <summary>
/// Interaction logic for ChannelSelectionWindow.xaml
/// </summary>
public partial class ChannelSelectionWindow : Window
{
    public event Action<List<int>>? SelectionSaved;
    
    public ChannelSelectionWindow(ViewModels.ChannelSelectionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        
        // Подписка на событие сохранения
        if (viewModel is ViewModels.ChannelSelectionViewModel vm)
        {
            vm.SaveSelectionCompleted += OnSaveSelectionCompleted;
        }
    }
    
    private void OnSaveSelectionCompleted(List<int> selectedIndices)
    {
        SelectionSaved?.Invoke(selectedIndices);
        DialogResult = true;
        Close();
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
