using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using JSQ.Core.Models;
using JSQ.UI.WPF.ViewModels;

namespace JSQ.UI.WPF;

public partial class MainWindow : Window
{
    private readonly string _layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "main_grid_layout.json");
    private Point _dragStartPoint;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => LoadGridLayout();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChannelDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is ChannelStatus ch)
            ((MainViewModel)DataContext).OpenChannelChartCommand.Execute(ch);
    }

    private async void MoveAToB_Click(object sender, RoutedEventArgs e)
    {
        await TransferSelectedChannelsAsync(PostADataGrid, "A", "B");
    }

    private async void MoveBToA_Click(object sender, RoutedEventArgs e)
    {
        await TransferSelectedChannelsAsync(PostBDataGrid, "B", "A");
    }

    private async void MoveBToC_Click(object sender, RoutedEventArgs e)
    {
        await TransferSelectedChannelsAsync(PostBDataGrid, "B", "C");
    }

    private async void MoveCToB_Click(object sender, RoutedEventArgs e)
    {
        await TransferSelectedChannelsAsync(PostCDataGrid, "C", "B");
    }

    private async void ChannelGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        if (sender is not DataGrid grid)
            return;

        if (!string.Equals(e.Column.SortMemberPath, nameof(ChannelStatus.IsSelected), StringComparison.Ordinal))
        {
            var sortMember = e.Column.SortMemberPath;
            if (string.IsNullOrWhiteSpace(sortMember))
                return;

            var direction = e.Column.SortDirection != ListSortDirection.Ascending
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;

            foreach (var column in grid.Columns)
            {
                if (!ReferenceEquals(column, e.Column))
                    column.SortDirection = null;
            }

            e.Column.SortDirection = direction;

            var view = CollectionViewSource.GetDefaultView(grid.ItemsSource) as ListCollectionView;
            if (view != null)
            {
                view.SortDescriptions.Clear();
                view.CustomSort = new ChannelGridComparer(sortMember, direction);
                view.Refresh();
            }

            return;
        }

        var postId = ResolvePostIdByGrid(grid);
        if (postId == null)
            return;

        var vm = (MainViewModel)DataContext;
        await vm.TogglePostSelectionAsync(postId);
    }

    private async Task TransferSelectedChannelsAsync(DataGrid sourceGrid, string sourcePostId, string targetPostId)
    {
        var vm = (MainViewModel)DataContext;
        var selectedRowIndices = sourceGrid.SelectedItems
            .OfType<ChannelStatus>()
            .Select(c => c.ChannelIndex)
            .Distinct()
            .ToList();

        var indices = vm.GetTransferCandidateIndices(sourcePostId, selectedRowIndices);

        await vm.TransferChannelsAsync(sourcePostId, targetPostId, indices);
    }

    private void ChannelGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
    }

    private void ChannelGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (sender is not DataGrid sourceGrid)
            return;

        var currentPos = e.GetPosition(this);
        var delta = _dragStartPoint - currentPos;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var sourcePostId = ResolvePostIdByGrid(sourceGrid);
        if (sourcePostId == null)
            return;

        var vm = (MainViewModel)DataContext;
        var selectedRowIndices = sourceGrid.SelectedItems
            .OfType<ChannelStatus>()
            .Select(c => c.ChannelIndex)
            .Distinct()
            .ToList();
        var indices = vm.GetTransferCandidateIndices(sourcePostId, selectedRowIndices);
        if (indices.Count == 0)
            return;

        var data = new DataObject();
        data.SetData("JSQ.TransferSourcePost", sourcePostId);
        data.SetData("JSQ.TransferIndices", indices.ToArray());
        DragDrop.DoDragDrop(sourceGrid, data, DragDropEffects.Move);
    }

    private async void ChannelGrid_Drop(object sender, DragEventArgs e)
    {
        if (sender is not DataGrid targetGrid)
            return;

        var targetPostId = ResolvePostIdByGrid(targetGrid);
        if (targetPostId == null)
            return;

        if (!e.Data.GetDataPresent("JSQ.TransferSourcePost") || !e.Data.GetDataPresent("JSQ.TransferIndices"))
            return;

        var sourcePostId = e.Data.GetData("JSQ.TransferSourcePost") as string;
        var indices = e.Data.GetData("JSQ.TransferIndices") as int[];

        if (string.IsNullOrWhiteSpace(sourcePostId) || indices == null || indices.Length == 0)
            return;

        var sourcePost = sourcePostId!;
        var vm = (MainViewModel)DataContext;
        await vm.TransferChannelsAsync(sourcePost, targetPostId, indices);
    }

    private string? ResolvePostIdByGrid(DataGrid grid)
    {
        if (ReferenceEquals(grid, PostADataGrid)) return "A";
        if (ReferenceEquals(grid, PostBDataGrid)) return "B";
        if (ReferenceEquals(grid, PostCDataGrid)) return "C";
        return null;
    }

    private sealed class ChannelGridComparer : IComparer
    {
        private readonly string _sortMember;
        private readonly ListSortDirection _direction;

        public ChannelGridComparer(string sortMember, ListSortDirection direction)
        {
            _sortMember = sortMember;
            _direction = direction;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not ChannelStatus a || y is not ChannelStatus b)
                return 0;

            // Общие каналы всегда сверху и не участвуют в сортировке.
            if (a.IsCommon && !b.IsCommon) return -1;
            if (!a.IsCommon && b.IsCommon) return 1;
            if (a.IsCommon && b.IsCommon) return a.ChannelIndex.CompareTo(b.ChannelIndex);

            var result = _sortMember switch
            {
                nameof(ChannelStatus.ChannelName) => string.Compare(a.ChannelName, b.ChannelName, StringComparison.CurrentCultureIgnoreCase),
                nameof(ChannelStatus.CurrentValue) => Nullable.Compare(a.CurrentValue, b.CurrentValue),
                nameof(ChannelStatus.Unit) => string.Compare(a.Unit, b.Unit, StringComparison.CurrentCultureIgnoreCase),
                nameof(ChannelStatus.MinLimit) => Nullable.Compare(a.MinLimit, b.MinLimit),
                nameof(ChannelStatus.MaxLimit) => Nullable.Compare(a.MaxLimit, b.MaxLimit),
                nameof(ChannelStatus.HighPrecision) => a.HighPrecision.CompareTo(b.HighPrecision),
                nameof(ChannelStatus.Status) => a.Status.CompareTo(b.Status),
                _ => a.ChannelIndex.CompareTo(b.ChannelIndex)
            };

            if (result == 0)
                result = a.ChannelIndex.CompareTo(b.ChannelIndex);

            return _direction == ListSortDirection.Descending ? -result : result;
        }
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
            // Игнорируем поврежденный файл раскладки.
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
            // Игнорируем временные ошибки ввода-вывода.
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
