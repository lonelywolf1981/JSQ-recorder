using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JSQ.Core.Models;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF;

public partial class MainWindow : Window
{
    private readonly string _layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "main_grid_layout.json");

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => LoadGridLayout();
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

        SaveGridLayout();
        base.OnClosing(e);
    }

    private void LoadGridLayout()
    {
        try
        {
            if (!File.Exists(_layoutPath)) return;

            var json = File.ReadAllText(_layoutPath);
            var file = JsonSerializer.Deserialize<GridLayoutFile>(json);
            if (file == null) return;

            ApplyGridLayout(PostADataGrid, file.PostA);
            ApplyGridLayout(PostBDataGrid, file.PostB);
            ApplyGridLayout(PostCDataGrid, file.PostC);
        }
        catch
        {
            // ignore invalid layout file
        }
    }

    private void SaveGridLayout()
    {
        try
        {
            var file = new GridLayoutFile
            {
                PostA = CaptureGridLayout(PostADataGrid),
                PostB = CaptureGridLayout(PostBDataGrid),
                PostC = CaptureGridLayout(PostCDataGrid)
            };

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_layoutPath, json);
        }
        catch
        {
            // ignore IO issues
        }
    }

    private static GridLayout CaptureGridLayout(DataGrid grid)
    {
        var layout = new GridLayout();
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var col = grid.Columns[i];
            var width = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
            if (width <= 0)
                width = col.ActualWidth;

            layout.Columns.Add(new GridColumnLayout
            {
                OriginalIndex = i,
                DisplayIndex = col.DisplayIndex,
                Width = width
            });
        }

        return layout;
    }

    private static void ApplyGridLayout(DataGrid grid, GridLayout? layout)
    {
        if (layout?.Columns == null || layout.Columns.Count == 0) return;

        foreach (var colLayout in layout.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (colLayout.OriginalIndex < 0 || colLayout.OriginalIndex >= grid.Columns.Count)
                continue;

            var col = grid.Columns[colLayout.OriginalIndex];

            if (colLayout.Width > 20)
                col.Width = new DataGridLength(colLayout.Width, DataGridLengthUnitType.Pixel);

            var displayIndex = Math.Max(0, Math.Min(colLayout.DisplayIndex, grid.Columns.Count - 1));
            col.DisplayIndex = displayIndex;
        }
    }

    private sealed class GridLayoutFile
    {
        public GridLayout? PostA { get; set; }
        public GridLayout? PostB { get; set; }
        public GridLayout? PostC { get; set; }
    }

    private sealed class GridLayout
    {
        public System.Collections.Generic.List<GridColumnLayout> Columns { get; set; } = new();
    }

    private sealed class GridColumnLayout
    {
        public int OriginalIndex { get; set; }
        public int DisplayIndex { get; set; }
        public double Width { get; set; }
    }
}
