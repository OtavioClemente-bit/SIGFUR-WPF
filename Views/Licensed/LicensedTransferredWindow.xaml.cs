using Microsoft.Win32;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Licensed;

public partial class LicensedTransferredWindow : Window
{
    private readonly LicensedTransferredRepository _repository;
    private readonly LicensedTransferredSpreadsheetService _spreadsheets;
    private readonly PaystubService _paystubs;
    private List<LicensedTransferredRecord> _records = [];
    private readonly List<LicensedTransferredRecord> _allRecords = [];
    private readonly Dictionary<int, SearchIndexEntry> _searchIndex = [];
    private readonly DispatcherTimer _searchTimer;
    private LicensedTransferredPreferences _preferences = new();
    private string _sortProperty = nameof(LicensedTransferredRecord.Rank);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private bool _initializing = true;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _paystubCts;
    private int _loadVersion;

    public LicensedTransferredWindow(LicensedTransferredRepository repository, LicensedTransferredSpreadsheetService spreadsheets, PaystubService paystubs)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository; _spreadsheets = spreadsheets; _paystubs = paystubs;
        MilitaryGrid.ItemsSource = _records;
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            _previewCts?.Cancel();
            _paystubCts?.Cancel();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowLoadingCard("Abrindo Licenciados/Transferidos", "Preparando a tela sem travar o programa...");
            await Task.Yield();
            _preferences = await App.Json.LoadAsync<LicensedTransferredPreferences>(App.Paths.LicensedTransferredSettingsFile) ?? new LicensedTransferredPreferences();
            ShowHiddenBox.IsChecked = _preferences.ShowHidden;
            _initializing = false;
            await LoadAsync();
            SearchBox.Focus();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task LoadAsync(int selectId = 0)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        try
        {
            ShowLoadingCard("Carregando registros", "Lendo a lista em segundo plano...");
            StatusText.Text = "Carregando licenciados e transferidos...";
            await Task.Yield();

            // Carrega uma única vez, inclusive os arquivados. A pesquisa e a opção
            // de exibição passam a trabalhar sobre esta lista em memória.
            var items = await Task.Run(
                () => _repository.GetAllAsync(includeHidden: true, filter: null, token), token);

            if (token.IsCancellationRequested || version != _loadVersion) return;

            _allRecords.Clear();
            _allRecords.AddRange(items);
            RebuildSearchIndex();
            ApplyFilter(selectId);

            // A leitura dos arquivos acontece somente após uma recarga real do banco,
            // e não mais a cada letra digitada.
            _paystubCts?.Cancel();
            _paystubCts?.Dispose();
            _paystubCts = new CancellationTokenSource();
            _ = UpdateSavedPaystubIndicatorsAsync(_allRecords.ToList(), _paystubCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                HideLoadingCard();
            }
            cts.Dispose();
        }
    }

    private void ReplaceRecords(IReadOnlyList<LicensedTransferredRecord> items)
    {
        MilitaryGrid.SelectionChanged -= MilitaryGrid_SelectionChanged;
        try
        {
            _records = items.ToList();
            MilitaryGrid.ItemsSource = _records;
        }
        finally
        {
            MilitaryGrid.SelectionChanged += MilitaryGrid_SelectionChanged;
        }
    }

    private void ApplyFilter(int selectId = 0)
    {
        var query = Normalize(SearchBox.Text);
        var queryDigits = MilitaryFormatting.Digits(SearchBox.Text);
        var includeHidden = ShowHiddenBox.IsChecked == true;

        var filtered = _allRecords.Where(item =>
        {
            if (!includeHidden && !item.IsVisible) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (!_searchIndex.TryGetValue(item.Id, out var index))
            {
                index = CreateSearchIndex(item);
                _searchIndex[item.Id] = index;
            }
            return index.Text.Contains(query)
                   || (!string.IsNullOrWhiteSpace(queryDigits) && index.Digits.Contains(queryDigits));
        }).ToList();

        filtered.Sort(new LicensedRecordComparer(_sortProperty, _sortDirection));
        ReplaceRecords(filtered);
        UpdateSortGlyphs();
        TotalText.Text = _allRecords.Count.ToString(CultureInfo.InvariantCulture);
        VisibleText.Text = _records.Count.ToString(CultureInfo.InvariantCulture);

        if (selectId != 0)
        {
            var selected = _records.FirstOrDefault(x => x.Id == selectId);
            if (selected is not null)
            {
                MilitaryGrid.SelectedItem = selected;
                MilitaryGrid.ScrollIntoView(selected);
            }
        }

        UpdateSelectionSummary();
        StatusText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{_records.Count} registro(s) exibido(s)."
            : $"Pesquisa concluída: {_records.Count} registro(s) encontrado(s).";
    }

    private void RebuildSearchIndex()
    {
        _searchIndex.Clear();
        foreach (var item in _allRecords) _searchIndex[item.Id] = CreateSearchIndex(item);
    }

    private static SearchIndexEntry CreateSearchIndex(LicensedTransferredRecord item)
    {
        var text = Normalize($"{item.Rank} {item.Name} {item.WarName} {item.Cpf} {item.PrecCp} {item.MilitaryId} {item.Reason} {item.Destination} {item.FormationYear} {item.Phone} {item.Email} {item.Education} {item.Address} {item.ZipCode}");
        var digits = MilitaryFormatting.Digits($"{item.Cpf} {item.PrecCp} {item.MilitaryId} {item.ZipCode} {item.Phone}");
        return new SearchIndexEntry(text, digits);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray())
            .ToLowerInvariant();
    }

    private readonly record struct SearchIndexEntry(string Text, string Digits);

    private void ShowLoadingCard(string title, string message)
    {
        LoadingOverlay.Title = title;
        LoadingOverlay.Message = message;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateLoadingCard(string message)
    {
        if (LoadingOverlay.Visibility == Visibility.Visible) LoadingOverlay.Message = message;
    }

    private void HideLoadingCard()
    {
        if (LoadingOverlay.Visibility == Visibility.Visible) LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _searchTimer.Stop(); _searchTimer.Start(); }
    private async void ShowHiddenBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        _preferences.ShowHidden = ShowHiddenBox.IsChecked == true;
        await App.Json.SaveAsync(App.Paths.LicensedTransferredSettingsFile, _preferences);
        ApplyFilter();
    }

    private List<LicensedTransferredRecord> SelectedMany => MilitaryGrid.SelectedItems.Cast<LicensedTransferredRecord>().ToList();
    private LicensedTransferredRecord? Selected => MilitaryGrid.SelectedItem as LicensedTransferredRecord;

    private void MilitaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionSummary();
    private void MilitaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = VisualTreeUtilities.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        var record = cell?.DataContext as LicensedTransferredRecord ?? Selected;
        if (cell is null || record is null) return;

        var header = cell.Column.Header?.ToString() ?? string.Empty;
        var value = header switch
        {
            "P/G" => record.ShortRank,
            "Nome completo" => record.Name,
            "PREC-CP" => record.FormattedPrecCp,
            "CPF" => record.FormattedCpf,
            "Motivo" => record.Reason,
            "Destino / OM" => record.Destination,
            "Ano" => record.FormationYear,
            "Status" => record.StatusText,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(value)) return;
        Clipboard.SetText(value);
        StatusText.Text = $"{header} copiado: {value}";
        e.Handled = true;
    }

    private void UpdateSelectionSummary()
    {
        SelectedText.Text = MilitaryGrid.SelectedItems.Count.ToString(CultureInfo.InvariantCulture);
        var item = Selected;
        if (item is null)
        {
            SelectedRankText.Text = string.Empty;
            SelectedNameText.FullName = "Nenhum militar selecionado"; SelectedNameText.WarName = string.Empty;
            SelectedIdentityText.Text = "Selecione uma linha para ver a carteira resumida."; SelectedSituationText.Text = "—"; SelectedDetailsText.Text = string.Empty; PreviewImage.Source = null; return;
        }
        SelectedRankText.Text = item.ShortRank;
        SelectedNameText.FullName = item.Name; SelectedNameText.WarName = item.WarName;
        SelectedIdentityText.Text = $"CPF {item.FormattedCpf}  •  PREC-CP {item.FormattedPrecCp}  •  IDT {item.MilitaryId}";
        SelectedSituationText.Text = $"{item.Reason}\nDestino: {item.Destination}\nStatus: {item.StatusText}";
        SelectedDetailsText.Text = $"Contracheques salvos: {item.SavedPaystubsText}\n\nTelefone: {item.Phone}\nE-mail: {item.Email}\nEscolaridade: {item.Education}\n\nNascimento: {item.BirthDate}\nData de praça: {item.EnlistmentDate}\nAno de formação: {item.FormationYear}\n\nEndereço: {item.Address}\nCEP: {item.ZipCode}\n\nBanco: {item.Bank}\nAgência: {item.Agency}\nConta: {item.Account}";
        LoadPreview(item.PhotoPath);
    }

    private async Task UpdateSavedPaystubIndicatorsAsync(IReadOnlyList<LicensedTransferredRecord> items, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paystubs = await _paystubs.FindForMilitaryAsync(item.ToMilitaryRecord(), cancellationToken);
                item.SavedPaystubsText = paystubs.Count == 0 ? "Nenhum salvo" : $"{paystubs.Count} salvo(s)";
            }
            UpdateSelectionSummary();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async void LoadPreview(string path)
    {
        PreviewImage.Source = null;
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var image = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 360;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }, token);
            if (!token.IsCancellationRequested) PreviewImage.Source = image;
        }
        catch { }
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var editor = new LicensedTransferredEditorWindow(_repository, new LicensedTransferredRecord()) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(editor.SavedId);
    }
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { NotifySelection(); return; }
        var editor = new LicensedTransferredEditorWindow(_repository, Selected) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(editor.SavedId);
    }
    private async void Wallet_Click(object sender, RoutedEventArgs e) => await OpenWalletAsync();
    private async Task OpenWalletAsync(int tab = 0)
    {
        if (Selected is null) { NotifySelection(); return; }
        var id = Selected.Id;
        var wallet = new LicensedTransferredWalletWindow(_repository, _paystubs, Selected, tab) { Owner = this };
        wallet.Closed += async (_, _) =>
        {
            if (wallet.Changed) await LoadAsync(wallet.Restored ? 0 : id);
        };
        wallet.Show();
        wallet.Activate();
    }

    private void Paystub_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        if (selected.Count == 0 && Selected is not null) selected.Add(Selected);
        if (selected.Count == 0) { NotifySelection(); return; }

        var records = selected.DistinctBy(x => x.Id).ToList();
        var military = records.Select(x => x.ToMilitaryRecord()).ToList();
        var window = new PaystubCenterWindow(
            App.MilitaryRepository,
            _paystubs,
            military,
            initialTab: records.Count > 1 ? 1 : 0,
            restrictedToSelection: true,
            licensedTransferredScope: true) { Owner = this };
        window.Closed += async (_, _) =>
        {
            _paystubs.InvalidateCache();
            await UpdateSavedPaystubIndicatorsAsync(records, CancellationToken.None);
            StatusText.Text = "Central fechada. Contracheques salvos atualizados.";
        };
        StatusText.Text = records.Count == 1
            ? $"Central aberta para {records[0].WarName}. Escolha o mês e o ano."
            : $"Central aberta para {records.Count} licenciados/transferidos selecionados.";
        window.Show();
        window.Activate();
    }

    private async void FinancialStatement_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { NotifySelection(); return; }
        var item = Selected;
        var prompt = new TextPromptWindow(
            "Ficha Financeira",
            "Informe o ano desejado. De 2021 em diante será usada a Área UA/SIPPES; para anos anteriores será usada a ficha financeira antiga do CPEx.",
            DateTime.Today.Year.ToString(CultureInfo.InvariantCulture)) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!int.TryParse(MilitaryFormatting.Digits(prompt.Value), out var year) || year is < 2000 or > 2200)
        {
            StatusText.Text = "Informe um ano válido.";
            return;
        }
        try
        {
            StatusText.Text = $"Baixando ficha financeira de {item.WarName} ({year})...";
            var result = await App.CpexPaystubs.DownloadLatestFinancialStatementForMilitaryAsync(item.ToMilitaryRecord(), year, openAfterDownload: true);
            _paystubs.InvalidateCache();
            StatusText.Text = result.Success
                ? $"Ficha financeira {result.Year} salva e aberta: {Path.GetFileName(result.FilePath)}"
                : result.Message;
            if (!result.Success) SigfurDialog.Show(this, result.Message, "Ficha financeira", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenPaystubFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { NotifySelection(); return; }
        var folder = PersonDocumentStorageService.PrepareRegisteredFolder(
            App.Paths, PersonDocumentStorageService.ResolveConfiguredRoot(App.Paths),
            Selected.Rank, Selected.Name, Selected.Cpf, Selected.PrecCp);
        Directory.CreateDirectory(folder); ShellService.OpenPath(folder);
    }
    private void CopyCpfDigits_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { NotifySelection(); return; }
        var cpf = MilitaryFormatting.Digits(Selected.Cpf);
        if (string.IsNullOrWhiteSpace(cpf))
        {
            SigfurDialog.Show(this, "CPF não informado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Clipboard.SetText(cpf);
        StatusText.Text = "CPF sem pontuação copiado.";
    }
    private void CopyFormat_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        if (selected.Count == 0 && Selected is not null) selected.Add(Selected);
        if (selected.Count == 0) selected = _records.ToList();
        if (selected.Count == 0) return;
        new LicensedCopyFormatWindow(selected, _preferences) { Owner = this }.ShowDialog();
    }

    private async void ToggleVisible_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedMany; if (list.Count == 0 && Selected is not null) list.Add(Selected); if (list.Count == 0) { NotifySelection(); return; }
        var makeVisible = list.Any(x => !x.IsVisible);
        await _repository.SetVisibleAsync(list.Select(x => x.Id), makeVisible);
        StatusText.Text = makeVisible ? "Registro(s) reexibido(s)." : "Registro(s) arquivado(s).";
        await LoadAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedMany; if (list.Count == 0 && Selected is not null) list.Add(Selected); if (list.Count == 0) { NotifySelection(); return; }
        if (SigfurDialog.Show(this, $"Restaurar {list.Count} militar(es) para a lista principal?\n\nDocumentos, certidões e histórico de Auxílio-Transporte serão preservados.", "Restaurar para ativos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var failures = new List<string>(); var restored = 0;
        foreach (var item in list)
        {
            try { await _repository.RestoreToActiveAsync(item); restored++; }
            catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
        }
        await LoadAsync();
        StatusText.Text = $"{restored} militar(es) restaurado(s).";
        if (failures.Count > 0) SigfurDialog.Show(this, string.Join("\n", failures), "Restauração parcial", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedMany; if (list.Count == 0 && Selected is not null) list.Add(Selected); if (list.Count == 0) { NotifySelection(); return; }
        if (SigfurDialog.Show(this, $"Excluir definitivamente {list.Count} registro(s) da lista de licenciados/transferidos?\n\nOs arquivos físicos já salvos não serão apagados.", "Excluir definitivamente", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _repository.DeleteAsync(list.Select(x => x.Id)); await LoadAsync();
    }

    private async void ExportTable_Click(object sender, RoutedEventArgs e)
    {
        var window = new LicensedExportWindow(_records.ToList(), SelectedMany) { Owner = this };
        if (window.ShowDialog() != true) return;

        var dialog = new SaveFileDialog
        {
            Title = "Exportar Licenciados / Transferidos",
            Filter = window.ExportExcel ? "Excel profissional|*.xlsx" : "CSV UTF-8|*.csv",
            DefaultExt = window.ExportExcel ? ".xlsx" : ".csv",
            AddExtension = true,
            FileName = window.ExportExcel ? "Licenciados_Transferidos.xlsx" : "Licenciados_Transferidos.csv",
            InitialDirectory = Directory.Exists(_preferences.LastExportDirectory) ? _preferences.LastExportDirectory : null
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (window.ExportExcel)
                await _spreadsheets.ExportXlsxAsync(dialog.FileName, window.Records, window.SelectedColumnKeys);
            else
                await _spreadsheets.ExportCsvAsync(dialog.FileName, window.Records, window.SelectedColumnKeys);

            _preferences.LastExportDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            await App.Json.SaveAsync(App.Paths.LicensedTransferredSettingsFile, _preferences);
            StatusText.Text = $"{window.Records.Count} registro(s) exportado(s) com {window.SelectedColumnKeys.Count} coluna(s).";
            ShellService.OpenPath(Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Importar Licenciados/Transferidos", Filter = "Planilhas e CSV|*.xlsx;*.csv;*.txt|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ShowLoadingCard("Importando planilha", "Lendo o arquivo sem travar a tela...");
            StatusText.Text = "Lendo arquivo de licenciados/transferidos...";
            await Task.Yield();

            var rows = await _spreadsheets.ImportAsync(dialog.FileName);
            if (rows.Count == 0) { StatusText.Text = "Nenhum registro válido encontrado."; return; }
            HideLoadingCard();
            if (SigfurDialog.Show(this, $"Importar {rows.Count} registro(s)?", "Confirmar importação", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            ShowLoadingCard("Salvando registros", $"Processando 0 de {rows.Count} registro(s)...");
            var failures = new List<string>();
            var count = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                UpdateLoadingCard($"Processando {i + 1} de {rows.Count}: {row.Name}");
                try { await _repository.SaveAsync(row); count++; }
                catch (Exception ex) { failures.Add($"{row.Name}: {ex.Message}"); }

                if (i % 20 == 0) await Task.Yield();
            }
            await LoadAsync();
            StatusText.Text = $"{count} registro(s) importado(s).";
            if (failures.Count > 0) SigfurDialog.Show(this, string.Join("\n", failures.Take(30)), "Importação parcial", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { HideLoadingCard(); }
    }
    private async void ExportWallet_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { NotifySelection(); return; }
        var dialog = new SaveFileDialog { Title = "Exportar carteira completa", Filter = "Arquivo ZIP|*.zip", FileName = $"Carteira_{Safe(Selected.Name)}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var paystubs = await _paystubs.FindForMilitaryAsync(Selected.ToMilitaryRecord());
        await _repository.ExportWalletAsync(Selected, dialog.FileName, paystubs); StatusText.Text = "Carteira completa exportada.";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { MilitaryGrid.UnselectAll(); Keyboard.ClearFocus(); UpdateSelectionSummary(); StatusText.Text = "Seleção limpa."; e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Enter && Selected is not null) { _ = OpenWalletAsync(); e.Handled = true; }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { New_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void MilitaryGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var property = string.IsNullOrWhiteSpace(e.Column.SortMemberPath)
            ? e.Column.Header?.ToString() ?? nameof(LicensedTransferredRecord.Name)
            : e.Column.SortMemberPath;
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortProperty = property;
        foreach (var column in MilitaryGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortDirection;
        ApplySort();
    }

    private void ApplySort()
    {
        if (CollectionViewSource.GetDefaultView(MilitaryGrid.ItemsSource) is not ListCollectionView view) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.CustomSort = new LicensedRecordComparer(_sortProperty, _sortDirection);
        }
        UpdateSortGlyphs();
    }

    private void UpdateSortGlyphs()
    {
        foreach (var column in MilitaryGrid.Columns)
            column.SortDirection = string.Equals(column.SortMemberPath, _sortProperty, StringComparison.Ordinal) ? _sortDirection : null;
    }

    private sealed class LicensedRecordComparer(string property, ListSortDirection direction) : IComparer, IComparer<LicensedTransferredRecord>
    {
        public int Compare(object? x, object? y)
            => Compare(x as LicensedTransferredRecord, y as LicensedTransferredRecord);

        public int Compare(LicensedTransferredRecord? left, LicensedTransferredRecord? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = property switch
            {
                nameof(LicensedTransferredRecord.Rank) => MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name),
                nameof(LicensedTransferredRecord.Name) => CompareText(left.Name, right.Name),
                nameof(LicensedTransferredRecord.Cpf) => CompareText(left.Cpf, right.Cpf),
                nameof(LicensedTransferredRecord.PrecCp) => CompareText(left.PrecCp, right.PrecCp),
                nameof(LicensedTransferredRecord.Reason) => CompareText(left.Reason, right.Reason),
                nameof(LicensedTransferredRecord.Destination) => CompareText(left.Destination, right.Destination),
                nameof(LicensedTransferredRecord.FormationYear) => CompareText(left.FormationYear, right.FormationYear),
                nameof(LicensedTransferredRecord.IsVisible) => left.IsVisible.CompareTo(right.IsVisible),
                _ => CompareText(left.Name, right.Name)
            };
            if (result == 0 && property != nameof(LicensedTransferredRecord.Rank))
                result = MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name);
            return direction == ListSortDirection.Descending ? -result : result;
        }

        private static int CompareText(string? left, string? right)
            => string.Compare(left, right, CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
    }

    private void NotifySelection() => StatusText.Text = "Selecione ao menos um militar.";
    private void ShowError(Exception ex) { StatusText.Text = ex.Message; SigfurDialog.Show(this, ex.Message, "SIGFUR — Licenciados/Transferidos", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static string Safe(string value) => string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Replace(' ', '_');
}
