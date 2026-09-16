using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class MilitaryExportWindow : Window
{
    private readonly IReadOnlyList<MilitaryRecord> _selected;
    private readonly IReadOnlyList<MilitaryRecord> _visible;
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    private MilitaryExportPreferences _preferences = new();
    private bool _ready;

    public MilitaryExportWindow(IReadOnlyList<MilitaryRecord> selected, IReadOnlyList<MilitaryRecord> visible)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _selected = selected.DistinctBy(x => x.Id).ToList();
        _visible = visible.DistinctBy(x => x.Id).ToList();
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _preferences = await App.Json.LoadAsync<MilitaryExportPreferences>(App.Paths.ExportPreferencesFile) ?? new MilitaryExportPreferences();
        foreach (var column in MilitaryExportService.Columns)
        {
            var check = new CheckBox { Content = column.Header, Tag = column.Key, Margin = new Thickness(3, 5, 10, 5) };
            _checks[column.Key] = check;
            ColumnsPanel.Children.Add(check);
        }
        var saved = _preferences.SelectedColumns?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var column in MilitaryExportService.Columns)
            _checks[column.Key].IsChecked = saved.Count == 0 ? column.Essential : saved.Contains(column.Key);

        SelectedOnlyRadio.IsEnabled = _selected.Count > 0;
        SelectedOnlyRadio.IsChecked = _selected.Count > 0 && _preferences.UseSelectedOnly;
        VisibleRadio.IsChecked = _selected.Count == 0 || !_preferences.UseSelectedOnly;
        var format = string.IsNullOrWhiteSpace(_preferences.Format) ? "Excel (.xlsx)" : _preferences.Format;
        FormatCombo.SelectedIndex = format.StartsWith("CSV", StringComparison.OrdinalIgnoreCase) ? 1
            : format.StartsWith("Texto", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        _ready = true;
        UpdateSummary();
    }

    private IReadOnlyList<MilitaryRecord> CurrentRows => SelectedOnlyRadio.IsChecked == true && _selected.Count > 0 ? _selected : _visible;
    private string CurrentFormat => (FormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Excel (.xlsx)";

    private void Scope_Changed(object sender, RoutedEventArgs e) { if (_ready) UpdateSummary(); }
    private void Format_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSummary(); }

    private void UpdateSummary()
    {
        var scope = SelectedOnlyRadio.IsChecked == true && _selected.Count > 0 ? "selecionados" : "visíveis";
        ScopeSummary.Text = $"{CurrentRows.Count} militar(es) {scope} • escolha exatamente o que aparecerá no arquivo";
        StatusText.Text = $"Formato: {CurrentFormat}";
    }

    private void Essential_Click(object sender, RoutedEventArgs e)
    {
        foreach (var column in MilitaryExportService.Columns) _checks[column.Key].IsChecked = column.Essential;
    }
    private void All_Click(object sender, RoutedEventArgs e) { foreach (var check in _checks.Values) check.IsChecked = true; }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var check in _checks.Values) check.IsChecked = false; }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var keys = _checks.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
        if (keys.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione ao menos um campo.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CurrentRows.Count == 0)
        {
            SigfurDialog.Show(this, "Não há militares neste escopo.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var format = CurrentFormat;
        var extension = format.StartsWith("CSV", StringComparison.OrdinalIgnoreCase) ? ".csv"
            : format.StartsWith("Texto", StringComparison.OrdinalIgnoreCase) ? ".txt" : ".xlsx";
        var filter = extension == ".xlsx" ? "Planilha Excel (*.xlsx)|*.xlsx"
            : extension == ".csv" ? "CSV (*.csv)|*.csv" : "Texto tabulado (*.txt)|*.txt";
        var dialog = new SaveFileDialog
        {
            Title = "Salvar exportação dos militares",
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"militares_{DateTime.Now:yyyyMMdd_HHmm}{extension}"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IsEnabled = false;
            StatusText.Text = $"Exportando {CurrentRows.Count} militar(es)...";
            await MilitaryExportService.ExportAsync(dialog.FileName, format, CurrentRows, keys);
            _preferences.SelectedColumns = keys;
            _preferences.Format = format;
            _preferences.UseSelectedOnly = SelectedOnlyRadio.IsChecked == true;
            await App.Json.SaveAsync(App.Paths.ExportPreferencesFile, _preferences);
            StatusText.Text = "Exportação concluída.";
            var open = SigfurDialog.Show(this, $"Arquivo gerado com sucesso:\n\n{dialog.FileName}\n\nAbrir agora?", "Exportar militares", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes) ShellService.OpenPath(dialog.FileName);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Exportar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
