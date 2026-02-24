using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JSQ.Core.Models;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace JSQ.UI.WPF.ViewModels;

/// <summary>
/// ViewModel графика в реальном времени для одного канала
/// </summary>
public partial class ChannelChartViewModel : ObservableObject
{
    private readonly IExperimentService _service;
    private readonly List<(DateTime time, double value)> _rawPoints = new(100000);
    private readonly object _pointsLock = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly LineSeries _series;
    private DateTime _experimentStartTime;
    private bool _historyLoaded;

    public int ChannelIndex { get; }
    public string ChannelName { get; }
    public string Unit { get; }
    public string WindowTitle => $"График: {ChannelName}";

    public PlotModel PlotModel { get; }

    [ObservableProperty]
    private string _selectedWindow = "1мин";

    public List<string> AvailableWindows { get; } = new() { "30с", "1мин", "5мин", "Весь" };

    [ObservableProperty]
    private string _currentValueText = "—";

    public ChannelChartViewModel(ChannelStatus channel, IExperimentService service,
        DateTime? experimentStart = null)
    {
        ChannelIndex = channel.ChannelIndex;
        ChannelName = channel.ChannelName;
        Unit = channel.Unit ?? string.Empty;
        _service = service;
        // Если запись идёт — запоминаем старт; иначе показываем последние 5 минут
        _experimentStartTime = experimentStart ?? JsqClock.Now.AddMinutes(-5);

        double? minLimit = null;
        double? maxLimit = null;
        if (ChannelRegistry.All.TryGetValue(ChannelIndex, out var def))
        {
            minLimit = def.MinLimit;
            maxLimit = def.MaxLimit;
        }

        PlotModel = BuildPlotModel(minLimit, maxLimit);

        _series = new LineSeries
        {
            Color = OxyColors.SteelBlue,
            StrokeThickness = 1.5,
            MarkerType = MarkerType.None,
            RenderInLegend = false
        };
        PlotModel.Series.Add(_series);

        _service.ChannelValueReceived += OnChannelValue;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += async (_, _) => await RefreshChartAsync();
        _refreshTimer.Start();
        
        // Загружаем историю при открытии
        _ = LoadHistoryAsync();
    }

    private PlotModel BuildPlotModel(double? minLimit, double? maxLimit)
    {
        var model = new PlotModel { Title = ChannelName, TitleFontSize = 13 };

        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm:ss",
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(220, 220, 220)
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = Unit,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(220, 220, 220)
        });

        if (minLimit.HasValue)
        {
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = minLimit.Value,
                Color = OxyColors.Orange,
                LineStyle = LineStyle.Dash,
                Text = $"Min: {minLimit.Value}"
            });
        }

        if (maxLimit.HasValue)
        {
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = maxLimit.Value,
                Color = OxyColors.Red,
                LineStyle = LineStyle.Dash,
                Text = $"Max: {maxLimit.Value}"
            });
        }

        return model;
    }

    partial void OnSelectedWindowChanged(string value) => _ = RefreshChartAsync();
    
    /// <summary>
    /// Загрузить исторические данные из БД
    /// </summary>
    private async Task LoadHistoryAsync()
    {
        if (_historyLoaded) return;
        
        // Получаем время старта эксперимента
        var endTime = JsqClock.Now;
        var startTime = _experimentStartTime;
        
        // Если эксперимент еще не запущен, показываем только новые данные
        if (startTime > endTime)
            startTime = endTime.AddMinutes(-5);
        
        var history = await _service.LoadChannelHistoryAsync(ChannelIndex, startTime, endTime);
        
        lock (_pointsLock)
        {
            _rawPoints.Clear();
            _rawPoints.AddRange(history);
            _historyLoaded = true;
        }
        
        await RefreshChartAsync();
    }
    
    /// <summary>
    /// Установить время старта эксперимента (вызывается извне)
    /// </summary>
    public void SetExperimentStartTime(DateTime startTime)
    {
        _experimentStartTime = startTime;
        _historyLoaded = false;
        _ = LoadHistoryAsync();
    }

    private void OnChannelValue(int index, double value)
    {
        if (index != ChannelIndex || double.IsNaN(value)) return;

        lock (_pointsLock)
        {
            _rawPoints.Add((JsqClock.Now, value));
            // Ограничиваем буфер — 100 000 точек (~24 часа при 1 Гц)
            if (_rawPoints.Count > 100000)
                _rawPoints.RemoveRange(0, 10000);
        }
    }

    private async Task RefreshChartAsync()
    {
        (DateTime time, double value)[] snapshot;
        lock (_pointsLock)
        {
            if (_rawPoints.Count == 0) return;
            snapshot = _rawPoints.ToArray();
        }

        var span = GetWindowSpan();
        var now = JsqClock.Now;
        var cutoff = span.HasValue ? now - span.Value : _experimentStartTime;

        var filtered = snapshot.Where(p => p.time >= cutoff).ToArray();

        _series.Points.Clear();
        foreach (var (time, value) in filtered)
            _series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(time), value));

        if (filtered.Length > 0)
        {
            var last = filtered[filtered.Length - 1];
            CurrentValueText = $"{last.value:F3} {Unit}";
        }

        // Явно выставляем границы X-оси — это даёт и автопрокрутку,
        // и мгновенное применение при смене диапазона
        var xAxis = PlotModel.Axes.OfType<DateTimeAxis>().FirstOrDefault();
        if (xAxis != null)
        {
            xAxis.Minimum = DateTimeAxis.ToDouble(cutoff);
            xAxis.Maximum = DateTimeAxis.ToDouble(now.AddSeconds(2)); // запас справа
        }

        // Y-ось: сбрасываем в авто, чтобы масштаб подстраивался под видимые данные
        var yAxis = PlotModel.Axes.OfType<LinearAxis>().FirstOrDefault();
        if (yAxis != null)
        {
            yAxis.Minimum = double.NaN;
            yAxis.Maximum = double.NaN;
        }

        PlotModel.InvalidatePlot(true);
        await Task.CompletedTask;
    }

    private TimeSpan? GetWindowSpan() => SelectedWindow switch
    {
        "30с"  => TimeSpan.FromSeconds(30),
        "1мин" => TimeSpan.FromMinutes(1),
        "5мин" => TimeSpan.FromMinutes(5),
        _      => null  // "Весь" — без ограничения
    };

    public void Unsubscribe()
    {
        _refreshTimer.Stop();
        _service.ChannelValueReceived -= OnChannelValue;
    }
}
