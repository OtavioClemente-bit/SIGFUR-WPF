using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;
using SIGFUR.Wpf.Views.Documents;

namespace SIGFUR.Wpf.Views.Vacation;

public partial class VacationPlanWindow : Window
{
    private sealed record AutomaticNoteChoice(string ModelName, VacationPeriod Period, int Days);

    private readonly VacationPlanService _service;
    private readonly BulletinService _bulletinService;
    private readonly ObservableCollection<VacationPeriod> _periods = [];
    private readonly ObservableCollection<VacationMilitaryOption> _military = [];
    private readonly ObservableCollection<VacationAllocation> _allocations = [];
    private ICollectionView? _militaryView;
    private ICollectionView? _allocationView;
    private VacationPreferences _preferences = new();
    private VacationBulletinStore _bulletinStore = new();
    private VacationSavedBulletin? _currentSaved;
    private Dictionary<int, int> _annualDays = [];
    private Dictionary<int, MilitaryRecord> _militaryLookup = [];
    private HashSet<int> _currentPeriodMilitaryIds = [];
    private int _militaryLoadedYear;
    private IReadOnlyList<int> _customOrder = [];
    private bool _loading = true;
    private int _allocationLoadVersion;
    private bool _operationInProgress;
    private readonly DispatcherTimer _modelSaveTimer;
    private readonly DispatcherTimer _savedSaveTimer;
    private readonly DispatcherTimer _militaryFilterTimer;
    private readonly DispatcherTimer _allocationFilterTimer;
    private readonly DispatcherTimer _preferenceSaveTimer;
    private readonly Dictionary<int, string> _militarySearchIndex = [];
    private readonly Dictionary<int, string> _allocationSearchIndex = [];
    private readonly Dictionary<int, VacationIndividualWindow> _individualWindows = [];

    private int CurrentYear => int.TryParse(YearBox.SelectedItem?.ToString(), out var value) ? value : DateTime.Today.Year;
    private VacationPeriod? SelectedPeriod => PeriodsList.SelectedItem as VacationPeriod;
    private VacationPeriod? BulletinPeriod => BulletinPeriodBox.SelectedItem as VacationPeriod;
    private string? SelectedModelName => ModelsList.SelectedItem?.ToString();

