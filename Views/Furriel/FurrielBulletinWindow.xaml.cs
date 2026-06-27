using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;
using SIGFUR.Wpf.Views.Intelligent;

namespace SIGFUR.Wpf.Views.Furriel;

public partial class FurrielBulletinWindow : Window
{
    private readonly FurrielBulletinService _service;
    private readonly ObservableCollection<FurrielBulletinFile> _visibleFiles = [];
    private readonly ObservableCollection<FurrielSearchResult> _results = [];
    private readonly ObservableCollection<FurrielMilitaryOption> _military = [];
    private List<FurrielMilitaryOption> _allMilitary = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _autoUpdateTimer;
    private FurrielIndexStore _index = new();
    private FurrielModuleSettings _moduleSettings = new();
    private bool _loaded;
    private bool _busy;
    private bool _autoUpdateRunning;
    private bool _suppressFilters;
    private string _lastSelectionArea = "files";

    public FurrielBulletinWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = new FurrielBulletinService(App.Paths, App.Settings, App.MilitaryRepository, App.Log);
        FilesGrid.ItemsSource = _visibleFiles;
        ResultsGrid.ItemsSource = _results;
        MilitaryBox.ItemsSource = _military;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            SearchNow();
        };
        _autoUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        // Monitoramento automático desativado: o Furriel só deve indexar PDFs importados/baixados por ação do usuário.
        Closed += (_, _) => _autoUpdateTimer.Stop();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            _suppressFilters = true;
            PersonnelSourceBox.ItemsSource = _service.PersonnelSources;
            MonthBox.ItemsSource = _service.MonthFilterLabels;
            _moduleSettings = await _service.LoadModuleSettingsAsync();
            PersonnelSourceBox.SelectedItem = _service.PersonnelSources.Contains(_moduleSettings.PersonnelSource)
                ? _moduleSettings.PersonnelSource
                : FurrielBulletinService.SourceActive;
            SearchUnregisteredCheck.IsChecked = _moduleSettings.SearchTextWhenNotRegistered;
            PeriodModeBox.SelectedIndex = _moduleSettings.PeriodMode.Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            MonthBox.SelectedIndex = Math.Clamp(_moduleSettings.Month, 0, Math.Max(0, _service.MonthFilterLabels.Count - 1));
            StartDatePicker.SelectedDate = _moduleSettings.StartDate ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            EndDatePicker.SelectedDate = _moduleSettings.EndDate ?? DateTime.Today;
            _moduleSettings.AutoUpdateFromLastFolder = false;
            AutoUpdateCheckBox.IsChecked = false;
            ConsequenceFilterBox.SelectedItem = ConsequenceFilterBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Content?.ToString(), _moduleSettings.ConsequenceFilter, StringComparison.OrdinalIgnoreCase))
                ?? ConsequenceFilterBox.Items[0];
            UpdatePeriodModeVisuals();
            _index = await _service.LoadIndexAsync();
            RefreshYearOptions();
            if (YearBox.Items.Contains(_moduleSettings.Year)) YearBox.SelectedItem = _moduleSettings.Year;
            _suppressFilters = false;
            await ReloadMilitaryAsync();
            RefreshFiles();
            UpdateMetrics();
            StatusText.Text = "Boletim Furriel pronto. Importe PDFs ou pesquise nos boletins já indexados.";
            UpdateAutoUpdateTimer();
            MilitaryBox.Focus();
        }
        catch (Exception ex)
        {
            _suppressFilters = false;
            ShowError(ex);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        _autoUpdateTimer.Stop();
        try
        {
            _moduleSettings.PersonnelSource = PersonnelSourceBox.SelectedItem as string ?? FurrielBulletinService.SourceActive;
            _moduleSettings.SearchTextWhenNotRegistered = SearchUnregisteredCheck.IsChecked == true;
            _moduleSettings.PeriodMode = SelectedPeriodMode();
            _moduleSettings.Month = Math.Max(0, MonthBox.SelectedIndex);
            _moduleSettings.Year = YearBox.SelectedItem as string ?? "Todos";
            _moduleSettings.StartDate = StartDatePicker.SelectedDate;
            _moduleSettings.EndDate = EndDatePicker.SelectedDate;
            _moduleSettings.AutoUpdateFromLastFolder = false;
            _moduleSettings.ConsequenceFilter = (ConsequenceFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
            await _service.SaveModuleSettingsAsync(_moduleSettings);
        }
        catch { }
    }

    private async Task ReloadMilitaryAsync()
    {
        if (!_loaded || _busy) return;
        var source = PersonnelSourceBox.SelectedItem as string ?? FurrielBulletinService.SourceActive;
        HeaderStateText.Text = "Lendo cadastro...";
        var rows = await _service.LoadMilitaryOptionsAsync(source);
        _allMilitary = rows.ToList();
        RefreshMilitarySuggestions(MilitaryBox.Text, openDropDown: false);
        HeaderStateText.Text = $"{_allMilitary.Count} nomes disponíveis";
        StatusText.Text = $"Fonte atual: {source}. {_allMilitary.Count} militar(es) disponível(is) para pesquisa.";
    }

    private async void PersonnelSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressFilters) return;
        try
        {
            MilitaryBox.SelectedItem = null;
            MilitaryBox.Text = string.Empty;
            await ReloadMilitaryAsync();
            ScheduleSearch();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e) => ScheduleSearch();
    private void SearchInput_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Tab) return;
        if (e.Key == Key.Escape) { MilitaryBox.IsDropDownOpen = false; return; }
        RefreshMilitarySuggestions(MilitaryBox.Text, openDropDown: e.Key != Key.Enter);
        if (e.Key == Key.Enter)
        {
            MilitaryBox.IsDropDownOpen = false;
            SearchNow();
        }
        else ScheduleSearch();
    }
    private void MilitaryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleSearch();
    private void RefreshMilitarySuggestions(string? text, bool openDropDown)
    {
        var typed = (text ?? string.Empty).Trim();
        var compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        var digits = Digits(typed);
        var matches = _allMilitary
            .Where(item => typed.Length == 0 ||
                compare.IndexOf(string.Join(" ", item.FullName, item.WarName, item.Cpf, item.Identity, item.PrecCp, item.Rank), typed,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
                (digits.Length >= 3 && Digits(string.Join(" ", item.Cpf, item.Identity, item.PrecCp)).Contains(digits, StringComparison.Ordinal)))
            .Take(35)
            .ToList();
        _military.Clear();
        foreach (var item in matches) _military.Add(item);
        if (!openDropDown || typed.Length == 0 || matches.Count == 0) return;
        MilitaryBox.IsDropDownOpen = true;
        MilitaryBox.Text = typed;
        if (MilitaryBox.Template.FindName("PART_EditableTextBox", MilitaryBox) is TextBox editor)
        {
            editor.CaretIndex = editor.Text.Length;
            editor.SelectionLength = 0;
        }
    }

    private string SelectedPeriodMode()
        => (PeriodModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Mês/Ano";

    private void UpdatePeriodModeVisuals()
    {
        if (MonthYearPanel is null || DateRangePanel is null) return;
        var interval = SelectedPeriodMode().Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase);
        MonthYearPanel.Visibility = interval ? Visibility.Collapsed : Visibility.Visible;
        DateRangePanel.Visibility = interval ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PeriodModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePeriodModeVisuals();
        if (_suppressFilters || !_loaded) return;
        RefreshFiles();
        ScheduleSearch();
    }

    private void AutoUpdateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _moduleSettings.AutoUpdateFromLastFolder = false;
        _autoUpdateTimer.Stop();
        if (AutoUpdateCheckBox is not null) AutoUpdateCheckBox.IsChecked = false;
    }

    private void SearchOption_Changed(object sender, RoutedEventArgs e) => ScheduleSearch();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilters || !_loaded) return;
        RefreshFiles();
        ScheduleSearch();
    }

    private void BulletinFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilters || !_loaded) return;
        RefreshFiles();
        ScheduleSearch();
    }

    private void ScheduleSearch()
    {
        if (!_loaded || _busy || _suppressFilters) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchNow_Click(object sender, RoutedEventArgs e) => SearchNow();

    private void SearchNow()
    {
        if (!_loaded || _busy) return;
        try
        {
            _searchTimer.Stop();
            var comboText = (MilitaryBox.Text ?? string.Empty).Trim();
            var freeText = (FreeSearchBox.Text ?? string.Empty).Trim();
            var selected = MilitaryBox.SelectedItem as FurrielMilitaryOption;
            selected ??= _service.FindBestMilitary(comboText, _allMilitary);
            // Se um militar foi resolvido/selecionado, o texto do campo Militar não entra como busca livre.
            // A busca livre só usa o campo próprio. Isso impede que uma nota coletiva retorne todos
            // os militares que aparecem no corpo quando o usuário queria apenas o militar escolhido.
            var query = selected is not null ? freeText : (freeText.Length > 0 ? freeText : comboText);
            if (selected is null && query.Length > 0 && SearchUnregisteredCheck.IsChecked != true) query = string.Empty;

            var consequenceFilter = (ConsequenceFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
            var rows = string.IsNullOrWhiteSpace(query) && selected is null && consequenceFilter == "Todos"
                ? new List<FurrielSearchResult>()
                : _service.Search(_index, query, selected, CurrentPeriod(), BulletinFilterBox.Text ?? string.Empty, consequenceFilter);
            _results.Clear();
            foreach (var row in rows) _results.Add(row);
            if (_results.Count > 0)
            {
                ResultsGrid.SelectedIndex = 0;
                ResultsGrid.ScrollIntoView(_results[0]);
                ShowResultDetail(_results[0]);
            }
            else
            {
                ResultDetailBox.Text = query.Length == 0 && selected is null
                    ? "Digite um militar, nome de guerra ou texto para pesquisar menções nos boletins filtrados."
                    : $"Nenhuma menção foi encontrada para “{query}” no período e nos boletins escolhidos.";
            }
            UpdateMetrics();
            StatusText.Text = _results.Count == 0
                ? "Pesquisa concluída sem resultados. Confira o período, o filtro de boletim e os PDFs importados."
                : $"{_results.Count} resultado(s) encontrado(s) em {_visibleFiles.Count} boletim(ns) visível(is).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private FurrielPeriodFilter CurrentPeriod()
    {
        var interval = SelectedPeriodMode().Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase);
        var month = !interval && MonthBox.SelectedIndex > 0 ? MonthBox.SelectedIndex.ToString("00", CultureInfo.InvariantCulture) : string.Empty;
        var year = !interval ? YearBox.SelectedItem as string : null;
        var start = interval ? StartDatePicker.SelectedDate : null;
        var end = interval ? EndDatePicker.SelectedDate : null;
        if (start is not null && end is not null && end < start) (start, end) = (end, start);
        return new FurrielPeriodFilter
        {
            Month = month,
            Year = year is null or "Todos" ? string.Empty : year,
            Start = start,
            End = end
        };
    }

    private void RefreshFiles()
    {
        if (!_loaded) return;
        var selectedId = (FilesGrid.SelectedItem as FurrielBulletinFile)?.Id;
        var files = _service.FilterFiles(_index, CurrentPeriod(), BulletinFilterBox.Text ?? string.Empty);
        _visibleFiles.Clear();
        foreach (var file in files) _visibleFiles.Add(file);
        var toSelect = selectedId is null ? _visibleFiles.FirstOrDefault() : _visibleFiles.FirstOrDefault(x => x.Id == selectedId) ?? _visibleFiles.FirstOrDefault();
        if (toSelect is not null)
        {
            FilesGrid.SelectedItem = toSelect;
            ShowFileDetail(toSelect);
        }
        else FileDetailBox.Text = "Nenhum boletim corresponde aos filtros atuais.";
        VisibleBulletinsText.Text = _visibleFiles.Count == 1 ? "1 boletim visível" : $"{_visibleFiles.Count} boletins visíveis";
        UpdateMetrics();
    }

    private void RefreshYearOptions()
    {
        var current = YearBox.SelectedItem as string ?? "Todos";
        var years = _index.Files
            .Select(x => ParseDate(x.Date)?.Year.ToString(CultureInfo.InvariantCulture))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderByDescending(x => x)
            .ToList();
        _suppressFilters = true;
        YearBox.ItemsSource = new[] { "Todos" }.Concat(years).ToList();
        YearBox.SelectedItem = years.Contains(current) ? current : "Todos";
        _suppressFilters = false;
    }

    private void UpdateMetrics()
    {
        IndexedCountText.Text = _index.Files.Count.ToString("N0");
        SignedCountText.Text = _index.SignedFiles.Values
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count().ToString("N0");
        PagesCountText.Text = _index.Files.Sum(x => Math.Max(0, x.Pages)).ToString("N0");
        ResultsCountText.Text = _results.Count.ToString("N0");
        UpdatedAtText.Text = string.IsNullOrWhiteSpace(_index.UpdatedAt) ? "—" : _index.UpdatedAt;
        HeaderStateText.Text = _busy ? "Processando..." : $"{_index.Files.Count} PDFs no índice";
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar PDFs e ZIPs do Boletim Furriel",
            Filter = "PDF e ZIP (*.pdf;*.zip)|*.pdf;*.zip|PDF (*.pdf)|*.pdf|ZIP (*.zip)|*.zip|Todos os arquivos (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportSourcesAsync(dialog.FileNames, false);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecionar pasta com PDFs/ZIPs do Boletim Furriel",
            InitialDirectory = Directory.Exists(_service.ModuleDirectory) ? _service.ModuleDirectory : App.Paths.DataDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportSourcesAsync([dialog.FolderName], false);
    }

    private async void ImportSigned_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Salvar PDF(s) assinado(s) do Furriel",
            Filter = "PDF e ZIP (*.pdf;*.zip)|*.pdf;*.zip|PDF (*.pdf)|*.pdf|ZIP (*.zip)|*.zip",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportSourcesAsync(dialog.FileNames, true);
    }

    private async void DownloadSisbolFurriel_Click(object sender, RoutedEventArgs e)
    {
        var initialYear = ResolveDownloadYear();
        var initialMonth = ResolveDownloadMonthFromFilter();
        var range = SisbolDownloadRangeDialog.Ask(this, "Aditamento do Furriel — SisBol", initialYear, initialMonth);
        if (range is null) return;
        var year = range.Year;

        if (SigfurDialog.Show(this,
                $"Baixar/atualizar automaticamente os Aditamentos do Furriel de {range.ScopeText}?\n\n" +
                "O SIGFUR usará a sessão já preparada do SisBol. O navegador ficará oculto e os PDFs serão indexados automaticamente no Boletim Furriel.",
                "Baixar Aditamento do Furriel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            SisbolBulletinDownloadResult? downloaded = null;
            var downloadDir = Path.Combine(_service.TempDirectory, "sisbol_aditamento_furriel", year.ToString(CultureInfo.InvariantCulture));
            await RunBusyAsync("Baixando Aditamentos do Furriel no SisBol", async progress =>
            {
                downloaded = await App.Sisbol.DownloadGeneratedBulletinsAsync(3, year, downloadDir, replaceExisting: true, progress, CancellationToken.None, range.Month);
                if (downloaded.DownloadedFiles.Count > 0)
                {
                    await _service.ImportAsync(_index, downloaded.DownloadedFiles, false, null, progress);
                    _index = await _service.LoadIndexAsync();
                }
            });

            RefreshYearOptions();
            RefreshFiles();
            SearchNow();
            if (downloaded is null) return;
            var message = $"Download SisBol concluído.\n\n" +
                $"Baixados/substituídos: {downloaded.Downloaded}\n" +
                $"Ignorados: {downloaded.Skipped}\n" +
                $"Ocorrências: {downloaded.Errors.Count}";
            if (downloaded.Errors.Count > 0)
                message += "\n\n" + string.Join(Environment.NewLine, downloaded.Errors.Take(18));
            SigfurDialog.Show(this, message, "Aditamento do Furriel", MessageBoxButton.OK,
                downloaded.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportSubjectIndex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar Índice por Assunto do Aditamento Furriel",
            Filter = "PDF do índice (*.pdf)|*.pdf|Todos os arquivos (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        int count = 0;
        await RunBusyAsync("Importando Índice por Assunto do Furriel", async progress =>
        {
            count = await _service.ImportSubjectIndexAsync(_index, dialog.FileName, progress);
            _index = await _service.LoadIndexAsync();
        });

        RefreshYearOptions();
        RefreshFiles();
        SearchNow();
        SigfurDialog.Show(this,
            $"Índice por Assunto importado com sucesso.\n\nRegistros oficiais de assunto/nota: {count:N0}.\n\nA linha ‘PAGAMENTO PESSOAL’ foi ignorada como seção do SisBol; somente assunto/nota reais foram usados.",
            "SIGFUR — Índice por Assunto", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private int ResolveDownloadYear()
    {
        var text = YearBox.SelectedItem?.ToString();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year >= 2000) return year;
        if (int.TryParse(_moduleSettings.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out year) && year >= 2000) return year;
        return DateTime.Today.Year;
    }

    private int? ResolveDownloadMonthFromFilter()
    {
        var index = MonthBox.SelectedIndex;
        return index is >= 1 and <= 12 ? index : null;
    }

    private async Task ImportSourcesAsync(IEnumerable<string> sources, bool forceSigned)
    {
        var sourceList = sources.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (!forceSigned && sourceList.Count > 0)
        {
            var first = sourceList[0];
            _moduleSettings.LastFolder = string.Empty;
            _moduleSettings.AutoUpdateFromLastFolder = false;
            await _service.SaveModuleSettingsAsync(_moduleSettings);
        }
        FurrielImportSummary? summary = null;
        await RunBusyAsync(forceSigned ? "Salvando PDFs assinados" : "Importando boletins do Furriel", async progress =>
        {
            summary = await _service.ImportAsync(_index, sourceList, forceSigned, FilesGrid.SelectedItem as FurrielBulletinFile, progress);
            _index = await _service.LoadIndexAsync();
        });
        RefreshYearOptions();
        RefreshFiles();
        SearchNow();
        if (summary is null) return;
        var message = forceSigned
            ? $"PDFs assinados processados.\n\nNovos: {summary.SignedNew}\nAtualizados: {summary.SignedUpdated}"
            : $"Importação concluída.\n\nPDFs normais — novos: {summary.CommonNew} | atualizados: {summary.CommonUpdated}\nPDFs assinados — novos: {summary.SignedNew} | atualizados: {summary.SignedUpdated}";
        if (summary.Errors.Count > 0)
        {
            message += "\n\nObservações/erros:\n" + string.Join('\n', summary.Errors.Take(12));
            SigfurDialog.Show(this, message, "SIGFUR — Boletim Furriel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else SigfurDialog.Show(this, message, "SIGFUR — Boletim Furriel", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Reindex_Click(object sender, RoutedEventArgs e)
    {
        _autoUpdateTimer.Stop();
        _moduleSettings.AutoUpdateFromLastFolder = false;
        StatusText.Text = "Atualização manual do índice foi desativada. Importe PDFs/ZIP ou baixe o ADT pelo SisBol.";
        SigfurDialog.Show(this,
            "Atualizar índice foi removido para evitar varrer pastas erradas e contaminar a base.\n\nUse somente Importar PDFs/ZIP ou Baixar ADT SisBol. Depois importe o Índice por Assunto, quando necessário.",
            "SIGFUR — Furriel", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private Task<bool> AutoSyncConfiguredFolderAsync()
    {
        _autoUpdateTimer.Stop();
        _moduleSettings.AutoUpdateFromLastFolder = false;
        return Task.FromResult(false);
    }

    private void ReindexAll_Click(object sender, RoutedEventArgs e)
    {
        _autoUpdateTimer.Stop();
        _moduleSettings.AutoUpdateFromLastFolder = false;
        StatusText.Text = "Reindexação geral desativada. Importe PDFs/ZIP ou baixe novamente pelo SisBol.";
        SigfurDialog.Show(this,
            "Reindexar boletins foi removido para evitar que arquivos antigos ou errados voltem para a base.\n\nPara atualizar, use Importar PDFs/ZIP ou Baixar ADT SisBol.",
            "SIGFUR — Furriel", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateAutoUpdateTimer()
    {
        _moduleSettings.AutoUpdateFromLastFolder = false;
        _autoUpdateTimer.Stop();
    }

    private Task AutoUpdateTickAsync()
    {
        _moduleSettings.AutoUpdateFromLastFolder = false;
        _autoUpdateTimer.Stop();
        return Task.CompletedTask;
    }

    private async Task RunBusyAsync(string title, Func<IProgress<string>, Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyTitleText.Text = title;
        BusyMessageText.Text = "Preparando...";
        BusyOverlay.Visibility = Visibility.Visible;
        UpdateMetrics();
        var progress = new Progress<string>(message => BusyMessageText.Text = message);
        try { await action(progress); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _busy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            UpdateMetrics();
        }
    }

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FurrielBulletinFile file)
        {
            _lastSelectionArea = "files";
            ShowFileDetail(file);
        }
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is FurrielSearchResult result)
        {
            _lastSelectionArea = "results";
            ShowResultDetail(result);
        }
    }

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPdf_Click(sender, e);
    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPdf_Click(sender, e);

    private void ShowFileDetail(FurrielBulletinFile file)
    {
        var signed = _service.GetSignedInfo(_index, file);
        FileDetailBox.Text =
            $"{file.Title}\n\n" +
            $"Boletim: {file.Bulletin}{(string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : $"  |  BAR {file.Bar}")}\n" +
            $"Data: {file.Date}\nPáginas: {file.Pages}\nLinhas indexadas: {file.LineCount}\n" +
            $"Arquivo: {file.OriginalName}\nIndexado em: {file.IndexedAt}\n\n" +
            $"PDF comum:\n{file.StoredPath}\n\n" +
            $"PDF assinado: {(signed is null ? "não cadastrado" : "DISPONÍVEL")}\n" +
            $"{signed?.Path ?? "—"}";
    }

    private void ShowResultDetail(FurrielSearchResult result)
    {
        ResultDetailBox.Text =
            $"MILITAR MENCIONADO\n{result.Military}\n\n" +
            $"TIPO\n{result.Type}\n\n" +
            $"BOLETIM\nAditamento do Furriel nº {result.Bulletin}{(string.IsNullOrWhiteSpace(result.Bar) ? string.Empty : $" BAR {result.Bar}")} — {result.Date} — página {result.Page}\n\n" +
            $"ASSUNTO / NOTA\n{result.SubjectNoteDisplay}\n\n" +
            $"TEXTO EXATO DA NOTA\n{result.NoteText}\n\n" +
            (result.HasConsequence ? $"TEXTO DE CONSEQUÊNCIA\n{result.ConsequenceText}\n\n" : string.Empty) +
            $"PDF comum\n{result.PdfPath}\n\nPDF assinado\n{(string.IsNullOrWhiteSpace(result.SignedPdfPath) ? "Não cadastrado" : result.SignedPdfPath)}";
    }

    private void OpenSelectedPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastSelectionArea == "results" && ResultsGrid.SelectedItem is FurrielSearchResult result)
            {
                _service.OpenPdf(result.PdfPath, string.IsNullOrWhiteSpace(result.FullName) ? result.Military : result.FullName, result.Page);
                StatusText.Text = $"Aberto {result.FileName} na menção selecionada.";
                return;
            }
            if (FilesGrid.SelectedItem is not FurrielBulletinFile file) { NotifyFileSelection(); return; }
            var selected = MilitaryBox.SelectedItem as FurrielMilitaryOption ?? _service.FindBestMilitary(MilitaryBox.Text ?? string.Empty, _allMilitary);
            var term = selected?.FullName ?? (FreeSearchBox.Text ?? MilitaryBox.Text ?? string.Empty);
            _service.OpenPdf(file.StoredPath, term);
            StatusText.Text = $"Aberto: {file.OriginalName}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenSelectedSigned_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastSelectionArea == "results" && ResultsGrid.SelectedItem is FurrielSearchResult result)
            {
                if (string.IsNullOrWhiteSpace(result.SignedPdfPath)) { NotifyNoSigned(); return; }
                _service.OpenPdf(result.SignedPdfPath, string.IsNullOrWhiteSpace(result.FullName) ? result.Military : result.FullName, result.Page);
                return;
            }
            if (FilesGrid.SelectedItem is not FurrielBulletinFile file) { NotifyFileSelection(); return; }
            var path = _service.GetSignedPath(_index, file);
            if (string.IsNullOrWhiteSpace(path)) { NotifyNoSigned(); return; }
            var selected = MilitaryBox.SelectedItem as FurrielMilitaryOption ?? _service.FindBestMilitary(MilitaryBox.Text ?? string.Empty, _allMilitary);
            _service.OpenPdf(path, selected?.FullName ?? FreeSearchBox.Text ?? string.Empty);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void UseAsBulletinKey_Click(object sender, RoutedEventArgs e)
    {
        var file = SelectedBulletinFile();
        if (file is null) { NotifyFileSelection(); return; }
        try
        {
            await _service.SaveAsBulletinKeysAsync(file);
            SigfurDialog.Show(this,
                $"Chaves atualizadas para o Aditamento {file.Bulletin}.\n\n[[ADT_REFERENCIA]]\n[[NUM_ADT]]\n[[DATA_ADT]]\n\nAs chaves compatíveis de BI também foram preenchidas.",
                "SIGFUR — Chaves do Boletim", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = $"Aditamento {file.Bulletin} definido como referência das chaves automáticas.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private FurrielBulletinFile? SelectedBulletinFile()
    {
        if (FilesGrid.SelectedItem is FurrielBulletinFile file) return file;
        if (ResultsGrid.SelectedItem is FurrielSearchResult result)
            return _index.Files.FirstOrDefault(x => x.StoredPath.Equals(result.PdfPath, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            SigfurDialog.Show(this, "Não há resultados para exportar.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exportar resultados do Boletim Furriel",
            Filter = "CSV do Excel (*.csv)|*.csv",
            FileName = $"boletim_furriel_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _service.ExportCsvAsync(_results, dialog.FileName);
            StatusText.Text = $"Resultados exportados: {dialog.FileName}";
            if (SigfurDialog.Show(this, "Arquivo exportado. Deseja abrir agora?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                ShellService.OpenPath(dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenSisbolIndex_Click(object sender, RoutedEventArgs e)
    {
        var window = new IntelligentBulletinWindow { Owner = this };
        window.Show();
        StatusText.Text = "Consulta do Índice SisBol aberta no Boletim Inteligente.";
    }

    private void OpenPdfFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(_service.PdfDirectory);
    private void OpenSignedFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(_service.SignedDirectory);
    private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e)
    {
        var file = SelectedBulletinFile();
        if (file is null) { NotifyFileSelection(); return; }
        var folder = Path.GetDirectoryName(file.StoredPath);
        if (!string.IsNullOrWhiteSpace(folder)) ShellService.OpenPath(folder);
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FurrielBulletinFile file) { NotifyFileSelection(); return; }
        if (SigfurDialog.Show(this, $"Remover o boletim {file.Bulletin} do índice e apagar a cópia salva?", "SIGFUR — Remover PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _service.RemoveFileAsync(_index, file);
            RefreshYearOptions();
            RefreshFiles();
            SearchNow();
            StatusText.Text = $"Boletim {file.Bulletin} removido do índice.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ClearBase_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this,
                "Apagar todo o índice do Boletim Furriel, os PDFs comuns e os PDFs assinados salvos neste módulo?\n\nOs PDFs originais fora do AppData não serão apagados.",
                "SIGFUR — Limpar base", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _service.ClearAsync(_index);
            _results.Clear();
            RefreshYearOptions();
            RefreshFiles();
            ResultDetailBox.Text = "Base limpa.";
            StatusText.Text = "Base do Boletim Furriel limpa com sucesso.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ClearBulletinFilter_Click(object sender, RoutedEventArgs e) => BulletinFilterBox.Clear();

    private void CopySelectedContext_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FurrielSearchResult result) return;
        Clipboard.SetText(result.Context ?? string.Empty);
        StatusText.Text = "Trecho copiado para a área de transferência.";
    }

    private void CopySelectedReference_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FurrielSearchResult result) return;
        var reference = $"Adt Furr Nr {result.Bulletin}, de {result.Date}, da 4ª Cia PE — p. {result.Page}";
        Clipboard.SetText(reference);
        StatusText.Text = "Referência copiada para a área de transferência.";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            StatusText.Text = "F5 não reindexa mais o Furriel. Use Importar PDFs/ZIP ou Baixar ADT SisBol.";
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            MilitaryBox.SelectedItem = null;
            MilitaryBox.Text = string.Empty;
            FreeSearchBox.Clear();
            _results.Clear();
            ResultDetailBox.Text = "Pesquisa limpa. A tabela de boletins permanece com os filtros atuais.";
            UpdateMetrics();
            StatusText.Text = "Pesquisa limpa.";
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && FilesGrid.IsKeyboardFocusWithin) { RemoveSelected_Click(sender, e); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { MilitaryBox.Focus(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E) { Export_Click(sender, e); e.Handled = true; }
    }

    private void NotifyFileSelection()
        => SigfurDialog.Show(this, "Selecione um boletim na tabela primeiro.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);

    private void NotifyNoSigned()
        => SigfurDialog.Show(this, "Este boletim ainda não possui PDF assinado vinculado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowError(Exception ex)
    {
        StatusText.Text = "Erro: " + ex.Message;
        SigfurDialog.Show(this, ex.Message, "SIGFUR — Boletim Furriel", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static DateTime? ParseDate(string? value)
    {
        foreach (var format in new[] { "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(value?.Trim(), format, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)) return date;
        return null;
    }
}
