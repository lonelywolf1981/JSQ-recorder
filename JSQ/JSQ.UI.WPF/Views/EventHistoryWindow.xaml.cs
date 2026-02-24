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

public partial class EventHistoryWindow : Window
{
    private readonly string _postId;
    private readonly string? _initialExperimentId;
    private readonly IExperimentService _service;

    private readonly ObservableCollection<PostExperimentRecord> _experiments = new();
    private readonly ObservableCollection<ChannelEventRecord> _events = new();
    private readonly ObservableCollection<ChannelSampleRecord> _samples = new();

    private readonly PlotModel _plotModel = new();
    private readonly LineSeries _series = new() { StrokeThickness = 1.5, MarkerType = MarkerType.None };

    private bool _selectionChangeSuppressed;
    private string? _selectedExperimentId;
    private List<ExperimentChannelInfo> _channels = new();
    private DateTime? _experimentStart;
    private DateTime? _experimentEnd;

    public EventHistoryWindow(string postId, IExperimentService service, string? initialExperimentId = null)
    {
        InitializeComponent();

        _postId = postId;
        _service = service;
        _initialExperimentId = initialExperimentId;

        TitleBlock.Text = $"История экспериментов поста {_postId}";

        ExperimentsGrid.ItemsSource = _experiments;
        EventsGrid.ItemsSource = _events;
        SamplesGrid.ItemsSource = _samples;

        BuildPlot();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadExperimentsAsync(selectPreferred: true);
    }

    private async Task LoadExperimentsAsync(bool selectPreferred)
    {
        Dispatcher.Invoke(() => StatusBlock.Text = $"Загрузка экспериментов поста {_postId}...");

        var startFrom = ExperimentsFromDatePicker.SelectedDate;
        var startTo = ExperimentsToDatePicker.SelectedDate?.Date.AddDays(1).AddTicks(-1);
        var search = string.IsNullOrWhiteSpace(SearchTextBox.Text) ? null : SearchTextBox.Text.Trim();

        try
        {
            var rows = await _service.GetPostExperimentsAsync(_postId, startFrom, startTo, search);

            Dispatcher.Invoke(() =>
            {
                _experiments.Clear();
                foreach (var row in rows)
                    _experiments.Add(row);

                StatusBlock.Text = $"Пост {_postId}: найдено экспериментов {_experiments.Count}";

                var preferredId = selectPreferred
                    ? (_selectedExperimentId ?? _initialExperimentId)
                    : _selectedExperimentId;

                var preferred = !string.IsNullOrWhiteSpace(preferredId)
                    ? _experiments.FirstOrDefault(e => string.Equals(e.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                    : null;

                _selectionChangeSuppressed = true;
                ExperimentsGrid.SelectedItem = preferred ?? _experiments.FirstOrDefault();
                _selectionChangeSuppressed = false;

                if (ExperimentsGrid.SelectedItem is not PostExperimentRecord selected)
                {
                    ClearDetails();
                }
                else
                {
                    _ = LoadSelectedExperimentAsync(selected);
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusBlock.Text = $"Ошибка загрузки списка экспериментов: {ex.Message}");
        }
    }

    private async Task LoadSelectedExperimentAsync(PostExperimentRecord selected)
    {
        _selectedExperimentId = selected.Id;
        SelectedExperimentBlock.Text =
            $"Эксперимент: {selected.Name} | Старт: {selected.StartDisplay} | Статус: {selected.StateDisplay}";

        await LoadEventsAsync(selected.Id);
        await LoadChannelsAsync(selected.Id);
        await LoadRangeAsync(selected.Id);
        await LoadChannelDataAsync();
    }

    private void ClearDetails()
    {
        _selectedExperimentId = null;
        SelectedExperimentBlock.Text = "Эксперимент не выбран";
        _events.Clear();
        _samples.Clear();
        _channels.Clear();
        ChannelCombo.ItemsSource = null;
        RangeInfoBlock.Text = "";
        _series.Points.Clear();
        _plotModel.InvalidatePlot(true);
    }

    private async Task LoadEventsAsync(string experimentId)
    {
        try
        {
            var records = await _service.GetExperimentEventsAsync(experimentId);
            Dispatcher.Invoke(() =>
            {
                _events.Clear();
                foreach (var r in records)
                    _events.Add(r);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusBlock.Text = $"Ошибка загрузки событий: {ex.Message}");
        }
    }

    private async Task LoadChannelsAsync(string experimentId)
    {
        try
        {
            _channels = await _service.GetExperimentChannelsAsync(experimentId);
            Dispatcher.Invoke(() =>
            {
                var current = ChannelCombo.SelectedItem as ExperimentChannelInfo;
                ChannelCombo.ItemsSource = _channels;
                if (current != null)
                {
                    ChannelCombo.SelectedItem = _channels.FirstOrDefault(c => c.ChannelIndex == current.ChannelIndex);
                }
                if (ChannelCombo.SelectedItem == null && _channels.Count > 0)
                {
                    ChannelCombo.SelectedIndex = 0;
                }
            });
        }
        catch
        {
            Dispatcher.Invoke(() => ChannelCombo.ItemsSource = null);
        }
    }

    private async Task LoadRangeAsync(string experimentId)
    {
        try
        {
            var range = await _service.GetExperimentDataRangeAsync(experimentId);
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
                RangeInfoBlock.Text = $"Диапазон выбранного эксперимента: {startText} — {endText}";
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
        if (string.IsNullOrWhiteSpace(_selectedExperimentId))
        {
            Dispatcher.Invoke(() => StatusBlock.Text = "Выберите эксперимент");
            return;
        }

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
            var safeExperimentId = _selectedExperimentId!;
            var rows = await _service.LoadExperimentChannelHistoryAsync(safeExperimentId, channel.ChannelIndex, start, end);
            Dispatcher.Invoke(() =>
            {
                _samples.Clear();
                foreach (var (time, value) in rows)
                    _samples.Add(new ChannelSampleRecord { Timestamp = time, Value = value });

                _plotModel.Title = $"{channel.ChannelName} ({channel.Unit})";
                _series.Points.Clear();
                foreach (var (time, value) in rows)
                    _series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(time), value));

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

    private async void ExperimentsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_selectionChangeSuppressed)
            return;

        if (ExperimentsGrid.SelectedItem is PostExperimentRecord selected)
            await LoadSelectedExperimentAsync(selected);
        else
            ClearDetails();
    }

    private async void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadExperimentsAsync(selectPreferred: false);
    }

    private async void ResetFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        ExperimentsFromDatePicker.SelectedDate = null;
        ExperimentsToDatePicker.SelectedDate = null;
        await LoadExperimentsAsync(selectPreferred: false);
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
        await LoadExperimentsAsync(selectPreferred: true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

public class ChannelSampleRecord
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}
