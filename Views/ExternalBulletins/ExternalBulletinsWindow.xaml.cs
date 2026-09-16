using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.ExternalBulletins;

public partial class ExternalBulletinsWindow : Window
{
    private readonly ExternalBulletinService _service;
    private readonly ObservableCollection<ExternalBulletinFile> _regionFiles = [];
    private readonly ObservableCollection<ExternalBulletinFile> _cmlFiles = [];
    private readonly ObservableCollection<ExternalBulletinMention> _regionMentions = [];
    private readonly ObservableCollection<ExternalBulletinMention> _cmlMentions = [];
    private readonly ObservableCollection<ExternalBulletinOnlineFile> _onlineCmlResults = [];
    private readonly ObservableCollection<ExternalBulletinOnlineFile> _onlineRegionResults = [];
    private readonly ObservableCollection<ExternalBulletinRegionPeriod> _onlineRegionPeriods = [];
    private ExternalBulletinStore _store = new();
    private ExternalBulletinSettings _settings = new();
    // Começa bloqueado porque eventos Checked/TextChanged podem disparar enquanto
    // o InitializeComponent ainda não criou todas as grades da janela.
    private bool _loading = true;

    public ExternalBulletinsWindow(ExternalBulletinService service)
    {
        InitializeComponent();
        _service = service;
        RegionFilesGrid.ItemsSource = _regionFiles;
        CmlFilesGrid.ItemsSource = _cmlFiles;
        RegionMentionsGrid.ItemsSource = _regionMentions;
        CmlMentionsGrid.ItemsSource = _cmlMentions;
        OnlineCmlResultsGrid.ItemsSource = _onlineCmlResults;
        OnlineRegionResultsGrid.ItemsSource = _onlineRegionResults;
        OnlineRegionMonthBox.ItemsSource = _onlineRegionPeriods;
    }

