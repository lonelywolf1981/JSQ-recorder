using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF.Views;

/// <summary>
/// История событий канала для конкретного эксперимента
/// </summary>
public partial class EventHistoryWindow : Window
{
    private readonly string _experimentId;
    private readonly IExperimentService _service;
    private readonly ObservableCollection<ChannelEventRecord> _events = new();

    public EventHistoryWindow(string experimentId, string experimentName, IExperimentService service)
    {
        InitializeComponent();
        _experimentId = experimentId;
        _service = service;

        TitleBlock.Text = $"История событий — {experimentName}";
        EventsGrid.ItemsSource = _events;

        _ = LoadEventsAsync();
    }

    private async Task LoadEventsAsync()
    {
        StatusBlock.Text = "Загрузка...";
        try
        {
            var records = await _service.GetExperimentEventsAsync(_experimentId);
            Dispatcher.Invoke(() =>
            {
                _events.Clear();
                foreach (var r in records)
                    _events.Add(r);
                StatusBlock.Text = $"Записей: {_events.Count}";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusBlock.Text = $"Ошибка: {ex.Message}");
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadEventsAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
