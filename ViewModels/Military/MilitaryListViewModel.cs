using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
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
    private string _activeListId = string.Empty;
    private string _activeListName = string.Empty;
    private string _searchText = string.Empty;
    private string _selectedRank = "Todos";
    private string _selectedYear = "Todos";
    private string _sortMode = "Ordem salva";
    private bool _favoritesOnly;
    private bool _attachedOnly;
    private bool _missingTransportOnly;
    private bool _markedOnly;
    private bool _orderLocked;
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
    }

    public ObservableCollection<MilitaryRecord> Military { get; } = [];
    public ObservableCollection<string> RankOptions { get; } = ["Todos"];
    public ObservableCollection<string> YearOptions { get; } = ["Todos"];
    public ObservableCollection<string> SortModes { get; } = ["Ordem salva", "Posto/Graduação", "Nome"];

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ApplyFilter(); } }
    public string SelectedRank { get => _selectedRank; set { if (SetProperty(ref _selectedRank, value)) ApplyFilter(); } }
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

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = "Lendo militares diretamente do SQLite…";
        try
        {
            _all.Clear();
            _all.AddRange(await _repository.GetAllAsync(cancellationToken));
            await _preferences.ApplyAsync(_all);
            await ApplyServiceTimeCalculationsAsync(cancellationToken);
            var customOrder = await _preferences.LoadCustomOrderAsync();
            _savedCustomOrder.Clear();
            _savedCustomOrder.AddRange(customOrder.Where(id => id > 0).Distinct());
            ApplySortModeCore();
            RemoveMissingIdsFromActiveList();
            BuildFilterOptions();
            ApplyFilter();
            StatusText = $"{TotalCount} militar(es) carregado(s) de {_repository.DatabasePath}.";
        }
        catch (Exception ex)
        {
            StatusText = "Falha ao carregar militares: " + ex.Message;
            await _log.WriteAsync("Falha ao carregar Listar Militares nativo.", ex);
            throw;
        }
        finally { IsBusy = false; }
    }

    public void ApplyNamedList(MilitarySavedList? savedList)
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
        ApplyFilter();
    }

    public IReadOnlyList<int> GetActiveListOrderIds() => _activeListOrder.ToList();
    public IReadOnlyList<int> GetCurrentVisibleOrderIds() => Military.Select(x => x.Id).ToList();
    public IReadOnlyList<MilitaryRecord> GetAllRecords() => _all.ToList();
    public void RefreshFilter() => ApplyFilter();

    public async Task ToggleFavoriteAsync(MilitaryRecord military)
    {
        await _preferences.ToggleFavoriteAsync(military);
        FavoriteCount = _all.Count(x => x.IsFavorite);
        ApplyFilter();
    }

    public async Task SetAttachedAsync(MilitaryRecord military, bool value)
    {
        await _preferences.SetAttachedAsync(military, value);
        ApplyFilter();
    }

    public async Task SetNoteAsync(MilitaryRecord military, string note)
    {
        await _preferences.SetNoteAsync(military, note);
        SelectedMilitary = null;
        SelectedMilitary = military;
    }

    public async Task RemoveAsync(MilitaryRecord military)
    {
        await _preferences.AddToTrashAsync(military);
        await _repository.DeleteAsync(military.Id);
        _all.RemoveAll(x => x.Id == military.Id);
        _activeListOrder.RemoveAll(x => x == military.Id);
        ApplyFilter();
    }

    public async Task RemoveTransferredAsync(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        _all.RemoveAll(x => set.Contains(x.Id));
        _activeListOrder.RemoveAll(set.Contains);
        _savedCustomOrder.RemoveAll(set.Contains);
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
            var index = _all.IndexOf(old);
            _all[index] = refreshed;
        }
        ApplyFilter();
        SelectedMilitary = refreshed;
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
                .OrderBy(x => RankOrder(x.Rank))
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Id)
                .ToList();
            _activeListOrder.Clear();
            _activeListOrder.AddRange(ordered);
            ApplyFilter();
            return;
        }

        _all.Sort((left, right) =>
        {
            var rank = RankOrder(left.Rank).CompareTo(RankOrder(right.Rank));
            return rank != 0 ? rank : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        _sortMode = "Posto/Graduação";
        OnPropertyChanged(nameof(SortMode));
        ApplyFilter();
    }

    public async Task SaveSettingsAsync()
    {
        await _preferences.SaveListSettingsAsync(new MilitaryListSettings
        {
            Search = SearchText,
            Rank = SelectedRank,
            Year = SelectedYear,
            FavoritesOnly = FavoritesOnly,
            AttachedOnly = AttachedOnly,
            MissingTransportOnly = MissingTransportOnly,
            OrderLocked = OrderLocked,
            SortMode = SortMode,
            CustomOrder = _savedCustomOrder.ToList()
        });
    }

    public async Task RestoreSettingsAsync()
    {
        var settings = await _preferences.LoadListSettingsAsync();
        _searchText = settings.Search ?? string.Empty;
        _selectedRank = string.IsNullOrWhiteSpace(settings.Rank) ? "Todos" : settings.Rank;
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
        ApplySortModeCore();
        ApplyFilter();
    }

    public IReadOnlyList<MilitaryRecord> GetSelectedOrVisible(IEnumerable<MilitaryRecord> selected)
    {
        var list = selected.DistinctBy(x => x.Id).ToList();
        return list.Count > 0 ? list : Military.ToList();
    }

    private async Task ApplyServiceTimeCalculationsAsync(CancellationToken cancellationToken)
    {
        var byMilitary = await _repository.GetAllServiceIntervalsAsync(cancellationToken);
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
        var query = Normalize(SearchText);
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
                var blob = Normalize($"{item.Rank} {item.Name} {item.WarName} {item.Cpf} {item.FormattedCpf} {item.PrecCp} {item.MilitaryId} {item.FormationYear} {item.Phone} {item.Email} {item.Address} {item.ZipCode} {item.Bank} {item.Agency} {item.Account} {item.Annotation}");
                var queryDigits = MilitaryFormatting.Digits(SearchText);
                var blobDigits = MilitaryFormatting.Digits($"{item.Cpf} {item.FormattedCpf} {item.PrecCp} {item.MilitaryId} {item.ZipCode} {item.Phone} {item.Agency} {item.Account}");
                if (!blob.Contains(query) && (string.IsNullOrWhiteSpace(queryDigits) || !blobDigits.Contains(queryDigits))) return false;
            }
            if (!string.Equals(SelectedRank, "Todos", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Rank, SelectedRank, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(SelectedYear, "Todos", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.FormationYear, SelectedYear, StringComparison.OrdinalIgnoreCase)) return false;
            if (FavoritesOnly && !item.IsFavorite) return false;
            if (AttachedOnly && !item.IsAttached) return false;
            if (MissingTransportOnly && (MilitaryRecord.IsYes(item.ReceivesTransportAid) || item.IsAttached)) return false;
            if (MarkedOnly && !item.IsMarkedForBatch) return false;
            return true;
        }).ToList();

        Military.Clear();
        foreach (var item in filtered) Military.Add(item);
        TotalCount = HasActiveNamedList ? _activeListOrder.Count : _all.Count;
        FilteredCount = filtered.Count;
        FavoriteCount = _all.Count(x => x.IsFavorite);
        MissingTransportCount = _all.Count(x => !MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached);
        MissingPhotoCount = _all.Count(x => string.IsNullOrWhiteSpace(x.PhotoPath) || !File.Exists(x.PhotoPath));
        StatusText = MarkedOnly
            ? $"Mostrando somente {FilteredCount} militar(es) marcado(s). Pressione Esc para limpar as marcações e voltar à lista completa."
            : HasActiveNamedList
                ? $"Lista “{ActiveListName}”: mostrando {FilteredCount} de {_activeListOrder.Count} militar(es). Arraste as linhas para ordenar."
                : $"Mostrando {FilteredCount} de {_all.Count} militar(es).";
    }

    private void BuildFilterOptions()
    {
        var currentRank = SelectedRank;
        var currentYear = SelectedYear;
        RankOptions.Clear();
        RankOptions.Add("Todos");
        foreach (var rank in _all.Select(x => x.Rank).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(RankOrder).ThenBy(x => x))
            RankOptions.Add(rank);
        YearOptions.Clear();
        YearOptions.Add("Todos");
        foreach (var year in _all.Select(x => x.FormationYear).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(ParseYear).ThenByDescending(x => x))
            YearOptions.Add(year);
        SelectedRank = RankOptions.Contains(currentRank) ? currentRank : "Todos";
        SelectedYear = YearOptions.Contains(currentYear) ? currentYear : "Todos";
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
    }

    private void ApplySortModeCore()
    {
        if (_all.Count == 0 || HasActiveNamedList) return;
        switch (NormalizeSortMode(_sortMode))
        {
            case "Posto/Graduação":
                _all.Sort((left, right) => MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name));
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
                    return MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name);
                });
                break;
        }
    }

    private static string NormalizeSortMode(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Contains("posto", StringComparison.OrdinalIgnoreCase) || text.Contains("hierarquia", StringComparison.OrdinalIgnoreCase))
            return "Posto/Graduação";
        if (text.Contains("nome", StringComparison.OrdinalIgnoreCase)) return "Nome";
        return "Ordem salva";
    }
    private static int ParseYear(string value) => int.TryParse(new string(value.Where(char.IsDigit).Take(4).ToArray()), out var year) ? year : 0;
    private static int RankOrder(string rank) => MilitaryRankService.GetOrder(rank);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }
}
