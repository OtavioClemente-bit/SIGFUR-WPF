using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Licensed;

public partial class LicensedExportWindow : Window
{
    private readonly IReadOnlyList<LicensedTransferredRecord> _displayed;
    private readonly IReadOnlyList<LicensedTransferredRecord> _selected;

    public ObservableCollection<ColumnChoice> Columns { get; } = [];
    public IReadOnlyList<LicensedTransferredRecord> Records { get; private set; } = [];
    public IReadOnlyList<string> SelectedColumnKeys { get; private set; } = [];
    public bool ExportExcel { get; private set; } = true;

    public LicensedExportWindow(
        IReadOnlyList<LicensedTransferredRecord> displayed,
        IReadOnlyList<LicensedTransferredRecord> selected,
        bool? preferExcel = null)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _displayed = displayed;
        _selected = selected;
        foreach (var column in LicensedTransferredSpreadsheetService.ExportColumns)
            Columns.Add(new ColumnChoice(column.Key, column.Header, column.DefaultSelected));
        DataContext = this;
        ExcelBox.IsChecked = preferExcel is not false;
        CsvBox.IsChecked = preferExcel is false;
        SelectedBox.IsEnabled = selected.Count > 0;
        ScopeInfoText.Text = $"Exibidos: {displayed.Count}  •  Selecionados: {selected.Count}";
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        var defaults = LicensedTransferredSpreadsheetService.ExportColumns.ToDictionary(x => x.Key, x => x.DefaultSelected, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Columns) item.IsSelected = defaults.GetValueOrDefault(item.Key);
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Columns) item.IsSelected = true;
    }

    private void None_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Columns) item.IsSelected = false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var selectedColumns = Columns.Where(x => x.IsSelected).Select(x => x.Key).ToList();
        if (selectedColumns.Count == 0)
        {
            ValidationText.Text = "Selecione ao menos uma coluna.";
            return;
        }

        Records = SelectedBox.IsChecked == true && _selected.Count > 0 ? _selected : _displayed;
        if (Records.Count == 0)
        {
            ValidationText.Text = "Não há registros para exportar.";
            return;
        }

        SelectedColumnKeys = selectedColumns;
        ExportExcel = ExcelBox.IsChecked == true;
        DialogResult = true;
    }

    public sealed class ColumnChoice : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string Key { get; }
        public string Header { get; }
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
        public ColumnChoice(string key, string header, bool selected) { Key = key; Header = header; _isSelected = selected; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
