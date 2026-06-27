using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;

namespace SIGFUR.Wpf.Views.Intelligent;

public partial class IntelligentBulletinWindow : Window
{
    private readonly IntelligentBulletinService _service;
    private readonly ObservableCollection<IntelligentBulletinFile> _files = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _review = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _serviceRows = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _vacations = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _convalescences = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _payments = [];
    private readonly ObservableCollection<SisbolPersonIndexItem> _militaryResults = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _admin = [];
    private readonly ObservableCollection<IntelligentBulletinFinding> _others = [];
    private readonly ObservableCollection<string> _subjectFilters = [];
    private readonly ObservableCollection<SisbolPersonIndexItem> _personIndexRows = [];
    private readonly ObservableCollection<string> _personIndexYears = ["Todos"];
    private readonly ObservableCollection<string> _personIndexSubjects = ["Todos"];
    private readonly ObservableCollection<string> _personIndexNotes = ["Todos"];
    private readonly ObservableCollection<string> _personIndexUsers = ["Todos"];
    private readonly ObservableCollection<SisbolPersonIndexPersonOption> _personIndexPeople = [];
    private readonly ObservableCollection<IntelligentBulletinMilitaryOption> _peopleSuggestions = [];
    private List<SisbolPersonIndexPersonOption> _personIndexAllPeople = [];
    private List<string> _personIndexAllSubjects = ["Todos"];
    private List<string> _personIndexAllNotes = ["Todos"];
    private IntelligentBulletinStore _store = new();
    private List<IntelligentBulletinFinding> _filtered = [];
    private List<IntelligentBulletinMilitaryOption> _people = [];
    private IntelligentBulletinMilitaryOption? _selectedPerson;
    private IntelligentBulletinSettings _settings = new();
    private bool _loading;
    private bool _autoUpdateRunning;
    private readonly DispatcherTimer _autoUpdateTimer;
    private readonly DispatcherTimer _personIndexFilterTimer;
    private CancellationTokenSource? _personIndexFilterCts;
    private bool _personIndexFilterQueuedMilitaryRefresh;
    private bool _personIndexFilterRunning;
    private Dictionary<string, string> _personIndexPeopleSearch = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _personIndexSubjectsSearch = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _personIndexNotesSearch = new(StringComparer.OrdinalIgnoreCase);

