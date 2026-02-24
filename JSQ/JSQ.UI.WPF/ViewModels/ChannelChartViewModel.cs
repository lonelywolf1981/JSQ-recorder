using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly List<(DateTime time, double value)> _rawPoints = new(10000);
    private readonly object _pointsLock = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly LineSeries _series;

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

    public ChannelChartViewModel(ChannelStatus channel, IExperimentService service)
    {
        ChannelIndex = channel.ChannelIndex;
        ChannelName = channel.ChannelName;
        Unit = channel.Unit ?? string.Empty;
        _service = service;

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
        _refreshTimer.Tick += (_, _) => RefreshChart();
        _refreshTimer.Start();
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

    partial void OnSelectedWindowChanged(string value) => RefreshChart();

    private void OnChannelValue(int index, double value)
    {
        if (index != ChannelIndex || double.IsNaN(value)) return;

        lock (_pointsLock)
        {
            _rawPoints.Add((DateTime.Now, value));
            // Ограничиваем буфер — 20 000 точек (~5 часов при 1 Гц)
            if (_rawPoints.Count > 20000)
                _rawPoints.RemoveRange(0, 1000);
        }
    }

    private void RefreshChart()
    {
        (DateTime time, double value)[] snapshot;
        lock (_pointsLock)
        {
            if (_rawPoints.Count == 0) return;
            snapshot = _rawPoints.ToArray();
        }

        var span = GetWindowSpan();
        var cutoff = span.HasValue ? DateTime.Now - span.Value : DateTime.MinValue;

        var filtered = span.HasValue
            ? snapshot.Where(p => p.time >= cutoff).ToArray()
            : snapshot;

        _series.Points.Clear();
        foreach (var (time, value) in filtered)
            _series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(time), value));

        if (filtered.Length > 0)
        {
            var last = filtered[filtered.Length - 1];
            CurrentValueText = $"{last.value:F3} {Unit}";
        }

        PlotModel.InvalidatePlot(true);
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
