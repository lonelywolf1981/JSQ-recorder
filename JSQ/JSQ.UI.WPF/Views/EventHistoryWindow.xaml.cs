using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
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
    private readonly ObservableCollection<ChannelSampleRecord> _samples = new();
    private readonly PlotModel _plotModel = new();
    private readonly LineSeries _series = new() { StrokeThickness = 1.5, MarkerType = MarkerType.None };
    private List<ExperimentChannelInfo> _channels = new();
    private DateTime? _experimentStart;
    private DateTime? _experimentEnd;

    public EventHistoryWindow(string experimentId, string experimentName, IExperimentService service)
    {
        InitializeComponent();
        _experimentId = experimentId;
        _service = service;

        TitleBlock.Text = $"История событий — {experimentName}";
        EventsGrid.ItemsSource = _events;
        SamplesGrid.ItemsSource = _samples;

        BuildPlot();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadEventsAsync();
        await LoadChannelsAsync();
        await LoadRangeAsync();
        await LoadChannelDataAsync();
    }

    private async Task LoadEventsAsync()
    {
        Dispatcher.Invoke(() => StatusBlock.Text = "Загрузка событий...");
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

    private async Task LoadChannelsAsync()
    {
        try
        {
            _channels = await _service.GetExperimentChannelsAsync(_experimentId);
            Dispatcher.Invoke(() =>
            {
                ChannelCombo.ItemsSource = _channels;
                if (_channels.Count > 0)
                    ChannelCombo.SelectedIndex = 0;
            });
        }
        catch
        {
            Dispatcher.Invoke(() => ChannelCombo.ItemsSource = null);
        }
    }

    private async Task LoadRangeAsync()
    {
        try
        {
            var range = await _service.GetExperimentDataRangeAsync(_experimentId);
            _experimentStart = range.start;
            _experimentEnd = range.end;

            Dispatcher.Invoke(() =>
            {
                if (_experimentStart.HasValue)
                {
                    FromDatePicker.SelectedDate = _experimentStart.Value.Date;
                    FromTimeBox.Text = _experimentStart.Value.ToString("HH:mm:ss");
                }
                else
                {
                    var fallback = DateTime.Now.AddHours(-1);
                    FromDatePicker.SelectedDate = fallback.Date;
                    FromTimeBox.Text = fallback.ToString("HH:mm:ss");
                }

                if (_experimentEnd.HasValue)
                {
                    ToDatePicker.SelectedDate = _experimentEnd.Value.Date;
                    ToTimeBox.Text = _experimentEnd.Value.ToString("HH:mm:ss");
                }
                else
                {
                    var fallback = DateTime.Now;
                    ToDatePicker.SelectedDate = fallback.Date;
                    ToTimeBox.Text = fallback.ToString("HH:mm:ss");
                }

                var startText = _experimentStart?.ToString("dd.MM.yyyy HH:mm:ss") ?? "—";
                var endText = _experimentEnd?.ToString("dd.MM.yyyy HH:mm:ss") ?? "—";
                RangeInfoBlock.Text = $"Диапазон эксперимента: {startText} — {endText}";
            });
        }
        catch
        {
            Dispatcher.Invoke(() => RangeInfoBlock.Text = "Диапазон времени недоступен");
        }
    }

    private void BuildPlot()
    {
        _plotModel.Title = "Данные канала";
        _plotModel.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm:ss",
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot
        });
        _plotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot
        });
        _plotModel.Series.Add(_series);
        ChannelPlot.Model = _plotModel;
    }

    private async Task LoadChannelDataAsync()
    {
        var channel = Dispatcher.Invoke(() => ChannelCombo.SelectedItem as ExperimentChannelInfo);
        if (channel == null)
        {
            Dispatcher.Invoke(() => StatusBlock.Text = "Выберите канал для загрузки истории");
            return;
        }

        var (start, end, ok, error) = GetSelectedRange();
        if (!ok)
        {
            Dispatcher.Invoke(() => StatusBlock.Text = error);
            return;
        }

        Dispatcher.Invoke(() => StatusBlock.Text = $"Загрузка истории канала {channel.ChannelName}...");
        try
        {
            var rows = await _service.LoadExperimentChannelHistoryAsync(_experimentId, channel.ChannelIndex, start, end);
            Dispatcher.Invoke(() =>
            {
                _samples.Clear();
                foreach (var (time, value) in rows)
                {
                    _samples.Add(new ChannelSampleRecord { Timestamp = time, Value = value });
                }

                _plotModel.Title = $"{channel.ChannelName} ({channel.Unit})";
                _series.Points.Clear();
                foreach (var (time, value) in rows)
                {
                    _series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(time), value));
                }
                _plotModel.InvalidatePlot(true);

                StatusBlock.Text = rows.Count == 0
                    ? $"Канал {channel.ChannelName}: данных нет"
                    : $"Канал {channel.ChannelName}: записей {rows.Count}";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusBlock.Text = $"Ошибка загрузки истории: {ex.Message}");
        }
    }

    private (DateTime start, DateTime end, bool ok, string error) GetSelectedRange()
    {
        var startDate = FromDatePicker.SelectedDate ?? _experimentStart ?? DateTime.Now.AddHours(-1);
        var endDate = ToDatePicker.SelectedDate ?? _experimentEnd ?? DateTime.Now;

        if (!TryParseTime(FromTimeBox.Text, out var startTime))
            return (DateTime.MinValue, DateTime.MinValue, false, "Некорректное время в поле 'С' (ожидается HH:mm:ss)");

        if (!TryParseTime(ToTimeBox.Text, out var endTime))
            return (DateTime.MinValue, DateTime.MinValue, false, "Некорректное время в поле 'По' (ожидается HH:mm:ss)");

        var start = startDate.Date + startTime;
        var end = endDate.Date + endTime;

        if (end < start)
            return (DateTime.MinValue, DateTime.MinValue, false, "Конец диапазона раньше начала");

        return (start, end, true, string.Empty);
    }

    private static bool TryParseTime(string? text, out TimeSpan result)
    {
        return TimeSpan.TryParseExact(text ?? string.Empty, "c", CultureInfo.InvariantCulture, out result) ||
               TimeSpan.TryParseExact(text ?? string.Empty, "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out result);
    }

    private async void LoadChannelDataButton_Click(object sender, RoutedEventArgs e) => await LoadChannelDataAsync();

    private async void WholeRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_experimentStart.HasValue)
        {
            FromDatePicker.SelectedDate = _experimentStart.Value.Date;
            FromTimeBox.Text = _experimentStart.Value.ToString("HH:mm:ss");
        }

        if (_experimentEnd.HasValue)
        {
            ToDatePicker.SelectedDate = _experimentEnd.Value.Date;
            ToTimeBox.Text = _experimentEnd.Value.ToString("HH:mm:ss");
        }

        await LoadChannelDataAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync();
        await LoadChannelsAsync();
        await LoadRangeAsync();
        await LoadChannelDataAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

public class ChannelSampleRecord
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}
