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
    private List<string> _lastSelectedBulletins = [];
    private bool _loading = true;
    private bool _resultControlsReady;
    private PaymentConferenceReviewWindow? _reviewWindow;

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
        YearBox.ItemsSource = Enumerable.Range(2000, DateTime.Today.Year + 2 - 2000 + 1).Reverse().ToList();
        MonthBox.SelectedIndex = DateTime.Today.Month - 1;
        YearBox.SelectedItem = DateTime.Today.Year;
        _resultControlsReady = true;
        _loading = false;
        UpdateHeader();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _settings = await _service.LoadSettingsAsync();
            MonthBox.SelectedIndex = Math.Clamp(_settings.Month, 1, 12) - 1;
            var savedYear = _settings.Year is >= 2000 and <= 2200 ? _settings.Year : DateTime.Today.Year;
            var years = ((IEnumerable<int>)YearBox.ItemsSource).Append(savedYear).Distinct().OrderDescending().ToList();
            YearBox.ItemsSource = years;
            YearBox.SelectedItem = savedYear;
            PaystubFolderBox.Text = string.IsNullOrWhiteSpace(_settings.PaystubFolder) ? App.Paths.PaystubsDirectory : _settings.PaystubFolder;
            RequirePrefixCheck.IsChecked = _settings.AcceptRubricPresence;
            VacationCheck.IsChecked = _settings.IncludeVacation;
            TransportCheck.IsChecked = _settings.IncludeTransportAid;
            GratCheck.IsChecked = _settings.IncludeGratification;
            QualificationCheck.IsChecked = _settings.IncludeQualification;
            OthersCheck.IsChecked = _settings.IncludeOthers;
            UpdateHeader();
        }
        catch (Exception ex) { ShowError(ex); }
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
                        BulletinCountText.Text = "Nenhum aditamento encontrado. Importe no Aditamento Furriel ou adicione um PDF avulso aqui.";
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
            var cache = await _service.LoadValidCacheAsync(selected, _settings);
            if (cache is not null)
            {
                var answer = SigfurDialog.Show(this,
                    $"Existe conferência salva válida para {_settings.Month:00}/{_settings.Year}.\n\nSalva em {cache.SavedAt:dd/MM/yyyy HH:mm}.\n\nUsar a última conferência em vez de recalcular agora?",
                    "Conferência de Pagamento",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    _lastSelectedBulletins = selected;
                    _lastResult = cache.Result;
                    LoadResult(cache.Result);
                    StatusText.Text = $"Resultado carregado do cache em {cache.SavedAt:dd/MM/yyyy HH:mm}.";
                    return;
                }
            }

            PaymentConferenceResult? result = null;
            await RunBusyAsync("Conferindo publicações x contracheques...", async progress =>
            {
                result = await _service.RunAsync(selected, _settings, progress);
            });
            if (result is null) return;
            _lastSelectedBulletins = selected;
            _lastResult = result;
            foreach (var bulletin in _bulletins)
                bulletin.ExpectedItems = result.ExpectedItems.Count(x => x.BulletinPath.Equals(bulletin.Path, StringComparison.OrdinalIgnoreCase));
            BulletinsGrid.Items.Refresh();
            LoadResult(result);
            try { await _service.SaveCacheAsync(result, selected, _settings); }
            catch (Exception ex) { await App.Log.WriteAsync("Falha ao salvar cache da Conferência de Pagamento.", ex); }
            StatusText.Text = $"Folha {_settings.Month:00}/{_settings.Year}: {result.PaystubFiles.Count} PDF(s), {_lastSelectedBulletins.Count} boletim(ns) · {result.Summary.Expected} item(ns), {result.Summary.Ok} OK, {result.Summary.Pending} para revisar, {result.Summary.NotApplicable} ato(s)/outra competência.";
            if (result.Warnings.Count > 0)
                SigfurDialog.Show(this, string.Join("\n", result.Warnings.Take(12)), "Conferência concluída com avisos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private PaymentConferenceSettings ReadSettingsFromUi()
    {
        var month = MonthBox.SelectedIndex >= 0 ? MonthBox.SelectedIndex + 1 : DateTime.Today.Month;
        if (YearBox.SelectedItem is not int year) throw new InvalidOperationException("Selecione o ano da folha.");
        var folder = _service.ResolvePaystubFolder(PaystubFolderBox.Text);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("A pasta dos contracheques não está disponível. Clique em Escolher pasta e selecione a pasta existente.\n\n" + folder);
        PaystubFolderBox.Text = folder;
        return new PaymentConferenceSettings
        {
            Month = month,
            Year = year,
            PaystubFolder = folder,
            RequirePrefix = true,
            AcceptRubricPresence = RequirePrefixCheck.IsChecked == true,
            IncludeVacation = VacationCheck.IsChecked == true,
            IncludeTransportAid = TransportCheck.IsChecked == true,
            IncludeGratification = GratCheck.IsChecked == true,
            IncludeQualification = QualificationCheck.IsChecked == true,
            IncludeOthers = OthersCheck.IsChecked == true
        };
    }

    private void LoadResult(PaymentConferenceResult result)
    {
        ApplyResultFilter();
        ScopeTextBlock.Text = result.ScopeText;
        PaystubFilesGrid.ItemsSource = result.PaystubFiles;
        _expected.Clear(); foreach (var item in result.ExpectedItems) _expected.Add(item);
        _rubrics.Clear(); foreach (var hit in result.RubricHits) _rubrics.Add(hit);
        ExpectedSummaryText.Text = result.Summary.ExpectedText;
        OkSummaryText.Text = result.Summary.OkText;
        PendingSummaryText.Text = result.Summary.PendingText;
        MissingPdfSummaryText.Text = result.Summary.MissingPaystubText;
        DivergentSummaryText.Text = result.Summary.MissingRubricText;
        ResultsGrid.SelectedIndex = _rows.Count > 0 ? 0 : -1;
        ExpectedGrid.SelectedIndex = _expected.Count > 0 ? 0 : -1;
    }

    private void ResultFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyResultFilter();
    private void ResultSearch_Changed(object sender, TextChangedEventArgs e) => ApplyResultFilter();
    private void ApplyResultFilter()
    {
        if (!_resultControlsReady) return;
        var search = ResultSearchBox.Text.Trim();
        var filter = ResultFilterBox.SelectedIndex;
        _rows.Clear();
        foreach (var row in _lastResult.Rows)
        {
            var include = filter switch
            {
                1 => row.Severity == "success",
                2 => row.Status is "SEM CONTRACHEQUE" or "NÃO ACHOU RUBRICA" or "VALOR DIVERGENTE" or "NATUREZA DIFERENTE",
                3 => row.Status == "REVISÃO NECESSÁRIA",
                4 => row.Status is "ATO CADASTRAL" or "OUTRA COMPETÊNCIA",
                5 => !row.IsVerified,
                6 => row.IsVerified,
                _ => true
            };
            var searchable = string.Join(" ", row.Military, row.Cpf, row.PrecCp, row.ExpectedCodesText, row.RubricsFound, row.Notes);
            if (include && (search.Length == 0 || searchable.Contains(search, StringComparison.CurrentCultureIgnoreCase))) _rows.Add(row);
        }
        ResultsGrid.SelectedIndex = _rows.Count > 0 ? 0 : -1;
    }

    private async void LoadLastConference_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = _bulletins.Where(x => x.Selected && x.Exists).Select(x => x.Path).ToList();
            if (selected.Count == 0)
            {
                SigfurDialog.Show(this, "Marque os mesmos Aditamentos do Furriel usados na conferência salva.", "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _settings = ReadSettingsFromUi();
            var cache = await _service.LoadValidCacheAsync(selected, _settings);
            if (cache is null)
            {
                StatusText.Text = "Não há cache válido para esta seleção.";
                SigfurDialog.Show(this, "Nenhuma conferência salva válida foi encontrada para a competência, pasta e aditamentos selecionados.", "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _lastSelectedBulletins = selected;
            _lastResult = cache.Result;
            LoadResult(cache.Result);
            StatusText.Text = $"Resultado carregado do cache em {cache.SavedAt:dd/MM/yyyy HH:mm}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void SaveConference_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult.Rows.Count == 0)
        {
            SigfurDialog.Show(this, "Faça uma conferência antes de salvar.", "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var selected = _lastSelectedBulletins.Count > 0 ? _lastSelectedBulletins : _bulletins.Where(x => x.Selected && x.Exists).Select(x => x.Path).ToList();
            if (selected.Count == 0)
            {
                SigfurDialog.Show(this, "Não foi possível identificar os aditamentos usados nesta conferência.", "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var path = await _service.SaveCacheAsync(_lastResult, selected, _settings);
            StatusText.Text = $"Conferência salva em cache: {path}";
        }
        catch (Exception ex) { ShowError(ex); }
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

    private void BulletinsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject source) return;
        if (VisualTreeUtilities.FindAncestor<CheckBox>(source) is not null) return;
        var row = VisualTreeUtilities.FindAncestor<DataGridRow>(source);
        if (row?.Item is not PaymentConferenceBulletinFile file) return;
        file.Selected = !file.Selected;
        BulletinsGrid.SelectedItem = file;
        row.IsSelected = true;
        BulletinsGrid.Items.Refresh();
    }

    private void BulletinsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BulletinsGrid.SelectedItem is PaymentConferenceBulletinFile file)
            StatusText.Text = $"Selecionado: {file.Display} · {file.OriginalName}";
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ResultDetailBox.Text = (ResultsGrid.SelectedItem as PaymentConferenceResultRow)?.DetailText ?? string.Empty;
        if (_reviewWindow is not null && ResultsGrid.SelectedItem is PaymentConferenceResultRow row)
            _reviewWindow.ShowRows(_lastResult.Rows, row);
    }

    private void ExpectedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ExpectedDetailBox.Text = (ExpectedGrid.SelectedItem as PaymentConferenceExpectedItem)?.Context ?? string.Empty;

    private void BulletinsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedBulletin_Click(sender, e);
    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenPaystub_Click(sender, e);
    private void ExpectedGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenExpectedBulletin_Click(sender, e);

    private async void OpenSelectedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (BulletinsGrid.SelectedItem is not PaymentConferenceBulletinFile file || !file.Exists) return;
        var review = GetReviewWindow();
        var selected = _lastResult.Rows.FirstOrDefault(r => r.BulletinPath == file.Path);
        if (selected is not null) review.ShowRows(_lastResult.Rows, selected);
        else await review.ShowBulletinAsync(file.Path);
    }

    private void RevealSelectedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (BulletinsGrid.SelectedItem is PaymentConferenceBulletinFile file) ShellService.RevealInExplorer(file.Path);
    }

    private void OpenPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PaymentConferenceResultRow row) GetReviewWindow().ShowRows(_lastResult.Rows, row);
    }

    private void RevealPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PaymentConferenceResultRow row) ShellService.RevealInExplorer(row.PaystubPath);
    }

    private void OpenBulletinFromResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PaymentConferenceResultRow row) GetReviewWindow().ShowRows(_lastResult.Rows, row);
    }

    private void OpenExpectedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (ExpectedGrid.SelectedItem is PaymentConferenceExpectedItem item)
        {
            var row = _lastResult.Rows.FirstOrDefault(r => r.BulletinPath == item.BulletinPath && r.Cpf == item.Cpf && r.BulletinPage == item.Page);
            if (row is not null) GetReviewWindow().ShowRows(_lastResult.Rows, row);
        }
    }

    private PaymentConferenceReviewWindow GetReviewWindow()
    {
        if (_reviewWindow is null)
        {
            _reviewWindow = new PaymentConferenceReviewWindow(SaveVerificationAsync) { Owner = this };
            _reviewWindow.Closed += (_, _) => _reviewWindow = null;
            _reviewWindow.Show();
        }
        _reviewWindow.Activate();
        return _reviewWindow;
    }

    private async Task SaveVerificationAsync(PaymentConferenceResultRow row, bool verified)
    {
        await _service.SetVerifiedAsync(row, verified);
        if (ResultFilterBox.SelectedIndex is 5 or 6) ApplyResultFilter();
        StatusText.Text = $"{_lastResult.Rows.Count(r => r.IsVerified)}/{_lastResult.Rows.Count} itens verificados · marcação salva.";
        ResultDetailBox.Text = (ResultsGrid.SelectedItem as PaymentConferenceResultRow)?.DetailText ?? "";
    }
    private async void VerifyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PaymentConferenceResultRow row } button) return;
        button.IsEnabled = false;
        try { await SaveVerificationAsync(row, !row.IsVerified); }
        catch (Exception ex) { ShowError(ex); }
        finally { button.IsEnabled = true; }
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
        foreach (var row in _lastResult.Rows.Where(x => x.Status != "ACHOU RUBRICA"))
            sb.AppendLine($"- {row.Status}: {row.Military} · {row.PaymentType}/{row.PaymentMode} · {row.Notes}");
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

    private void MonthYear_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_resultControlsReady) return;
        UpdateHeader();
        _lastResult = new PaymentConferenceResult();
        _lastSelectedBulletins.Clear();
        _reviewWindow?.Close();
        LoadResult(_lastResult);
        StatusText.Text = "Competência alterada. Os boletins marcados foram mantidos; clique em Conferir para comparar com a nova folha.";
    }

    private async void PaystubFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PaystubFilesGrid.SelectedItem is not PaymentConferencePaystubFile file || !File.Exists(file.Path)) return;
        var review = GetReviewWindow();
        var row = _lastResult.Rows.FirstOrDefault(r => r.PaystubPath == file.Path);
        if (row is not null) review.ShowRows(_lastResult.Rows, row);
        else await review.ShowPaystubAsync(file.Path);
    }
    private void UpdateHeader()
    {
        var month = MonthBox.SelectedIndex >= 0 ? MonthBox.SelectedIndex + 1 : DateTime.Today.Month;
        var year = YearBox.SelectedItem is int selectedYear ? selectedYear : DateTime.Today.Year;
        HeaderBadge.Text = $"Folha selecionada: {month:00}/{year}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Não associe Esc a Close(): a conferência mantém bastante estado em memória
        // e visualizadores de PDF podem enviar essa tecla de forma atrasada.
        if (e.Key == Key.F5) _ = ReloadBulletinsAsync();
    }

    private void ShowError(Exception ex)
        => SigfurDialog.Show(this, ex.Message, "Conferência de Pagamento", MessageBoxButton.OK, MessageBoxImage.Error);
}
