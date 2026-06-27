using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class PaymentConferenceWindow : Window
{
    private readonly PaymentConferenceService _service;
    private readonly ObservableCollection<PaymentConferenceBulletinFile> _bulletins = [];
    private readonly ObservableCollection<PaymentConferenceResultRow> _rows = [];
    private readonly ObservableCollection<PaymentConferenceExpectedItem> _expected = [];
    private readonly ObservableCollection<PaymentConferenceRubricHit> _rubrics = [];
    private PaymentConferenceSettings _settings = new();
    private PaymentConferenceResult _lastResult = new();
    private bool _loading;

    private static readonly string[] Months =
    [
        "01 - Janeiro", "02 - Fevereiro", "03 - Março", "04 - Abril", "05 - Maio", "06 - Junho",
        "07 - Julho", "08 - Agosto", "09 - Setembro", "10 - Outubro", "11 - Novembro", "12 - Dezembro"
    ];

    public PaymentConferenceWindow(PaymentConferenceService service)
    {
        InitializeComponent();
        _service = service;
        BulletinsGrid.ItemsSource = _bulletins;
        ResultsGrid.ItemsSource = _rows;
        ExpectedGrid.ItemsSource = _expected;
        RubricsGrid.ItemsSource = _rubrics;
        MonthBox.ItemsSource = Months;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _settings = await _service.LoadSettingsAsync();
            MonthBox.SelectedIndex = Math.Clamp(_settings.Month, 1, 12) - 1;
            YearBox.Text = _settings.Year <= 0 ? DateTime.Today.Year.ToString() : _settings.Year.ToString();
            PaystubFolderBox.Text = string.IsNullOrWhiteSpace(_settings.PaystubFolder) ? App.Paths.PaystubsDirectory : _settings.PaystubFolder;
            RequirePrefixCheck.IsChecked = _settings.RequirePrefix;
            VacationCheck.IsChecked = _settings.IncludeVacation;
            TransportCheck.IsChecked = _settings.IncludeTransportAid;
            GratCheck.IsChecked = _settings.IncludeGratification;
            QualificationCheck.IsChecked = _settings.IncludeQualification;
            OthersCheck.IsChecked = _settings.IncludeOthers;
            UpdateHeader();
        }
        finally { _loading = false; }
        await ReloadBulletinsAsync();
    }

    private async Task ReloadBulletinsAsync()
    {
        try
        {
            await RunBusyAsync("Carregando aditamentos do Furriel...", async progress =>
            {
                var files = await _service.LoadFurrielBulletinsAsync();
                Dispatcher.Invoke(() =>
                {
                    _bulletins.Clear();
                    foreach (var file in files)
                    {
                        file.Selected = false;
                        _bulletins.Add(file);
                    }
                    BulletinCountText.Text = $"{_bulletins.Count} aditamento(s) indexado(s) no módulo do Furriel.";
                    if (_bulletins.Count == 0)
                        BulletinCountText.Text = "Nenhum aditamento encontrado. Importe no Boletim Furriel ou adicione PDF avulso aqui.";
                });
                await Task.CompletedTask;
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ReloadBulletins_Click(object sender, RoutedEventArgs e) => await ReloadBulletinsAsync();

    private async void AddPdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Adicionar Aditamento do Furriel para conferência",
            Filter = "Arquivos PDF (*.pdf)|*.pdf",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await RunBusyAsync("Lendo PDF(s) avulso(s)...", async progress =>
            {
                for (var i = 0; i < dialog.FileNames.Length; i++)
                {
                    progress.Report($"Lendo {i + 1}/{dialog.FileNames.Length}: {System.IO.Path.GetFileName(dialog.FileNames[i])}");
                    var file = await _service.BuildFileFromPdfAsync(dialog.FileNames[i]);
                    file.Selected = true;
                    Dispatcher.Invoke(() =>
                    {
                        if (!_bulletins.Any(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                            _bulletins.Insert(0, file);
                    });
                }
            });
            BulletinCountText.Text = $"{_bulletins.Count} aditamento(s) disponível(is).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RunConference_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = _bulletins.Where(x => x.Selected && x.Exists).Select(x => x.Path).ToList();
            if (selected.Count == 0)
            {
                SigfurDialog.Show(this, "Marque pelo menos um Aditamento do Furriel para conferir.", "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _settings = ReadSettingsFromUi();
            await _service.SaveSettingsAsync(_settings);

            PaymentConferenceResult? result = null;
            await RunBusyAsync("Conferindo publicações x contracheques...", async progress =>
            {
                result = await _service.RunAsync(selected, _settings, progress);
            });
            if (result is null) return;
            _lastResult = result;
            foreach (var bulletin in _bulletins)
                bulletin.ExpectedItems = result.ExpectedItems.Count(x => x.BulletinPath.Equals(bulletin.Path, StringComparison.OrdinalIgnoreCase));
            BulletinsGrid.Items.Refresh();
            LoadResult(result);
            StatusText.Text = $"Conferência concluída: {result.Summary.Expected} item(ns), {result.Summary.Ok} OK, {result.Summary.Pending} pendência(s).";
            if (result.Warnings.Count > 0)
                SigfurDialog.Show(this, string.Join("\n", result.Warnings.Take(12)), "Conferência concluída com avisos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private PaymentConferenceSettings ReadSettingsFromUi()
    {
        var month = MonthBox.SelectedIndex >= 0 ? MonthBox.SelectedIndex + 1 : DateTime.Today.Month;
        if (!int.TryParse(YearBox.Text, out var year) || year < 2000) year = DateTime.Today.Year;
        return new PaymentConferenceSettings
        {
            Month = month,
            Year = year,
            PaystubFolder = PaystubFolderBox.Text.Trim(),
            RequirePrefix = RequirePrefixCheck.IsChecked == true,
            IncludeVacation = VacationCheck.IsChecked == true,
            IncludeTransportAid = TransportCheck.IsChecked == true,
            IncludeGratification = GratCheck.IsChecked == true,
            IncludeQualification = QualificationCheck.IsChecked == true,
            IncludeOthers = OthersCheck.IsChecked == true
        };
    }

    private void LoadResult(PaymentConferenceResult result)
    {
        _rows.Clear(); foreach (var row in result.Rows) _rows.Add(row);
        _expected.Clear(); foreach (var item in result.ExpectedItems) _expected.Add(item);
        _rubrics.Clear(); foreach (var hit in result.RubricHits) _rubrics.Add(hit);
        ExpectedSummaryText.Text = result.Summary.ExpectedText;
        OkSummaryText.Text = result.Summary.OkText;
        PendingSummaryText.Text = result.Summary.PendingText;
        MissingPdfSummaryText.Text = result.Summary.MissingPaystubText;
        DivergentSummaryText.Text = result.Summary.DivergentText;
        ResultsGrid.SelectedIndex = _rows.Count > 0 ? 0 : -1;
        ExpectedGrid.SelectedIndex = _expected.Count > 0 ? 0 : -1;
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult.Rows.Count == 0)
        {
            SigfurDialog.Show(this, "Faça uma conferência antes de exportar.", "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var path = await _service.ExportCsvAsync(_lastResult);
            StatusText.Text = $"CSV gerado: {path}";
            ShellService.RevealInExplorer(path);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ChoosePaystubFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pasta com os contracheques da competência",
            InitialDirectory = Directory.Exists(PaystubFolderBox.Text) ? PaystubFolderBox.Text : App.Paths.PaystubsDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) PaystubFolderBox.Text = dialog.FolderName;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _bulletins) item.Selected = true;
        BulletinsGrid.Items.Refresh();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _bulletins) item.Selected = false;
        BulletinsGrid.Items.Refresh();
    }

    private void BulletinsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BulletinsGrid.SelectedItem is PaymentConferenceBulletinFile file)
            StatusText.Text = $"Selecionado: {file.Display} · {file.OriginalName}";
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ResultDetailBox.Text = (ResultsGrid.SelectedItem as PaymentConferenceResultRow)?.DetailText ?? string.Empty;

    private void ExpectedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ExpectedDetailBox.Text = (ExpectedGrid.SelectedItem as PaymentConferenceExpectedItem)?.Context ?? string.Empty;

    private void BulletinsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedBulletin_Click(sender, e);
    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenPaystub_Click(sender, e);
    private void ExpectedGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenExpectedBulletin_Click(sender, e);

    private void OpenSelectedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (BulletinsGrid.SelectedItem is PaymentConferenceBulletinFile file && file.Exists) ShellService.OpenPath(file.Path);
    }

    private void RevealSelectedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (BulletinsGrid.SelectedItem is PaymentConferenceBulletinFile file) ShellService.RevealInExplorer(file.Path);
    }

    private void OpenPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PaymentConferenceResultRow row) _service.OpenPaystubAtRow(row);
    }

    private void RevealPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PaymentConferenceResultRow row) ShellService.RevealInExplorer(row.PaystubPath);
    }

    private void OpenBulletinFromResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PaymentConferenceResultRow row) _service.OpenBulletinAtRow(row);
    }

    private void OpenExpectedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (ExpectedGrid.SelectedItem is PaymentConferenceExpectedItem item) _service.OpenBulletinAtItem(item);
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult.Rows.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("CONFERÊNCIA DE PAGAMENTO");
        sb.AppendLine($"Competência: {_settings.Month:00}/{_settings.Year}");
        sb.AppendLine($"Itens publicados: {_lastResult.Summary.Expected}");
        sb.AppendLine($"OK: {_lastResult.Summary.Ok}");
        sb.AppendLine($"Pendências: {_lastResult.Summary.Pending}");
        sb.AppendLine();
        foreach (var row in _lastResult.Rows.Where(x => x.Status != "OK" && x.Status != "ACHOU RUBRICA"))
            sb.AppendLine($"- {row.Status}: {row.Military} · {row.PaymentType}/{row.PaymentMode} · Esperado {row.ExpectedAmountText} · Recebido {row.PaidAmountText} · {row.Notes}");
        Clipboard.SetText(sb.ToString());
        StatusText.Text = "Resumo copiado para a área de transferência.";
    }

    private async Task RunBusyAsync(string initialStatus, Func<IProgress<string>, Task> action)
    {
        IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        StatusText.Text = initialStatus;
        var progress = new Progress<string>(message => StatusText.Text = message);
        try { await action(progress); }
        finally { BusyBar.Visibility = Visibility.Collapsed; IsEnabled = true; }
    }

    private void MonthYear_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateHeader(); }
    private void YearBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) UpdateHeader(); }
    private void UpdateHeader()
    {
        var month = MonthBox.SelectedIndex >= 0 ? MonthBox.SelectedIndex + 1 : DateTime.Today.Month;
        HeaderBadge.Text = $"Competência: {month:00}/{YearBox.Text}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        if (e.Key == Key.F5) _ = ReloadBulletinsAsync();
    }

    private void ShowError(Exception ex)
        => SigfurDialog.Show(this, ex.Message, "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Error);
}