    public IntelligentBulletinWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = App.IntelligentBulletins;
        LibraryGrid.ItemsSource = _files;
        ReviewGrid.ItemsSource = _review;
        ServiceGrid.ItemsSource = _serviceRows;
        VacationGrid.ItemsSource = _vacations;
        ConvalescenceGrid.ItemsSource = _convalescences;
        PaymentGrid.ItemsSource = _payments;
        MilitaryResultsGrid.ItemsSource = _militaryResults;
        AdminGrid.ItemsSource = _admin;
        OtherGrid.ItemsSource = _others;
        SubjectFilterBox.ItemsSource = _subjectFilters;
        PersonIndexGrid.ItemsSource = _personIndexRows;
        PersonIndexYearBox.ItemsSource = _personIndexYears;
        PersonIndexSubjectBox.ItemsSource = _personIndexSubjects;
        PersonIndexNoteBox.ItemsSource = _personIndexNotes;
        PersonIndexUserBox.ItemsSource = _personIndexUsers;
        PersonIndexPersonBox.ItemsSource = _personIndexPeople;
        PersonIndexMonthBox.ItemsSource = new[] { "Todos", "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho", "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro" };
        PersonIndexLinkBox.ItemsSource = new[] { "Todos", "Vinculados", "Não vinculados" };
        PersonIndexMonthBox.SelectedIndex = 0;
        PersonIndexLinkBox.SelectedIndex = 0;
        foreach (var tab in ResultTabs.Items.OfType<TabItem>())
        {
            var header = tab.Header?.ToString() ?? string.Empty;
            if (header is "Serviço" or "Férias" or "Convalescença" or "Alertas Adm" or "Outros")
                tab.Visibility = Visibility.Collapsed;
        }
        foreach (var column in MilitaryResultsGrid.Columns.Where(x => string.Equals(x.Header?.ToString(), "Prévia", StringComparison.OrdinalIgnoreCase)))
            column.Visibility = Visibility.Collapsed;
        MilitaryBox.ItemsSource = _peopleSuggestions;
        _autoUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _autoUpdateTimer.Tick += async (_, _) => await AutoUpdateTickAsync();
        _personIndexFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
        _personIndexFilterTimer.Tick += async (_, _) => await RunQueuedPersonIndexFilterAsync();
        Closing += (_, _) => SaveCurrentPageSettings();
        Closed += (_, _) =>
        {
            _autoUpdateTimer.Stop();
            _personIndexFilterTimer.Stop();
            _personIndexFilterCts?.Cancel();
            _personIndexFilterCts?.Dispose();
        };
    }

    private IntelligentBulletinFile? SelectedFile => LibraryGrid.SelectedItem as IntelligentBulletinFile;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _settings = await App.Json.LoadAsync<IntelligentBulletinSettings>(App.Paths.IntelligentBulletinSettingsFile) ?? new IntelligentBulletinSettings();
            SearchBox.Text = _settings.Search;
            PersonnelSourceBox.SelectedIndex = _settings.PersonnelSource switch { "Ativos + licenciados/transferidos" => 1, "Só licenciados/transferidos" => 2, _ => 0 };
            PeriodModeBox.SelectedIndex = _settings.PeriodMode.Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            StartDatePicker.SelectedDate = _settings.StartDate ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            EndDatePicker.SelectedDate = _settings.EndDate ?? DateTime.Today;
            AutoUpdateCheckBox.IsChecked = _settings.AutoUpdateFromLastFolder;
            if (string.IsNullOrWhiteSpace(_settings.SubjectFilter)) _settings.SubjectFilter = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexYear)) _settings.PersonIndexYear = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexMonth)) _settings.PersonIndexMonth = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexSubject)) _settings.PersonIndexSubject = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexNote)) _settings.PersonIndexNote = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexLinkFilter)) _settings.PersonIndexLinkFilter = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexUser)) _settings.PersonIndexUser = "Todos";
            if (string.IsNullOrWhiteSpace(_settings.PersonIndexPerson)) _settings.PersonIndexPerson = "Todos";
            MilitaryBox.Text = _settings.MilitarySearch;
            PersonIndexSearchBox.Clear();
            PersonIndexSubjectSearchBox.Clear();
            PersonIndexSubjectNoteSearchBox.Clear();
            PersonIndexDownloadStartPicker.SelectedDate = _settings.PersonIndexDownloadStartDate ?? new DateTime(DateTime.Today.Year, 1, 1);
            PersonIndexDownloadEndPicker.SelectedDate = _settings.PersonIndexDownloadEndDate ?? DateTime.Today;
            ConsequenceFilterBox.SelectedItem = ConsequenceFilterBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Content?.ToString(), _settings.ConsequenceFilter, StringComparison.OrdinalIgnoreCase))
                ?? ConsequenceFilterBox.Items[0];
            UpdatePeriodModeVisuals();
        }
        finally { _loading = false; }
        if (_settings.AutoUpdateFromLastFolder && Directory.Exists(_settings.LastFolder))
            await SyncLastFolderAsync();
        await ReloadAsync();
        RestoreSelectedTab();
        UpdateAutoUpdateTimer();
    }

    private async Task ReloadAsync(string? selectFileId = null)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            StatusText.Text = "Carregando biblioteca e conferências...";
            _store = await _service.LoadAsync();
            _files.Clear();
            foreach (var file in _store.Items) _files.Add(file);
            BuildPeriods();
            BuildYears();
            BuildSubjectFilters();
            await LoadPeopleAsync();
            await ReloadPersonIndexAsync();
            ApplyFilters();
            if (!string.IsNullOrWhiteSpace(selectFileId))
            {
                var selected = _files.FirstOrDefault(x => x.Id == selectFileId);
                if (selected is not null) { LibraryGrid.SelectedItem = selected; LibraryGrid.ScrollIntoView(selected); }
            }
            LibraryCountText.Text = _files.Count.ToString(CultureInfo.InvariantCulture);
            StatusText.Text = $"{_files.Count} boletim(ns) na biblioteca • {_filtered.Count} achado(s) exibido(s).";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _loading = false; }
    }

    private void BuildPeriods()
    {
        var current = PeriodBox.SelectedItem?.ToString() ?? _settings.Period;
        var periods = _store.Items.Select(x => x.DisplayPeriod).Where(x => x != "—").Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(PeriodSortKey).ToList();
        PeriodBox.ItemsSource = new[] { "Todos" }.Concat(periods).ToList();
        PeriodBox.SelectedItem = PeriodBox.Items.Cast<string>().FirstOrDefault(x => x.Equals(current, StringComparison.CurrentCultureIgnoreCase)) ?? "Todos";
    }

    private void BuildYears()
    {
        var current = YearBox.SelectedItem?.ToString() ?? _settings.Year;
        var years = _store.Items
            .Select(FileYear)
            .Where(x => x > 0)
            .Distinct()
            .OrderByDescending(x => x)
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .ToList();
        YearBox.ItemsSource = new[] { "Todos" }.Concat(years).ToList();
        YearBox.SelectedItem = YearBox.Items.Cast<string>()
            .FirstOrDefault(x => x.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? "Todos";
    }

    private void BuildSubjectFilters()
    {
        var current = SubjectFilterBox.SelectedItem?.ToString() ?? _settings.SubjectFilter;
        var subjects = _store.Items
            .SelectMany(file => file.Findings)
            .Where(IsFurrielRelevantFinding)
            .Select(finding => CleanSubjectFilterValue(finding.Subject, finding.DisplaySubject))
            .Where(subject => !string.IsNullOrWhiteSpace(subject))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(subject => subject, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _subjectFilters.Clear();
        _subjectFilters.Add("Todos");
        foreach (var subject in subjects) _subjectFilters.Add(subject);
        SubjectFilterBox.SelectedItem = _subjectFilters.FirstOrDefault(x => x.Equals(current, StringComparison.CurrentCultureIgnoreCase)) ?? "Todos";
    }

    private async Task LoadPeopleAsync()
    {
        var source = SelectedPersonnelSource();
        _people = (await _service.LoadMilitaryOptionsAsync(source)).ToList();
        if (_selectedPerson is not null && !_people.Any(x => x.Id == _selectedPerson.Id && x.Source == _selectedPerson.Source))
            _selectedPerson = null;
        RefreshMilitarySuggestions(MilitaryBox.Text, openDropDown: false);
    }

    private string SelectedPersonnelSource() => (PersonnelSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ativos";

    private void ApplyFilters()
    {
        var search = SearchBox.Text.Trim();
        var period = PeriodBox.SelectedItem?.ToString() ?? "Todos";
        var year = YearBox.SelectedItem?.ToString() ?? "Todos";
        var periodMode = SelectedPeriodMode();
        var consequenceFilter = (ConsequenceFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";

        var periodFindings = _store.Items
            .Where(file => year == "Todos" || FileYear(file).ToString(CultureInfo.InvariantCulture) == year)
            .Where(file => FileMatchesPeriod(file, periodMode, period))
            .SelectMany(file => file.Findings)
            .Where(IsPrimaryIntelligentFinding)
            .ToList();

        var furrielRows = periodFindings
            .Where(IsFurrielRelevantFinding)
            .Where(finding => MatchesSearch(finding, search));

        if (consequenceFilter == "Apenas com consequência")
            furrielRows = furrielRows.Where(finding => finding.HasConsequence || finding.IsFurrielConsequence);

        var furrielList = furrielRows
            .OrderByDescending(x => ParseBulletinNumber(x.Bulletin).Year)
            .ThenByDescending(x => ParseBulletinNumber(x.Bulletin).Number)
            .ThenBy(x => x.Page)
            .ToList();

        // A pesquisa da aba Furriel não filtra Convalescença em casa.
        // Convalescença é assunto próprio do BI e deve aparecer pelo período/ano selecionado.
        var convalescenceList = periodFindings
            .Where(IsConvalescenceHomeFinding)
            .OrderByDescending(x => ParseBulletinNumber(x.Bulletin).Year)
            .ThenByDescending(x => ParseBulletinNumber(x.Bulletin).Number)
            .ThenBy(x => x.Page)
            .ToList();

        _filtered = furrielList.Concat(convalescenceList)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => ParseBulletinNumber(x.Bulletin).Year)
            .ThenByDescending(x => ParseBulletinNumber(x.Bulletin).Number)
            .ThenBy(x => x.Page)
            .ToList();

        var reviewRows = furrielList.Where(x => x.Reviewable);
        if (PendingOnlyCheckBox.IsChecked == true) reviewRows = reviewRows.Where(x => !x.Reviewed);
        ResetCollection(_review, reviewRows);
        ResetCollection(_serviceRows, _filtered.Where(x => x.Category == "Serviço"));
        ResetCollection(_vacations, _filtered.Where(x => x.Category == "Férias"));
        ResetCollection(_convalescences, convalescenceList);
        ResetCollection(_payments, furrielList);
        ResetCollection(_admin, _filtered.Where(x => x.Category == "Alertas Adm"));
        ResetCollection(_others, _filtered.Where(x => x.Category == "Outros"));
        _ = ApplyMilitarySearchAsync();
        ServiceCountText.Text = _serviceRows.Count.ToString(CultureInfo.InvariantCulture);
        VacationCountText.Text = _vacations.Count.ToString(CultureInfo.InvariantCulture);
        ConvalescenceCountText.Text = _convalescences.Count.ToString(CultureInfo.InvariantCulture);
        PaymentCountText.Text = _payments.Count.ToString(CultureInfo.InvariantCulture);
        AdminCountText.Text = _admin.Count.ToString(CultureInfo.InvariantCulture);
        OtherCountText.Text = _others.Count.ToString(CultureInfo.InvariantCulture);
        SummaryText.Text = string.Empty;
        StatusText.Text = $"{furrielList.Count} ocorrência(s) de Furriel e {_convalescences.Count} convalescença(s) em casa exibida(s). As demais notas ficam no Índice por Pessoa.";
        SaveCurrentPageSettings();
    }

    private static bool IsPrimaryIntelligentFinding(IntelligentBulletinFinding finding)
        => IsFurrielRelevantFinding(finding) || IsConvalescenceHomeFinding(finding);

    private static bool IsFurrielRelevantFinding(IntelligentBulletinFinding finding)
    {
        // A aba Furriel mostra somente registros indexados a partir da palavra literal
        // "Furriel" no texto do BI. Se o bloco também tiver militar identificado,
        // a pesquisa do PDF usa o nome completo/documento, mas a origem continua sendo Furriel.
        if (finding.Category.Equals("Furriel", StringComparison.OrdinalIgnoreCase)) return true;
        var haystack = string.Join(" ", finding.NoteText, finding.Context, finding.ConsequenceText, finding.Detail, finding.SubjectNoteDisplay, finding.Type);
        return System.Text.RegularExpressions.Regex.IsMatch(haystack, @"\bfurriel\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsConvalescenceHomeFinding(IntelligentBulletinFinding finding)
    {
        if (!finding.Category.Equals("Convalescença", StringComparison.OrdinalIgnoreCase)) return false;
        var haystack = NormalizeForSearch(string.Join(" ", finding.Subject, finding.SubjectNoteDisplay, finding.Type, finding.Detail, finding.Context, finding.NoteText));
        return haystack.Contains("convale", StringComparison.Ordinal) &&
               (haystack.Contains("casa", StringComparison.Ordinal) ||
                haystack.Contains("residencia", StringComparison.Ordinal) ||
                haystack.Contains("domicilio", StringComparison.Ordinal) ||
                haystack.Contains("lar", StringComparison.Ordinal));
    }

    private void SaveCurrentPageSettings()
    {
        if (_loading || !IsLoaded) return;
        _settings.Search = SearchBox.Text;
        _settings.PeriodMode = SelectedPeriodMode();
        _settings.Period = PeriodBox.SelectedItem?.ToString() ?? "Todos";
        _settings.Year = YearBox.SelectedItem?.ToString() ?? "Todos";
        _settings.SubjectFilter = "Todos";
        _settings.StartDate = StartDatePicker.SelectedDate;
        _settings.EndDate = EndDatePicker.SelectedDate;
        _settings.PersonnelSource = SelectedPersonnelSource();
        _settings.ConsequenceFilter = (ConsequenceFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
        _settings.SelectedTabIndex = ResultTabs.SelectedIndex;
        _settings.MilitarySearch = MilitaryBox.Text?.Trim() ?? string.Empty;
        _settings.PersonIndexSearch = string.Empty;
        _settings.PersonIndexYear = PersonIndexYearBox.SelectedItem?.ToString() ?? "Todos";
        _settings.PersonIndexMonth = PersonIndexMonthBox.SelectedItem?.ToString() ?? "Todos";
        _settings.PersonIndexSubject = FirstNonEmpty(PersonIndexSubjectBox.Text, PersonIndexSubjectBox.SelectedItem?.ToString(), "Todos");
        _settings.PersonIndexNote = FirstNonEmpty(PersonIndexNoteBox.Text, PersonIndexNoteBox.SelectedItem?.ToString(), "Todos");
        _settings.PersonIndexLinkFilter = PersonIndexLinkBox.SelectedItem?.ToString() ?? "Todos";
        _settings.PersonIndexUser = "Todos";
        _settings.PersonIndexPerson = ResolvePersonIndexPersonFilter();
        _settings.PersonIndexSubjectSearch = string.Empty;
        _settings.PersonIndexSubjectNoteSearch = string.Empty;
        _settings.PersonIndexDownloadStartDate = PersonIndexDownloadStartPicker.SelectedDate;
        _settings.PersonIndexDownloadEndDate = PersonIndexDownloadEndPicker.SelectedDate;
        _ = App.Json.SaveAsync(App.Paths.IntelligentBulletinSettingsFile, _settings);
    }

    private void ResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != ResultTabs) return;
        SaveCurrentPageSettings();
    }

    private void RestoreSelectedTab()
    {
        var requested = _settings.SelectedTabIndex;
        if (requested >= 0 && requested < ResultTabs.Items.Count && ResultTabs.Items[requested] is TabItem requestedTab && requestedTab.Visibility == Visibility.Visible)
        {
            ResultTabs.SelectedIndex = requested;
            return;
        }
        for (var i = 0; i < ResultTabs.Items.Count; i++)
        {
            if (ResultTabs.Items[i] is TabItem tab && tab.Visibility == Visibility.Visible)
            {
                ResultTabs.SelectedIndex = i;
                return;
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static void ResetCollection(ObservableCollection<IntelligentBulletinFinding> target, IEnumerable<IntelligentBulletinFinding> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static void ResetCollection(ObservableCollection<SisbolPersonIndexItem> target, IEnumerable<SisbolPersonIndexItem> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static bool MatchesSearch(IntelligentBulletinFinding finding, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var haystack = string.Join(" ", finding.Category, finding.Type, finding.Bulletin, finding.BulletinDate,
            finding.Rank, finding.FullName, finding.WarName, finding.Military, finding.DisplayMilitary, finding.Subject,
            finding.SubjectNoteDisplay, finding.DisplaySubject, finding.NoteTitle, finding.NoteText, finding.MentionedCpf,
            finding.MentionedPrecCp, finding.ConsequenceText, finding.FileName);
        var compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        if (compare.IndexOf(haystack, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0) return true;
        var normalizedHaystack = NormalizeForSearch(haystack);
        var normalizedSearch = NormalizeForSearch(search);
        if (normalizedSearch.Length > 0 && normalizedHaystack.Contains(normalizedSearch, StringComparison.Ordinal)) return true;
        var terms = normalizedSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length > 1 && terms.All(term => normalizedHaystack.Contains(term, StringComparison.Ordinal))) return true;
        var digits = Digits(search);
        return digits.Length >= 5 && Digits(haystack).Contains(digits, StringComparison.Ordinal);
    }

    private static bool MatchesSubjectFilter(IntelligentBulletinFinding finding, string subjectFilter)
    {
        if (string.IsNullOrWhiteSpace(subjectFilter) || subjectFilter.Equals("Todos", StringComparison.CurrentCultureIgnoreCase)) return true;
        var selected = NormalizeForSearch(subjectFilter);
        var subject = NormalizeForSearch(CleanSubjectFilterValue(finding.Subject, finding.DisplaySubject));
        var display = NormalizeForSearch(finding.DisplaySubject);
        return subject.Equals(selected, StringComparison.Ordinal) || display.Contains(selected, StringComparison.Ordinal);
    }

    private static string CleanSubjectFilterValue(string? subject, string? displaySubject = null)
    {
        var value = string.IsNullOrWhiteSpace(subject) ? displaySubject ?? string.Empty : subject;
        value = System.Text.RegularExpressions.Regex.Split(value, @"\s+—\s+").FirstOrDefault() ?? value;
        value = value.Trim();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded && !_loading) ApplyFilters(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_loading) ApplyFilters(); }
    private void PeriodModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePeriodModeVisuals();
        if (IsLoaded && !_loading) ApplyFilters();
    }
    private void DateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loading) ApplyFilters();
    }
    private void AutoUpdateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        _settings.AutoUpdateFromLastFolder = AutoUpdateCheckBox.IsChecked == true;
        UpdateAutoUpdateTimer();
        _ = App.Json.SaveAsync(App.Paths.IntelligentBulletinSettingsFile, _settings);
    }
    private async void PersonnelSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!IsLoaded || _loading) return; await LoadPeopleAsync(); await ApplyMilitarySearchAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var selectedId = SelectedFile?.Id;
        if (Directory.Exists(_settings.LastFolder)) await SyncLastFolderAsync();
        await ReloadAsync(selectedId);
    }

    private async void ReindexAll_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Items.Count == 0) { StatusText.Text = "Não há boletins para reindexar."; return; }
        try
        {
            IsEnabled = false;
            var errors = await _service.ReindexAllAsync(new Progress<string>(x => StatusText.Text = x));
            await ReloadAsync(SelectedFile?.Id);
            StatusText.Text = errors.Count == 0
                ? "Todos os boletins foram reindexados."
                : $"Reindexação concluída com {errors.Count} ocorrência(s).";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Importar boletins PDF ou ZIP", Filter = "PDF e ZIP|*.pdf;*.zip|PDF|*.pdf|ZIP|*.zip", Multiselect = true, InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : null };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? string.Empty;
        UpdateAutoUpdateTimer();
        await ImportAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecionar pasta de boletins", Multiselect = true, InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : null };
        if (dialog.ShowDialog(this) != true) return;
        var folders = dialog.FolderNames.Length > 0 ? dialog.FolderNames : [dialog.FolderName];
        _settings.LastFolder = folders.FirstOrDefault() ?? string.Empty;
        UpdateAutoUpdateTimer();
        await ImportAsync(folders);
    }

    private async Task ImportAsync(IEnumerable<string> sources)
    {
        try
        {
            IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await _service.ImportAsync(sources, progress);
            await ReloadAsync();
            var message = $"Novos: {result.Imported}\nAtualizados: {result.Updated}\nDuplicados ignorados: {result.Duplicates}";
            if (result.Errors.Count > 0) message += "\n\nOcorrências:\n" + string.Join("\n", result.Errors.Take(15));
            SigfurDialog.Show(this, message, "Importação de boletins", MessageBoxButton.OK, result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }


    private async void DownloadSisbolBulletins_Click(object sender, RoutedEventArgs e)
    {
        var initialYear = ResolveDownloadYear();
        var initialMonth = ResolveDownloadMonthFromFilter();
        var range = SisbolDownloadRangeDialog.Ask(this, "Boletim Interno — SisBol", initialYear, initialMonth);
        if (range is null) return;
        var year = range.Year;

        if (SigfurDialog.Show(this,
                $"Baixar/atualizar automaticamente os Boletins Internos de {range.ScopeText}?\n\n" +
                "O SIGFUR usará a sessão já preparada do SisBol. O navegador ficará oculto e a importação será feita automaticamente para a Biblioteca de Boletins.",
                "Baixar boletins do SisBol", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            IsEnabled = false;
            var downloadDir = Path.Combine(App.Paths.IntelligentBulletinTempDirectory, "sisbol_boletim_interno", year.ToString(CultureInfo.InvariantCulture));
            var progress = new Progress<string>(message => StatusText.Text = message);
            var downloaded = await App.Sisbol.DownloadGeneratedBulletinsAsync(1, year, downloadDir, replaceExisting: true, progress, CancellationToken.None, range.Month);
            if (downloaded.DownloadedFiles.Count > 0)
            {
                var import = await _service.ImportAsync(downloaded.DownloadedFiles, progress);
                await ReloadAsync();
                var message = $"Download SisBol concluído.\n\n" +
                    $"Baixados/substituídos: {downloaded.Downloaded}\n" +
                    $"Ignorados: {downloaded.Skipped}\n" +
                    $"Importados novos: {import.Imported}\n" +
                    $"Atualizados no índice: {import.Updated}\n" +
                    $"Duplicados: {import.Duplicates}";
                if (downloaded.Errors.Count > 0 || import.Errors.Count > 0)
                    message += "\n\nOcorrências:\n" + string.Join(Environment.NewLine, downloaded.Errors.Concat(import.Errors).Take(18));
                SigfurDialog.Show(this, message, "Boletins SisBol", MessageBoxButton.OK,
                    downloaded.Errors.Count == 0 && import.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            else
            {
                await ReloadAsync();
                var message = $"Nenhum Boletim Interno novo foi baixado para {year}.";
                if (downloaded.Errors.Count > 0)
                    message += "\n\nOcorrências:\n" + string.Join(Environment.NewLine, downloaded.Errors.Take(18));
                SigfurDialog.Show(this, message, "Boletins SisBol", MessageBoxButton.OK,
                    downloaded.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private int ResolveDownloadYear()
    {
        var text = YearBox.SelectedItem?.ToString();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year >= 2000) return year;
        if (int.TryParse(_settings.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out year) && year >= 2000) return year;
        return DateTime.Today.Year;
    }

    private int? ResolveDownloadMonthFromFilter()
    {
        var text = PeriodBox.SelectedItem?.ToString() ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(\d{1,2})[/-](\d{4})(?!\d)");
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12)
            return month;
        return null;
    }

    private void LibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var file = SelectedFile;
        if (file is null) { FilePreviewText.Text = "Selecione um boletim para conferir os dados do arquivo."; return; }
        FilePreviewText.Text = $"BI: {file.DisplayNumber}\nData: {file.DisplayDate}\nPeríodo: {file.DisplayPeriod}\nPáginas: {file.Pages}\nMenções indexadas: {file.Mentions.Count}\nTamanho: {file.SizeText}\n\nArquivo: {file.FileName}\nOrigem: {file.SourceFolder}\nSalvo em: {file.PdfPath}";
    }
    private void LibraryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFile_Click(sender, new RoutedEventArgs());
    private void OpenSelectedFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is null) { StatusText.Text = "Selecione um boletim."; return; }
        try
        {
                        var term = _selectedPerson?.FullName ?? string.Empty;
            _service.OpenPdf(SelectedFile.PdfPath, term);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.IntelligentBulletinLibraryDirectory);

    private async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is null) { StatusText.Text = "Selecione um boletim."; return; }
        try { IsEnabled = false; var id = SelectedFile.Id; await _service.ReindexAsync(id, new Progress<string>(x => StatusText.Text = x)); await ReloadAsync(id); }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is null) { StatusText.Text = "Selecione um boletim."; return; }
        if (SigfurDialog.Show(this, $"Remover o BI {SelectedFile.DisplayNumber} da biblioteca e apagar a cópia salva?", "Remover boletim", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _service.RemoveAsync(SelectedFile.Id, true);
        await ReloadAsync();
    }

    private IntelligentBulletinFinding? SelectedFindingFrom(object sender)
        => sender is DataGrid grid ? grid.SelectedItem as IntelligentBulletinFinding : null;
    private void FindingGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var finding = SelectedFindingFrom(sender);
        if (finding is null) return;
        try { _service.OpenPdf(finding); } catch (Exception ex) { ShowError(ex); }
    }
    private void OpenFinding_Click(object sender, RoutedEventArgs e)
    {
        if (ReviewGrid.SelectedItem is not IntelligentBulletinFinding finding) { StatusText.Text = "Selecione um achado."; return; }
        try { _service.OpenPdf(finding); } catch (Exception ex) { ShowError(ex); }
    }
    private async void MarkReviewed_Click(object sender, RoutedEventArgs e) => await SetReviewAsync(true);
    private async void MarkPending_Click(object sender, RoutedEventArgs e) => await SetReviewAsync(false);
    private async Task SetReviewAsync(bool reviewed)
    {
        var selected = ReviewGrid.SelectedItems.Cast<IntelligentBulletinFinding>().ToList();
        if (selected.Count == 0 && ReviewGrid.SelectedItem is IntelligentBulletinFinding single) selected.Add(single);
        if (selected.Count == 0) { StatusText.Text = "Selecione um ou mais itens da conferência."; return; }
        foreach (var finding in selected) await _service.SetReviewedAsync(finding, reviewed);
        ApplyFilters();
        StatusText.Text = reviewed
            ? $"Ciente registrado em {selected.Count} item(ns) do Furriel."
            : $"{selected.Count} item(ns) devolvido(s) para pendente.";
    }

    private void ReviewFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_loading) ApplyFilters();
    }

    private async void MilitaryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        if (MilitaryBox.SelectedItem is IntelligentBulletinMilitaryOption selected)
        {
            _selectedPerson = selected;
            MilitaryBox.Text = selected.DisplayLabel;
        }
        await ApplyMilitarySearchAsync();
    }

    private async void MilitaryBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Tab) return;
        if (e.Key == Key.Escape) { MilitaryBox.IsDropDownOpen = false; return; }

        var typed = MilitaryBox.Text.Trim();
        if (_selectedPerson is not null &&
            !typed.Equals(_selectedPerson.DisplayLabel, StringComparison.CurrentCultureIgnoreCase) &&
            !typed.Equals(_selectedPerson.FullName, StringComparison.CurrentCultureIgnoreCase) &&
            !typed.Equals(_selectedPerson.WarName, StringComparison.CurrentCultureIgnoreCase))
            _selectedPerson = null;

        RefreshMilitarySuggestions(typed, openDropDown: e.Key != Key.Enter);
        if (e.Key == Key.Enter && MilitaryBox.SelectedItem is IntelligentBulletinMilitaryOption chosen)
        {
            _selectedPerson = chosen;
            MilitaryBox.Text = chosen.DisplayLabel;
        }
        await ApplyMilitarySearchAsync();
        if (e.Key == Key.Enter) MilitaryBox.IsDropDownOpen = false;
    }

    private void RefreshMilitarySuggestions(string? text, bool openDropDown)
    {
        var typed = (text ?? string.Empty).Trim();
        var normalizedTyped = NormalizeForSearch(typed);
        var typedTerms = normalizedTyped.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        var matches = _people
            .Where(person => typed.Length == 0 || compare.IndexOf(person.SearchText, typed, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
                             (normalizedTyped.Length > 0 && NormalizeForSearch(person.SearchText).Contains(normalizedTyped, StringComparison.Ordinal)) ||
                             (typedTerms.Length > 1 && typedTerms.All(term => NormalizeForSearch(person.SearchText).Contains(term, StringComparison.Ordinal))) ||
                             (Digits(typed).Length >= 3 && Digits(person.SearchText).Contains(Digits(typed), StringComparison.Ordinal)))
            .Take(35)
            .ToList();
        _peopleSuggestions.Clear();
        foreach (var person in matches) _peopleSuggestions.Add(person);
        if (!openDropDown || typed.Length == 0 || matches.Count == 0) return;
        MilitaryBox.IsDropDownOpen = true;
        MilitaryBox.Text = typed;
        if (MilitaryBox.Template.FindName("PART_EditableTextBox", MilitaryBox) is TextBox editor)
        {
            editor.CaretIndex = editor.Text.Length;
            editor.SelectionLength = 0;
        }
    }

    private async void MilitarySearch_Click(object sender, RoutedEventArgs e) => await ApplyMilitarySearchAsync();
    private async void ClearMilitaryFilter_Click(object sender, RoutedEventArgs e)
    {
        _selectedPerson = null;
        MilitaryBox.SelectedItem = null;
        MilitaryBox.Text = string.Empty;
        MilitaryBox.IsDropDownOpen = false;
        ResetCollection(_militaryResults, Array.Empty<SisbolPersonIndexItem>());
        await ApplyMilitarySearchAsync();
        StatusText.Text = "Filtro por pessoa limpo.";
    }

    private async Task ApplyMilitarySearchAsync()
    {
        if (!IsLoaded) return;
        var typed = MilitaryBox.Text.Trim();
        var selected = _selectedPerson;
        var queryText = selected?.FullName ?? typed;
        if (string.IsNullOrWhiteSpace(queryText))
        {
            ResetCollection(_militaryResults, Array.Empty<SisbolPersonIndexItem>());
            return;
        }

        try
        {
            var query = new SisbolPersonIndexQuery
            {
                Search = queryText,
                Year = PersonIndexYearBox.SelectedItem?.ToString() ?? "Todos",
                Month = PersonIndexMonthBox.SelectedItem?.ToString() ?? "Todos",
                Subject = FirstNonEmpty(PersonIndexSubjectBox.Text, PersonIndexSubjectBox.SelectedItem?.ToString(), "Todos"),
                User = "Todos",
                LinkFilter = PersonIndexLinkBox.SelectedItem?.ToString() ?? "Todos",
                Note = FirstNonEmpty(PersonIndexNoteBox.Text, PersonIndexNoteBox.SelectedItem?.ToString(), "Todos"),
                SubjectOrNote = string.Empty
            };
            var rows = await App.SisbolPersonIndex.SearchAsync(query, 2500);
            ResetCollection(_militaryResults, rows);
            StatusText.Text = selected is not null
                ? $"{_militaryResults.Count} nota(s) do Índice SisBol encontrada(s) para {selected.DisplayLabel}."
                : $"{_militaryResults.Count} nota(s) do Índice SisBol encontrada(s) para {typed}.";
            SaveCurrentPageSettings();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao pesquisar notas por pessoa no Índice SisBol.", ex);
            StatusText.Text = "Não foi possível pesquisar as notas da pessoa no Índice SisBol.";
        }
    }

    private async void MilitaryIndexGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => await OpenSelectedPersonIndexBulletinFromGridAsync(MilitaryResultsGrid);

    private static bool ExactPersonMatch(
        IntelligentBulletinFinding finding,
        IntelligentBulletinMilitaryOption selected,
        IReadOnlyCollection<string> documents)
    {
        if (finding.MilitaryId.HasValue && finding.MilitaryId.Value == selected.Id) return true;
        var full = NormalizePerson(selected.FullName);
        var war = NormalizePerson(selected.WarName);
        var findingFull = NormalizePerson(finding.FullName);
        var findingWar = NormalizePerson(finding.WarName);
        var display = NormalizePerson(finding.DisplayMilitary);
        var context = NormalizePerson(string.Join(" ", finding.Context, finding.Detail));

        if (full.Length > 0 && (findingFull == full || display == full)) return true;
        if (war.Length > 1 && (findingWar == war || display == war)) return true;

        var findingDigits = Digits(string.Join(" ", finding.MentionedCpf, finding.MentionedPrecCp));
        if (documents.Any(document => findingDigits.Contains(document, StringComparison.Ordinal))) return true;
        if (finding.IsDatabaseMatch) return false;
        var contextDigits = Digits(string.Join(" ", finding.Context, finding.Detail));
        return documents.Any(document => contextDigits.Contains(document, StringComparison.Ordinal)) ||
               (full.Length > 0 && ContainsWholePhrase(context, full)) ||
               (war.Length > 1 && ContainsWholePhrase(context, war));
    }

    private static string NormalizePerson(string? value)
    {
        var characters = (value ?? string.Empty).Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ')
            .ToArray();
        return string.Join(" ", new string(characters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsWholePhrase(string text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phrase)) return false;
        return (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);
    }

    private static string NormalizeForSearch(string? value)
    {
        var characters = (value ?? string.Empty).Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')
            .ToArray();
        return string.Join(" ", new string(characters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private string SelectedPeriodMode()
        => (PeriodModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Mês/Ano";

    private void UpdatePeriodModeVisuals()
    {
        if (MonthPeriodPanel is null || DatePeriodPanel is null) return;
        var dates = SelectedPeriodMode().Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase);
        MonthPeriodPanel.Visibility = dates ? Visibility.Collapsed : Visibility.Visible;
        DatePeriodPanel.Visibility = dates ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool FileMatchesPeriod(IntelligentBulletinFile file, string mode, string period)
    {
        if (!mode.Equals("Intervalo de datas", StringComparison.OrdinalIgnoreCase))
            return period == "Todos" || file.DisplayPeriod.Equals(period, StringComparison.CurrentCultureIgnoreCase);
        var date = ParseFileDate(file);
        if (date is null) return false;
        var start = StartDatePicker.SelectedDate?.Date ?? DateTime.MinValue.Date;
        var end = EndDatePicker.SelectedDate?.Date ?? DateTime.MaxValue.Date;
        if (end < start) (start, end) = (end, start);
        return date.Value.Date >= start && date.Value.Date <= end;
    }

    private static int FileYear(IntelligentBulletinFile file)
    {
        var parsed = ParseFileDate(file);
        if (parsed is not null) return parsed.Value.Year;
        var match = System.Text.RegularExpressions.Regex.Match(
            string.Join(" ", file.Period, file.BulletinDate, file.DateIso, file.FileName, file.SourceFolderLabel),
            @"\b(20\d{2})\b");
        return match.Success && int.TryParse(match.Groups[1].Value, out var year) ? year : 0;
    }

    private static DateTime? ParseFileDate(IntelligentBulletinFile file)
    {
        if (DateTime.TryParseExact(file.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;
        return DateTime.TryParse(file.BulletinDate, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var local) ? local : null;
    }

    private async Task<bool> SyncLastFolderAsync()
    {
        if (!Directory.Exists(_settings.LastFolder)) return false;
        try
        {
            StatusText.Text = "Verificando novos boletins na pasta monitorada...";
            var result = await _service.ImportAsync([_settings.LastFolder], new Progress<string>(message => StatusText.Text = message));
            var changed = result.Imported + result.Updated > 0;
            StatusText.Text = changed
                ? $"Atualização automática: {result.Imported} novo(s) e {result.Updated} atualizado(s)."
                : "Biblioteca já estava atualizada.";
            return changed;
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha na atualização automática do Boletim Inteligente.", ex);
            StatusText.Text = "Não foi possível verificar automaticamente a pasta monitorada.";
            return false;
        }
    }

    private void UpdateAutoUpdateTimer()
    {
        if (_settings.AutoUpdateFromLastFolder && Directory.Exists(_settings.LastFolder))
        {
            if (!_autoUpdateTimer.IsEnabled) _autoUpdateTimer.Start();
        }
        else _autoUpdateTimer.Stop();
    }

    private async Task AutoUpdateTickAsync()
    {
        if (_autoUpdateRunning || _loading || !_settings.AutoUpdateFromLastFolder || !Directory.Exists(_settings.LastFolder)) return;
        _autoUpdateRunning = true;
        try
        {
            if (await SyncLastFolderAsync()) await ReloadAsync();
        }
        finally { _autoUpdateRunning = false; }
    }


    private async Task ReloadPersonIndexAsync()
    {
        try
        {
            var summary = await App.SisbolPersonIndex.GetSummaryAsync();
            var years = await App.SisbolPersonIndex.LoadYearsAsync();
            var subjects = await App.SisbolPersonIndex.LoadSubjectsAsync();
            var notes = await App.SisbolPersonIndex.LoadNotesAsync();
            var users = await App.SisbolPersonIndex.LoadUsersAsync();
            var people = await App.SisbolPersonIndex.LoadPersonOptionsAsync();
            _personIndexAllSubjects = NormalizeSuggestionList(subjects);
            _personIndexAllNotes = NormalizeSuggestionList(notes);
            _personIndexAllPeople = people.ToList();

            var selectedYear = FirstNonEmpty(PersonIndexYearBox.SelectedItem?.ToString(), _settings.PersonIndexYear, "Todos");
            var selectedMonth = FirstNonEmpty(PersonIndexMonthBox.SelectedItem?.ToString(), _settings.PersonIndexMonth, "Todos");
            var selectedSubject = FirstNonEmpty(PersonIndexSubjectBox.Text, PersonIndexSubjectBox.SelectedItem?.ToString(), _settings.PersonIndexSubject, "Todos");
            var selectedNote = FirstNonEmpty(PersonIndexNoteBox.Text, PersonIndexNoteBox.SelectedItem?.ToString(), _settings.PersonIndexNote, "Todos");
            var selectedUser = "Todos";
            var selectedLink = FirstNonEmpty(PersonIndexLinkBox.SelectedItem?.ToString(), _settings.PersonIndexLinkFilter, "Todos");
            var selectedPerson = FirstNonEmpty(PersonIndexPersonBox.Text, _settings.PersonIndexPerson, "Todos");
            ResetStringCollection(_personIndexYears, years);
            ResetStringCollection(_personIndexSubjects, _personIndexAllSubjects);
            ResetStringCollection(_personIndexNotes, _personIndexAllNotes);
            ResetStringCollection(_personIndexUsers, users);
            ResetPersonCollection(_personIndexPeople, _personIndexAllPeople);
            RebuildSuggestionSearchCaches();
            PersonIndexYearBox.SelectedItem = _personIndexYears.FirstOrDefault(x => x.Equals(selectedYear, StringComparison.OrdinalIgnoreCase)) ?? "Todos";
            PersonIndexMonthBox.SelectedItem = PersonIndexMonthBox.Items.Cast<string>().FirstOrDefault(x => x.Equals(selectedMonth, StringComparison.OrdinalIgnoreCase)) ?? "Todos";
            PersonIndexSubjectBox.Text = string.IsNullOrWhiteSpace(selectedSubject) ? "Todos" : selectedSubject;
            PersonIndexNoteBox.Text = string.IsNullOrWhiteSpace(selectedNote) ? "Todos" : selectedNote;
            PersonIndexUserBox.SelectedItem = _personIndexUsers.FirstOrDefault(x => x.Equals(selectedUser, StringComparison.OrdinalIgnoreCase)) ?? "Todos";
            PersonIndexLinkBox.SelectedItem = PersonIndexLinkBox.Items.Cast<string>().FirstOrDefault(x => x.Equals(selectedLink, StringComparison.OrdinalIgnoreCase)) ?? "Todos";
            RestorePersonIndexPersonSelection(selectedPerson);

            PersonIndexTotalText.Text = summary.TotalRecords.ToString(CultureInfo.InvariantCulture);
            PersonIndexPeopleText.Text = summary.PeopleCount.ToString(CultureInfo.InvariantCulture);
            PersonIndexLinkedText.Text = summary.LinkedCount.ToString(CultureInfo.InvariantCulture);
            PersonIndexUnlinkedText.Text = summary.UnlinkedCount.ToString(CultureInfo.InvariantCulture);
            PersonIndexPeriodText.Text = summary.PeriodText;
            PersonIndexMissingBiText.Text = await App.SisbolPersonIndex.GetMissingBulletinsTextAsync(PersonIndexYearBox.SelectedItem?.ToString() ?? "Todos");
            await ApplyPersonIndexFiltersAsync();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao carregar Índice por Pessoa na tela do Boletim Inteligente.", ex);
        }
    }

    private static void ResetStringCollection(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var list = values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.Any(x => x.Equals("Todos", StringComparison.OrdinalIgnoreCase))) list.Insert(0, "Todos");
        target.Clear();
        foreach (var item in list) target.Add(item);
    }

    private static void ResetPersonCollection(ObservableCollection<SisbolPersonIndexPersonOption> target, IEnumerable<SisbolPersonIndexPersonOption> values)
    {
        target.Clear();
        foreach (var item in values
                     .Where(x => !string.IsNullOrWhiteSpace(x.FullName))
                     .GroupBy(x => NormalizeForSearch(x.FullName), StringComparer.Ordinal)
                     .Select(group => group.First())
                     .OrderBy(x => NormalizeForSearch(x.FullName), StringComparer.Ordinal))
            target.Add(item);
    }

    private static List<string> NormalizeSuggestionList(IEnumerable<string> values)
    {
        var list = values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.Any(x => x.Equals("Todos", StringComparison.OrdinalIgnoreCase))) list.Insert(0, "Todos");
        return list;
    }

    private void FilterComboSuggestions(ComboBox combo, ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        var typed = combo.Text?.Trim() ?? string.Empty;
        var normalized = NormalizeForSearch(typed);
        var digits = Digits(typed);
        Dictionary<string, string> searchCache = combo == PersonIndexPersonBox
            ? _personIndexPeopleSearch
            : combo == PersonIndexSubjectBox
                ? _personIndexSubjectsSearch
                : _personIndexNotesSearch;

        IEnumerable<string> filtered;
        if (string.IsNullOrWhiteSpace(typed) || typed.Equals("Todos", StringComparison.OrdinalIgnoreCase))
        {
            filtered = source.Take(80);
        }
        else
        {
            var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = source.Where(value => value.Equals("Todos", StringComparison.OrdinalIgnoreCase)
                || SuggestionMatches(value, terms, digits, searchCache))
                .Take(80);
        }

        target.Clear();
        foreach (var item in filtered) target.Add(item);
        if (!string.IsNullOrWhiteSpace(typed) && target.Count > 0)
        {
            combo.IsDropDownOpen = true;
            combo.Text = typed;
            if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
            {
                editor.CaretIndex = editor.Text.Length;
                editor.SelectionLength = 0;
            }
        }
    }

    private static bool SuggestionMatches(string value, IReadOnlyList<string> terms, string digits, IReadOnlyDictionary<string, string> searchCache)
    {
        if (!searchCache.TryGetValue(value, out var normalized)) normalized = NormalizeForSearch(value) + " " + Digits(value);
        if (terms.Count > 0 && terms.All(term => normalized.Contains(term, StringComparison.Ordinal))) return true;
        return digits.Length >= 3 && normalized.Contains(digits, StringComparison.Ordinal);
    }

    private void RebuildSuggestionSearchCaches()
    {
        _personIndexPeopleSearch = _personIndexAllPeople
            .GroupBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => NormalizeForSearch(group.First().SearchText) + " " + Digits(group.First().SearchText), StringComparer.OrdinalIgnoreCase);
        _personIndexSubjectsSearch = BuildSuggestionSearchCache(_personIndexAllSubjects);
        _personIndexNotesSearch = BuildSuggestionSearchCache(_personIndexAllNotes);
    }

    private static Dictionary<string, string> BuildSuggestionSearchCache(IEnumerable<string> values)
        => values.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value, value => NormalizeForSearch(value) + " " + Digits(value), StringComparer.OrdinalIgnoreCase);

    private void RestorePersonIndexPersonSelection(string selectedPerson)
    {
        selectedPerson = string.IsNullOrWhiteSpace(selectedPerson) ? "Todos" : selectedPerson.Trim();
        if (selectedPerson.Equals("Todos", StringComparison.OrdinalIgnoreCase))
        {
            PersonIndexPersonBox.SelectedItem = null;
            PersonIndexPersonBox.Text = "Todos";
            return;
        }
        var normalized = NormalizeForSearch(selectedPerson);
        var option = _personIndexAllPeople.FirstOrDefault(x => NormalizeForSearch(x.FullName).Equals(normalized, StringComparison.Ordinal)
                                                              || NormalizeForSearch(x.Display).Equals(normalized, StringComparison.Ordinal)
                                                              || NormalizeForSearch(x.SearchText).Contains(normalized, StringComparison.Ordinal));
        if (option is not null) PersonIndexPersonBox.SelectedItem = option;
        PersonIndexPersonBox.Text = option?.Display ?? selectedPerson;
    }

    private string ResolvePersonIndexPersonFilter()
    {
        if (PersonIndexPersonBox.SelectedItem is SisbolPersonIndexPersonOption option)
            return option.FullName;
        var text = PersonIndexPersonBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return "Todos";
        return Regex.Replace(text, @"\s*\(\d+\)\s*$", string.Empty).Trim();
    }

    private void FilterPersonSuggestions(ComboBox combo)
    {
        var typed = combo.Text?.Trim() ?? string.Empty;
        var normalized = NormalizeForSearch(typed);
        var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var digits = Digits(typed);
        IEnumerable<SisbolPersonIndexPersonOption> filtered = string.IsNullOrWhiteSpace(typed) || typed.Equals("Todos", StringComparison.OrdinalIgnoreCase)
            ? _personIndexAllPeople.Take(80)
            : _personIndexAllPeople.Where(option => SuggestionMatches(option.Display, terms, digits, _personIndexPeopleSearch)
                                                    || terms.All(term => NormalizeForSearch(option.SearchText).Contains(term, StringComparison.Ordinal)))
                .Take(80);
        _personIndexPeople.Clear();
        foreach (var item in filtered) _personIndexPeople.Add(item);
        if (!string.IsNullOrWhiteSpace(typed) && _personIndexPeople.Count > 0)
        {
            combo.IsDropDownOpen = true;
            combo.Text = typed;
            if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
            {
                editor.CaretIndex = editor.Text.Length;
                editor.SelectionLength = 0;
            }
        }
    }

    private void QueuePersonIndexFilter(bool includeMilitaryResults = false)
    {
        if (!IsLoaded || _loading) return;
        _personIndexFilterQueuedMilitaryRefresh |= includeMilitaryResults;
        _personIndexFilterTimer.Stop();
        _personIndexFilterTimer.Start();
        StatusText.Text = "Aguardando digitação para pesquisar no Índice SisBol...";
        SaveCurrentPageSettings();
    }

    private async Task RunQueuedPersonIndexFilterAsync()
    {
        _personIndexFilterTimer.Stop();
        if (!IsLoaded || _loading) return;
        if (_personIndexFilterRunning)
        {
            _personIndexFilterTimer.Start();
            return;
        }
        var refreshMilitary = _personIndexFilterQueuedMilitaryRefresh;
        _personIndexFilterQueuedMilitaryRefresh = false;
        _personIndexFilterCts?.Cancel();
        _personIndexFilterCts?.Dispose();
        _personIndexFilterCts = new CancellationTokenSource();
        var token = _personIndexFilterCts.Token;
        try
        {
            await ApplyPersonIndexFiltersAsync(token);
            if (refreshMilitary) await ApplyMilitarySearchAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ApplyPersonIndexFiltersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLoaded) return;
        _personIndexFilterRunning = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusText.Text = "Pesquisando no Índice SisBol...";
            var query = new SisbolPersonIndexQuery
            {
                Search = string.Empty,
                Year = PersonIndexYearBox.SelectedItem?.ToString() ?? "Todos",
                Month = PersonIndexMonthBox.SelectedItem?.ToString() ?? "Todos",
                Subject = FirstNonEmpty(PersonIndexSubjectBox.Text, PersonIndexSubjectBox.SelectedItem?.ToString(), "Todos"),
                User = "Todos",
                LinkFilter = PersonIndexLinkBox.SelectedItem?.ToString() ?? "Todos",
                Person = ResolvePersonIndexPersonFilter(),
                Note = FirstNonEmpty(PersonIndexNoteBox.Text, PersonIndexNoteBox.SelectedItem?.ToString(), "Todos"),
                SubjectOrNote = string.Empty
            };
            var limit = ResolvePersonIndexLimit(query);
            var rows = await App.SisbolPersonIndex.SearchAsync(query, limit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplacePersonIndexRows(rows);
            PersonIndexMissingBiText.Text = await App.SisbolPersonIndex.GetMissingBulletinsTextAsync(query.Year, cancellationToken);
            PersonIndexVisibleText.Text = _personIndexRows.Count.ToString(CultureInfo.InvariantCulture);
            var capped = rows.Count >= limit ? $" Exibindo os {limit:N0} primeiros resultados; refine pessoa, assunto ou nota para afinar." : string.Empty;
            StatusText.Text = $"Pesquisa concluída: {_personIndexRows.Count:N0} registro(s).{capped}";
            SaveCurrentPageSettings();
        }
        finally
        {
            _personIndexFilterRunning = false;
        }
    }

    private static int ResolvePersonIndexLimit(SisbolPersonIndexQuery query)
    {
        var hasPreciseFilter = HasMeaningfulFilter(query.Person)
            || HasMeaningfulFilter(query.Subject)
            || HasMeaningfulFilter(query.Note)
            || !string.IsNullOrWhiteSpace(query.SubjectOrNote)
            || !string.IsNullOrWhiteSpace(query.Search)
            || HasMeaningfulFilter(query.User)
            || HasMeaningfulFilter(query.Year)
            || HasMeaningfulFilter(query.Month)
            || HasMeaningfulFilter(query.LinkFilter);
        return hasPreciseFilter ? 2500 : 800;
    }

    private static bool HasMeaningfulFilter(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.Trim().Equals("Todos", StringComparison.OrdinalIgnoreCase);

    private void ReplacePersonIndexRows(IReadOnlyList<SisbolPersonIndexItem> rows)
    {
        PersonIndexGrid.ItemsSource = null;
        try
        {
            _personIndexRows.Clear();
            foreach (var row in rows) _personIndexRows.Add(row);
        }
        finally
        {
            PersonIndexGrid.ItemsSource = _personIndexRows;
        }
    }

    private void PersonIndexFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        QueuePersonIndexFilter(includeMilitaryResults: true);
    }

    private void PersonIndexSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueuePersonIndexFilter(includeMilitaryResults: true);
    }

    private void PersonIndexDownloadDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        SaveCurrentPageSettings();
    }

    private void PersonIndexCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Tab) return;
        if (sender is ComboBox combo)
        {
            if (combo == PersonIndexPersonBox) FilterPersonSuggestions(combo);
            else if (combo == PersonIndexSubjectBox) FilterComboSuggestions(combo, _personIndexSubjects, _personIndexAllSubjects);
            else if (combo == PersonIndexNoteBox) FilterComboSuggestions(combo, _personIndexNotes, _personIndexAllNotes);
        }
        QueuePersonIndexFilter(includeMilitaryResults: true);
    }

    private async void ClearPersonIndexFilters_Click(object sender, RoutedEventArgs e)
    {
        PersonIndexSearchBox.Clear();
        PersonIndexSubjectSearchBox.Clear();
        PersonIndexSubjectNoteSearchBox.Clear();
        PersonIndexPersonBox.SelectedItem = null;
        PersonIndexPersonBox.Text = "Todos";
        PersonIndexYearBox.SelectedItem = "Todos";
        PersonIndexMonthBox.SelectedItem = "Todos";
        PersonIndexSubjectBox.SelectedItem = null;
        PersonIndexSubjectBox.Text = "Todos";
        PersonIndexNoteBox.SelectedItem = null;
        PersonIndexNoteBox.Text = "Todos";
        PersonIndexLinkBox.SelectedItem = "Todos";
        PersonIndexUserBox.SelectedItem = "Todos";
        await ApplyPersonIndexFiltersAsync();
        StatusText.Text = "Filtros do Índice por Pessoa limpos.";
    }

    private async void ImportPersonIndex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar Índice Remissivo por Pessoa do SisBol",
            Filter = "PDF do Índice por Pessoa|*.pdf|PDF|*.pdf",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await App.SisbolPersonIndex.ImportAsync(dialog.FileName, progress);
            await ReloadPersonIndexAsync();
            SigfurDialog.Show(this,
                $"Índice por Pessoa importado com sucesso.\n\nRegistros: {result.Imported}\nVinculados ao banco: {result.Linked}\nSem vínculo: {result.Unlinked}\nPeríodo: {result.IndexStartDate:dd/MM/yyyy} a {result.IndexEndDate:dd/MM/yyyy}",
                "Índice SisBol", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private async void DownloadPersonIndex_Click(object sender, RoutedEventArgs e)
    {
        var start = PersonIndexDownloadStartPicker.SelectedDate ?? _settings.PersonIndexDownloadStartDate ?? new DateTime(DateTime.Today.Year, 1, 1);
        var end = PersonIndexDownloadEndPicker.SelectedDate ?? _settings.PersonIndexDownloadEndDate ?? DateTime.Today;
        if (end.Date < start.Date) (start, end) = (end.Date, start.Date);
        PersonIndexDownloadStartPicker.SelectedDate = start.Date;
        PersonIndexDownloadEndPicker.SelectedDate = end.Date;
        SaveCurrentPageSettings();
        if (SigfurDialog.Show(this,
                $"Baixar o Índice por Pessoa do SisBol de {start:dd/MM/yyyy} a {end:dd/MM/yyyy}?\n\nO SIGFUR usará a sessão já preparada do SisBol, aceitará a confirmação da página e importará o PDF automaticamente.",
                "Baixar Índice por Pessoa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            var downloaded = await App.Sisbol.DownloadPersonIndexAsync(start, end, 1, App.Paths.SisbolPersonIndexDirectory, progress, CancellationToken.None);
            if (!downloaded.Downloaded)
            {
                StatusText.Text = "O SisBol não retornou o PDF do Índice por Pessoa.";
                return;
            }
            var imported = await App.SisbolPersonIndex.ImportAsync(downloaded.FilePath, progress);
            await ReloadPersonIndexAsync();
            SigfurDialog.Show(this,
                $"Índice por Pessoa baixado e importado.\n\nArquivo: {Path.GetFileName(downloaded.FilePath)}\nRegistros: {imported.Imported}\nVinculados: {imported.Linked}\nSem vínculo: {imported.Unlinked}",
                "Índice SisBol", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private async void ReprocessPersonIndex_Click(object sender, RoutedEventArgs e)
    {
        if (PersonIndexGrid.SelectedItem is not SisbolPersonIndexItem selected || string.IsNullOrWhiteSpace(selected.SourcePdfPath) || !File.Exists(selected.SourcePdfPath))
        {
            StatusText.Text = "Selecione um registro do índice que possua PDF de origem.";
            return;
        }
        try
        {
            IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            await App.SisbolPersonIndex.ImportAsync(selected.SourcePdfPath, progress);
            await ReloadPersonIndexAsync();
            StatusText.Text = "Índice por Pessoa reprocessado.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private void OpenPersonIndexSource_Click(object sender, RoutedEventArgs e)
    {
        if (PersonIndexGrid.SelectedItem is not SisbolPersonIndexItem selected || string.IsNullOrWhiteSpace(selected.SourcePdfPath) || !File.Exists(selected.SourcePdfPath))
        {
            StatusText.Text = "Selecione um registro com PDF de origem disponível.";
            return;
        }
        ShellService.OpenPath(selected.SourcePdfPath);
        StatusText.Text = "PDF do Índice por Pessoa aberto.";
    }

    private async void OpenPersonIndexBulletin_Click(object sender, RoutedEventArgs e)
        => await OpenSelectedPersonIndexBulletinFromGridAsync(PersonIndexGrid);

    private async void PersonIndexGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => await OpenSelectedPersonIndexBulletinFromGridAsync(PersonIndexGrid);

    private async Task OpenSelectedPersonIndexBulletinFromGridAsync(DataGrid grid)
    {
        if (grid.SelectedItem is not SisbolPersonIndexItem selected)
        {
            StatusText.Text = "Selecione um registro do Índice por Pessoa.";
            return;
        }
        var store = await _service.LoadAsync();
        var bulletinNumber = selected.BulletinNumber;
        var bulletinDate = selected.BulletinDate?.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;
        var bulletinShortNumber = bulletinNumber.Split('/').FirstOrDefault() ?? string.Empty;
        var file = store.Items.FirstOrDefault(item =>
        {
            var itemShortNumber = item.BulletinNumber.Split('/').FirstOrDefault() ?? string.Empty;
            var sameNumber = item.BulletinNumber.Equals(bulletinNumber, StringComparison.OrdinalIgnoreCase)
                             || itemShortNumber.Equals(bulletinShortNumber, StringComparison.OrdinalIgnoreCase);
            var sameDate = string.IsNullOrWhiteSpace(bulletinDate) || item.BulletinDate.Equals(bulletinDate, StringComparison.OrdinalIgnoreCase);
            return sameNumber && sameDate;
        });
        if (file is not null && File.Exists(file.PdfPath))
        {
            var hasPersonFilter = HasMeaningfulFilter(ResolvePersonIndexPersonFilter());
            var searchTerm = selected.OpenSearchTerm;
            if (selected.AggregatedPersonCount > 1 && !hasPersonFilter)
                searchTerm = string.Empty;

            _service.OpenPdf(file.PdfPath, searchTerm, selected.BulletinPage ?? 0);
            StatusText.Text = string.IsNullOrWhiteSpace(searchTerm)
                ? $"BI {selected.BulletinNumber} aberto. A nota tem {selected.AggregatedPersonCount} pessoa(s); selecione uma pessoa para pesquisar o nome automaticamente."
                : $"BI {selected.BulletinNumber} aberto; pesquisando {searchTerm}.";
            return;
        }
        if (SigfurDialog.Show(this,
                $"BI nº {selected.BulletinNumber} de {selected.DateText} ainda não foi localizado na biblioteca do SIGFUR.\n\nDeseja tentar baixar os boletins do SisBol agora?",
                "BI não localizado", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            DownloadSisbolBulletins_Click(this, new RoutedEventArgs());
        }
    }

    private async void ExportPersonIndexCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_personIndexRows.Count == 0) { StatusText.Text = "Não há registros do Índice por Pessoa para exportar."; return; }
        var dialog = new SaveFileDialog { Title = "Exportar Índice por Pessoa", Filter = "CSV UTF-8|*.csv", FileName = "Indice_SisBol_Por_Pessoa.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await App.SisbolPersonIndex.ExportCsvAsync(dialog.FileName, _personIndexRows);
        StatusText.Text = "Índice por Pessoa exportado.";
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_filtered.Count == 0) { StatusText.Text = "Não há achados para exportar."; return; }
        var dialog = new SaveFileDialog { Title = "Exportar achados do Boletim Inteligente", Filter = "CSV UTF-8|*.csv", FileName = "Boletim_Inteligente_Achados.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await _service.ExportCsvAsync(dialog.FileName, _filtered);
        StatusText.Text = "Achados exportados.";
    }
    private void CopySummary_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(SummaryText.Text); StatusText.Text = "Resumo copiado."; }
    private async void SaveSummary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Salvar resumo dos boletins", Filter = "Texto|*.txt", FileName = "Resumo_Boletins.txt" };
        if (dialog.ShowDialog(this) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, SummaryText.Text, new UTF8Encoding(true));
        StatusText.Text = "Resumo salvo.";
    }

    private async void OpenTransportAid_Click(object sender, RoutedEventArgs e) => await OpenHostActionAsync("aux_transporte");
    private async void OpenVacationPlan_Click(object sender, RoutedEventArgs e) => await OpenHostActionAsync("plano_ferias");
    private async void OpenBulletin_Click(object sender, RoutedEventArgs e) => await OpenHostActionAsync("boletim");

    private async Task OpenHostActionAsync(string actionId)
    {
        if (Owner is MainWindow main)
        {
            await main.ExecuteChildActionAsync(actionId);
            return;
        }
        StatusText.Text = "Abra este módulo a partir da janela principal para usar este atalho.";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { Refresh_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); LibraryGrid.UnselectAll(); e.Handled = true; }
    }

    private static (int Year, int Number) ParseBulletinNumber(string value)
    {
        var parts = (value ?? string.Empty).Split('/');
        return (parts.Length > 1 && int.TryParse(parts[1], out var year) ? year : 0, int.TryParse(parts[0], out var number) ? number : 0);
    }
    private static int PeriodSortKey(string period)
    {
        if (DateTime.TryParseExact("01/" + period, "dd/MMMM/yyyy", CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)) return date.Year * 100 + date.Month;
        return 0;
    }
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private void ShowError(Exception ex) { StatusText.Text = ex.Message; SigfurDialog.Show(this, ex.Message, "SIGFUR — Boletim Inteligente", MessageBoxButton.OK, MessageBoxImage.Error); }
}
