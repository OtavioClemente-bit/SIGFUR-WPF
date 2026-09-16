using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class BulletinTableEditorWindow : Window
{
    private BulletinTableData _table;
    private readonly List<List<TextBox>> _cellEditors = [];
    private readonly List<TextBox> _widthEditors = [];
    private bool _building;

    public BulletinTableEditorWindow(BulletinTableData table, string? importStatus = null)
    {
        InitializeComponent();
        _table = BulletinTableCodec.ParseOrCreate(BulletinTableCodec.Serialize(table));
        RowsBox.ItemsSource = Enumerable.Range(1, 30).ToList();
        ColumnsBox.ItemsSource = Enumerable.Range(1, 12).ToList();
        RowsBox.SelectedItem = _table.Rows;
        ColumnsBox.SelectedItem = _table.Columns;
        WidthBox.Text = _table.WidthPercent + "%";
        HeaderCheck.IsChecked = _table.HasHeader;
        ImportStatusText.Text = importStatus ?? string.Empty;
        BuildEditor();
        App.UiState.Attach(this);
    }

    public BulletinTableData Table => BulletinTableCodec.Normalize(_table);

    private void Dimensions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _building || RowsBox.SelectedItem is not int rows || ColumnsBox.SelectedItem is not int columns) return;
        CaptureCells();
        _table.Rows = rows;
        _table.Columns = columns;
        BulletinTableCodec.Normalize(_table);
        BuildEditor();
    }

    private void BuildEditor()
    {
        _building = true;
        try
        {
            BulletinTableCodec.Normalize(_table);
            CellsGrid.Children.Clear();
            CellsGrid.RowDefinitions.Clear();
            CellsGrid.ColumnDefinitions.Clear();
            _cellEditors.Clear();
            for (var column = 0; column < _table.Columns; column++)
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(130, 720d * _table.ColumnWidths[column] / 100d)) });
            for (var row = 0; row < _table.Rows; row++)
            {
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var editors = new List<TextBox>();
                for (var column = 0; column < _table.Columns; column++)
                {
                    var box = new TextBox
                    {
                        Text = _table.Cells[row][column],
                        MinHeight = row == 0 && _table.HasHeader ? 38 : 34,
                        Padding = new Thickness(7, 5, 7, 5),
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        FontWeight = row == 0 && _table.HasHeader ? FontWeights.SemiBold : FontWeights.Normal,
                        Tag = new CellPosition(row, column),
                        BorderThickness = new Thickness(0)
                    };
                    box.PreviewKeyDown += Cell_PreviewKeyDown;
                    var border = new Border
                    {
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                        Child = box
                    };
                    Grid.SetRow(border, row);
                    Grid.SetColumn(border, column);
                    CellsGrid.Children.Add(border);
                    editors.Add(box);
                }
                _cellEditors.Add(editors);
            }

            ColumnWidthsPanel.Children.Clear();
            _widthEditors.Clear();
            for (var column = 0; column < _table.Columns; column++)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                item.Children.Add(new TextBlock { Text = $"Col. {column + 1}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                var width = new TextBox { Text = _table.ColumnWidths[column].ToString(CultureInfo.InvariantCulture), Width = 52, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
                _widthEditors.Add(width);
                item.Children.Add(width);
                ColumnWidthsPanel.Children.Add(item);
            }
        }
        finally { _building = false; }
    }

    private void Cell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || sender is not TextBox { Tag: CellPosition position }) return;
        var clipboard = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        if (!clipboard.Contains('\t') && !clipboard.Contains('\n') && !clipboard.Contains('\r')) return;
        PasteMatrix(clipboard, position.Row, position.Column);
        e.Handled = true;
    }

    private void PasteExcel_Click(object sender, RoutedEventArgs e)
    {
        if (BulletinTableClipboardImporter.TryReadClipboard(out var imported, out var source))
        {
            _table = imported;
            RowsBox.SelectedItem = _table.Rows;
            ColumnsBox.SelectedItem = _table.Columns;
            WidthBox.Text = _table.WidthPercent + "%";
            HeaderCheck.IsChecked = _table.HasHeader;
            ImportStatusText.Text = source;
            BuildEditor();
            return;
        }
        if (!Clipboard.ContainsText())
        {
            SigfurDialog.Show(this, "A área de transferência não contém uma tabela em texto.", "Tabela", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PasteMatrix(Clipboard.GetText(), 0, 0);
    }

    private void PasteMatrix(string clipboard, int startRow, int startColumn)
    {
        CaptureCells();
        var lines = clipboard.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n').Split('\n');
        var matrix = lines.Select(line => line.Split('\t')).ToList();
        if (matrix.Count == 0) return;
        var columns = matrix.Max(row => row.Length);
        _table.Rows = Math.Clamp(Math.Max(_table.Rows, startRow + matrix.Count), 1, 30);
        _table.Columns = Math.Clamp(Math.Max(_table.Columns, startColumn + columns), 1, 12);
        BulletinTableCodec.Normalize(_table);
        for (var row = 0; row < matrix.Count && startRow + row < _table.Rows; row++)
            for (var column = 0; column < matrix[row].Length && startColumn + column < _table.Columns; column++)
                _table.Cells[startRow + row][startColumn + column] = matrix[row][column].Trim();
        RowsBox.SelectedItem = _table.Rows;
        ColumnsBox.SelectedItem = _table.Columns;
        BuildEditor();
    }

    private void CaptureCells()
    {
        for (var row = 0; row < _cellEditors.Count && row < _table.Cells.Count; row++)
            for (var column = 0; column < _cellEditors[row].Count && column < _table.Cells[row].Count; column++)
                _table.Cells[row][column] = _cellEditors[row][column].Text.Trim();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureCells();
        _table.Rows = RowsBox.SelectedItem as int? ?? _table.Rows;
        _table.Columns = ColumnsBox.SelectedItem as int? ?? _table.Columns;
        _table.WidthPercent = ParsePercent(WidthBox.Text, 100);
        _table.HasHeader = HeaderCheck.IsChecked == true;
        _table.ColumnWidths = _widthEditors.Select(editor => ParsePercent(editor.Text, 1)).ToList();
        BulletinTableCodec.Normalize(_table);
        DialogResult = true;
    }

    private static int ParsePercent(string? value, int fallback)
        => int.TryParse(new string((value ?? string.Empty).Where(char.IsDigit).ToArray()), out var parsed) ? parsed : fallback;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private sealed record CellPosition(int Row, int Column);
}