    public VacationPlanWindow(VacationPlanService service)
    {
        _service = service;
        _bulletinService = new BulletinService(App.Paths, App.Json, App.Log);
        InitializeComponent();
        App.UiState.Attach(this);
        _modelSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _modelSaveTimer.Tick += async (_, _) => { _modelSaveTimer.Stop(); await SaveCurrentModelAsync(); };
        _savedSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _savedSaveTimer.Tick += async (_, _) => { _savedSaveTimer.Stop(); await SaveCurrentBulletinAsync(); };
        _militaryFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _militaryFilterTimer.Tick += (_, _) => { _militaryFilterTimer.Stop(); RefreshMilitaryFilter(sort: false); };
        _allocationFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _allocationFilterTimer.Tick += (_, _) => { _allocationFilterTimer.Stop(); RefreshAllocationFilter(); };
        _preferenceSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _preferenceSaveTimer.Tick += async (_, _) => { _preferenceSaveTimer.Stop(); await PersistPreferencesAsync(); };
        Loaded += OnLoaded;
        Closing += async (_, _) => await PersistPreferencesAsync();
        Closed += (_, _) =>
        {
            _militaryFilterTimer.Stop();
            _allocationFilterTimer.Stop();
            _preferenceSaveTimer.Stop();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var preferencesTask = Task.Run(() => _service.LoadPreferencesAsync());
            var bulletinStoreTask = Task.Run(() => _service.LoadBulletinStoreAsync());
            var customOrderTask = Task.Run(() => App.MilitaryPreferences.LoadCustomOrderAsync());
            await Task.WhenAll(preferencesTask, bulletinStoreTask, customOrderTask);
            _preferences = await preferencesTask;
            _bulletinStore = await bulletinStoreTask;
            _customOrder = await customOrderTask;
            for (var year = DateTime.Today.Year - 4; year <= DateTime.Today.Year + 6; year++) YearBox.Items.Add(year.ToString(CultureInfo.InvariantCulture));
            YearBox.SelectedItem = _preferences.LastYear.ToString(CultureInfo.InvariantCulture);
            if (YearBox.SelectedItem is null) YearBox.SelectedItem = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
            DaysBox.SelectedIndex = _preferences.DefaultDays switch { 10 => 0, 15 => 1, _ => 2 };
            AvailableOnlyCheck.IsChecked = _preferences.AvailableOnly;
            MilitarySearchBox.Text = _preferences.Search;
            AllocationSearchBox.Text = _preferences.AllocationSearch;
            AllocationStatusBox.SelectedItem = AllocationStatusBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), _preferences.AllocationStatus, StringComparison.OrdinalIgnoreCase)) ?? AllocationStatusBox.Items[0];
            SelectSortMode(_preferences.SortMode);
            BulletinBiBox.Text = FirstVacationValue("BI_REFERENCIA", "PUBLICACAO_BI", "REFERENCIA_BOLETIM", "NUM_BI", "BI_NUMERO");
            SpecificSubjectBox.Text = _preferences.SpecificSubjectName;
            SpecificCodeBox.Text = _preferences.SpecificSubjectCode;
            MainTabs.SelectedIndex = Math.Clamp(_preferences.LastTab, 0, 1);
            PeriodsList.ItemsSource = _periods;
            BulletinPeriodBox.ItemsSource = _periods;
            MilitaryGrid.ItemsSource = _military;
            AllocationsGrid.ItemsSource = _allocations;
            await RefreshAllAsync(selectPeriodId: _preferences.LastPeriodId);
            RefreshModelsList(_preferences.LastModel);
            RefreshSavedBulletins();
            StatusText.Text = "Plano de Férias carregado.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao abrir o Plano de Férias.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _loading = false; RefreshSisbolStatus(); }
    }

    private async Task RefreshAllAsync(int? selectPeriodId = null, bool reloadMilitary = false)
    {
        _loading = true;
        try
        {
            HeaderYearText.Text = CurrentYear.ToString(CultureInfo.InvariantCulture);
            var oldId = selectPeriodId ?? SelectedPeriod?.Id ?? _preferences.LastPeriodId;
            var bulletinId = BulletinPeriod?.Id ?? oldId;
            var year = CurrentYear;
            var periodsTask = Task.Run(() => _service.GetPeriodsAsync(year));
            var annualTask = Task.Run(() => _service.GetAnnualDaysAsync(year));
            if (reloadMilitary || _militaryLookup.Count == 0 || _militaryLoadedYear != CurrentYear)
                await EnsureMilitaryCacheAsync(force: true);
            var periods = await periodsTask;
            _annualDays = await annualTask;
            _periods.Clear();
            foreach (var period in periods) _periods.Add(period);
            PeriodsList.SelectedItem = _periods.FirstOrDefault(x => x.Id == oldId) ?? _periods.FirstOrDefault();
            BulletinPeriodBox.SelectedItem = _periods.FirstOrDefault(x => x.Id == bulletinId) ?? _periods.FirstOrDefault();
            await LoadAllocationsAsync();
            RefreshMilitaryOptions();
            UpdateSummary();
        }
        finally { _loading = false; }
    }

    private async Task EnsureMilitaryCacheAsync(bool force = false)
    {
        if (!force && _militaryLookup.Count > 0 && _militaryLoadedYear == CurrentYear) return;
        var rows = await Task.Run(() => App.MilitaryRepository.GetAllAsync());
        await Task.Run(() => App.MilitaryPreferences.ApplyAsync(rows));
        _militaryLookup = rows.ToDictionary(x => x.Id);
        _militaryLoadedYear = CurrentYear;
    }

    private void RefreshMilitaryOptions()
    {
        var selectedIds = MilitaryGrid.SelectedItems.Cast<VacationMilitaryOption>().Select(x => x.Military.Id).ToHashSet();
        MilitaryGrid.ItemsSource = null;
        _military.Clear();
        _militarySearchIndex.Clear();
        foreach (var item in _militaryLookup.Values.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var option = new VacationMilitaryOption { Military = item, UsedDays = _annualDays.GetValueOrDefault(item.Id) };
            _military.Add(option);
            _militarySearchIndex[item.Id] = Normalize(option.SearchText);
        }
        _militaryView = CollectionViewSource.GetDefaultView(_military);
        _militaryView.Filter = item => FilterMilitary(item as VacationMilitaryOption);
        MilitaryGrid.ItemsSource = _militaryView;
        RefreshRanks();
        ApplyMilitarySort();
        _militaryView.Refresh();
        foreach (var option in _military.Where(x => selectedIds.Contains(x.Military.Id))) MilitaryGrid.SelectedItems.Add(option);
        AvailableCountText.Text = $"{_militaryView.Cast<object>().Count()} disponível(is)";
    }

    private void RefreshMilitaryAvailability(bool updateAnnualDays)
    {
        if (updateAnnualDays)
        {
            foreach (var option in _military)
                option.UsedDays = _annualDays.GetValueOrDefault(option.Military.Id);
        }
        _militaryView?.Refresh();
        AvailableCountText.Text = $"{_militaryView?.Cast<object>().Count() ?? 0} disponível(is)";
    }

    private bool FilterMilitary(VacationMilitaryOption? option)
    {
        if (option is null) return false;
        if (AvailableOnlyCheck.IsChecked == true && (option.RemainingDays <= 0 || _currentPeriodMilitaryIds.Contains(option.Military.Id))) return false;
        var rank = RankFilterBox.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(rank) && rank != "Todos" && !string.Equals(option.Military.Rank, rank, StringComparison.OrdinalIgnoreCase)) return false;
        var search = Normalize(MilitarySearchBox.Text);
        if (string.IsNullOrWhiteSpace(search)) return true;
        return _militarySearchIndex.TryGetValue(option.Military.Id, out var indexed)
               && indexed.Contains(search, StringComparison.Ordinal);
    }

    private void RefreshRanks()
    {
        var current = RankFilterBox.SelectedItem?.ToString() ?? _preferences.Rank;
        RankFilterBox.Items.Clear();
        RankFilterBox.Items.Add("Todos");
        foreach (var rank in _military.Select(x => x.Military.Rank).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => MilitaryRankService.GetOrder(x))) RankFilterBox.Items.Add(rank);
        RankFilterBox.SelectedItem = RankFilterBox.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), current, StringComparison.OrdinalIgnoreCase)) ?? "Todos";
    }

    private void ApplyMilitarySort()
    {
        if (_militaryView is null) return;
        var mode = (SortModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Posto/Graduação";
        var positions = _customOrder.Select((id, index) => (id, index)).GroupBy(x => x.id).ToDictionary(x => x.Key, x => x.Min(v => v.index));
        using (_militaryView.DeferRefresh())
        {
            _militaryView.SortDescriptions.Clear();
            if (_militaryView is not ListCollectionView listView) return;
            listView.CustomSort = Comparer<VacationMilitaryOption>.Create((a, b) =>
            {
                if (mode == "Nome")
                    return StringComparer.CurrentCultureIgnoreCase.Compare(a.Military.Name, b.Military.Name);
                if (mode == "Ordem salva")
                {
                    var order = positions.GetValueOrDefault(a.Military.Id, int.MaxValue).CompareTo(positions.GetValueOrDefault(b.Military.Id, int.MaxValue));
                    if (order != 0) return order;
                }
                return MilitaryRankService.Compare(a.Military.Rank, a.Military.Name, b.Military.Rank, b.Military.Name);
            });
        }
    }

    private async Task LoadAllocationsAsync()
    {
        var version = ++_allocationLoadVersion;
        var period = SelectedPeriod;
        if (period is null)
        {
            ApplyAllocations([], null);
            return;
        }
        await EnsureMilitaryCacheAsync();
        var year = CurrentYear;
        var rows = await Task.Run(() => _service.GetAllocationsAsync(year, period.Id, _militaryLookup));
        if (version != _allocationLoadVersion || SelectedPeriod?.Id != period.Id) return;
        ApplyAllocations(rows, period);
    }

    private void ApplyAllocations(IReadOnlyList<VacationAllocation> rows, VacationPeriod? period)
    {
        AllocationsGrid.ItemsSource = null;
        _allocations.Clear();
        _currentPeriodMilitaryIds.Clear();
        _allocationSearchIndex.Clear();
        foreach (var allocation in rows)
        {
            _allocations.Add(allocation);
            _currentPeriodMilitaryIds.Add(allocation.MilitaryId);
            _allocationSearchIndex[allocation.Id] = Normalize(string.Join(" ", allocation.Military.Rank, allocation.Rank,
                allocation.Name, allocation.WarName, allocation.Cpf, allocation.PrecCp, allocation.Military.MilitaryId));
        }
        _allocationView = CollectionViewSource.GetDefaultView(_allocations);
        _allocationView.Filter = FilterAllocation;
        AllocationsGrid.ItemsSource = _allocationView;
        SelectedPeriodCaption.Text = period?.FullLabel ?? "Nenhum período selecionado";
        RefreshAllocationFilter();
    }

    private bool FilterAllocation(object item)
    {
        if (item is not VacationAllocation allocation) return false;
        var status = (AllocationStatusBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
        if (status == "Pendentes" && allocation.IsPaid) return false;
        if (status == "Pagos" && !allocation.IsPaid) return false;
        var query = Normalize(AllocationSearchBox.Text);
        if (string.IsNullOrWhiteSpace(query)) return true;
        return _allocationSearchIndex.TryGetValue(allocation.Id, out var indexed)
               && indexed.Contains(query, StringComparison.Ordinal);
    }

    private void RefreshAllocationFilter()
    {
        _allocationView?.Refresh();
        AllocatedCountText.Text = $"{_allocationView?.Cast<object>().Count() ?? 0}/{_allocations.Count} militar(es)";
    }

    private void AllocationFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshAllocationFilter();
    }
    private void AllocationFilter_Changed(object sender, TextChangedEventArgs e) => AllocationFilter_Changed(sender, new RoutedEventArgs());
    private void AllocationFilter_Changed(object sender, SelectionChangedEventArgs e) => AllocationFilter_Changed(sender, new RoutedEventArgs());

    private void AllocationSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _allocationFilterTimer.Stop();
        _allocationFilterTimer.Start();
    }

    private void UpdateSummary()
    {
        var used = _annualDays.Count(x => x.Value > 0);
        var completed = _annualDays.Count(x => x.Value >= 30);
        SummaryText.Text = $"{_periods.Count} período(s) · {used} militar(es) com planejamento · {completed} com 30 dias distribuídos";
        AvailableCountText.Text = _militaryView is null ? "0" : $"{_militaryView.Cast<object>().Count()} disponível(is)";
        RefreshSisbolStatus();
    }

    private void RefreshSisbolStatus() => SisbolStatusText.Text = App.Sisbol.IsReady ? "SisBol: sessão preparada" : "SisBol: não preparado (prepare na janela principal)";

    private async Task RefreshOperationalStateAsync()
    {
        var period = SelectedPeriod;
        await EnsureMilitaryCacheAsync();
        var year = CurrentYear;
        var annualTask = Task.Run(() => _service.GetAnnualDaysAsync(year));
        var allocationsTask = period is null
            ? Task.FromResult<IReadOnlyList<VacationAllocation>>([])
            : Task.Run(() => _service.GetAllocationsAsync(year, period.Id, _militaryLookup));
        await Task.WhenAll(annualTask, allocationsTask);
        _annualDays = await annualTask;
        if (SelectedPeriod?.Id == period?.Id) ApplyAllocations(await allocationsTask, period);
        RefreshMilitaryAvailability(updateAnnualDays: true);
        UpdateSummary();
    }

    private async Task RunOperationAsync(Func<Task> operation, string progressText)
    {
        if (_operationInProgress) return;
        _operationInProgress = true;
        try
        {
            StatusText.Text = progressText;
            await Dispatcher.Yield(DispatcherPriority.Background);
            await operation();
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private async void YearBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        await RefreshAllAsync(reloadMilitary: true);
        await PersistPreferencesAsync();
    }

    private async void PeriodsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPeriod is not { } period) return;
        PeriodNameBox.Text = period.Name;
        PeriodStartPicker.SelectedDate = period.StartDate;
        PeriodEndPicker.SelectedDate = period.EndDate;
        _preferences.LastPeriodId = period.Id;
        if (_loading) return;
        await LoadAllocationsAsync();
        RefreshMilitaryAvailability(updateAnnualDays: false);
        UpdateSummary();
        await PersistPreferencesAsync();
    }

    private async void SavePeriod_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPeriod is not { } period) return;
        try
        {
            // Força o DatePicker a confirmar o texto digitado antes da leitura.
            // Sem isso, ao clicar em Salvar logo após digitar, o WPF podia manter
            // apenas a data inicial ou apenas a final no SelectedDate.
            Keyboard.ClearFocus();
            period.Name = PeriodNameBox.Text.Trim();
            period.StartDate = ReadDatePicker(PeriodStartPicker);
            period.EndDate = ReadDatePicker(PeriodEndPicker);
            await Task.Run(() => _service.SavePeriodAsync(period));
            await RefreshAllAsync(period.Id);
            StatusText.Text = "Período salvo.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static DateTime? ReadDatePicker(DatePicker picker)
    {
        if (picker.SelectedDate is { } selected) return selected.Date;
        var text = picker.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(text, formats, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            return parsed.Date;
        throw new InvalidOperationException($"Data inválida: {text}. Use o formato dd/mm/aaaa.");
    }

    private async void NewSpecialPeriod_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var year = CurrentYear;
            var period = await Task.Run(() => _service.CreateSpecialPeriodAsync(year));
            await RefreshAllAsync(period.Id);
            PeriodNameBox.Focus();
            PeriodNameBox.SelectAll();
            StatusText.Text = "Período especial criado. Informe nome e datas.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DeleteSpecialPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPeriod is not { } period) return;
        if (!period.IsSpecial) { SigfurDialog.Show(this, "Os nove períodos padrão não podem ser excluídos.", "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (SigfurDialog.Show(this, $"Excluir '{period.Name}' e todas as alocações nele?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await Task.Run(() => _service.DeleteSpecialPeriodAsync(period.Id)); await RefreshAllAsync(); StatusText.Text = "Período especial excluído."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void MilitaryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshMilitaryFilter(sort: true);
    }
    private void MilitaryFilter_Changed(object sender, SelectionChangedEventArgs e) => MilitaryFilter_Changed(sender, new RoutedEventArgs());

    private void MilitarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _militaryFilterTimer.Stop();
        _militaryFilterTimer.Start();
    }

    private void RefreshMilitaryFilter(bool sort)
    {
        if (sort) ApplyMilitarySort();
        _militaryView?.Refresh();
        AvailableCountText.Text = $"{_militaryView?.Cast<object>().Count() ?? 0} disponível(is)";
    }

    private async void Allocate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPeriod is null) { SigfurDialog.Show(this, "Selecione um período.", "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var selected = MilitaryGrid.SelectedItems.Cast<VacationMilitaryOption>().Select(x => x.Military).ToList();
        if (selected.Count == 0) return;
        var days = SelectedDays();
        try
        {
            (int Added, List<string> Failures) result = (0, []);
            await RunOperationAsync(async () =>
            {
                var year = CurrentYear;
                var periodId = SelectedPeriod.Id;
                result = await Task.Run(() => _service.AllocateAsync(year, periodId, selected, days));
                await RefreshOperationalStateAsync();
            }, "Incluindo militares no período...");
            StatusText.Text = $"{result.Added} militar(es) incluído(s).";
            if (result.Failures.Count > 0) SigfurDialog.Show(this, string.Join(Environment.NewLine, result.Failures.Take(20)), "Itens não incluídos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private int SelectedDays() => int.TryParse((DaysBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var days) ? days : 30;

    private void MilitaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFromMouseEvent(MilitaryGrid, e) is not { Item: VacationMilitaryOption option }) return;
        e.Handled = true;
        OpenIndividualForMilitary(option.Military);
    }

    private async void CreateAutomaticNote_Click(object sender, RoutedEventArgs e)
    {
        var source = (sender as FrameworkElement)?.Tag?.ToString();
        var selected = source == "Available"
            ? MilitaryGrid.SelectedItems.Cast<VacationMilitaryOption>().Select(x => x.Military).ToList()
            : AllocationsGrid.SelectedItems.Cast<VacationAllocation>().Select(x => x.Military).ToList();
        if (selected.Count == 0)
        {
            selected = MilitaryGrid.SelectedItems.Cast<VacationMilitaryOption>().Select(x => x.Military).ToList();
            if (selected.Count == 0) selected = AllocationsGrid.SelectedItems.Cast<VacationAllocation>().Select(x => x.Military).ToList();
        }
        selected = selected.Where(x => x.Id > 0).GroupBy(x => x.Id).Select(x => x.First()).ToList();
        if (selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione pelo menos um militar para criar a nota.", "Nota automática", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = ShowAutomaticNoteDialog(selected);
        if (choice is null) return;
        try
        {
            BulletinPeriodBox.SelectedItem = choice.Period;
            RefreshModelsList(choice.ModelName);
            SpecificSubjectBox.Text = choice.ModelName;
            IncludeLateValuesCheck.IsChecked = Normalize(choice.ModelName).Contains("atrasad", StringComparison.OrdinalIgnoreCase);
            MainTabs.SelectedIndex = 1;

            var text = await GeneratePreviewCoreAsync(selected, choice.Days);
            if (string.IsNullOrWhiteSpace(text)) return;

            var personLabel = selected.Count == 1
                ? (string.IsNullOrWhiteSpace(selected[0].WarName) ? selected[0].Name : selected[0].WarName)
                : $"{selected.Count} militares";
            var saved = new VacationSavedBulletin
            {
                Title = $"{choice.ModelName} — {personLabel}",
                Text = text,
                ModelName = choice.ModelName,
                Year = CurrentYear,
                PeriodId = choice.Period.Id,
                MilitaryIds = selected.Select(x => x.Id).Distinct().ToList()
            };
            _bulletinStore.Saved.Add(saved);
            _currentSaved = saved;
            await _service.SaveBulletinStoreAsync(_bulletinStore);
            RefreshSavedBulletins(saved.Id);
            StatusText.Text = $"Nota criada e salva automaticamente para {personLabel}. Confira a prévia antes de enviar.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private AutomaticNoteChoice? ShowAutomaticNoteDialog(IReadOnlyCollection<MilitaryRecord> selected)
    {
        var models = _bulletinStore.Models.Keys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (models.Count == 0) return null;

        var dialog = new Window
        {
            Title = "Criar nota automática",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 650,
            Height = 380,
            MinWidth = 560,
            ResizeMode = ResizeMode.NoResize,
            Background = FindResource("AppBackgroundBrush") as System.Windows.Media.Brush,
            Icon = Icon
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var names = string.Join(", ", selected.Take(5).Select(x => $"{x.ShortRank} {x.WarName}".Trim()));
        if (selected.Count > 5) names += $" e mais {selected.Count - 5}";
        root.Children.Add(new TextBlock { Text = $"Militar(es): {names}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        var modelPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        modelPanel.Children.Add(new TextBlock { Text = "Tipo da nota", Style = FindResource("SubtleTextStyle") as Style });
        var modelBox = new ComboBox { ItemsSource = models, IsEditable = false };
        modelBox.SelectedItem = models.FirstOrDefault(x => Normalize(x).Contains("saque de atrasad", StringComparison.OrdinalIgnoreCase)) ?? models[0];
        modelPanel.Children.Add(modelBox); Grid.SetRow(modelPanel, 1); root.Children.Add(modelPanel);

        var periodPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        periodPanel.Children.Add(new TextBlock { Text = "Período de férias", Style = FindResource("SubtleTextStyle") as Style });
        var periodBox = new ComboBox { ItemsSource = _periods, DisplayMemberPath = "FullLabel", SelectedItem = SelectedPeriod ?? BulletinPeriod ?? _periods.FirstOrDefault() };
        periodPanel.Children.Add(periodBox); Grid.SetRow(periodPanel, 2); root.Children.Add(periodPanel);

        var daysPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Orientation = Orientation.Horizontal };
        daysPanel.Children.Add(new TextBlock { Text = "Dias considerados:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var daysBox = new ComboBox { ItemsSource = new[] { 10, 15, 30 }, SelectedItem = SelectedDays(), Width = 100 };
        daysPanel.Children.Add(daysBox); Grid.SetRow(daysPanel, 3); root.Children.Add(daysPanel);

        var hint = new TextBlock { Text = "A nota será montada com identificação, período e valor individual calculado. Ela ficará salva e poderá ser revisada antes do envio ao SisBol.", TextWrapping = TextWrapping.Wrap, Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush, Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetRow(hint, 4); root.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Style = FindResource("SecondaryButtonStyle") as Style, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var create = new Button { Content = "Criar nota", Style = FindResource("PrimaryButtonStyle") as Style };
        create.Click += (_, _) =>
        {
            if (modelBox.SelectedItem is null || periodBox.SelectedItem is null) return;
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(create); Grid.SetRow(buttons, 5); root.Children.Add(buttons);
        dialog.Content = root;

        return dialog.ShowDialog() == true
               && modelBox.SelectedItem is string model
               && periodBox.SelectedItem is VacationPeriod period
               && daysBox.SelectedItem is int days
            ? new AutomaticNoteChoice(model, period, days)
            : null;
    }

    private void SelectAllMilitary_Click(object sender, RoutedEventArgs e)
    {
        try { MilitaryGrid.SelectAll(); } catch { }
        StatusText.Text = $"{MilitaryGrid.SelectedItems.Count} militar(es) selecionado(s).";
    }

    private void ClearMilitarySelection_Click(object sender, RoutedEventArgs e)
    {
        try { MilitaryGrid.UnselectAll(); } catch { }
        StatusText.Text = "Seleção de militares limpa.";
    }

    private void CopySelectedMilitary_Click(object sender, RoutedEventArgs e)
    {
        var lines = MilitaryGrid.SelectedItems.Cast<VacationMilitaryOption>()
            .Select(x => $"{x.Rank} {x.Name} | CPF {x.Military.FormattedCpf} | PREC-CP {x.Military.PrecCp} | Dias {x.DaysText}")
            .ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusText.Text = lines.Count == 0 ? "Nenhum militar selecionado para copiar." : $"{lines.Count} militar(es) copiado(s).";
    }

    private void SelectAllAllocations_Click(object sender, RoutedEventArgs e)
    {
        try { AllocationsGrid.SelectAll(); } catch { }
        StatusText.Text = $"{AllocationsGrid.SelectedItems.Count} lançamento(ões) selecionado(s).";
    }

    private void ClearAllocationsSelection_Click(object sender, RoutedEventArgs e)
    {
        try { AllocationsGrid.UnselectAll(); } catch { }
        StatusText.Text = "Seleção de lançamentos limpa.";
    }

    private void CopySelectedAllocations_Click(object sender, RoutedEventArgs e)
    {
        var lines = AllocationsGrid.SelectedItems.Cast<VacationAllocation>()
            .Select(x => $"{x.Rank} {x.Name} | CPF {x.Cpf} | PREC-CP {x.PrecCp} | {x.Days} dia(s) | {x.PaidText} | {x.FoodAidText}")
            .ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusText.Text = lines.Count == 0 ? "Nenhum lançamento selecionado para copiar." : $"{lines.Count} lançamento(ões) copiado(s).";
    }

    private async void RemoveAllocation_Click(object sender, RoutedEventArgs e)
    {
        var selected = AllocationsGrid.SelectedItems.Cast<VacationAllocation>().ToList();
        if (selected.Count == 0) return;
        if (SigfurDialog.Show(this, $"Remover {selected.Count} alocação(ões)?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var ids = selected.Select(x => x.Id).ToList();
        try { await Task.Run(() => _service.RemoveAllocationsAsync(ids)); await RefreshOperationalStateAsync(); StatusText.Text = "Alocações removidas."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MarkPaid_Click(object sender, RoutedEventArgs e) => await SetPaidAsync(true);
    private async void MarkUnpaid_Click(object sender, RoutedEventArgs e) => await SetPaidAsync(false);
    private async Task SetPaidAsync(bool paid)
    {
        var selected = AllocationsGrid.SelectedItems.Cast<VacationAllocation>().ToList();
        if (selected.Count == 0) return;
        var confirmFoodAid = false;
        if (paid && selected.Any(x => x.RequiresVacationFoodAid))
        {
            var answer = SigfurDialog.Show(this,
                "Há Cabo/Soldado na seleção. Para concluir o pagamento das férias, confirme que a nota e o pagamento do Auxílio-Alimentação de férias também foram incluídos.\n\nConfirmar férias + Auxílio-Alimentação como pagos?",
                "Conferência obrigatória", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            confirmFoodAid = true;
        }
        var ids = selected.Select(x => x.Id).ToList();
        try { await Task.Run(() => _service.SetPaidAsync(ids, paid, confirmFoodAid)); await RefreshOperationalStateAsync(); StatusText.Text = paid ? "Pagamento anual de férias e Auxílio-Alimentação aplicável marcado como pago em todos os períodos." : "Pagamento anual marcado como pendente em todos os períodos."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MoveAllocation_Click(object sender, RoutedEventArgs e)
    {
        if (AllocationsGrid.SelectedItem is not VacationAllocation allocation) return;
        var choice = ShowMoveDialog(allocation);
        if (choice is null) return;
        try
        {
            await Task.Run(() => _service.MoveAllocationAsync(allocation, choice.Value.PeriodId, choice.Value.Days));
            PeriodsList.SelectedItem = _periods.FirstOrDefault(x => x.Id == choice.Value.PeriodId) ?? SelectedPeriod;
            await RefreshOperationalStateAsync();
            StatusText.Text = "Período e dias atualizados.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private (int PeriodId, int Days)? ShowMoveDialog(VacationAllocation allocation)
    {
        var dialog = new Window { Title = "Mover férias / alterar dias", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 470, Height = 260, ResizeMode = ResizeMode.NoResize, Background = FindResource("AppBackgroundBrush") as System.Windows.Media.Brush, Icon = Icon };
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = $"{allocation.Rank} {allocation.Name}", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var periodBox = new ComboBox { ItemsSource = _periods, DisplayMemberPath = "FullLabel", SelectedItem = _periods.FirstOrDefault(x => x.Id == allocation.PeriodId), Margin = new Thickness(0, 16, 0, 10) }; Grid.SetRow(periodBox, 1); root.Children.Add(periodBox);
        var daysBox = new ComboBox { ItemsSource = new[] { 10, 15, 30 }, SelectedItem = VacationPlanService.NormalizeDays(allocation.Days), Width = 120, HorizontalAlignment = HorizontalAlignment.Left }; Grid.SetRow(daysBox, 2); root.Children.Add(daysBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Style = FindResource("SecondaryButtonStyle") as Style, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.DialogResult = false;
        var ok = new Button { Content = "Aplicar", Style = FindResource("PrimaryButtonStyle") as Style }; ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); Grid.SetRow(buttons, 3); root.Children.Add(buttons); dialog.Content = root;
        return dialog.ShowDialog() == true && periodBox.SelectedItem is VacationPeriod p && daysBox.SelectedItem is int d ? (p.Id, d) : null;
    }

    private void OpenIndividual_Click(object sender, RoutedEventArgs e)
    {
        MilitaryRecord? military = (AllocationsGrid.SelectedItem as VacationAllocation)?.Military ?? (MilitaryGrid.SelectedItem as VacationMilitaryOption)?.Military;
        if (military is null)
        {
            StatusText.Text = "Selecione um militar para abrir a carteira.";
            return;
        }
        OpenIndividualForMilitary(military);
    }

    private void OpenIndividualForMilitary(MilitaryRecord military)
    {
        if (_individualWindows.TryGetValue(military.Id, out var existing) && existing.IsVisible)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new VacationIndividualWindow(_service, military, CurrentYear, SelectedPeriod?.Id ?? 0) { Owner = this };
        _individualWindows[military.Id] = window;
        window.Closed += (_, _) =>
        {
            _individualWindows.Remove(military.Id);
            if (!window.HasChanges || !IsLoaded || !IsVisible) return;
            StatusText.Text = "Atualizando o planejamento...";
            _ = RefreshAfterIndividualAsync();
        };
        window.Show();
        StatusText.Text = $"Carteira de férias aberta para {military.Name}.";
    }
    private async Task RefreshAfterIndividualAsync()
    {
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await RefreshOperationalStateAsync();
            StatusText.Text = "Plano individual atualizado.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void AllocationsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFromMouseEvent(AllocationsGrid, e) is not { Item: VacationAllocation allocation }) return;
        e.Handled = true;
        OpenIndividualForMilitary(allocation.Military);
    }

    private static DataGridRow? RowFromMouseEvent(DataGrid grid, MouseButtonEventArgs e)
        => e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(grid, source) as DataGridRow
            : null;

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || RowFromMouseEvent(grid, e) is not { } row) return;
        if (!row.IsSelected) grid.UnselectAll();
        row.IsSelected = true;
        grid.SelectedItem = row.Item;
        row.Focus();
    }

    private void RefreshModelsList(string? select = null)
    {
        var names = _bulletinStore.Models.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        ModelsList.ItemsSource = names;
        ModelsList.SelectedItem = names.FirstOrDefault(x => string.Equals(x, select, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
    }

    private void RefreshSavedBulletins(string? selectId = null)
    {
        var items = _bulletinStore.Saved.OrderByDescending(x => x.UpdatedAt).ToList();
        SavedBulletinsList.ItemsSource = items;
        if (!string.IsNullOrWhiteSpace(selectId)) SavedBulletinsList.SelectedItem = items.FirstOrDefault(x => x.Id == selectId);
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedModelName is not { } name || !_bulletinStore.Models.TryGetValue(name, out var text)) return;
        _loading = true;
        try { _currentSaved = null; SavedBulletinsList.SelectedItem = null; ModelEditor.Text = text; _preferences.LastModel = name; }
        finally { _loading = false; }
    }

    private void ModelEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || SelectedModelName is null) return;
        _modelSaveTimer.Stop(); _modelSaveTimer.Start();
    }

    private async Task SaveCurrentModelAsync()
    {
        if (_loading || SelectedModelName is not { } name) return;
        _bulletinStore.Models[name] = ModelEditor.Text;
        await _service.SaveBulletinStoreAsync(_bulletinStore);
        StatusText.Text = "Modelo salvo automaticamente.";
    }

    private void SavedBulletinsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedBulletinsList.SelectedItem is not VacationSavedBulletin saved) return;
        _loading = true;
        try
        {
            _currentSaved = saved; ModelsList.SelectedItem = null; SetPreviewText(saved.Text);
            BulletinPeriodBox.SelectedItem = _periods.FirstOrDefault(x => x.Id == saved.PeriodId) ?? BulletinPeriodBox.SelectedItem;
        }
        finally { _loading = false; }
    }

    private void PreviewEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _currentSaved is null) return;
        _savedSaveTimer.Stop(); _savedSaveTimer.Start();
    }

    private async Task SaveCurrentBulletinAsync()
    {
        if (_loading || _currentSaved is null) return;
        _currentSaved.Text = GetPreviewText();
        _currentSaved.UpdatedAt = DateTime.Now;
        await _service.SaveBulletinStoreAsync(_bulletinStore);
        RefreshSavedBulletins(_currentSaved.Id);
        StatusText.Text = "Boletim salvo automaticamente.";
    }

    private async void NewModel_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Novo modelo", "Nome do modelo:");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_bulletinStore.Models.ContainsKey(name)) { SigfurDialog.Show(this, "Já existe um modelo com esse nome.", "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _bulletinStore.Models[name] = "{{LISTA}}";
        await _service.SaveBulletinStoreAsync(_bulletinStore); RefreshModelsList(name);
    }

    private async void RenameModel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedModelName is not { } old) return;
        var name = Prompt("Renomear modelo", "Novo nome:", old);
        if (string.IsNullOrWhiteSpace(name) || name == old) return;
        if (_bulletinStore.Models.ContainsKey(name)) { SigfurDialog.Show(this, "Já existe um modelo com esse nome.", "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _bulletinStore.Models[name] = _bulletinStore.Models[old]; _bulletinStore.Models.Remove(old);
        await _service.SaveBulletinStoreAsync(_bulletinStore); RefreshModelsList(name);
    }

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedModelName is not { } name) return;
        if (name.StartsWith("ADICIONAL FERIAS", StringComparison.OrdinalIgnoreCase)) { SigfurDialog.Show(this, "Modelos padrão podem ser editados, mas não excluídos.", "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (SigfurDialog.Show(this, $"Excluir o modelo '{name}'?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _bulletinStore.Models.Remove(name); await _service.SaveBulletinStoreAsync(_bulletinStore); RefreshModelsList();
    }

    private async void GeneratePreview_Click(object sender, RoutedEventArgs e)
        => await GeneratePreviewCoreAsync();

    private async Task<string?> GeneratePreviewCoreAsync(IReadOnlyCollection<MilitaryRecord>? selectedMilitary = null, int selectedDays = 30)
    {
        if (BulletinPeriod is not { } period) return null;
        ApplyBiPublicationFields(_preferences.FormFields);
        var missing = VacationFormFields().Where(x => string.IsNullOrWhiteSpace(FindFormValue(x.Key))).ToList();
        if (missing.Count > 0)
        {
            SigfurDialog.Show(this,
                "Preencha os campos obrigatórios do modelo antes de gerar: " + string.Join(", ", missing.Select(x => FriendlyFieldName(x.Key))),
                "Campos obrigatórios", MessageBoxButton.OK, MessageBoxImage.Warning);
            FormFields_Click(this, new RoutedEventArgs());
            missing = VacationFormFields().Where(x => string.IsNullOrWhiteSpace(FindFormValue(x.Key))).ToList();
            if (missing.Count > 0) return null;
        }
        try
        {
            var profile = await App.Settings.LoadProfileAsync();
            var fields = new Dictionary<string, string>(_preferences.FormFields, StringComparer.OrdinalIgnoreCase)
            {
                ["OM"] = profile.Organization,
                ["ORGANIZACAO_MILITAR"] = profile.Organization,
                ["COMANDANTE"] = profile.CommanderName,
                ["POSTO_COMANDANTE"] = profile.CommanderRank,
                ["OPERADOR"] = profile.Operator
            };
            ApplyBiPublicationFields(fields);
            var text = await _service.GeneratePreviewAsync(
                ModelEditor.Text,
                period,
                CurrentYear,
                IncludeLateValuesCheck.IsChecked == true,
                fields,
                SelectedModelName,
                selectedMilitary,
                selectedDays);
            _loading = true; try { SetPreviewText(text); } finally { _loading = false; }
            if (selectedMilitary is null) await ShowVacationOperationalWarningsAsync(period);
            StatusText.Text = IsVacationMainForComplementaries(CurrentModelIdentity())
                ? "Prévia principal de férias gerada. As publicações obrigatórias de Auxílio-Alimentação de Cb/Sd e DA do AT serão criadas uma única vez no envio ao SisBol."
                : "Prévia gerada. No envio ao SisBol será enviada somente esta publicação, sem criar outro saque.";
            return text;
        }
        catch (Exception ex) { ShowError(ex); return null; }
    }

    private async void SaveBulletin_Click(object sender, RoutedEventArgs e)
    {
        var previewText = GetPreviewText();
        if (string.IsNullOrWhiteSpace(previewText)) return;
        var title = Prompt("Salvar boletim", "Título:", $"Plano de Férias {CurrentYear} — {BulletinPeriod?.DisplayName}");
        if (string.IsNullOrWhiteSpace(title)) return;
        var saved = new VacationSavedBulletin { Title = title, Text = previewText, ModelName = SelectedModelName ?? string.Empty, Year = CurrentYear, PeriodId = BulletinPeriod?.Id ?? 0 };
        _bulletinStore.Saved.Add(saved); _currentSaved = saved;
        await _service.SaveBulletinStoreAsync(_bulletinStore); RefreshSavedBulletins(saved.Id); StatusText.Text = "Boletim salvo.";
    }

    private async void SaveBulletinEdition_Click(object sender, RoutedEventArgs e)
    {
        var previewText = GetPreviewText();
        if (string.IsNullOrWhiteSpace(previewText)) return;
        if (_currentSaved is null)
        {
            SaveBulletin_Click(sender, e);
            return;
        }
        _currentSaved.Text = previewText;
        _currentSaved.ModelName = SelectedModelName ?? _currentSaved.ModelName;
        _currentSaved.Year = CurrentYear;
        _currentSaved.PeriodId = BulletinPeriod?.Id ?? _currentSaved.PeriodId;
        _currentSaved.UpdatedAt = DateTime.Now;
        await _service.SaveBulletinStoreAsync(_bulletinStore);
        RefreshSavedBulletins(_currentSaved.Id);
        StatusText.Text = "Edição do boletim salva.";
    }

    private async void RenameBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (SavedBulletinsList.SelectedItem is not VacationSavedBulletin saved) return;
        var title = Prompt("Renomear boletim", "Novo título:", saved.Title);
        if (string.IsNullOrWhiteSpace(title)) return;
        saved.Title = title; saved.UpdatedAt = DateTime.Now; await _service.SaveBulletinStoreAsync(_bulletinStore); RefreshSavedBulletins(saved.Id);
    }

    private async void DeleteBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (SavedBulletinsList.SelectedItem is not VacationSavedBulletin saved) return;
        if (SigfurDialog.Show(this, $"Excluir '{saved.Title}'?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _bulletinStore.Saved.Remove(saved); _currentSaved = null; ClearPreview(); await _service.SaveBulletinStoreAsync(_bulletinStore); RefreshSavedBulletins();
    }

    private async void CopyPreview_Click(object sender, RoutedEventArgs e)
    {
        var previewText = GetPreviewText();
        if (string.IsNullOrWhiteSpace(previewText)) return;
        var military = await GetBulletinMilitaryForCurrentPeriodAsync();
        var render = new BulletinRenderResult
        {
            Text = previewText,
            BoldRanges = BulletinTextFormatter.FindWarNameRanges(previewText, military),
            UnresolvedTokens = []
        };
        var document = _bulletinService.BuildDocument(render);
        BulletinService.CopyForWord(document, render.Text);
        StatusText.Text = "Texto copiado em Times New Roman 10, com somente o nome de guerra em negrito.";
    }

    private async void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        var previewText = GetPreviewText();
        if (string.IsNullOrWhiteSpace(previewText) || BulletinPeriod is null) return;
        Directory.CreateDirectory(_service.OutputDirectory);
        var dialog = new SaveFileDialog { Title = "Salvar boletim de férias em Word", Filter = "Documento Word|*.docx", DefaultExt = ".docx", AddExtension = true, InitialDirectory = _service.OutputDirectory, FileName = $"Boletim_Ferias_{CurrentYear}_{BulletinPeriod.Index:00}.docx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var military = await GetBulletinMilitaryForCurrentPeriodAsync();
            await _service.ExportPreviewWordAsync(dialog.FileName, $"PLANO DE FÉRIAS — {BulletinPeriod.DisplayName}", previewText, military);
            ShellService.OpenPath(dialog.FileName); StatusText.Text = "Documento Word gerado.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void SendSisbol_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Sisbol.IsReady)
        {
            SigfurDialog.Show(this,
                "O SisBol não está preparado. Vá na janela principal, clique em ‘Preparar SisBol’, conclua o login/captcha e valide a sessão.",
                "SisBol não preparado", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "SisBol não preparado. Prepare na janela principal antes de enviar.";
            RefreshSisbolStatus();
            return;
        }
        var previewText = GetPreviewText();
        if (string.IsNullOrWhiteSpace(previewText) || BulletinPeriod is null) return;
        try
        {
            IsEnabled = false; StatusText.Text = "Enviando ao SisBol…";
            var military = await GetBulletinMilitaryForCurrentPeriodAsync();
            var effectiveModel = CurrentModelIdentity();
            var subject = string.IsNullOrWhiteSpace(SpecificSubjectBox.Text) ? effectiveModel : SpecificSubjectBox.Text.Trim();
            var code = SpecificCodeBox.Text.Trim();
            var sisbolSubject = string.IsNullOrWhiteSpace(code) ? subject : $"{code} - {subject}";

            var includeConsequences = IncludeConsequencesCheck.IsChecked == true;
            await App.Sisbol.SendAsync(previewText, military, sisbolSubject, includeConsequences, ConsequencesTextBox.Text);
            await SaveAutomaticBulletinAsync(sisbolSubject, previewText, effectiveModel);

            var fields = BuildSisbolFieldContext();
            var selectedForAutomaticNote = _currentSaved?.MilitaryIds is { Count: > 0 } ? military : null;
            var complementaries = await _service.GenerateComplementaryBulletinsAsync(
                ModelEditor.Text,
                effectiveModel,
                BulletinPeriod,
                CurrentYear,
                fields,
                selectedForAutomaticNote,
                SelectedDays());
            var sentComplementaries = 0;
            var failedComplementaries = new List<string>();
            foreach (var complementary in complementaries)
            {
                if (string.IsNullOrWhiteSpace(complementary.Text)) continue;
                var complementarySubject = string.IsNullOrWhiteSpace(complementary.SisbolSubject) ? complementary.Title : complementary.SisbolSubject;
                try
                {
                    StatusText.Text = $"Principal enviado. Enviando complementar: {complementarySubject}…";
                    await App.Sisbol.SendAsync(
                        complementary.Text,
                        complementary.Military,
                        complementarySubject,
                        includeConsequences,
                        SisbolTexts.ForSubject(complementarySubject));
                    await SaveAutomaticBulletinAsync(complementarySubject, complementary.Text, complementary.Title);
                    sentComplementaries++;
                }
                catch (Exception compEx)
                {
                    failedComplementaries.Add($"{complementarySubject}: {compEx.Message}");
                    await App.Log.WriteAsync($"Falha ao enviar complementar de férias ao SisBol: {complementarySubject}", compEx);
                }
            }

            if (failedComplementaries.Count > 0)
            {
                StatusText.Text = $"Principal enviado. Complementares enviados: {sentComplementaries}. Falha(s): {failedComplementaries.Count}.";
                SigfurDialog.Show(this,
                    "A publicação principal foi incluída no SisBol, mas uma ou mais complementares falharam e as demais foram tentadas." +
                    Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failedComplementaries),
                    "Complementares de férias", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                StatusText.Text = sentComplementaries > 0
                    ? $"Texto principal enviado. {sentComplementaries} publicação(ões) complementar(es) criada(s), incluída(s) no SisBol e salva(s)."
                    : "Texto enviado, incluído no SisBol e salvo.";
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; RefreshSisbolStatus(); }
    }

    private Dictionary<string, string> BuildSisbolFieldContext()
    {
        var fields = new Dictionary<string, string>(_preferences.FormFields, StringComparer.OrdinalIgnoreCase);
        ApplyBiPublicationFields(fields);
        return fields;
    }

    private async Task SaveAutomaticBulletinAsync(string title, string text, string modelName)
    {
        if (string.IsNullOrWhiteSpace(text) || BulletinPeriod is null) return;
        var saved = new VacationSavedBulletin
        {
            Title = $"SisBol — {title}",
            Text = text,
            ModelName = modelName,
            Year = CurrentYear,
            PeriodId = BulletinPeriod.Id,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        _bulletinStore.Saved.Add(saved);
        _currentSaved = saved;
        await _service.SaveBulletinStoreAsync(_bulletinStore);
        RefreshSavedBulletins(saved.Id);
    }

    private async Task ShowVacationOperationalWarningsAsync(VacationPeriod period)
    {
        if (!IsVacationMainForComplementaries(CurrentModelIdentity())) return;

        var allocations = (await _service.GetAllocationsAsync(CurrentYear, period.Id)).Where(x => !x.IsPaid).ToList();
        var cbSd = allocations.Count(x => IsCaboOuSoldadoForNotice(x.Military));
        var transport = allocations.Count(x => MilitaryRecord.IsYes(x.Military.ReceivesTransportAid) || x.Military.TransportGrossTotal.GetValueOrDefault() > 0 || ParseMoneyForNotice(x.Military.TransportAidValue) > 0);
        if (cbSd <= 0 && transport <= 0) return;

        var notes = new List<string>();
        if (cbSd > 0) notes.Add($"{cbSd} Cabo/Soldado na relação: ao enviar ao SisBol, o SIGFUR criará automaticamente uma publicação separada de Auxílio-Alimentação por motivo de férias.");
        if (transport > 0) notes.Add($"{transport} militar(es) com Auxílio-Transporte cadastrado: ao enviar ao SisBol, o SIGFUR criará automaticamente uma publicação separada de Despesa a Anular do Auxílio-Transporte, sem misturar com o boletim principal.");
        SigfurDialog.Show(this, string.Join(Environment.NewLine + Environment.NewLine, notes), "Conferência de férias", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool IsVacationMainForComplementaries(string? value)
    {
        // Normaliza também hífens e pontuação. Sem isso, "AUXÍLIO-ALIMENTAÇÃO"
        // virava "auxilio-alimentacao" e não era reconhecido pela exclusão abaixo,
        // fazendo o saque de atrasado abrir indevidamente a conferência de férias.
        var modelKey = Regex.Replace(Normalize(value ?? string.Empty), @"[^a-z0-9]+", " ").Trim();
        return modelKey.Contains("ferias", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("auxilio alimentacao", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("auxilio transporte", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("auxilio trasnporte", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("despesa a anular", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("despesa anular", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("indenizacao", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("reserva", StringComparison.OrdinalIgnoreCase)
            && !modelKey.Contains("natalino", StringComparison.OrdinalIgnoreCase);
    }

    private string CurrentModelIdentity()
    {
        var identity = SelectedModelName ?? _currentSaved?.ModelName;
        if (!string.IsNullOrWhiteSpace(identity)) return identity.Trim();
        return string.IsNullOrWhiteSpace(ModelEditor.Text) ? "Plano de Férias" : ModelEditor.Text;
    }

    private static bool IsCaboOuSoldadoForNotice(MilitaryRecord military)
    {
        var rank = Normalize(MilitaryRankService.Canonicalize(military.Rank));
        return rank.Contains("cabo", StringComparison.OrdinalIgnoreCase) || rank.Contains("soldado", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ParseMoneyForNotice(string? value)
    {
        var text = new string((value ?? string.Empty).Where(c => char.IsDigit(c) || c is ',' or '.' or '-').ToArray());
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out var result)) return result;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result) ? result : 0m;
    }

    private string GetPreviewText()
    {
        try
        {
            var text = new TextRange(PreviewEditor.Document.ContentStart, PreviewEditor.Document.ContentEnd).Text;
            return (text ?? string.Empty).TrimEnd('\r', '\n');
        }
        catch
        {
            return string.Empty;
        }
    }

    private void SetPreviewText(string text)
    {
        var plain = text ?? string.Empty;
        var military = GetBulletinMilitarySnapshot();
        var render = new BulletinRenderResult
        {
            Text = plain,
            BoldRanges = BulletinTextFormatter.FindWarNameRanges(plain, military),
            UnresolvedTokens = []
        };
        var document = _bulletinService.BuildDocument(render);
        document.PagePadding = new Thickness(14, 10, 14, 10);
        document.ColumnWidth = double.PositiveInfinity;
        PreviewEditor.Document = document;
    }

    private void ClearPreview()
    {
        PreviewEditor.Document.Blocks.Clear();
    }

    private IReadOnlyList<MilitaryRecord> GetBulletinMilitarySnapshot()
    {
        var list = _militaryLookup.Values.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        return list.Count > 0 ? list : _military.Select(x => x.Military).ToList();
    }

    private async Task<IReadOnlyList<MilitaryRecord>> GetBulletinMilitaryForCurrentPeriodAsync()
    {
        if (BulletinPeriod is null) return Array.Empty<MilitaryRecord>();
        if (_currentSaved?.MilitaryIds is { Count: > 0 } selectedIds)
        {
            var selected = selectedIds
                .Distinct()
                .Where(_militaryLookup.ContainsKey)
                .Select(id => _militaryLookup[id])
                .ToList();
            if (selected.Count > 0) return selected;
        }
        var rows = await _service.GetAllocationsAsync(CurrentYear, BulletinPeriod.Id);
        return rows.Where(x => !x.IsPaid).Select(x => x.Military).ToList();
    }

    private void FormFields_Click(object sender, RoutedEventArgs e)
    {
        var definitions = VacationFormFields();
        if (definitions.Count == 0)
        {
            SigfurDialog.Show(this, "Este modelo não possui campos manuais além do BI fixo da tela. Você pode editar o texto e inserir chaves como {{NUM_BI}}.", "Campos do modelo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = "Preencher campos obrigatórios do modelo",
            Owner = this,
            Width = 720,
            Height = 760,
            MinWidth = 600,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Icon = Icon
        };
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Preencha os dados exigidos pela nota. Os valores ficam salvos neste Plano de Férias e continuam editáveis no texto do modelo.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var panel = new StackPanel();
        var controls = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definitions)
        {
            var card = new Border { Style = FindResource("SoftCardStyle") as Style, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = FriendlyFieldName(field.Key) + "  • obrigatório", FontWeight = FontWeights.SemiBold, Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush, Margin = new Thickness(0, 0, 0, 6) });
            var control = CreateVacationFieldControl(field, FindFormValue(field.Key));
            controls[field.Key] = control;
            stack.Children.Add(control);
            card.Child = stack;
            panel.Children.Add(card);
        }
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Style = FindResource("SecondaryButtonStyle") as Style, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Salvar campos", Style = FindResource("PrimaryButtonStyle") as Style };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        save.Click += (_, _) =>
        {
            var empty = controls.Where(x => string.IsNullOrWhiteSpace(ReadVacationFieldValue(x.Value))).Select(x => FriendlyFieldName(x.Key)).ToList();
            if (empty.Count > 0)
            {
                SigfurDialog.Show(dialog, "Preencha: " + string.Join(", ", empty), "Campos obrigatórios", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var pair in controls) _preferences.FormFields[pair.Key] = ReadVacationFieldValue(pair.Value);
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        dialog.Content = root;
        if (dialog.ShowDialog() != true) return;
        _ = PersistPreferencesAsync();
        StatusText.Text = $"{controls.Count} campo(s) obrigatório(s) salvo(s).";
    }

    private List<BulletinFieldDefinition> VacationFormFields()
    {
        var automatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LISTA", "ANO", "ANO_REF", "DIAS", "PERIODO", "DATA_INICIO", "DATA_FIM", "DATA_INICIO_ABREV", "DATA_FIM_ABREV", "QUANTIDADE",
            "VALOR_ATRASADO", "VALOR_ATRASADO_EXTENSO", "TEXTO_VALOR_ATRASADO", "POSTO", "POSTO_ABREV",
            "NOME", "NOME_COMPLETO", "NOME_GUERRA", "CPF", "PREC_CP", "IDT", "DATA_PUBLICACAO", "DATA_BI"
        };

        // No Plano de Férias o campo operacional que costuma mudar é apenas o BI.
        // Ele fica em campo fixo da tela e é persistido nas preferências para reutilização.
        var fields = _bulletinService.DetectFields(ModelEditor.Text, includeAutomatic: true)
            .Where(x => !automatic.Contains(x.Key))
            .Where(x => Normalize(x.Key) is "num bi" or "bi numero" or "bi")
            .GroupBy(x => Normalize(x.Key))
            .Select(x => x.First())
            .ToList();

        var modelIdentity = CurrentModelIdentity();
        if (IsVacationMainForComplementaries(modelIdentity)
            || Normalize(modelIdentity).Contains("auxilio alimentacao", StringComparison.OrdinalIgnoreCase))
        {
            fields.Add(new BulletinFieldDefinition
            {
                Key = "FUNDAMENTO_AUX_ALIMENTACAO_FERIAS",
                DisplayKey = "Fundamento legal do Auxílio-Alimentação de férias",
                Type = "select",
                Options = [VacationPlanService.VacationFoodAidSippesReason]
            });
        }

        return fields;
    }

    private string FindFormValue(string key)
    {
        var value = _preferences.FormFields.FirstOrDefault(x => Normalize(x.Key) == Normalize(key)).Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            && Normalize(key).Equals("fundamento aux alimentacao ferias", StringComparison.OrdinalIgnoreCase))
            return VacationPlanService.VacationFoodAidSippesReason;
        return value;
    }

    private FrameworkElement CreateVacationFieldControl(BulletinFieldDefinition field, string value)
    {
        var options = field.Options.Count > 0 ? field.Options : _bulletinService.SuggestOptions(field.Key, SelectedModelName).ToList();
        if (options.Count > 0)
        {
            var lockedSelection = Normalize(field.Key).Equals("fundamento aux alimentacao ferias", StringComparison.OrdinalIgnoreCase);
            var combo = new ComboBox { IsEditable = !lockedSelection, ItemsSource = options, Text = value, MinWidth = 560 };
            if (lockedSelection)
                combo.SelectedItem = options.FirstOrDefault(item => item.Equals(value, StringComparison.CurrentCultureIgnoreCase));
            return combo;
        }
        if (field.Type == "date")
        {
            var picker = new DatePicker { MinWidth = 560 };
            if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)) picker.SelectedDate = date;
            return picker;
        }
        if (field.Type == "month") return new ComboBox
        {
            IsEditable = true,
            MinWidth = 560,
            Text = value,
            ItemsSource = Enumerable.Range(-24, 73).Select(i => DateTime.Today.AddMonths(i).ToString("MMM yy", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR"))).ToList()
        };
        if (field.Type == "money") return new BulletinValueInputControl(value, field.MoneyFormat);
        return new TextBox { Text = value, MinWidth = 560, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
    }

    private static string ReadVacationFieldValue(FrameworkElement control) => control switch
    {
        BulletinValueInputControl money => money.Value,
        DatePicker picker => picker.SelectedDate?.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty,
        ComboBox combo => combo.Text.Trim(),
        TextBox text => text.Text.Trim(),
        _ => string.Empty
    };

    private static string FriendlyFieldName(string key)
        => Normalize(key).Equals("fundamento aux alimentacao ferias", StringComparison.OrdinalIgnoreCase)
            ? "Fundamento legal do Auxílio-Alimentação de férias"
            : CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase((key ?? string.Empty).Replace('_', ' ').ToLowerInvariant());

    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_service.OutputDirectory);
        var dialog = new SaveFileDialog { Title = "Salvar relatório do Plano de Férias", Filter = "Planilha Excel|*.xlsx", DefaultExt = ".xlsx", AddExtension = true, InitialDirectory = _service.OutputDirectory, FileName = $"Relatorio_Ferias_{CurrentYear}.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _service.ExportReportAsync(dialog.FileName, CurrentYear); ShellService.OpenPath(dialog.FileName); StatusText.Text = "Relatório de férias gerado."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ReviewPublications_Click(object sender, RoutedEventArgs e)
    {
        new VacationBulletinReviewWindow(_service, CurrentYear, _periods.ToList()) { Owner = this }.ShowDialog();
        await RefreshOperationalStateAsync();
    }

    private void OpenSippesManual_Click(object sender, RoutedEventArgs e)
        => LegislationWindow.ShowTopic(
            this,
            "férias, adicional de férias, indenização, gozo e lançamento no SIPPES",
            manualOnly: true);

    private void OpenVacationLegislation_Click(object sender, RoutedEventArgs e)
        => LegislationWindow.ShowTopic(
            this,
            "férias, adicional de férias, indenização, interrupção e pagamento",
            showSummary: true);

    private async void UseSavedBi_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SavedBulletinPickerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedReference is not { } reference) return;
        if (IsAdtReference(reference))
        {
            SigfurDialog.Show(this, "Selecione um Boletim Interno. Para férias, o campo BI usa a publicação principal do BI, não o ADT.", "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BulletinBiBox.Text = FormatSavedPublicationReference(reference);
        ApplyBiPublicationFields(_preferences.FormFields);
        await PersistPreferencesAsync();
        StatusText.Text = $"BI de publicação aplicado: {BulletinBiBox.Text}";
    }

    private void BulletinPeriodBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && BulletinPeriod is { } p) StatusText.Text = "Período do boletim: " + p.FullLabel; }
    private void BulletinBiBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        ApplyBiPublicationFields(_preferences.FormFields);
        QueuePersistPreferences();
    }
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && e.Source == MainTabs) await PersistPreferencesAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync(SelectedPeriod?.Id, reloadMilitary: true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task PersistPreferencesAsync()
    {
        if (_loading) return;
        _preferences.LastYear = CurrentYear;
        _preferences.LastPeriodId = SelectedPeriod?.Id ?? 0;
        _preferences.LastTab = MainTabs.SelectedIndex;
        _preferences.Search = MilitarySearchBox.Text;
        _preferences.AllocationSearch = AllocationSearchBox.Text;
        _preferences.AllocationStatus = (AllocationStatusBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
        _preferences.Rank = RankFilterBox.SelectedItem?.ToString() ?? "Todos";
        _preferences.SortMode = (SortModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Posto/Graduação";
        _preferences.AvailableOnly = AvailableOnlyCheck.IsChecked == true;
        _preferences.DefaultDays = SelectedDays();
        _preferences.LastModel = SelectedModelName ?? _preferences.LastModel;
        ApplyBiPublicationFields(_preferences.FormFields);
        _preferences.SpecificSubjectName = SpecificSubjectBox.Text;
        _preferences.SpecificSubjectCode = SpecificCodeBox.Text;
        await _service.SavePreferencesAsync(_preferences);
    }

    private void QueuePersistPreferences()
    {
        if (_loading) return;
        _preferenceSaveTimer.Stop();
        _preferenceSaveTimer.Start();
    }

    private void SelectSortMode(string? mode)
    {
        SortModeBox.SelectedItem = SortModeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), mode, StringComparison.OrdinalIgnoreCase)) ?? SortModeBox.Items[0];
    }

    private string FirstVacationValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = FindFormValue(key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private void ApplyBiPublicationFields(IDictionary<string, string> fields)
    {
        var raw = BulletinBiBox.Text.Trim();
        var reference = BuildBiReference(raw);
        var number = ExtractBiNumber(raw);
        fields["NUM_BI"] = number;
        fields["BI_NUMERO"] = number;
        fields["BI_REFERENCIA"] = reference;
        fields["PUBLICACAO_BI"] = reference;
        fields["REFERENCIA_BOLETIM"] = reference;
        fields["BOLETIM_REFERENCIA"] = reference;
    }

    private static string ExtractBiNumber(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var match = Regex.Match(text, @"(?:BI|BOLETIM\s+INTERNO)?\s*(?:NR|N[º°O.]*)?\s*(\d{1,5})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : text;
    }

    private static string BuildBiReference(string? value)
    {
        var text = PublicationNumberWithoutYear((value ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = Normalize(text);
        if (normalized.Contains("BI NR", StringComparison.OrdinalIgnoreCase) || normalized.Contains("BOLETIM INTERNO", StringComparison.OrdinalIgnoreCase))
            return RemoveYearFromPublicationReference(text);
        return $"BI Nrº {text}, {OrganizationIdentity.BulletinIssuer}";
    }

    private static string PublicationNumberWithoutYear(string? value)
        => Regex.Replace((value ?? string.Empty).Trim(), @"\b(?<num>\d{1,5})\s*/\s*(?:20)?\d{2}\b", match => match.Groups["num"].Value, RegexOptions.CultureInvariant);

    private static string RemoveYearFromPublicationReference(string? value)
        => PublicationNumberWithoutYear(value);

    private static bool IsAdtReference(SavedBulletinReference reference)
        => reference.Kind.Contains("Aditamento", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(reference.Bar);

    private static string FormatSavedPublicationReference(SavedBulletinReference reference)
        => reference.ReferenceText;

    private static string AbbreviatedDate(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AllowWhiteSpaces, out var date))
            return date.ToString("dd MMM yy", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        return value;
    }

    private string? Prompt(string title, string label, string initial = "") => PromptMultiline(title, label, initial, multiline: false);
    private string? PromptMultiline(string title, string label, string initial, bool multiline = true)
    {
        var dialog = new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 580, Height = multiline ? 410 : 220, MinWidth = 460, ResizeMode = multiline ? ResizeMode.CanResize : ResizeMode.NoResize, Background = FindResource("AppBackgroundBrush") as System.Windows.Media.Brush, Icon = Icon };
        var root = new Grid { Margin = new Thickness(20) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, MinHeight = multiline ? 230 : 32 }; Grid.SetRow(box, 1); root.Children.Add(box);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Style = FindResource("SecondaryButtonStyle") as Style, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.DialogResult = false;
        var ok = new Button { Content = "Salvar", Style = FindResource("PrimaryButtonStyle") as Style }; ok.Click += (_, _) => dialog.DialogResult = true;
        bar.Children.Add(cancel); bar.Children.Add(ok); Grid.SetRow(bar, 2); root.Children.Add(bar); dialog.Content = root;
        dialog.Loaded += (_, _) => { box.Focus(); if (!multiline) box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Concat(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant().Trim();
    }

    private void ShowError(Exception ex)
    {
        _ = App.Log.WriteAsync("Falha no Plano de Férias.", ex);
        SigfurDialog.Show(this, ex.Message, "SIGFUR — Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Error);
        StatusText.Text = "Não foi possível concluir a operação.";
    }
}
