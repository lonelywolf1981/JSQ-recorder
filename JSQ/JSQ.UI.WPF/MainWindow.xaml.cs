using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization;
using JSQ.Core.Models;
using JSQ.UI.WPF.ViewModels;
using WinForms = System.Windows.Forms;

namespace JSQ.UI.WPF;

public partial class MainWindow : Window
{
    private readonly string _layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "main_grid_layout.json");
    private Point _dragStartPoint;
    private bool _isLogsVisible = true;
    private GridLength _savedLogsHeight = new(180);
    private readonly Dictionary<DataGrid, DropLineAdorner> _dropAdorners = new();

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

    private void ToggleLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var logsRow = FindName("LogsRow") as RowDefinition;
        var logsSplitterRow = FindName("LogsSplitterRow") as RowDefinition;
        var logsGroupBox = FindName("LogsGroupBox") as GroupBox;
        var toggleButton = sender as Button ?? FindName("ToggleLogsButton") as Button;

        if (logsRow == null || logsSplitterRow == null || logsGroupBox == null || toggleButton == null)
            return;

        if (_isLogsVisible)
        {
            if (logsRow.ActualHeight > 30)
                _savedLogsHeight = new GridLength(logsRow.ActualHeight);

            logsRow.Height = new GridLength(0);
            logsSplitterRow.Height = new GridLength(0);
            logsGroupBox.Visibility = Visibility.Collapsed;
            toggleButton.Content = "Показать логи";
            _isLogsVisible = false;
        }
        else
        {
            logsRow.Height = _savedLogsHeight.Value > 0 ? _savedLogsHeight : new GridLength(180);
            logsSplitterRow.Height = new GridLength(5);
            logsGroupBox.Visibility = Visibility.Visible;
            toggleButton.Content = "Скрыть логи";
            _isLogsVisible = true;
        }
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
        if (vm.IsAnyPostRunning)
            return;

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

    /// <summary>
    /// При правом клике: если кликнутая строка не входит в текущее выделение —
    /// выбираем только её (стандартное поведение контекстного меню).
    /// </summary>
    private void ChannelGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is DataGrid grid))
            return;

        var hit = VisualTreeHelper.HitTest(grid, e.GetPosition(grid));
        if (hit == null)
            return;

        // Поднимаемся по визуальному дереву до DataGridRow
        DependencyObject obj = hit.VisualHit;
        while (obj != null && !(obj is DataGridRow))
            obj = VisualTreeHelper.GetParent(obj);

        var row = obj as DataGridRow;
        if (row == null || row.Item == null)
            return;

        if (!grid.SelectedItems.Contains(row.Item))
        {
            grid.UnselectAll();
            grid.SelectedItem = row.Item;
        }
    }

    /// <summary>
    /// Применяет выбранный hex-цвет (или сброс при пустом теге) ко всем
    /// выделенным строкам того DataGrid, из которого открыто контекстное меню.
    /// </summary>
    private void HighlightColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveContextGrid(sender, out var grid))
            return;

        var menuItem = (MenuItem)sender;
        var color = menuItem.Tag as string;
        var colorValue = string.IsNullOrEmpty(color) ? null : color;

        foreach (var ch in grid.SelectedItems.OfType<ChannelStatus>())
            ch.RowHighlightColor = colorValue;
    }

    private void HighlightCustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveContextGrid(sender, out var grid))
            return;

        var selected = grid.SelectedItems.OfType<ChannelStatus>().ToList();
        if (selected.Count == 0)
            return;

        var initialHex = selected
            .Select(ch => ch.RowHighlightColor)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var initialColor = TryParseHexColor(initialHex, out var parsedColor)
            ? parsedColor
            : System.Drawing.Color.White;

        using var dialog = new WinForms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            Color = initialColor
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var selectedColor = dialog.Color;
        var hex = $"#{selectedColor.R:X2}{selectedColor.G:X2}{selectedColor.B:X2}";

        foreach (var ch in selected)
            ch.RowHighlightColor = hex;
    }

    private static bool TryResolveContextGrid(object sender, out DataGrid grid)
    {
        grid = null!;

        if (sender is not MenuItem menuItem)
            return false;

        DependencyObject parent = menuItem;
        while (parent != null && parent is not System.Windows.Controls.ContextMenu)
            parent = LogicalTreeHelper.GetParent(parent);

        var resolvedGrid = (parent as System.Windows.Controls.ContextMenu)?.PlacementTarget as DataGrid;
        if (resolvedGrid == null)
            return false;

        grid = resolvedGrid;
        return true;
    }

    private static bool TryParseHexColor(string? hex, out System.Drawing.Color color)
    {
        color = System.Drawing.Color.Empty;

        if (hex is null)
            return false;

        var value = hex.Trim();
        if (value.Length == 0)
            return false;

        if (value.StartsWith("#", StringComparison.Ordinal))
            value = value.Substring(1);

        if (value.Length != 6)
            return false;

        if (!byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r))
            return false;
        if (!byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g))
            return false;
        if (!byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;

        color = System.Drawing.Color.FromArgb(r, g, b);
        return true;
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
        if (vm.IsAnyPostRunning)
            return;

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
        if (!(sender is DataGrid targetGrid))
            return;

        HideDropAdorner(targetGrid);

        var targetPostId = ResolvePostIdByGrid(targetGrid);
        if (targetPostId == null)
            return;

        if (!e.Data.GetDataPresent("JSQ.TransferSourcePost") || !e.Data.GetDataPresent("JSQ.TransferIndices"))
            return;

        var sourcePostId = e.Data.GetData("JSQ.TransferSourcePost") as string;
        var indices = e.Data.GetData("JSQ.TransferIndices") as int[];

        if (string.IsNullOrWhiteSpace(sourcePostId) || indices == null || indices.Length == 0)
            return;

        var vm = (MainViewModel)DataContext;
        if (vm.IsAnyPostRunning)
            return;

        if (string.Equals(sourcePostId, targetPostId, StringComparison.OrdinalIgnoreCase))
        {
            // Drag внутри одного поста → меняем порядок
            var insertBefore = GetDropTargetChannelIndex(targetGrid, e.GetPosition(targetGrid));
            await vm.ReorderChannelsAsync(targetPostId, indices, insertBefore);
            return;
        }

        await vm.TransferChannelsAsync(sourcePostId, targetPostId, indices);
    }

    private void ChannelGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!(sender is DataGrid grid))
            return;

        if (!e.Data.GetDataPresent("JSQ.TransferSourcePost"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // Индикатор вставки только для drag внутри одного поста
        var sourcePost = e.Data.GetData("JSQ.TransferSourcePost") as string;
        var targetPost = ResolvePostIdByGrid(grid);
        if (!string.Equals(sourcePost, targetPost, StringComparison.OrdinalIgnoreCase))
        {
            HideDropAdorner(grid);
            return;
        }

        var dropPos = e.GetPosition(grid);
        ShowDropAdorner(grid, ComputeDropLineY(grid, dropPos));
    }

    private void ChannelGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (!(sender is DataGrid grid))
            return;

        // Скрываем только когда мышь действительно вышла за границу грида
        var pos = e.GetPosition(grid);
        if (pos.X >= 0 && pos.Y >= 0 && pos.X <= grid.ActualWidth && pos.Y <= grid.ActualHeight)
            return;

        HideDropAdorner(grid);
    }

    // ── Drop-line adorner ─────────────────────────────────────────────────────

    private void ShowDropAdorner(DataGrid grid, double y)
    {
        if (!_dropAdorners.TryGetValue(grid, out var adorner))
        {
            var layer = AdornerLayer.GetAdornerLayer(grid);
            if (layer == null)
                return;
            adorner = new DropLineAdorner(grid);
            layer.Add(adorner);
            _dropAdorners[grid] = adorner;
        }
        adorner.UpdatePosition(y);
    }

    private void HideDropAdorner(DataGrid grid)
    {
        if (_dropAdorners.TryGetValue(grid, out var adorner))
            adorner.Hide();
    }

    private static double ComputeDropLineY(DataGrid grid, Point dropPos)
    {
        var hit = VisualTreeHelper.HitTest(grid, dropPos);
        if (hit == null)
            return dropPos.Y;

        DependencyObject obj = hit.VisualHit;
        while (obj != null && !(obj is DataGridRow))
            obj = VisualTreeHelper.GetParent(obj);

        if (!(obj is DataGridRow row))
            return dropPos.Y;

        var rowTop = row.TranslatePoint(new Point(0, 0), grid).Y;
        var relY = dropPos.Y - rowTop;

        // Верхняя половина → линия над строкой, нижняя → под строкой
        return relY > row.ActualHeight / 2.0 ? rowTop + row.ActualHeight : rowTop;
    }

    /// <summary>
    /// Определяет ChannelIndex строки, ПЕРЕД которой нужно вставить перетаскиваемые элементы.
    /// Возвращает -1 если нужно добавить в конец (дроп в пустую зону или на нижнюю половину последней строки).
    /// </summary>
    private static int GetDropTargetChannelIndex(DataGrid grid, Point dropPos)
    {
        var hit = VisualTreeHelper.HitTest(grid, dropPos);
        if (hit == null)
            return -1;

        DependencyObject obj = hit.VisualHit;
        while (obj != null && !(obj is DataGridRow))
            obj = VisualTreeHelper.GetParent(obj);

        if (!(obj is DataGridRow row) || !(row.Item is ChannelStatus ch))
            return -1;

        var rowTop = row.TranslatePoint(new Point(0, 0), grid).Y;
        var relY = dropPos.Y - rowTop;

        if (relY > row.ActualHeight / 2.0)
        {
            // Нижняя половина строки → вставить ПОСЛЕ неё
            // Ищем следующую строку в источнике данных
            if (grid.ItemsSource is System.Collections.IEnumerable src)
            {
                var items = src.Cast<ChannelStatus>().ToList();
                var pos = items.IndexOf(ch);
                if (pos >= 0 && pos + 1 < items.Count)
                    return items[pos + 1].ChannelIndex;
            }
            return -1; // в конец
        }

        return ch.ChannelIndex; // вставить перед этой строкой
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
                nameof(ChannelStatus.ChannelName) => string.Compare(a.Alias, b.Alias, StringComparison.CurrentCultureIgnoreCase),
                nameof(ChannelStatus.Alias) => string.Compare(a.Alias, b.Alias, StringComparison.CurrentCultureIgnoreCase),
                nameof(ChannelStatus.CurrentValue) => Nullable.Compare(a.CurrentValue, b.CurrentValue),
                nameof(ChannelStatus.Unit) => string.Compare(a.Unit, b.Unit, StringComparison.CurrentCultureIgnoreCase),
                nameof(ChannelStatus.MinLimit) => Nullable.Compare(a.MinLimit, b.MinLimit),
                nameof(ChannelStatus.MaxLimit) => Nullable.Compare(a.MaxLimit, b.MaxLimit),
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

    // ── Визуальный индикатор позиции вставки ─────────────────────────────────

    private sealed class DropLineAdorner : Adorner
    {
        private static readonly Pen LinePen;
        private double _y = -1;

        static DropLineAdorner()
        {
            LinePen = new Pen(new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4)), 2);
            LinePen.Freeze();
        }

        public DropLineAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public void UpdatePosition(double y)
        {
            _y = y;
            InvalidateVisual();
        }

        public void Hide()
        {
            _y = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_y < 0)
                return;

            var w = AdornedElement.RenderSize.Width;
            dc.DrawLine(LinePen, new Point(0, _y), new Point(w, _y));
            dc.DrawEllipse(LinePen.Brush, null, new Point(5, _y), 4, 4);
            dc.DrawEllipse(LinePen.Brush, null, new Point(w - 5, _y), 4, 4);
        }
    }
}
