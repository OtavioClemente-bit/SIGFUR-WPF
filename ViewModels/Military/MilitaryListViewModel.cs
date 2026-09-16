using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.ViewModels.Military;

public sealed class MilitaryListViewModel : ObservableObject
{
    private readonly MilitaryRepository _repository;
    private readonly MilitaryPreferenceService _preferences;
    private readonly LogService _log;
    private readonly List<MilitaryRecord> _all = [];
    private readonly List<int> _activeListOrder = [];
    private readonly List<int> _savedCustomOrder = [];
    private readonly Dictionary<int, SearchIndexEntry> _searchIndex = [];
    private readonly DispatcherTimer _searchTimer;
    private ObservableCollection<MilitaryRecord> _military = [];
    private string _activeListId = string.Empty;
    private string _activeListName = string.Empty;
    private string _searchText = string.Empty;
    private string _selectedRank = "Todos";
    private string _selectedYear = "Todos";
    private string _sortMode = "Posto/Graduação";
    private bool _favoritesOnly;
    private bool _attachedOnly;
    private bool _missingTransportOnly;
    private bool _markedOnly;
    private bool _orderLocked;
    private bool _suspendFiltering;
    private bool _isBusy;
    private string _statusText = "Carregando militares…";
    private MilitaryRecord? _selectedMilitary;
    private int _totalCount;
    private int _filteredCount;
    private int _favoriteCount;
    private int _missingTransportCount;
    private int _missingPhotoCount;

    public MilitaryListViewModel(MilitaryRepository repository, MilitaryPreferenceService preferences, LogService log)
    {
        _repository = repository;
        _preferences = preferences;
        _log = log;
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
    }

