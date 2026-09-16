using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class SalaryWindow : Window
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly SalaryService _service;
    private readonly ObservableCollection<SalaryRecord> _rows = [];
    private ICollectionView? _view;
    private bool _loading;

    public SalaryWindow(SalaryService service)
    {
        InitializeComponent();
        _service = service;
        App.UiState.Attach(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync(string? selectRank = null)
    {
        _loading = true;
        try
        {
            var all = await _service.GetAllAsync(includeHidden: true);
            _rows.Clear();
            foreach (var row in all) _rows.Add(row);
            RankBox.ItemsSource = all.Select(x => x.Rank).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = FilterRow;
            _view.SortDescriptions.Clear();
            if (_view is ListCollectionView salaryView) salaryView.CustomSort = new SalaryComparer();
            SalaryGrid.ItemsSource = _view;
            _view.Refresh();
            if (!string.IsNullOrWhiteSpace(selectRank))
            {
                var selected = _rows.FirstOrDefault(x => x.Rank.Equals(selectRank, StringComparison.CurrentCultureIgnoreCase));
                if (selected is not null)
                {
                    SalaryGrid.SelectedItem = selected;
                    SalaryGrid.ScrollIntoView(selected);
                    LoadEditor(selected);
                }
            }
            UpdateMetrics();
            StatusText.Text = $"{_rows.Count} registro(s) carregado(s) do banco oficial do SIGFUR.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao abrir a tabela de soldos.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Soldos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _loading = false; }
    }

    private bool FilterRow(object item)
    {
        if (item is not SalaryRecord row) return false;
        if (row.IsHidden && ShowHiddenBox.IsChecked != true) return false;
        var query = Normalize(SearchBox.Text);
        if (string.IsNullOrWhiteSpace(query)) return true;
        return Normalize($"{row.Rank} {row.ShortRank} {row.SalaryText} {row.Official2026Text} {row.StatusText}").Contains(query);
    }

    private void UpdateMetrics()
    {
        var visible = _rows.Where(x => !x.IsHidden || ShowHiddenBox.IsChecked == true).ToList();
        VisibleCountText.Text = visible.Count.ToString(PtBr);
        OfficialCountText.Text = visible.Count(x => x.HasOfficialReference && Math.Abs(x.Difference) < 0.005m).ToString(PtBr);
        DifferentCountText.Text = visible.Count(x => x.HasOfficialReference && Math.Abs(x.Difference) >= 0.005m).ToString(PtBr);
        HiddenCountText.Text = _rows.Count(x => x.IsHidden).ToString(PtBr);
    }

    private void LoadEditor(SalaryRecord row)
    {
        RankBox.Text = row.Rank;
        SalaryBox.Text = row.Salary.ToString("N2", PtBr);
        StatusText.Text = $"Editando {row.Rank}. Referência oficial 2026: {row.Official2026Text}.";
    }

    private static bool TryParseMoney(string? text, out decimal value)
    {
        var raw = (text ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, PtBr, out value)) return true;
        return decimal.TryParse(raw.Replace(".", string.Empty).Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var rank = MilitaryRankService.Canonicalize(RankBox.Text.Trim());
        if (!TryParseMoney(SalaryBox.Text, out var salary))
        {
            SigfurDialog.Show(this, "Informe um valor válido para o soldo.", "SIGFUR — Soldos", MessageBoxButton.OK, MessageBoxImage.Warning);
            SalaryBox.Focus();
            return;
        }
        try
        {
            await _service.SaveAsync(rank, salary);
            await ReloadAsync(rank);
            StatusText.Text = $"Soldo de {rank} salvo: {salary.ToString("C2", PtBr)}.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "SIGFUR — Soldos", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        SalaryGrid.SelectedItem = null;
        RankBox.Text = string.Empty;
        SalaryBox.Text = "0,00";
        RankBox.Focus();
        StatusText.Text = "Informe um posto/graduação e o soldo para cadastrar.";
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        var row = SalaryGrid.SelectedItem as SalaryRecord;
        var rank = row?.Rank ?? RankBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rank)) return;
        if (SigfurDialog.Show(this, $"Ocultar ‘{rank}’ desta tela?\n\nO registro continuará no banco e poderá ser reexibido marcando ‘Mostrar ocultos’.", "Ocultar soldo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _service.SetHiddenAsync(rank, true);
        await ReloadAsync();
        StatusText.Text = $"{rank} foi ocultado da lista.";
    }

    private async void ToggleHidden_Click(object sender, RoutedEventArgs e)
    {
        if (SalaryGrid.SelectedItem is not SalaryRecord row) return;
        await _service.SetHiddenAsync(row.Rank, !row.IsHidden);
        await ReloadAsync(row.Rank);
    }

    private async void ApplyOfficial_Click(object sender, RoutedEventArgs e)
    {
        var answer = SigfurDialog.Show(this,
            "Aplicar a coluna vigente a partir de 1º JAN 2026 da Lei nº 15.167/2025 aos postos reconhecidos?\n\nOs valores personalizados desses postos serão substituídos. Postos não mapeados serão preservados.",
            "Atualizar soldos oficiais", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var updated = await _service.ApplyOfficial2026Async(overwriteExisting: true);
        await ReloadAsync();
        StatusText.Text = $"Referência oficial de 2026 aplicada em {updated} registro(s).";
    }

    private void OfficialSearch_Click(object sender, RoutedEventArgs e)
        => new OfficialSalaryReferenceWindow { Owner = this }.ShowDialog();

    private void OfficialSite_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SalaryService.OfficialLawUrl) { UseShellExecute = true }); }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Abrir site oficial", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var records = _view?.Cast<SalaryRecord>().ToList() ?? [];
        if (records.Count == 0) return;
        var dialog = new SaveFileDialog { Title = "Exportar tabela de soldos", Filter = "Arquivo CSV|*.csv", FileName = $"soldos_sigfur_{DateTime.Now:yyyyMMdd}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await _service.ExportCsvAsync(dialog.FileName, records);
        StatusText.Text = $"Tabela exportada: {dialog.FileName}";
    }

    private void SalaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SalaryGrid.SelectedItem is not SalaryRecord row) return;
        LoadEditor(row);
    }

    private void RankBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var rank = RankBox.SelectedItem?.ToString() ?? RankBox.Text;
        var row = _rows.FirstOrDefault(x => x.Rank.Equals(rank, StringComparison.CurrentCultureIgnoreCase));
        if (row is not null)
        {
            SalaryGrid.SelectedItem = row;
            SalaryGrid.ScrollIntoView(row);
            LoadEditor(row);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateMetrics();
    }

    private void ShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        UpdateMetrics();
    }

    private void SalaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SalaryGrid.SelectedItem is not SalaryRecord row) return;
        Clipboard.SetText($"{row.Rank}\t{row.SalaryText}");
        StatusText.Text = $"Linha copiada: {row.Rank} — {row.SalaryText}.";
    }

    private void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SalaryGrid.SelectedItem is SalaryRecord row) { LoadEditor(row); SalaryBox.Focus(); SalaryBox.SelectAll(); }
    }

    private void CopyRank_Click(object sender, RoutedEventArgs e) { if (SalaryGrid.SelectedItem is SalaryRecord row) Clipboard.SetText(row.Rank); }
    private void CopySalary_Click(object sender, RoutedEventArgs e) { if (SalaryGrid.SelectedItem is SalaryRecord row) Clipboard.SetText(row.Salary.ToString("N2", PtBr)); }
    private void CopyRow_Click(object sender, RoutedEventArgs e) { if (SalaryGrid.SelectedItem is SalaryRecord row) Clipboard.SetText($"{row.Rank}\t{row.SalaryText}\t{row.Official2026Text}\t{row.StatusText}"); }

    private void SalaryGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // A ordenação hierárquica inicial é customizada; ao clicar, o DataGrid assume a ordenação normal da coluna.
        if (_view is ListCollectionView listView) listView.CustomSort = null;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Save_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { New_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Delete) { Hide_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { await ReloadAsync((SalaryGrid.SelectedItem as SalaryRecord)?.Rank); e.Handled = true; }
    }

    private static string Normalize(string? value) => MilitaryRankService.Normalize(value);

    private sealed class SalaryComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not SalaryRecord left || y is not SalaryRecord right) return 0;
            var rank = MilitaryRankService.GetOrder(left.Rank).CompareTo(MilitaryRankService.GetOrder(right.Rank));
            return rank != 0 ? rank : string.Compare(left.Rank, right.Rank, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
