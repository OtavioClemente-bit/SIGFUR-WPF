using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views;
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
    private bool _suppressFilters;
    private string _lastSelectionArea = "files";
    private int _searchVersion;

    public FurrielBulletinWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = new FurrielBulletinService(App.Paths, App.Settings, App.MilitaryRepository, App.Log);
        FilesGrid.ItemsSource = _visibleFiles;
        ResultsGrid.ItemsSource = _results;
        MilitaryBox.ItemsSource = _military;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchNowAsync();
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
            BusyOverlay.Title = "Abrindo Aditamento Furriel";
            BusyOverlay.Message = "Carregando índice e nomes disponíveis...";
            BusyOverlay.Visibility = Visibility.Visible;
            _suppressFilters = true;
            PersonnelSourceBox.ItemsSource = _service.PersonnelSources;
            MonthBox.ItemsSource = _service.MonthFilterLabels;
            _moduleSettings = await _service.LoadModuleSettingsAsync();
            PersonnelSourceBox.SelectedItem = _service.PersonnelSources.Contains(_moduleSettings.PersonnelSource)
                ? _moduleSettings.PersonnelSource
                : FurrielBulletinService.SourceActive;
            SearchUnregisteredCheck.IsChecked = _moduleSettings.SearchTextWhenNotRegistered;
            // A tela sempre abre consolidada. Filtros antigos não podem esconder anos/meses sem o usuário perceber.
            PeriodModeBox.SelectedIndex = 0;
            MonthBox.SelectedIndex = 0;
            StartDatePicker.SelectedDate = _moduleSettings.StartDate ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            EndDatePicker.SelectedDate = _moduleSettings.EndDate ?? DateTime.Today;
            _moduleSettings.AutoUpdateFromLastFolder = false;
            AutoUpdateCheckBox.IsChecked = false;
            ConsequenceFilterBox.SelectedItem = ConsequenceFilterBox.Items[0];
            UpdatePeriodModeVisuals();
            _index = await Task.Run(() => _service.LoadIndexAsync());
            BusyOverlay.Message = "Preparando pesquisa rápida...";
            await _service.WarmSearchIndexAsync(_index);
            RefreshYearOptions();
            YearBox.SelectedItem = "Todos";
            await ReloadMilitaryAsync();
            MilitaryBox.SelectedItem = null;
            SetEditableComboBoxText(MilitaryBox, string.Empty);
            _suppressFilters = false;
            RefreshFiles();
            UpdateMetrics();
            StatusText.Text = "Aditamento Furriel pronto. Importe ADTs ou pesquise na biblioteca já indexada.";
            UpdateAutoUpdateTimer();
            MilitaryBox.Focus();
        }
        catch (Exception ex)
        {
            _suppressFilters = false;
            ShowError(ex);
        }
        finally
        {
            if (!_busy) BusyOverlay.Visibility = Visibility.Collapsed;
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
            _moduleSettings.SubjectIndexStartDate ??= new DateTime(DateTime.Today.Year, 1, 1);
            _moduleSettings.SubjectIndexEndDate ??= DateTime.Today;
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
        var rows = await Task.Run(() => _service.LoadMilitaryOptionsAsync(source));
        _allMilitary = rows.ToList();
        RefreshMilitarySuggestions(EditableComboBoxText(MilitaryBox), openDropDown: false);
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
        RefreshMilitarySuggestions(EditableComboBoxRawText(MilitaryBox), openDropDown: e.Key != Key.Enter);
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
        var rawText = text ?? string.Empty;
        var typed = rawText.Trim();
        var compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        var digits = Digits(typed);
        var terms = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _allMilitary
            .Where(item =>
            {
                var identity = string.Join(" ", item.FullName, item.WarName, item.Cpf, item.Identity, item.PrecCp, item.Rank);
                return typed.Length == 0 ||
                       compare.IndexOf(identity, typed, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
                       terms.Length > 1 && terms.All(term => compare.IndexOf(identity, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0) ||
                       digits.Length >= 3 && Digits(string.Join(" ", item.Cpf, item.Identity, item.PrecCp)).Contains(digits, StringComparison.Ordinal);
            })
            .Take(35)
            .ToList();
        _military.Clear();
        foreach (var item in matches) _military.Add(item);
        if (openDropDown && typed.Length > 0)
            SetEditableComboBoxText(MilitaryBox, rawText);

        if (!openDropDown || typed.Length == 0 || matches.Count == 0) return;
        MilitaryBox.IsDropDownOpen = true;
        SetEditableComboBoxText(MilitaryBox, rawText);
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
        Interlocked.Increment(ref _searchVersion);
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void SearchNow_Click(object sender, RoutedEventArgs e) => await SearchNowAsync();

    private async void SearchNow() => await SearchNowAsync();

    private async Task SearchNowAsync()
    {
        if (!_loaded || _busy) return;
        var searchVersion = Interlocked.Increment(ref _searchVersion);
        try
        {
            _searchTimer.Stop();
            var comboText = EditableComboBoxText(MilitaryBox);
            var freeText = (FreeSearchBox.Text ?? string.Empty).Trim();
            var selected = MilitaryBox.SelectedItem as FurrielMilitaryOption;
            if (selected is not null && comboText.Length > 0 && !TextMatchesMilitary(comboText, selected))
                selected = null;

            if (selected is not null && comboText.Length == 0)
                comboText = selected.DisplayLabel;

            selected ??= _service.FindBestMilitary(comboText, _allMilitary);
            // Se um militar foi resolvido/selecionado, o texto do campo Militar não entra como busca livre.
            // A busca livre só usa o campo próprio. Isso impede que uma nota coletiva retorne todos
            // os militares que aparecem no corpo quando o usuário queria apenas o militar escolhido.
            var query = selected is not null ? freeText : (freeText.Length > 0 ? freeText : comboText);
            if (selected is null && query.Length > 0 && SearchUnregisteredCheck.IsChecked != true) query = string.Empty;
            var searchedLabel = selected?.DisplayLabel ?? query;

            var consequenceFilter = (ConsequenceFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
            StatusText.Text = _index.SubjectIndex.Count == 0
                ? "Pesquisando diretamente nos PDFs do ADT Furriel..."
                : "Pesquisando menções no índice otimizado dos aditamentos...";
            var period = CurrentPeriod();
            var bulletinFilter = BulletinFilterBox.Text ?? string.Empty;
            var rows = string.IsNullOrWhiteSpace(query) && selected is null && consequenceFilter == "Todos"
                ? new List<FurrielSearchResult>()
                : await Task.Run(() => _service.Search(_index, query, selected, period, bulletinFilter, consequenceFilter));
            if (searchVersion != Volatile.Read(ref _searchVersion)) return;
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
                ResultDetailBox.Text = string.IsNullOrWhiteSpace(searchedLabel) && selected is null
                    ? "Digite um militar, nome de guerra ou texto para pesquisar nas menções dos aditamentos filtrados."
                    : $"Nenhuma menção foi encontrada para \"{searchedLabel}\" no período e nos ADTs escolhidos.";
            }
            UpdateMetrics();
            StatusText.Text = _results.Count == 0
                ? "Pesquisa concluída sem resultados. Confira o período, os filtros e os ADTs importados."
                : $"{_results.Count} resultado(s) encontrado(s) em {_visibleFiles.Count} aditamento(s) visível(is).";
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
        else FileDetailBox.Text = "Nenhum aditamento corresponde ao filtro atual.";
        VisibleBulletinsText.Text = _visibleFiles.Count == 1 ? "1 aditamento visível" : $"{_visibleFiles.Count} aditamentos visíveis";
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
        HeaderStateText.Text = _busy ? "Processando..." : $"{_index.Files.Count} ADTs • {_index.SubjectIndex.Count} notas no índice oficial";
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar PDFs e ZIPs do Aditamento Furriel",
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
            Title = "Selecionar pasta com PDFs/ZIPs do Aditamento Furriel",
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
                "O SIGFUR usará a sessão já preparada do SisBol. O navegador ficará oculto e os PDFs serão indexados automaticamente no Aditamento Furriel.",
                "Baixar Aditamento do Furriel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            SisbolBulletinDownloadResult? downloaded = null;
            var subjectIndexCount = 0;
            var subjectIndexPath = string.Empty;
            var downloadDir = Path.Combine(_service.TempDirectory, "sisbol_aditamento_furriel", year.ToString(CultureInfo.InvariantCulture));
            var ok = await RunBusyAsync("Baixando Aditamentos do Furriel no SisBol", async progress =>
            {
                downloaded = await Task.Run(() => App.Sisbol.DownloadGeneratedBulletinsAsync(3, year, downloadDir, replaceExisting: true, progress, CancellationToken.None, range.Month));
                if (downloaded.DownloadedFiles.Count > 0)
                {
                    await Task.Run(() => _service.ImportAsync(_index, downloaded.DownloadedFiles, false, null, progress));
                    _index = await Task.Run(() => _service.LoadIndexAsync());
                }

                var indexRange = ResolveDownloadIndexRange(range);
                progress.Report("Baixando Indice por Assunto obrigatorio do Furriel...");
                var subjectOutputDir = Path.Combine(_service.TempDirectory, "sisbol_indice_assunto");
                var subjectIndex = await Task.Run(() => App.Sisbol.DownloadSubjectIndexAsync(indexRange.Start, indexRange.End, 3, subjectOutputDir, progress, CancellationToken.None));
                if (!subjectIndex.Downloaded)
                    throw new InvalidOperationException("O SisBol nao retornou o PDF do Indice por Assunto do Furriel.");

                subjectIndexPath = subjectIndex.FilePath;
                subjectIndexCount = await Task.Run(() => _service.ImportSubjectIndexAsync(_index, subjectIndex.FilePath, progress));
                _index = await Task.Run(() => _service.LoadIndexAsync());
                _moduleSettings.SubjectIndexStartDate = indexRange.Start;
                _moduleSettings.SubjectIndexEndDate = indexRange.End;
                await _service.SaveModuleSettingsAsync(_moduleSettings);
            });
            if (!ok) return;

            RefreshYearOptions();
            RefreshFiles();
            SearchNow();
            if (downloaded is null) return;
            var message = $"Download SisBol concluído.\n\n" +
                $"Baixados/substituídos: {downloaded.Downloaded}\n" +
                $"Ignorados: {downloaded.Skipped}\n" +
                $"Ocorrências: {downloaded.Errors.Count}";
            message += $"\nIndice por Assunto: {subjectIndexCount:N0} registro(s) oficial(is)";
            if (!string.IsNullOrWhiteSpace(subjectIndexPath))
                message += $" ({Path.GetFileName(subjectIndexPath)})";
            if (downloaded.Errors.Count > 0)
                message += "\n\n" + string.Join(Environment.NewLine, downloaded.Errors.Take(18));
            SigfurDialog.Show(this, message, "Aditamento do Furriel", MessageBoxButton.OK,
                downloaded.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DownloadSubjectIndex_Click(object sender, RoutedEventArgs e)
    {
        var (initialStart, initialEnd) = ResolveSubjectIndexDefaultRange();
        var range = SisbolIndexDateRangeDialog.Ask(this, "Indice por Assunto do Furriel", initialStart, initialEnd);
        if (range is null) return;

        if (SigfurDialog.Show(this,
                $"Baixar o Indice por Assunto do Aditamento do Furriel de {range.PeriodText}?\n\n" +
                "O SIGFUR usara a sessao preparada do SisBol, abrira gerarindice.php?codTipoBol=3, baixara o PDF e importara o indice automaticamente.",
                "Baixar Indice por Assunto", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var count = 0;
        var downloadedPath = string.Empty;
        var ok = await RunBusyAsync("Baixando Indice por Assunto do Furriel", async progress =>
        {
            var outputDir = Path.Combine(_service.TempDirectory, "sisbol_indice_assunto");
            var downloaded = await Task.Run(() => App.Sisbol.DownloadSubjectIndexAsync(range.StartDate, range.EndDate, 3, outputDir, progress, CancellationToken.None));
            if (!downloaded.Downloaded)
                throw new InvalidOperationException("O SisBol nao retornou o PDF do Indice por Assunto.");

            downloadedPath = downloaded.FilePath;
            count = await Task.Run(() => _service.ImportSubjectIndexAsync(_index, downloaded.FilePath, progress));
            _index = await Task.Run(() => _service.LoadIndexAsync());
            _moduleSettings.SubjectIndexStartDate = range.StartDate;
            _moduleSettings.SubjectIndexEndDate = range.EndDate;
            await _service.SaveModuleSettingsAsync(_moduleSettings);
        });
        if (!ok) return;

        if (count <= 0) return;
        RefreshYearOptions();
        RefreshFiles();
        SearchNow();
        SigfurDialog.Show(this,
            $"Indice por Assunto baixado e importado.\n\nArquivo: {Path.GetFileName(downloadedPath)}\nPeriodo: {range.PeriodText}\nRegistros oficiais de assunto/nota: {count:N0}.",
            "SIGFUR - Indice por Assunto", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var ok = await RunBusyAsync("Importando Índice por Assunto do Furriel", async progress =>
        {
            count = await Task.Run(() => _service.ImportSubjectIndexAsync(_index, dialog.FileName, progress));
            _index = await Task.Run(() => _service.LoadIndexAsync());
        });
        if (!ok) return;

        RefreshYearOptions();
        RefreshFiles();
        SearchNow();
        if (count <= 0) return;
        SigfurDialog.Show(this,
            $"Índice por Assunto importado com sucesso.\n\nRegistros oficiais de assunto/nota: {count:N0}.\n\nA linha ‘PAGAMENTO PESSOAL’ foi ignorada como seção do SisBol; somente assunto/nota reais foram usados.",
            "SIGFUR — Índice por Assunto", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ExportSubjectIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_index.SubjectIndex.Count == 0)
        {
            SigfurDialog.Show(this, "Nao ha Indice por Assunto importado para exportar. Baixe pelo SisBol ou importe o PDF primeiro.", "SIGFUR - Indice por Assunto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar Indice por Assunto do Furriel",
            Filter = "CSV do Excel (*.csv)|*.csv",
            FileName = $"indice_furriel_por_assunto_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await _service.ExportSubjectIndexCsvAsync(_index, dialog.FileName);
            StatusText.Text = $"Indice por Assunto exportado: {dialog.FileName}";
            if (SigfurDialog.Show(this, "Arquivo exportado. Deseja abrir agora?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                ShellService.OpenPath(dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static (DateTime Start, DateTime End) ResolveDownloadIndexRange(SisbolDownloadRangeSelection range)
    {
        var start = range.Month is >= 1 and <= 12
            ? new DateTime(range.Year, range.Month.Value, 1)
            : new DateTime(range.Year, 1, 1);
        var end = range.Month is >= 1 and <= 12
            ? new DateTime(range.Year, range.Month.Value, DateTime.DaysInMonth(range.Year, range.Month.Value))
            : new DateTime(range.Year, 12, 31);
        if (range.Year == DateTime.Today.Year && end.Date > DateTime.Today) end = DateTime.Today;
        return (start.Date, end.Date);
    }

    private (DateTime Start, DateTime End) ResolveSubjectIndexDefaultRange()
    {
        if (_moduleSettings.SubjectIndexStartDate is { } savedStart && _moduleSettings.SubjectIndexEndDate is { } savedEnd)
        {
            if (savedEnd.Date < savedStart.Date) (savedStart, savedEnd) = (savedEnd.Date, savedStart.Date);
            return (savedStart.Date, savedEnd.Date);
        }

        if (SelectedPeriodMode().Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase) &&
            StartDatePicker.SelectedDate is { } start &&
            EndDatePicker.SelectedDate is { } end)
        {
            if (end.Date < start.Date) (start, end) = (end.Date, start.Date);
            return (start.Date, end.Date);
        }

        var year = ResolveDownloadYear();
        var month = ResolveDownloadMonthFromFilter();
        if (month is >= 1 and <= 12)
        {
            var monthStart = new DateTime(year, month.Value, 1);
            var monthEnd = new DateTime(year, month.Value, DateTime.DaysInMonth(year, month.Value));
            if (year == DateTime.Today.Year && month.Value == DateTime.Today.Month && monthEnd > DateTime.Today) monthEnd = DateTime.Today;
            return (monthStart, monthEnd);
        }

        return (new DateTime(year, 1, 1), year == DateTime.Today.Year ? DateTime.Today : new DateTime(year, 12, 31));
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
        var ok = await RunBusyAsync(forceSigned ? "Salvando PDFs assinados" : "Importando Aditamentos Furriel", async progress =>
        {
            var selectedFile = FilesGrid.SelectedItem as FurrielBulletinFile;
            summary = await Task.Run(() => _service.ImportAsync(_index, sourceList, forceSigned, selectedFile, progress));
            _index = await Task.Run(() => _service.LoadIndexAsync());
        });
        if (!ok) return;
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
            SigfurDialog.Show(this, message, "SIGFUR — Aditamento Furriel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            if (!forceSigned && _index.SubjectIndex.Count == 0)
                message += "\n\nPara pesquisar assunto/nota, baixe ou importe o Indice por Assunto do Furriel.";
            SigfurDialog.Show(this, message, "SIGFUR — Aditamento Furriel", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<FurrielBulletinFile>().ToList();
        if (selected.Count == 0 && FilesGrid.SelectedItem is FurrielBulletinFile one) selected.Add(one);
        if (selected.Count == 0) { NotifyFileSelection(); return; }

        List<string> errors = [];
        var ok = await RunBusyAsync(
            selected.Count == 1 ? "Reindexando aditamento" : $"Reindexando {selected.Count} aditamentos",
            async progress =>
            {
                errors = await _service.ReindexFilesAsync(_index, selected, progress);
                _index = await _service.LoadIndexAsync();
            });
        if (!ok) return;
        RefreshYearOptions();
        RefreshFiles();
        await SearchNowAsync();
        StatusText.Text = errors.Count == 0
            ? $"{selected.Count} aditamento(s) reindexado(s) com sucesso."
            : $"Reindexação concluída com {errors.Count} ocorrência(s).";
        if (errors.Count > 0)
            SigfurDialog.Show(this, string.Join(Environment.NewLine, errors.Take(12)), "SIGFUR — Reindexação", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private Task<bool> AutoSyncConfiguredFolderAsync()
    {
        _autoUpdateTimer.Stop();
        _moduleSettings.AutoUpdateFromLastFolder = false;
        return Task.FromResult(false);
    }

    private async void ReindexAll_Click(object sender, RoutedEventArgs e)
    {
        if (_index.Files.Count == 0) return;
        if (SigfurDialog.Show(this,
                $"Reprocessar os {_index.Files.Count} ADTs salvos e atualizar assuntos, notas e consequências?",
                "SIGFUR — Reindexar biblioteca", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        List<string> errors = [];
        var ok = await RunBusyAsync("Reindexando biblioteca de aditamentos", async progress =>
        {
            errors = await _service.ReindexAllAsync(_index, progress);
            _index = await _service.LoadIndexAsync();
        });
        if (!ok) return;
        RefreshYearOptions();
        RefreshFiles();
        await SearchNowAsync();
        StatusText.Text = errors.Count == 0
            ? "Biblioteca reindexada com sucesso."
            : $"Biblioteca reindexada com {errors.Count} ocorrência(s).";
        if (errors.Count > 0)
            SigfurDialog.Show(this, string.Join(Environment.NewLine, errors.Take(12)), "SIGFUR — Reindexação", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private async Task<bool> RunBusyAsync(string title, Func<IProgress<string>, Task> action)
    {
        if (_busy) return false;
        _busy = true;
        BusyOverlay.Title = title;
        BusyOverlay.Message = "Preparando...";
        BusyOverlay.Visibility = Visibility.Visible;
        UpdateMetrics();
        var progress = new Progress<string>(message =>
        {
            BusyOverlay.Message = message;
            StatusText.Text = message;
        });
        try
        {
            await action(progress);
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
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

    private void ResultsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var row = FindVisualParent<DataGridRow>(source);
        if (row is null) return;
        ResultsGrid.SelectedItem = row.Item;
        row.IsSelected = true;
        row.Focus();
        _lastSelectionArea = "results";
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ShowFileDetail(FurrielBulletinFile file)
    {
        var signed = _service.GetSignedInfo(_index, file);
        FileDetailBox.Text =
            $"{file.Title}\n\n" +
            $"Aditamento: {file.Bulletin}{(string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : $"  |  BAR {file.Bar}")}\n" +
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
            $"ADITAMENTO\nAditamento Furriel nº {result.Bulletin}{(string.IsNullOrWhiteSpace(result.Bar) ? string.Empty : $" BAR {result.Bar}")} — {result.Date} — página {result.Page}\n\n" +
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
            var comboText = EditableComboBoxText(MilitaryBox);
            var selected = MilitaryBox.SelectedItem as FurrielMilitaryOption ?? _service.FindBestMilitary(comboText, _allMilitary);
            var term = selected?.FullName ?? (string.IsNullOrWhiteSpace(FreeSearchBox.Text) ? comboText : FreeSearchBox.Text);
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
            var comboText = EditableComboBoxText(MilitaryBox);
            var selected = MilitaryBox.SelectedItem as FurrielMilitaryOption ?? _service.FindBestMilitary(comboText, _allMilitary);
            var term = selected?.FullName ?? (string.IsNullOrWhiteSpace(FreeSearchBox.Text) ? comboText : FreeSearchBox.Text);
            _service.OpenPdf(path, term ?? string.Empty);
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
                "SIGFUR — Chaves do Aditamento", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = $"Aditamento {file.Bulletin} definido como referência das chaves automáticas.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private FurrielBulletinFile? SelectedBulletinFile()
    {
        if (_lastSelectionArea == "results" && ResultsGrid.SelectedItem is FurrielSearchResult selectedResult)
            return FindResultBulletinFile(selectedResult);
        if (FilesGrid.SelectedItem is FurrielBulletinFile file) return file;
        if (ResultsGrid.SelectedItem is FurrielSearchResult result) return FindResultBulletinFile(result);
        return null;
    }

    private FurrielBulletinFile? FindResultBulletinFile(FurrielSearchResult result)
    {
        var file = _index.Files.FirstOrDefault(x => SamePath(x.StoredPath, result.PdfPath));
        if (file is not null) return file;
        if (string.IsNullOrWhiteSpace(result.PdfPath) || !File.Exists(result.PdfPath)) return null;
        return new FurrielBulletinFile
        {
            Bulletin = result.Bulletin,
            Bar = result.Bar,
            Date = result.Date,
            OriginalName = string.IsNullOrWhiteSpace(result.FileName) ? Path.GetFileName(result.PdfPath) : result.FileName,
            StoredPath = result.PdfPath
        };
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return left.Equals(right, StringComparison.OrdinalIgnoreCase); }
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
            Title = "Exportar resultados do Aditamento Furriel",
            Filter = "CSV do Excel (*.csv)|*.csv",
            FileName = $"aditamento_furriel_{DateTime.Now:yyyyMMdd_HHmm}.csv"
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

    private async void ExportIndividualBulletin_Click(object sender, RoutedEventArgs e)
    {
        var selectedResult = _lastSelectionArea == "results" ? ResultsGrid.SelectedItem as FurrielSearchResult : null;
        var file = SelectedBulletinFile();
        if (file is null) { NotifyFileSelection(); return; }

        try
        {
            var sourcePath = file.StoredPath;
            var signedPath = selectedResult is not null && File.Exists(selectedResult.SignedPdfPath)
                ? selectedResult.SignedPdfPath
                : _service.GetSignedPath(_index, file);
            var signed = false;
            if (!string.IsNullOrWhiteSpace(signedPath) && File.Exists(signedPath))
            {
                var choice = SigfurDialog.Show(this,
                    "Este aditamento possui PDF assinado.\n\nSim: exportar a versão assinada.\nNão: exportar a versão comum.",
                    "Exportar aditamento individual", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes)
                {
                    sourcePath = signedPath;
                    signed = true;
                }
            }

            var bar = string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : $" - BAR {file.Bar}";
            var signedSuffix = signed ? " - ASSINADO" : string.Empty;
            var exported = await BulletinIndividualExportService.ExportAsync(this, sourcePath,
                $"ADT Furr Nr {file.Bulletin}{bar} - {file.Date}{signedSuffix}.pdf",
                "Exportar aditamento individual");
            if (!string.IsNullOrWhiteSpace(exported)) StatusText.Text = $"Aditamento individual exportado: {exported}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ExportBulletinRange_Click(object sender, RoutedEventArgs e)
    {
        var exportItems = _index.Files
            .Select(file => BulletinBatchExportItem.Create(
                file.Id,
                "ADT",
                file.Bulletin,
                file.Date,
                file.StoredPath,
                file.OriginalName,
                _service.GetSignedPath(_index, file),
                string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : $"BAR {file.Bar}"))
            .OfType<BulletinBatchExportItem>()
            .ToList();
        if (exportItems.Count == 0)
        {
            SigfurDialog.Show(this, "Não há Aditamentos Furriel com número, ano e PDF disponíveis para exportação.",
                "SIGFUR — Exportar aditamentos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedIds = FilesGrid.SelectedItems.Cast<FurrielBulletinFile>()
            .Select(file => file.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var window = new BulletinBatchExportWindow(exportItems, "Aditamentos Furriel", "ADT", selectedIds) { Owner = this };
        window.ShowDialog();
    }

    private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e)
    {
        var file = SelectedBulletinFile();
        if (file is null) { NotifyFileSelection(); return; }
        var folder = Path.GetDirectoryName(file.StoredPath);
        if (!string.IsNullOrWhiteSpace(folder)) ShellService.OpenPath(folder);
    }

    private void SendEbMail_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<FurrielBulletinFile>().DistinctBy(x => x.Id).ToList();
        if (selected.Count == 0 && FilesGrid.SelectedItem is FurrielBulletinFile one) selected.Add(one);
        var documents = selected.Select(file =>
        {
            var signed = _service.GetSignedInfo(_index, file);
            var useSigned = signed is not null && File.Exists(signed.Path);
            return new EbMailDocument
            {
                Kind = "ADT",
                Reference = $"ADT Furr Nr {file.DisplayBulletin}" + (string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : $" BAR {file.Bar}"),
                Date = file.DisplayDate,
                FilePath = file.StoredPath,
                OriginalFilePath = File.Exists(file.StoredPath) ? file.StoredPath : string.Empty,
                SignedFilePath = useSigned ? signed!.Path : string.Empty,
                IsSigned = false,
                AttachmentMode = useSigned ? EbMailAttachmentMode.Signed : EbMailAttachmentMode.Original
            };
        }).Where(x => x.HasAnyAttachment).ToList();
        if (documents.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione um ou mais ADTs com PDF disponível.", "EBMail", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new EbMailComposeWindow(documents) { Owner = this }.ShowDialog();
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<FurrielBulletinFile>().ToList();
        if (selected.Count == 0 && FilesGrid.SelectedItem is FurrielBulletinFile one) selected.Add(one);
        if (selected.Count == 0) { NotifyFileSelection(); return; }

        var label = selected.Count == 1 ? $"o ADT {selected[0].Bulletin}" : $"os {selected.Count} ADTs selecionados";
        if (SigfurDialog.Show(this, $"Remover {label} da biblioteca e apagar as cópias salvas?",
                "SIGFUR — Remover aditamentos", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var ok = await RunBusyAsync("Removendo aditamentos", async progress =>
            await _service.RemoveFilesAsync(_index, selected, deleteStoredFiles: true, progress));
        if (!ok) return;
        RefreshYearOptions();
        RefreshFiles();
        await SearchNowAsync();
        StatusText.Text = $"{selected.Count} aditamento(s) removido(s) da biblioteca.";
    }

    private async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_index.Files.Count == 0) return;
        if (SigfurDialog.Show(this,
                $"Remover todos os {_index.Files.Count} aditamentos e PDFs assinados da biblioteca?\n\nO Índice por Assunto será preservado.",
                "SIGFUR — Esvaziar biblioteca", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var ok = await RunBusyAsync("Esvaziando biblioteca de aditamentos", async progress =>
            await _service.RemoveAllBulletinsAsync(_index, progress));
        if (!ok) return;
        _results.Clear();
        RefreshYearOptions();
        RefreshFiles();
        ResultDetailBox.Text = "A biblioteca está vazia. O Índice por Assunto foi preservado.";
        StatusText.Text = "Biblioteca de aditamentos esvaziada com sucesso.";
    }

    private async void RefreshIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var ok = await RunBusyAsync("Atualizando biblioteca", async progress =>
        {
            progress.Report("Carregando índice e cache de pesquisa...");
            _index = await Task.Run(() => _service.LoadIndexAsync());
        });
        if (!ok) return;
        RefreshYearOptions();
        RefreshFiles();
        await SearchNowAsync();
        StatusText.Text = "Biblioteca atualizada.";
    }

    private async void ClearBase_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this,
                "Apagar todo o índice do Aditamento Furriel, os PDFs comuns e os PDFs assinados salvos neste módulo?\n\nOs PDFs originais fora do AppData não serão apagados.",
                "SIGFUR — Limpar base", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _searchTimer.Stop();
            _busy = true;
            await _service.ClearAsync(_index);
            _index = await Task.Run(() => _service.LoadIndexAsync());
            _results.Clear();
            _visibleFiles.Clear();
            FilesGrid.SelectedItem = null;
            ResultsGrid.SelectedItem = null;
            _busy = false;
            RefreshYearOptions();
            RefreshFiles();
            UpdateMetrics();
            ResultDetailBox.Text = "Base limpa.";
            StatusText.Text = "Módulo Aditamento Furriel limpo com sucesso.";
        }
        catch (Exception ex)
        {
            _busy = false;
            ShowError(ex);
        }
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
        var reference = $"Adt Furr Nr {result.Bulletin}, de {result.Date}, {OrganizationIdentity.BulletinIssuer} — p. {result.Page}";
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
        => SigfurDialog.Show(this, "Selecione um aditamento na biblioteca ou nos resultados da pesquisa.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);

    private void NotifyNoSigned()
        => SigfurDialog.Show(this, "Este aditamento ainda não possui PDF assinado vinculado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowError(Exception ex)
    {
        StatusText.Text = "Erro: " + ex.Message;
        SigfurDialog.Show(this, ex.Message, "SIGFUR — Aditamento Furriel", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string EditableComboBoxText(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
            return (editor.Text ?? string.Empty).Trim();

        return (combo.Text ?? string.Empty).Trim();
    }

    private static string EditableComboBoxRawText(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
            return editor.Text ?? string.Empty;
        return combo.Text ?? string.Empty;
    }

    private static void SetEditableComboBoxText(ComboBox combo, string text)
    {
        combo.ApplyTemplate();
        combo.Text = text;
        if (combo.Template.FindName("PART_EditableTextBox", combo) is not TextBox editor) return;

        if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
            editor.Text = text;
        editor.CaretIndex = editor.Text.Length;
        editor.SelectionLength = 0;
    }

    private static bool TextMatchesMilitary(string text, FurrielMilitaryOption item)
    {
        var typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0) return true;

        var compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        var identity = string.Join(" ", item.DisplayLabel, item.FullName, item.WarName, item.Cpf, item.Identity, item.PrecCp, item.Rank);
        if (compare.IndexOf(identity, typed, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            return true;

        var digits = Digits(typed);
        return digits.Length >= 3 && Digits(identity).Contains(digits, StringComparison.Ordinal);
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static DateTime? ParseDate(string? value)
    {
        foreach (var format in new[] { "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(value?.Trim(), format, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)) return date;
        return null;
    }
}