    public ObservableCollection<MilitaryRecord> Military
    {
        get => _military;
        private set => SetProperty(ref _military, value);
    }
    public ObservableCollection<string> RankOptions { get; } = ["Todos"];
    public ObservableCollection<string> YearOptions { get; } = ["Todos"];
    public ObservableCollection<string> SortModes { get; } = ["Posto/Graduação", "Nome", "Ordem salva"];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            // Evita reconstruir a tabela a cada tecla. O texto continua aparecendo
            // imediatamente, mas a pesquisa só roda quando há uma pequena pausa.
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }
    public string SelectedRank
    {
        get => _selectedRank;
        set
        {
            var normalized = NormalizeRankFilterValue(value);
            if (!SetProperty(ref _selectedRank, normalized)) return;
            RefreshYearOptionsForRank();
            ApplyFilter();
        }
    }
    public string SelectedYear { get => _selectedYear; set { if (SetProperty(ref _selectedYear, value)) ApplyFilter(); } }
    public string SortMode
    {
        get => _sortMode;
        set
        {
            var normalized = NormalizeSortMode(value);
            if (!SetProperty(ref _sortMode, normalized)) return;
            ApplySortModeCore();
            ApplyFilter();
        }
    }
    public bool FavoritesOnly { get => _favoritesOnly; set { if (SetProperty(ref _favoritesOnly, value)) ApplyFilter(); } }
    public bool AttachedOnly { get => _attachedOnly; set { if (SetProperty(ref _attachedOnly, value)) ApplyFilter(); } }
    public bool MissingTransportOnly { get => _missingTransportOnly; set { if (SetProperty(ref _missingTransportOnly, value)) ApplyFilter(); } }
    public bool MarkedOnly { get => _markedOnly; set { if (SetProperty(ref _markedOnly, value)) ApplyFilter(); } }
    public bool OrderLocked { get => _orderLocked; set => SetProperty(ref _orderLocked, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public MilitaryRecord? SelectedMilitary { get => _selectedMilitary; set => SetProperty(ref _selectedMilitary, value); }
    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }
    public int FilteredCount { get => _filteredCount; private set => SetProperty(ref _filteredCount, value); }
    public int FavoriteCount { get => _favoriteCount; private set => SetProperty(ref _favoriteCount, value); }
    public int MissingTransportCount { get => _missingTransportCount; private set => SetProperty(ref _missingTransportCount, value); }
    public int MissingPhotoCount { get => _missingPhotoCount; private set => SetProperty(ref _missingPhotoCount, value); }
    public string ActiveListId => _activeListId;
    public string ActiveListName => _activeListName;
    public bool HasActiveNamedList => !string.IsNullOrWhiteSpace(_activeListId);

    public async Task LoadAsync(
        MilitaryListSettings? initialSettings = null,
        MilitarySavedList? initialList = null,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = "Lendo militares diretamente do SQLite…";
        try
        {
            // Dá ao WPF a oportunidade de desenhar a janela antes das leituras.
            await Task.Yield();

            // Microsoft.Data.Sqlite may execute its async methods synchronously.
            // Keep SQLite work off the UI thread, including connection/schema setup.
            var recordsTask = Task.Run(() => _repository.GetAllAsync(cancellationToken), cancellationToken);
            var intervalsTask = Task.Run(() => _repository.GetAllServiceIntervalsAsync(cancellationToken), cancellationToken);
            var customOrderTask = _preferences.LoadCustomOrderAsync();
            var loaded = await recordsTask;
            var preferencesTask = _preferences.ApplyAsync(loaded);
            await Task.WhenAll(preferencesTask, intervalsTask, customOrderTask);

            var markedIds = _all.Where(x => x.IsMarkedForBatch).Select(x => x.Id).ToHashSet();
            var selectedId = SelectedMilitary?.Id;
            foreach (var record in loaded) record.IsMarkedForBatch = markedIds.Contains(record.Id);
            _all.Clear();
            _all.AddRange(loaded);
            ApplyServiceTimeCalculations(intervalsTask.Result);
            RebuildSearchIndex();
            _savedCustomOrder.Clear();
            _savedCustomOrder.AddRange(customOrderTask.Result.Where(id => id > 0).Distinct());
            _suspendFiltering = true;
            try
            {
                if (initialSettings is not null) ApplySettingsCore(initialSettings);
                ApplySortModeCore();
                if (initialList is not null) ApplyNamedList(initialList, refresh: false);
                else RemoveMissingIdsFromActiveList();
                BuildFilterOptions();
                UpdateStatistics();
            }
            finally { _suspendFiltering = false; }
            ApplyFilter();
            SelectedMilitary = Military.FirstOrDefault(x => x.Id == selectedId);
            StatusText = $"{FilteredCount} de {TotalCount} militares · Efetivo atualizado.";
        }
        catch (Exception ex)
        {
            StatusText = "Falha ao carregar militares: " + ex.Message;
            await _log.WriteAsync("Falha ao carregar Listar Militares nativo.", ex);
            throw;
        }
        finally { IsBusy = false; }
    }

    public void ApplyNamedList(MilitarySavedList? savedList, bool refresh = true)
    {
        _activeListOrder.Clear();
        if (savedList is null)
        {
            _activeListId = string.Empty;
            _activeListName = string.Empty;
        }
        else
        {
            _activeListId = savedList.Id;
            _activeListName = savedList.Name;
            _activeListOrder.AddRange(savedList.OrderedMilitaryIds.Where(id => id > 0).Distinct());
            RemoveMissingIdsFromActiveList();
        }
        OnPropertyChanged(nameof(ActiveListId));
        OnPropertyChanged(nameof(ActiveListName));
        OnPropertyChanged(nameof(HasActiveNamedList));
        SelectedMilitary = null;
        if (!HasActiveNamedList) ApplySortModeCore();
        if (refresh) ApplyFilter();
    }

    public IReadOnlyList<int> GetActiveListOrderIds() => _activeListOrder.ToList();
    public IReadOnlyList<int> GetCurrentVisibleOrderIds() => Military.Select(x => x.Id).ToList();
    public IReadOnlyList<MilitaryRecord> GetAllRecords() => _all.ToList();
    public void RefreshFilter()
    {
        _searchTimer.Stop();
        ApplyFilter();
    }

    public async Task ToggleFavoriteAsync(MilitaryRecord military)
    {
        await _preferences.ToggleFavoriteAsync(military);
        UpdateStatistics();
        ApplyFilter();
    }

    public async Task SetAttachedAsync(MilitaryRecord military, bool value)
    {
        await _preferences.SetAttachedAsync(military, value);
        UpdateStatistics();
        ApplyFilter();
    }

    public async Task SetNoteAsync(MilitaryRecord military, string note)
    {
        await _preferences.SetNoteAsync(military, note);
        UpdateSearchIndex(military);
        SelectedMilitary = null;
        SelectedMilitary = military;
    }

    public async Task RemoveAsync(MilitaryRecord military)
    {
        await _preferences.AddToTrashAsync(military);
        await _repository.DeleteAsync(military.Id);
        _all.RemoveAll(x => x.Id == military.Id);
        _searchIndex.Remove(military.Id);
        _activeListOrder.RemoveAll(x => x == military.Id);
        UpdateStatistics();
        ApplyFilter();
    }

    public async Task RemoveTransferredAsync(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        _all.RemoveAll(x => set.Contains(x.Id));
        foreach (var id in set) _searchIndex.Remove(id);
        _activeListOrder.RemoveAll(set.Contains);
        _savedCustomOrder.RemoveAll(set.Contains);
        UpdateStatistics();
        ApplyFilter();
        if (!HasActiveNamedList) await _preferences.SaveCustomOrderAsync(_savedCustomOrder);
    }

    public async Task RefreshRecordAsync(int id)
    {
        var refreshed = await _repository.GetByIdAsync(id);
        if (refreshed is null) return;
        await _preferences.ApplyAsync(new[] { refreshed });
        ApplyServiceTime(refreshed, await _repository.GetServiceIntervalsAsync(id));
        var old = _all.FirstOrDefault(x => x.Id == id);
        if (old is not null)
        {
            refreshed.IsMarkedForBatch = old.IsMarkedForBatch;
            var index = _all.IndexOf(old);
            _all[index] = refreshed;
        }
        UpdateSearchIndex(refreshed);
        UpdateStatistics();
        ApplyFilter();
        SelectedMilitary = Military.FirstOrDefault(x => x.Id == refreshed.Id);
    }

    public async Task MoveAsync(MilitaryRecord military, int delta)
    {
        if (HasActiveNamedList)
        {
            var index = _activeListOrder.IndexOf(military.Id);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= _activeListOrder.Count) return;
            (_activeListOrder[index], _activeListOrder[target]) = (_activeListOrder[target], _activeListOrder[index]);
            ApplyFilter();
            SelectedMilitary = military;
            return;
        }

        var globalIndex = _all.FindIndex(x => x.Id == military.Id);
        var globalTarget = globalIndex + delta;
        if (globalIndex < 0 || globalTarget < 0 || globalTarget >= _all.Count) return;
        (_all[globalIndex], _all[globalTarget]) = (_all[globalTarget], _all[globalIndex]);
        await SaveCurrentOrderAsync();
        ApplyFilter();
        SelectedMilitary = military;
    }

    public async Task MoveItemsAsync(IEnumerable<MilitaryRecord> items, MilitaryRecord target, bool insertAfter = false)
    {
        var movingIds = items.Select(x => x.Id).Distinct().ToHashSet();
        if (movingIds.Count == 0 || movingIds.Contains(target.Id)) return;

        if (HasActiveNamedList)
        {
            var moving = _activeListOrder.Where(movingIds.Contains).ToList();
            if (moving.Count == 0) return;
            _activeListOrder.RemoveAll(movingIds.Contains);
            var targetIndex = _activeListOrder.IndexOf(target.Id);
            if (targetIndex < 0) targetIndex = _activeListOrder.Count;
            else if (insertAfter) targetIndex++;
            _activeListOrder.InsertRange(Math.Clamp(targetIndex, 0, _activeListOrder.Count), moving);
            ApplyFilter();
            SelectedMilitary = _all.FirstOrDefault(x => x.Id == moving[0]);
            return;
        }

        var globalMoving = _all.Where(x => movingIds.Contains(x.Id)).ToList();
        if (globalMoving.Count == 0) return;
        _all.RemoveAll(x => movingIds.Contains(x.Id));
        var globalTargetIndex = _all.FindIndex(x => x.Id == target.Id);
        if (globalTargetIndex < 0) globalTargetIndex = _all.Count;
        else if (insertAfter) globalTargetIndex++;
        _all.InsertRange(Math.Clamp(globalTargetIndex, 0, _all.Count), globalMoving);
        await SaveCurrentOrderAsync();
        ApplyFilter();
        SelectedMilitary = globalMoving[0];
    }

    public async Task SetColorAsync(MilitaryRecord military, string? color)
        => await SetColorsAsync([military], color);

    public async Task SetColorsAsync(IEnumerable<MilitaryRecord> military, string? color)
    {
        var selected = military.DistinctBy(x => x.Id).ToList();
        if (selected.Count == 0) return;

        var selectedId = SelectedMilitary?.Id;
        await _preferences.SetColorsAsync(selected, color);
        ApplyFilter();
        SelectedMilitary = selected.FirstOrDefault(x => x.Id == selectedId) ?? selected[0];
    }

    public async Task ResetOrderAsync()
    {
        if (HasActiveNamedList)
        {
            var map = _all.ToDictionary(x => x.Id);
            var ordered = _activeListOrder
                .Where(map.ContainsKey)
                .Select(id => map[id])
                .Order(Comparer<MilitaryRecord>.Create(CompareByRankFormationYearName))
                .Select(x => x.Id)
                .ToList();
            _activeListOrder.Clear();
            _activeListOrder.AddRange(ordered);
            ApplyFilter();
            return;
        }

        _all.Sort(CompareByRankFormationYearName);
        _sortMode = "Posto/Graduação";
        OnPropertyChanged(nameof(SortMode));
        ApplyFilter();
    }

    public void SortByArmyHierarchy(ListSortDirection direction = ListSortDirection.Ascending)
    {
        SortCurrentScope(ApplyDirection(CompareByRankFormationYearName, direction));
        if (!HasActiveNamedList && direction == ListSortDirection.Ascending)
        {
            _sortMode = "Posto/Graduação";
            OnPropertyChanged(nameof(SortMode));
        }
        StatusText = direction == ListSortDirection.Ascending
            ? "Ordenado por P/G: hierarquia do Exército e ano do mais antigo para o mais moderno."
            : "Ordenado por P/G em ordem inversa.";
    }

    public void SortByFormationYear(ListSortDirection direction = ListSortDirection.Ascending)
    {
        SortCurrentScope(ApplyDirection(CompareByFormationYearRankName, direction));
        StatusText = direction == ListSortDirection.Ascending
            ? "Ordenado por ano: mais antigo para mais moderno, respeitando P/G dentro do ano."
            : "Ordenado por ano: mais moderno para mais antigo.";
    }

    public void SortByName(ListSortDirection direction = ListSortDirection.Ascending)
    {
        SortCurrentScope(ApplyDirection(
            (left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase),
            direction));
        if (!HasActiveNamedList && direction == ListSortDirection.Ascending)
        {
            _sortMode = "Nome";
            OnPropertyChanged(nameof(SortMode));
        }
    }

    public async Task SaveSettingsAsync(
        IEnumerable<string>? visibleColumns = null,
        IReadOnlyDictionary<string, double>? columnWidths = null,
        IEnumerable<string>? columnOrder = null)
    {
        // Preserva o layout quando a gravação for disparada apenas por uma mudança
        // de ordem/filtro. A implementação antiga recriava o objeto e apagava
        // VisibleColumns/ColumnWidths em toda saída da janela.
        var settings = await _preferences.LoadListSettingsAsync();
        settings.Search = SearchText;
        settings.Rank = SelectedRank;
        settings.Year = SelectedYear;
        settings.FavoritesOnly = FavoritesOnly;
        settings.AttachedOnly = AttachedOnly;
        settings.MissingTransportOnly = MissingTransportOnly;
        settings.OrderLocked = OrderLocked;
        settings.SortMode = SortMode;
        settings.CustomOrder = _savedCustomOrder.ToList();
        if (visibleColumns is not null) settings.VisibleColumns = visibleColumns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (columnWidths is not null) settings.ColumnWidths = new Dictionary<string, double>(columnWidths, StringComparer.OrdinalIgnoreCase);
        if (columnOrder is not null) settings.ColumnOrder = columnOrder.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _preferences.SaveListSettingsAsync(settings);
    }

    public async Task RestoreSettingsAsync()
    {
        var settings = await _preferences.LoadListSettingsAsync();
        ApplySettingsCore(settings);
        ApplySortModeCore();
        BuildFilterOptions();
        ApplyFilter();
    }

    private void ApplySettingsCore(MilitaryListSettings settings)
    {
        _searchText = settings.Search ?? string.Empty;
        _selectedRank = NormalizeRankFilterValue(settings.Rank);
        _selectedYear = string.IsNullOrWhiteSpace(settings.Year) ? "Todos" : settings.Year;
        _favoritesOnly = settings.FavoritesOnly;
        _attachedOnly = settings.AttachedOnly;
        _missingTransportOnly = settings.MissingTransportOnly;
        _orderLocked = settings.OrderLocked;
        _sortMode = NormalizeSortMode(settings.SortMode);
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedRank));
        OnPropertyChanged(nameof(SelectedYear));
        OnPropertyChanged(nameof(FavoritesOnly));
        OnPropertyChanged(nameof(AttachedOnly));
        OnPropertyChanged(nameof(MissingTransportOnly));
        OnPropertyChanged(nameof(OrderLocked));
        OnPropertyChanged(nameof(SortMode));
    }

    public IReadOnlyList<MilitaryRecord> GetSelectedOrVisible(IEnumerable<MilitaryRecord> selected)
    {
        var list = selected.DistinctBy(x => x.Id).ToList();
        return list.Count > 0 ? list : Military.ToList();
    }

    private void SortCurrentScope(Comparison<MilitaryRecord> comparison)
    {
        if (HasActiveNamedList)
        {
            var map = _all.ToDictionary(x => x.Id);
            _activeListOrder.Sort((leftId, rightId) =>
            {
                var leftFound = map.TryGetValue(leftId, out var left);
                var rightFound = map.TryGetValue(rightId, out var right);
                if (!leftFound && !rightFound) return leftId.CompareTo(rightId);
                if (!leftFound) return 1;
                if (!rightFound) return -1;
                return comparison(left!, right!);
            });
        }
        else
        {
            _all.Sort(comparison);
        }

        ApplyFilter();
    }

    private static Comparison<MilitaryRecord> ApplyDirection(Comparison<MilitaryRecord> comparison, ListSortDirection direction)
        => direction == ListSortDirection.Ascending ? comparison : (left, right) => comparison(right, left);

    private void ApplyServiceTimeCalculations(IReadOnlyDictionary<int, List<ServiceIntervalRecord>> byMilitary)
    {
        foreach (var military in _all)
            ApplyServiceTime(military, byMilitary.TryGetValue(military.Id, out var intervals) ? intervals : null);
    }

    private static void ApplyServiceTime(MilitaryRecord military, IEnumerable<ServiceIntervalRecord>? intervals)
    {
        var ranges = (intervals ?? [])
            .Select(x => (Start: MilitaryFormatting.ParseDate(x.StartDate), End: MilitaryFormatting.ParseDate(x.EndDate) ?? DateTime.Today))
            .Where(x => x.Start is not null && x.End.Date >= x.Start.Value.Date)
            .Select(x => (Start: x.Start!.Value.Date, End: x.End.Date))
            .OrderBy(x => x.Start)
            .ToList();

        if (ranges.Count == 0)
        {
            military.CalculatedServiceTimeDays = null;
            military.CalculatedServiceTimeText = string.Empty;
            return;
        }

        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End.AddDays(1))
            {
                merged.Add(range);
                continue;
            }
            var last = merged[^1];
            if (range.End > last.End) merged[^1] = (last.Start, range.End);
        }

        var days = merged.Sum(x => Math.Max(0, (x.End - x.Start).Days + 1));
        var years = days / 365;
        var months = (days % 365) / 30;
        var rest = (days % 365) % 30;
        military.CalculatedServiceTimeDays = days;
        military.CalculatedServiceTimeText = $"{years}a, {months:00}m e {rest:00}d ({days:N0} dias) — intervalos cadastrados";
    }

    private void ApplyFilter()
    {
        if (_suspendFiltering) return;
        var query = Normalize(SearchText);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MilitaryRecord> source = _all;
        if (HasActiveNamedList)
        {
            var map = _all.ToDictionary(x => x.Id);
            source = _activeListOrder.Where(map.ContainsKey).Select(id => map[id]);
        }

        var filtered = source.Where(item =>
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!_searchIndex.TryGetValue(item.Id, out var index))
                {
                    index = CreateSearchIndex(item);
                    _searchIndex[item.Id] = index;
                }
                // Each term must match. Numeric identifiers stay separate so a CPF
                // cannot accidentally match digits spanning two unrelated fields.
                if (!terms.All(term => index.Text.Contains(term) ||
                    (term.All(c => char.IsDigit(c) || c is '.' or '-' or '/' or '(' or ')' or '+') &&
                     MilitaryFormatting.Digits(term) is { Length: > 0 } digits &&
                     index.Identifiers.Any(value => value.Contains(digits))))) return false;
            }
            if (!RankMatches(item, SelectedRank)) return false;
            if (!YearMatches(item.FormationYear, SelectedYear)) return false;
            if (FavoritesOnly && !item.IsFavorite) return false;
            if (AttachedOnly && !item.IsAttached) return false;
            if (MissingTransportOnly && (MilitaryRecord.IsYes(item.ReceivesTransportAid) || item.IsAttached)) return false;
            if (MarkedOnly && !item.IsMarkedForBatch) return false;
            return true;
        }).ToList();

        // Do not rebuild the visual rows for a favorite, mark or unchanged search.
        // Capture selection before ItemsSource changes (WPF clears it synchronously).
        var selectedId = SelectedMilitary?.Id;
        if (!Military.SequenceEqual(filtered))
            Military = new ObservableCollection<MilitaryRecord>(filtered);
        SelectedMilitary = selectedId is { } id ? filtered.FirstOrDefault(x => x.Id == id) : null;
        TotalCount = HasActiveNamedList ? _activeListOrder.Count : _all.Count;
        FilteredCount = filtered.Count;
        StatusText = MarkedOnly
            ? $"Mostrando somente {FilteredCount} militar(es) marcado(s). Pressione Esc para limpar as marcações e voltar à lista completa."
            : HasActiveNamedList
                ? $"Lista “{ActiveListName}”: mostrando {FilteredCount} de {_activeListOrder.Count} militar(es). Arraste as linhas para ordenar."
                : $"Mostrando {FilteredCount} de {_all.Count} militar(es).";
    }

    private void RebuildSearchIndex()
    {
        _searchIndex.Clear();
        foreach (var item in _all) _searchIndex[item.Id] = CreateSearchIndex(item);
    }

    private void UpdateSearchIndex(MilitaryRecord item)
        => _searchIndex[item.Id] = CreateSearchIndex(item);

    private static SearchIndexEntry CreateSearchIndex(MilitaryRecord item)
    {
        var text = Normalize($"{item.Rank} {item.Name} {item.WarName} {item.Cpf} {item.FormattedCpf} {item.PrecCp} {item.MilitaryId} {item.FormationYear} {item.Phone} {item.Email} {item.Address} {item.ZipCode} {item.Bank} {item.Agency} {item.Account} {item.Annotation}");
        var identifiers = new[] { item.Cpf, item.PrecCp, item.MilitaryId, item.ZipCode, item.Phone, item.Agency, item.Account }
            .Select(MilitaryFormatting.Digits).Where(x => x.Length > 0).ToArray();
        return new SearchIndexEntry(text, identifiers);
    }

    private void UpdateStatistics()
    {
        FavoriteCount = _all.Count(x => x.IsFavorite);
        MissingTransportCount = _all.Count(x => !MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached);
        // Count registered photos without probing OneDrive/network paths on opening.
        MissingPhotoCount = _all.Count(x => string.IsNullOrWhiteSpace(x.PhotoPath));
    }

    private readonly record struct SearchIndexEntry(string Text, string[] Identifiers);

    public void ClearFilters()
    {
        _suspendFiltering = true;
        try
        {
            SearchText = string.Empty;
            SelectedRank = "Todos";
            SelectedYear = "Todos";
            FavoritesOnly = AttachedOnly = MissingTransportOnly = MarkedOnly = false;
        }
        finally { _suspendFiltering = false; }
        RefreshFilter();
    }

    public void StopBackgroundWork()
    {
        _searchTimer.Stop();
    }

    private void BuildFilterOptions()
    {
        var currentRank = NormalizeRankFilterValue(SelectedRank);
        var currentYear = SelectedYear;
        RankOptions.Clear();
        RankOptions.Add("Todos");
        foreach (var rank in _all
                     .Select(x => MilitaryRankService.ShortName(x.Rank))
                     .Where(x => !string.IsNullOrWhiteSpace(x) && x != "—")
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(RankOrder)
                     .ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            RankOptions.Add(rank);
        _selectedRank = ContainsOption(RankOptions, currentRank) ? currentRank : "Todos";
        OnPropertyChanged(nameof(SelectedRank));
        RefreshYearOptionsForRank(currentYear);
    }

    private void RefreshYearOptionsForRank(string? preferredYear = null)
    {
        var currentYear = string.IsNullOrWhiteSpace(preferredYear) ? SelectedYear : preferredYear;
        var source = _all.Where(item => RankMatches(item, SelectedRank));
        YearOptions.Clear();
        YearOptions.Add("Todos");
        foreach (var year in source
                     .Select(x => x.FormationYear?.Trim() ?? string.Empty)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(FormationYearOrder)
                     .ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            YearOptions.Add(year);

        _selectedYear = YearOptions.FirstOrDefault(option => YearMatches(option, currentYear)) ?? "Todos";
        OnPropertyChanged(nameof(SelectedYear));
    }

    private void RemoveMissingIdsFromActiveList()
    {
        if (!HasActiveNamedList) return;
        var valid = _all.Select(x => x.Id).ToHashSet();
        _activeListOrder.RemoveAll(id => !valid.Contains(id));
    }

    private async Task SaveCurrentOrderAsync()
    {
        _savedCustomOrder.Clear();
        _savedCustomOrder.AddRange(_all.Select(x => x.Id));
        if (!_sortMode.Equals("Ordem salva", StringComparison.OrdinalIgnoreCase))
        {
            _sortMode = "Ordem salva";
            OnPropertyChanged(nameof(SortMode));
        }
        await _preferences.SaveCustomOrderAsync(_savedCustomOrder);
        // A ordem dos IDs já era salva imediatamente, mas o modo "Ordem salva"
        // ficava para o evento Closing (async void) e podia não chegar ao disco.
        // Persistir os dois juntos faz a posição reaparecer na próxima abertura.
        await SaveSettingsAsync();
    }

    private void ApplySortModeCore()
    {
        if (_all.Count == 0 || HasActiveNamedList) return;
        switch (NormalizeSortMode(_sortMode))
        {
            case "Posto/Graduação":
                _all.Sort(CompareByRankFormationYearName);
                break;
            case "Nome":
                _all.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
                break;
            default:
                var positions = _savedCustomOrder
                    .Select((id, index) => (id, index))
                    .ToDictionary(x => x.id, x => x.index);
                _all.Sort((left, right) =>
                {
                    var lp = positions.TryGetValue(left.Id, out var l) ? l : int.MaxValue;
                    var rp = positions.TryGetValue(right.Id, out var r) ? r : int.MaxValue;
                    if (lp != rp) return lp.CompareTo(rp);
                    return CompareByRankFormationYearName(left, right);
                });
                break;
        }
    }

    private static string NormalizeSortMode(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Contains("ordem", StringComparison.OrdinalIgnoreCase) || text.Contains("salva", StringComparison.OrdinalIgnoreCase))
            return "Ordem salva";
        if (text.Contains("posto", StringComparison.OrdinalIgnoreCase) || text.Contains("hierarquia", StringComparison.OrdinalIgnoreCase))
            return "Posto/Graduação";
        if (text.Contains("nome", StringComparison.OrdinalIgnoreCase)) return "Nome";
        return "Posto/Graduação";
    }

    private static bool ContainsOption(IEnumerable<string> options, string? value)
        => options.Any(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeRankFilterValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Todos", StringComparison.OrdinalIgnoreCase)) return "Todos";
        var shortRank = MilitaryRankService.ShortName(value);
        return string.IsNullOrWhiteSpace(shortRank) ? value.Trim() : shortRank;
    }

    private static bool RankMatches(MilitaryRecord item, string? selectedRank)
    {
        var normalized = NormalizeRankFilterValue(selectedRank);
        if (normalized.Equals("Todos", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(MilitaryRankService.ShortName(item.Rank), normalized, StringComparison.OrdinalIgnoreCase)
               || string.Equals(MilitaryRankService.Canonicalize(item.Rank), MilitaryRankService.Canonicalize(normalized), StringComparison.OrdinalIgnoreCase)
               || string.Equals(item.Rank, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool YearMatches(string? formationYear, string? selectedYear)
    {
        if (string.IsNullOrWhiteSpace(selectedYear) || selectedYear.Equals("Todos", StringComparison.OrdinalIgnoreCase)) return true;
        var selected = ParseYear(selectedYear);
        var current = ParseYear(formationYear ?? string.Empty);
        if (selected > 0 && current > 0) return selected == current;
        return string.Equals((formationYear ?? string.Empty).Trim(), selectedYear.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareByRankFormationYearName(MilitaryRecord left, MilitaryRecord right)
    {
        var rank = RankOrder(left.Rank).CompareTo(RankOrder(right.Rank));
        if (rank != 0) return rank;

        var canonical = string.Compare(
            MilitaryRankService.Canonicalize(left.Rank),
            MilitaryRankService.Canonicalize(right.Rank),
            StringComparison.CurrentCultureIgnoreCase);
        if (canonical != 0) return canonical;

        var year = FormationYearOrder(left.FormationYear).CompareTo(FormationYearOrder(right.FormationYear));
        if (year != 0) return year;

        return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    private static int CompareByFormationYearRankName(MilitaryRecord left, MilitaryRecord right)
    {
        var year = FormationYearOrder(left.FormationYear).CompareTo(FormationYearOrder(right.FormationYear));
        if (year != 0) return year;
        return CompareByRankFormationYearName(left, right);
    }

    private static int ParseYear(string value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= 4 && int.TryParse(digits[..4], out var fullYear)) return fullYear;
        if (digits.Length == 2 && int.TryParse(digits, out var shortYear))
            return shortYear >= 70 ? 1900 + shortYear : 2000 + shortYear;
        return 0;
    }
    private static int FormationYearOrder(string value)
    {
        var year = ParseYear(value);
        return year > 0 ? year : int.MaxValue;
    }
    private static int RankOrder(string rank) => MilitaryRankService.GetOrder(rank);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }
}