    private ExternalBulletinFile? SelectedRegionFile => RegionFilesGrid?.SelectedItem as ExternalBulletinFile;
    private ExternalBulletinFile? SelectedCmlFile => CmlFilesGrid?.SelectedItem as ExternalBulletinFile;
    private ExternalBulletinMention? SelectedRegionMention => RegionMentionsGrid?.SelectedItem as ExternalBulletinMention;
    private ExternalBulletinMention? SelectedCmlMention => CmlMentionsGrid?.SelectedItem as ExternalBulletinMention;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _settings = await _service.LoadSettingsAsync();
            SearchTermBox.Text = string.IsNullOrWhiteSpace(_settings.SearchTerm) || _settings.SearchTerm.Equals("4ª Cia PE", StringComparison.OrdinalIgnoreCase)
                ? OrganizationIdentity.Name
                : _settings.SearchTerm;
            RegionFilterBox.Text = _settings.RegionFilter ?? string.Empty;
            CmlFilterBox.Text = _settings.CmlFilter ?? string.Empty;
            HideReadRegionCheck.IsChecked = _settings.HideReadRegionBulletins;
            OnlineCmlYearBox.ItemsSource = Enumerable.Range(2010, Math.Max(1, DateTime.Today.Year - 2009)).Reverse().ToList();
            OnlineCmlYearBox.SelectedItem = _settings.CmlOnlineYear is >= 2010 and <= 2100 ? _settings.CmlOnlineYear : DateTime.Today.Year;
            OnlineCmlSearchBox.Text = string.IsNullOrWhiteSpace(_settings.CmlOnlineSearchTerm) ? SearchTermBox.Text : _settings.CmlOnlineSearchTerm;
            OnlineRegionYearBox.ItemsSource = Enumerable.Range(2000, Math.Max(1, DateTime.Today.Year - 1999)).Reverse().ToList();
            var regionYear = _settings.RegionOnlineYear is >= 2000 and <= 2100 ? _settings.RegionOnlineYear : DateTime.Today.Year;
            OnlineRegionYearBox.SelectedItem = regionYear;
            _onlineRegionPeriods.Add(new ExternalBulletinRegionPeriod { Year = regionYear, Month = 0, Name = "Todos os meses" });
            if (_settings.RegionOnlineMonth is >= 1 and <= 12)
                _onlineRegionPeriods.Add(CreateRegionPeriodChoice(regionYear, _settings.RegionOnlineMonth));
            OnlineRegionMonthBox.SelectedItem = _onlineRegionPeriods.FirstOrDefault(x => x.Month == _settings.RegionOnlineMonth)
                ?? _onlineRegionPeriods[0];
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _loading = false; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { await SaveSettingsAsync(); } catch { }
    }

    private async Task SaveSettingsAsync()
    {
        _settings.SearchTerm = SearchTermBox.Text.Trim();
        _settings.RegionFilter = RegionFilterBox.Text.Trim();
        _settings.CmlFilter = CmlFilterBox.Text.Trim();
        _settings.CmlOnlineYear = OnlineCmlYearBox.SelectedItem is int year ? year : DateTime.Today.Year;
        _settings.CmlOnlineSearchTerm = OnlineCmlSearchBox.Text.Trim();
        _settings.RegionOnlineYear = OnlineRegionYearBox.SelectedItem is int regionYear ? regionYear : DateTime.Today.Year;
        _settings.RegionOnlineMonth = OnlineRegionMonthBox.SelectedItem is ExternalBulletinRegionPeriod period ? period.Month : 0;
        _settings.HideReadRegionBulletins = HideReadRegionCheck.IsChecked == true;
        await _service.SaveSettingsAsync(_settings);
    }

    private async Task ReloadAsync(string? selectRegionId = null, string? selectCmlId = null)
    {
        _store = await _service.LoadAsync();
        ApplyFilters(selectRegionId, selectCmlId);
        UpdateSummary();
        HeaderSearchBadge.Text = $"Pesquisa: {CurrentSearchTerm}";
    }

    private string CurrentSearchTerm => string.IsNullOrWhiteSpace(SearchTermBox.Text) ? OrganizationIdentity.Name : SearchTermBox.Text.Trim();

    private void ApplyFilters(string? selectRegionId = null, string? selectCmlId = null)
    {
        var oldRegionId = selectRegionId ?? SelectedRegionFile?.Id;
        var oldCmlId = selectCmlId ?? SelectedCmlFile?.Id;
        var regionFilter = Normalize(RegionFilterBox.Text);
        var cmlFilter = Normalize(CmlFilterBox.Text);

        var regions = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Region)
            .Where(x => ShowAllRegionCheck.IsChecked == true || x.MentionCount > 0)
            .Where(x => HideReadRegionCheck.IsChecked != true || !x.IsRead)
            .Where(x => MatchesFile(x, regionFilter))
            .OrderByDescending(x => x.DateIso).ThenByDescending(x => x.DisplayNumber).ToList();
        Reset(_regionFiles, regions);

        var cml = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Cml)
            .Where(x => ShowAllCmlCheck.IsChecked == true || x.MentionCount > 0)
            .Where(x => MatchesFile(x, cmlFilter))
            .OrderByDescending(x => x.DateIso).ThenByDescending(x => x.DisplayNumber).ToList();
        Reset(_cmlFiles, cml);

        SelectFile(RegionFilesGrid, _regionFiles, oldRegionId);
        SelectFile(CmlFilesGrid, _cmlFiles, oldCmlId);
        RefreshRegionMentions();
        RefreshCmlMentions();
        var unreadRegionCount = _store.Items.Count(x => x.Kind == ExternalBulletinKinds.Region && !x.IsRead && x.MentionCount > 0);
        RegionFilesCountText.Text = $"{_regionFiles.Count} exibido(s) · {unreadRegionCount} pendente(s)";
        CmlFilesCountText.Text = $"{_cmlFiles.Count} aditamento(s) exibido(s)";
    }

    private static bool MatchesFile(ExternalBulletinFile file, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var text = Normalize(string.Join(" ", file.DisplayNumber, file.DisplayDate, file.OriginalFileName,
            file.Mentions.Select(x => string.Join(" ", x.Section, x.Type, x.Summary, x.Context, x.Amount, x.Event))));
        return text.Contains(filter, StringComparison.Ordinal);
    }

    private static void SelectFile(DataGrid grid, ObservableCollection<ExternalBulletinFile> items, string? id)
    {
        var selected = !string.IsNullOrWhiteSpace(id) ? items.FirstOrDefault(x => x.Id == id) : null;
        selected ??= items.FirstOrDefault();
        grid.SelectedItem = selected;
        if (selected is not null) grid.ScrollIntoView(selected);
    }

    private void RefreshRegionMentions()
    {
        var file = SelectedRegionFile;
        var occurrences = (file?.Mentions ?? [])
            .OrderBy(x => x.Page)
            .ThenBy(x => x.DocumentOccurrence)
            .ToList();
        var consolidated = file is null ? null : BuildConsolidatedRegionMention(file, occurrences);
        Reset(_regionMentions, consolidated is null ? [] : [consolidated]);
        RegionMentionsCountText.Text = file is null
            ? "Selecione um boletim."
            : occurrences.Count == 0
                ? "Nenhuma ocorrência localizada depois de Serviços Diários."
                : $"{occurrences.Count} ocorrência(s) reunida(s) em um único resultado · {file.IgnoredText}";
        RegionMentionsGrid.SelectedItem = _regionMentions.FirstOrDefault();
        RegionDetailText.Text = consolidated?.Context ?? "Selecione um boletim para abrir a leitura consolidada.";
    }

    private static ExternalBulletinMention? BuildConsolidatedRegionMention(
        ExternalBulletinFile file,
        IReadOnlyList<ExternalBulletinMention> occurrences)
    {
        if (occurrences.Count == 0) return null;
        var first = occurrences[0];
        var pages = occurrences.Select(x => x.Page).Distinct().Order().ToList();
        var sections = occurrences.Select(x => CleanRegionalSection(x.Section)).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(4).ToList();
        var term = string.IsNullOrWhiteSpace(first.PdfSearchTerm) ? "termo pesquisado" : first.PdfSearchTerm;
        var pagesText = string.Join(", ", pages.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        var sectionText = sections.Count == 0 ? "Não identificada" : string.Join(" · ", sections);

        var reader = string.Join(Environment.NewLine, new[]
        {
            $"{file.BulletinType.ToUpperInvariant()} {file.DisplayNumber}",
            $"Data: {file.DisplayDate}",
            string.Empty,
            "RESULTADO CONSOLIDADO",
            $"Termo pesquisado: {term}",
            $"Ocorrências úteis: {occurrences.Count}",
            $"Páginas com ocorrência: {pagesText}",
            $"Seções identificadas: {sectionText}",
            string.Empty,
            "PRIMEIRO DESTAQUE",
            first.Summary,
            string.Empty,
            CleanRegionalContext(first.Context),
            string.Empty,
            "COMO CONFERIR AS DEMAIS OCORRÊNCIAS",
            "Clique em “Abrir PDF na primeira ocorrência”. O SIGFUR posicionará a pesquisa no primeiro resultado útil. Depois, pressione F3 no leitor de PDF para avançar pelas demais aparições do mesmo termo."
        });

        return new ExternalBulletinMention
        {
            Id = $"{file.Id}:consolidado",
            FileId = file.Id,
            Kind = file.Kind,
            Bulletin = file.DisplayNumber,
            BulletinDate = file.DisplayDate,
            Page = first.Page,
            DocumentOccurrence = first.DocumentOccurrence,
            PdfSearchTerm = first.PdfSearchTerm,
            Section = sections.Count <= 1 ? sectionText : $"{sections.Count} seções reunidas",
            Type = "Resultado consolidado",
            Summary = $"{occurrences.Count} ocorrência(s) de “{term}” reunida(s) para leitura",
            MatchLine = first.MatchLine,
            Context = reader,
            PdfPath = file.StoredPath
        };
    }

    private static string CleanRegionalContext(string? context)
    {
        var lines = (context ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var boundary = lines.FindLastIndex(x => System.Text.RegularExpressions.Regex.IsMatch(
            Normalize(x), @"^(?:2a|segunda)\s+parte\b"));
        if (boundary >= 0) lines = lines.Skip(boundary + 1).ToList();
        return string.Join(Environment.NewLine, lines);
    }

    private static string CleanRegionalSection(string? section)
    {
        var parts = (section ?? string.Empty).Split('›', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var boundary = parts.FindLastIndex(x => System.Text.RegularExpressions.Regex.IsMatch(
            Normalize(x), @"^(?:2a|segunda)\s+parte\b"));
        if (boundary >= 0) parts = parts.Skip(boundary).ToList();
        parts.RemoveAll(x =>
        {
            var normalized = Normalize(x);
            return normalized.Contains("servicos diarios", StringComparison.Ordinal) ||
                   System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^(?:1a|primeira)\s+parte\b");
        });
        return string.Join(" › ", parts);
    }

    private void RefreshCmlMentions()
    {
        var selectedId = SelectedCmlMention?.Id;
        var rows = SelectedCmlFile?.Mentions ?? [];
        Reset(_cmlMentions, rows.OrderBy(x => x.Page).ThenBy(x => x.DocumentOccurrence));
        CmlMentionsCountText.Text = SelectedCmlFile is null ? "Selecione um aditamento." : $"{_cmlMentions.Count} liberação(ões)/menção(ões)";
        CmlMentionsGrid.SelectedItem = _cmlMentions.FirstOrDefault(x => x.Id == selectedId) ?? _cmlMentions.FirstOrDefault();
        CmlDetailText.Text = SelectedCmlMention?.DetailText ?? "Selecione uma liberação para ver evento, valor, efetivo e contexto.";
    }

    private void UpdateSummary()
    {
        var regionFiles = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Region && x.MentionCount > 0).ToList();
        var cmlFiles = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Cml && x.MentionCount > 0).ToList();
        var mentions = regionFiles.Sum(x => x.MentionCount) + cmlFiles.Sum(x => x.MentionCount);
        RegionSummaryText.Text = $"{regionFiles.Count} boletim(ns)";
        CmlSummaryText.Text = $"{cmlFiles.Count} aditamento(s)";
        MentionsSummaryText.Text = $"{mentions} encontrada(s)";
    }

    private async void ReindexAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchTermBox.Text))
        {
            SigfurDialog.Show(this, "Informe o nome ou a OM que deseja pesquisar.", "Boletins de Fora", MessageBoxButton.OK, MessageBoxImage.Warning);
            SearchTermBox.Focus();
            return;
        }
        try
        {
            var isRegion = SourceTabs.SelectedIndex == 0;
            await RunBusyAsync(isRegion ? "Pesquisando somente o conteúdo útil dos Boletins Regionais..." : "Relendo os aditamentos do CML...", async progress =>
            {
                await SaveSettingsAsync();
                var kind = isRegion ? ExternalBulletinKinds.Region : ExternalBulletinKinds.Cml;
                await _service.ReindexAsync(kind, CurrentSearchTerm, progress);
                await ReloadAsync();
            });
            StatusText.Text = isRegion
                ? $"Pesquisa regional atualizada para “{CurrentSearchTerm}”, somente após Serviços Diários."
                : $"Pesquisa do CML atualizada para “{CurrentSearchTerm}”.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportRegionFolder_Click(object sender, RoutedEventArgs e) => await ImportFolderAsync(ExternalBulletinKinds.Region);
    private async void ImportCmlFolder_Click(object sender, RoutedEventArgs e) => await ImportFolderAsync(ExternalBulletinKinds.Cml);
    private async void ImportRegionFiles_Click(object sender, RoutedEventArgs e) => await ImportFilesAsync(ExternalBulletinKinds.Region);
    private async void ImportCmlFiles_Click(object sender, RoutedEventArgs e) => await ImportFilesAsync(ExternalBulletinKinds.Cml);

    private async void SearchCmlOnline_Click(object sender, RoutedEventArgs e) => await SearchCmlOnlineAsync();

    private void OnlineCmlSearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsKeyboardFocusWithin) return;

        // Garante que o clique permaneça neste campo. Em algumas combinações de
        // escala do Windows/tema, o foco visual aparecia aqui, mas a digitação
        // continuava sendo enviada para a pesquisa geral no topo da janela.
        e.Handled = true;
        textBox.Focus();
        Keyboard.Focus(textBox);
        textBox.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            textBox.Focus();
            Keyboard.Focus(textBox);
            textBox.CaretIndex = textBox.Text.Length;
        }));
    }

    private async void OnlineCmlSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await SearchCmlOnlineAsync();
    }

    private async Task SearchCmlOnlineAsync()
    {
        if (OnlineCmlYearBox.SelectedItem is not int year)
        {
            SigfurDialog.Show(this, "Selecione o ano que deseja consultar.", "Intranet do CML", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var term = OnlineCmlSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            SigfurDialog.Show(this, "Escreva o texto que deve ser pesquisado no site do CML.", "Intranet do CML", MessageBoxButton.OK, MessageBoxImage.Warning);
            OnlineCmlSearchBox.Focus();
            return;
        }

        try
        {
            List<ExternalBulletinOnlineFile> found = [];
            await RunBusyAsync($"Consultando {year} na intranet do CML...", async progress =>
            {
                await SaveSettingsAsync();
                found = await _service.SearchCmlOnlineAsync(year, term, progress);
            });
            Reset(_onlineCmlResults, found);
            var available = found.Count(x => !x.AlreadyInLibrary);
            var stored = found.Count - available;
            OnlineResultSummaryText.Text = $"{found.Count} encontrado(s) · {available} novo(s) · {stored} já salvo(s)";
            OnlineDownloadButton.IsEnabled = available > 0;
            StatusText.Text = found.Count == 0
                ? $"Nenhum aditamento localizado para “{term}” em {year}."
                : $"Consulta concluída: {found.Count} aditamento(s) localizado(s). Marque os desejados e baixe.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SelectAllOnline_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _onlineCmlResults.Where(x => !x.AlreadyInLibrary)) item.IsSelected = true;
        OnlineDownloadButton.IsEnabled = _onlineCmlResults.Any(x => x.IsSelected && !x.AlreadyInLibrary);
        StatusText.Text = $"{_onlineCmlResults.Count(x => x.IsSelected && !x.AlreadyInLibrary)} arquivo(s) novo(s) marcado(s) para download.";
    }

    private async void DownloadCmlOnline_Click(object sender, RoutedEventArgs e)
    {
        OnlineCmlResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        OnlineCmlResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _onlineCmlResults.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SigfurDialog.Show(this, "Marque ao menos um aditamento para baixar.", "Intranet do CML", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ExternalBulletinOnlineDownloadResult? result = null;
            var term = OnlineCmlSearchBox.Text.Trim();
            var reindexExisting = _store.Items.Any(x => Normalize(x.IndexedSearchTerm) != Normalize(term));
            SearchTermBox.Text = term;
            await RunBusyAsync("Baixando os aditamentos selecionados...", async progress =>
            {
                await SaveSettingsAsync();
                result = await _service.DownloadAndImportCmlOnlineAsync(selected, term, progress);
                if (reindexExisting && result.Downloaded > 0)
                    await _service.ReindexAsync(ExternalBulletinKinds.Cml, term, progress);
                await ReloadAsync();
            });
            if (result is null) return;
            OnlineDownloadButton.IsEnabled = _onlineCmlResults.Any(x => x.IsSelected && !x.AlreadyInLibrary);
            var available = _onlineCmlResults.Count(x => !x.AlreadyInLibrary);
            OnlineResultSummaryText.Text = $"{_onlineCmlResults.Count} encontrado(s) · {available} novo(s) · {_onlineCmlResults.Count - available} já salvo(s)";

            var message = $"Baixados do CML: {result.Downloaded}\nJá existentes: {result.AlreadyStored}\nNovos na biblioteca: {result.Import.Imported}\nAtualizados: {result.Import.Updated}\nDuplicados ignorados: {result.Import.Duplicates}";
            if (result.Errors.Count > 0) message += "\n\nOcorrências:\n" + string.Join("\n", result.Errors.Take(12));
            SigfurDialog.Show(this, message, "Download dos aditamentos concluído", MessageBoxButton.OK,
                result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            StatusText.Text = $"Download concluído: {result.Downloaded} PDF(s) salvo(s) e indexado(s).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenCmlSite_Click(object sender, RoutedEventArgs e)
        => ShellService.OpenPath("https://intranet.cml.eb.mil.br/index.php/pt/boletins-aditamentos/aditamentos/interno-2");

    private async void LoadRegionPeriods_Click(object sender, RoutedEventArgs e) => await LoadRegionPeriodsAsync();

    private async Task<bool> LoadRegionPeriodsAsync()
    {
        if (OnlineRegionYearBox.SelectedItem is not int year)
        {
            SigfurDialog.Show(this, "Selecione o ano que deseja consultar.", "Intranet da 4ª RM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            List<ExternalBulletinRegionPeriod> periods = [];
            var previousMonth = OnlineRegionMonthBox.SelectedItem is ExternalBulletinRegionPeriod selected ? selected.Month : _settings.RegionOnlineMonth;
            await RunBusyAsync($"Carregando os meses publicados em {year}...", async progress =>
            {
                periods = await _service.GetRegionOnlinePeriodsAsync(year, progress);
            });

            _onlineRegionPeriods.Clear();
            _onlineRegionPeriods.Add(new ExternalBulletinRegionPeriod { Year = year, Month = 0, Name = "Todos os meses" });
            foreach (var period in periods) _onlineRegionPeriods.Add(period);
            OnlineRegionMonthBox.SelectedItem = _onlineRegionPeriods.FirstOrDefault(x => x.Month == previousMonth)
                ?? _onlineRegionPeriods[0];
            await SaveSettingsAsync();
            StatusText.Text = $"{periods.Count} mês(es) publicado(s) localizado(s) para {year}.";
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
    }

    private async void SearchRegionOnline_Click(object sender, RoutedEventArgs e) => await SearchRegionOnlineAsync();

    private async Task<bool> SearchRegionOnlineAsync()
    {
        if (OnlineRegionYearBox.SelectedItem is not int year)
        {
            SigfurDialog.Show(this, "Selecione o ano que deseja consultar.", "Intranet da 4ª RM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var period = OnlineRegionMonthBox.SelectedItem as ExternalBulletinRegionPeriod;
        var month = period?.Month ?? 0;
        var periodLabel = month == 0 ? "todos os meses" : period?.DisplayName ?? $"mês {month:00}";
        try
        {
            List<ExternalBulletinOnlineFile> found = [];
            await RunBusyAsync($"Consultando {periodLabel} de {year} na intranet da 4ª RM...", async progress =>
            {
                await SaveSettingsAsync();
                found = await _service.SearchRegionOnlineAsync(year, month, progress);
            });
            Reset(_onlineRegionResults, found);
            UpdateRegionOnlineSummary();
            StatusText.Text = found.Count == 0
                ? $"Nenhum Boletim Regional localizado em {periodLabel} de {year}."
                : $"Consulta concluída: {found.Count} PDF(s) localizado(s). Os arquivos novos já estão marcados.";
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
    }

    private void SelectAllRegionOnline_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _onlineRegionResults.Where(x => !x.AlreadyInLibrary)) item.IsSelected = true;
        UpdateRegionOnlineSummary();
        StatusText.Text = $"{_onlineRegionResults.Count(x => x.IsSelected && !x.AlreadyInLibrary)} PDF(s) novo(s) marcado(s) para download.";
    }

    private async void DownloadRegionOnline_Click(object sender, RoutedEventArgs e) => await DownloadRegionOnlineAsync();

    private async void DownloadAllRegionOnline_Click(object sender, RoutedEventArgs e)
    {
        // Refaz a consulta para que um clique sempre respeite o ano e o mês que estão na tela.
        if (!await SearchRegionOnlineAsync()) return;
        foreach (var item in _onlineRegionResults.Where(x => !x.AlreadyInLibrary)) item.IsSelected = true;
        UpdateRegionOnlineSummary();
        if (!_onlineRegionResults.Any(x => x.IsSelected && !x.AlreadyInLibrary))
        {
            SigfurDialog.Show(this, "Todos os PDFs desse período já estão salvos na biblioteca regional.", "Intranet da 4ª RM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await DownloadRegionOnlineAsync();
    }

    private async Task DownloadRegionOnlineAsync()
    {
        OnlineRegionResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        OnlineRegionResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _onlineRegionResults.Where(x => x.IsSelected && !x.AlreadyInLibrary).ToList();
        if (selected.Count == 0)
        {
            SigfurDialog.Show(this, "Marque ao menos um Boletim Regional novo para baixar.", "Intranet da 4ª RM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ExternalBulletinOnlineDownloadResult? result = null;
            var term = CurrentSearchTerm;
            var reindexExisting = _store.Items.Any(x => Normalize(x.IndexedSearchTerm) != Normalize(term));
            await RunBusyAsync("Baixando e indexando os Boletins Regionais...", async progress =>
            {
                await SaveSettingsAsync();
                result = await _service.DownloadAndImportRegionOnlineAsync(selected, term, progress);
                if (reindexExisting && result.Downloaded > 0)
                    await _service.ReindexAsync(ExternalBulletinKinds.Region, term, progress);
                await ReloadAsync();
            });
            if (result is null) return;
            UpdateRegionOnlineSummary();

            var message = $"Baixados da 4ª RM: {result.Downloaded}\nJá existentes: {result.AlreadyStored}\nNovos na biblioteca: {result.Import.Imported}\nAtualizados: {result.Import.Updated}\nDuplicados ignorados: {result.Import.Duplicates}";
            if (result.Errors.Count > 0) message += "\n\nOcorrências:\n" + string.Join("\n", result.Errors.Take(12));
            SigfurDialog.Show(this, message, "Download dos Boletins Regionais concluído", MessageBoxButton.OK,
                result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            StatusText.Text = $"Download concluído: {result.Downloaded} PDF(s) salvo(s) e indexado(s) na biblioteca regional.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateRegionOnlineSummary()
    {
        var available = _onlineRegionResults.Count(x => !x.AlreadyInLibrary);
        var selected = _onlineRegionResults.Count(x => x.IsSelected && !x.AlreadyInLibrary);
        OnlineRegionResultSummaryText.Text = $"{_onlineRegionResults.Count} localizado(s) · {available} novo(s) · {selected} marcado(s)";
        OnlineRegionDownloadButton.IsEnabled = selected > 0;
    }

    private static ExternalBulletinRegionPeriod CreateRegionPeriodChoice(int year, int month)
    {
        string[] monthNames = ["", "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho", "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro"];
        return new ExternalBulletinRegionPeriod { Year = year, Month = month, Name = monthNames[month] };
    }

    private async Task ImportFolderAsync(string kind)
    {
        var last = kind == ExternalBulletinKinds.Region ? _settings.LastRegionFolder : _settings.LastCmlFolder;
        var dialog = new OpenFolderDialog
        {
            Title = kind == ExternalBulletinKinds.Region ? "Selecionar pasta dos Boletins Regionais" : "Selecionar pasta dos Aditamentos do CML",
            Multiselect = false,
            InitialDirectory = Directory.Exists(last) ? last : null
        };
        if (dialog.ShowDialog(this) != true) return;
        if (kind == ExternalBulletinKinds.Region) _settings.LastRegionFolder = dialog.FolderName; else _settings.LastCmlFolder = dialog.FolderName;
        await ImportAsync(kind, [dialog.FolderName]);
    }

    private async Task ImportFilesAsync(string kind)
    {
        var last = kind == ExternalBulletinKinds.Region ? _settings.LastRegionFolder : _settings.LastCmlFolder;
        var dialog = new OpenFileDialog
        {
            Title = kind == ExternalBulletinKinds.Region ? "Adicionar Boletins Regionais" : "Adicionar Aditamentos do CML",
            Filter = "Arquivos PDF (*.pdf)|*.pdf",
            Multiselect = true,
            InitialDirectory = Directory.Exists(last) ? last : null
        };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Path.GetDirectoryName(dialog.FileNames.FirstOrDefault() ?? string.Empty) ?? string.Empty;
        if (kind == ExternalBulletinKinds.Region) _settings.LastRegionFolder = folder; else _settings.LastCmlFolder = folder;
        await ImportAsync(kind, dialog.FileNames);
    }

    private async Task ImportAsync(string kind, IEnumerable<string> sources)
    {
        try
        {
            ExternalBulletinImportResult? result = null;
            var reindexExisting = _store.Items.Any(x => Normalize(x.IndexedSearchTerm) != Normalize(CurrentSearchTerm));
            await RunBusyAsync("Importando e lendo os PDFs...", async progress =>
            {
                await SaveSettingsAsync();
                result = await _service.ImportAsync(kind, sources, CurrentSearchTerm, progress);
                if (reindexExisting)
                    await _service.ReindexAsync(kind, CurrentSearchTerm, progress);
                await ReloadAsync();
            });
            if (result is null) return;
            var message = $"Novos: {result.Imported}\nAtualizados: {result.Updated}\nDuplicados ignorados: {result.Duplicates}\nSem menção: {result.WithoutMention}";
            if (result.Errors.Count > 0) message += "\n\nOcorrências:\n" + string.Join("\n", result.Errors.Take(12));
            SigfurDialog.Show(this, message, "Importação concluída", MessageBoxButton.OK, result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
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

    private void RegionFilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyFilters(); }
    private void CmlFilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyFilters(); }
    private void ShowAllRegionCheck_Changed(object sender, RoutedEventArgs e) { if (!_loading) ApplyFilters(); }
    private void HideReadRegionCheck_Changed(object sender, RoutedEventArgs e) { if (!_loading && IsLoaded) ApplyFilters(); }
    private void ShowAllCmlCheck_Changed(object sender, RoutedEventArgs e) { if (!_loading) ApplyFilters(); }

    private void RegionFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRegionMentions();
    private void CmlFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCmlMentions();
    private void RegionMentionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RegionDetailText.Text = SelectedRegionMention?.Context ?? "Selecione um boletim para abrir a leitura consolidada.";
    private void CmlMentionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => CmlDetailText.Text = SelectedCmlMention?.DetailText ?? "Selecione uma liberação.";

    private void RegionFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenRegionPdf_Click(sender, e);
    private void CmlFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenCmlPdf_Click(sender, e);
    private void RegionMentionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenRegionMention_Click(sender, e);
    private void CmlMentionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenCmlMention_Click(sender, e);

    private void OpenRegionPdf_Click(object sender, RoutedEventArgs e) => OpenFile(SelectedRegionFile);
    private void OpenCmlPdf_Click(object sender, RoutedEventArgs e) => OpenFile(SelectedCmlFile);
    private void OpenRegionMention_Click(object sender, RoutedEventArgs e) => OpenMention(SelectedRegionMention);
    private void OpenCmlMention_Click(object sender, RoutedEventArgs e) => OpenMention(SelectedCmlMention);

    private async void MarkRegionRead_Click(object sender, RoutedEventArgs e) => await SetRegionReadStateAsync(true);
    private async void MarkRegionUnread_Click(object sender, RoutedEventArgs e) => await SetRegionReadStateAsync(false);

    private async Task SetRegionReadStateAsync(bool isRead)
    {
        var file = SelectedRegionFile;
        if (file is null)
        {
            StatusText.Text = "Selecione um Boletim Regional.";
            return;
        }
        try
        {
            var id = file.Id;
            await _service.SetReadStateAsync(id, isRead);
            await ReloadAsync(isRead && HideReadRegionCheck.IsChecked == true ? null : id);
            StatusText.Text = isRead
                ? $"{file.DisplayNumber} marcado como lido."
                : $"{file.DisplayNumber} voltou para os pendentes.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenFile(ExternalBulletinFile? file)
    {
        if (file is null) { StatusText.Text = "Selecione um boletim."; return; }
        try
        {
                        var first = file.Mentions.FirstOrDefault();
            if (first is not null) _service.OpenPdf(first); else _service.OpenPdf(file, CurrentSearchTerm);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenMention(ExternalBulletinMention? mention)
    {
        if (mention is null) { StatusText.Text = "Selecione uma menção."; return; }
        try { _service.OpenPdf(mention); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RevealRegionPdf_Click(object sender, RoutedEventArgs e) => RevealFile(SelectedRegionFile);
    private void RevealCmlPdf_Click(object sender, RoutedEventArgs e) => RevealFile(SelectedCmlFile);
    private static void RevealFile(ExternalBulletinFile? file) { if (file is not null) ShellService.RevealInExplorer(file.StoredPath); }
    private void OpenRegionFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.ExternalBulletinRegionDirectory);
    private void OpenCmlFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.ExternalBulletinCmlDirectory);

    private async void RemoveRegion_Click(object sender, RoutedEventArgs e) => await RemoveAsync(SelectedRegionFile);
    private async void RemoveCml_Click(object sender, RoutedEventArgs e) => await RemoveAsync(SelectedCmlFile);

    private async Task RemoveAsync(ExternalBulletinFile? file)
    {
        if (file is null) { StatusText.Text = "Selecione um boletim."; return; }
        if (SigfurDialog.Show(this, $"Remover da biblioteca e apagar a cópia salva?\n\n{file.OriginalFileName}", "Remover boletim externo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _service.RemoveAsync(file.Id, true); await ReloadAsync(); StatusText.Text = "Boletim removido."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (SourceTabs.SelectedIndex == 0) OpenMention(SelectedRegionMention); else OpenMention(SelectedCmlMention);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ReindexAll_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        SigfurDialog.Show(this, ex.Message, "Boletins de Fora", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Replace('ª', 'a').Replace('º', 'o').Replace('°', 'o').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        return System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"[^a-z0-9]+", " ").Trim();
    }
}
