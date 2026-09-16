using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Personnel;

public partial class DutyRosterWindow : Window
{
    private const string DragPersonKeyFormat = "SIGFUR.DutyRoster.PersonKey";
    private const string DragSourceDateFormat = "SIGFUR.DutyRoster.SourceDate";
    private const string DragRosterRowFormat = "SIGFUR.DutyRoster.RosterRow";
    private const double RosterNameColumnWidth = 220;
    private const double DutyDayColumnWidth = 32;
    private const double RosterHeaderHeight = 44;
    private const double RosterRowHeight = 36;
    private const string VacationPresentationMark = "APRESENTACAO";
    private readonly DutyRosterService _service = new(App.Paths, App.Json);
    private DutyRosterStore _store = new();
    private readonly ObservableCollection<DutyRosterPerson> _selected = [];
    private readonly ObservableCollection<DutyRosterPerson> _available = [];
    private readonly Dictionary<string, Button> _dutyButtons = new(StringComparer.OrdinalIgnoreCase);
    private List<DutyRosterPerson> _all = [];
    private RedDayStore _redDayStore = new();
    private readonly Stack<string> _undo = new();
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;
    private bool _loading;
    private bool _effectivePanelCollapsed;
    private Point _dragStartPoint;
    private DutyRosterPerson? _pendingDragPerson;
    private DateTime? _pendingDragSourceDate;
    private bool _pendingDragMovesRosterRow;
    private Button? _calendarFilterButton;

    public DutyRosterWindow()
    {
        InitializeComponent();
        InsertCalendarFilterButton();
        App.UiState.Attach(this);
        SelectedPeopleList.FontSize = 11;
        AvailablePeopleList.FontSize = 11;
        SelectedPeopleList.ItemsSource = _selected;
        AvailablePeopleList.ItemsSource = _available;
        SelectedPeopleList.AllowDrop = true;
        SelectedPeopleList.PreviewMouseLeftButtonDown += PeopleList_PreviewMouseLeftButtonDown;
        SelectedPeopleList.MouseMove += PeopleList_MouseMove;
        SelectedPeopleList.DragOver += SelectedPeopleList_DragOver;
        SelectedPeopleList.Drop += SelectedPeopleList_Drop;
        Loaded += async (_, _) => await LoadAsync();
        Closing += async (_, _) => await SaveAsync(false);
    }

    private void InsertCalendarFilterButton()
    {
        if (EffectiveToggleButton.Parent is not Panel panel) return;

        _calendarFilterButton = new Button
        {
            Content = "Filtro calendario: todos",
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Filtra quem aparece no calendario operacional. Nao altera a prioridade da escala."
        };
        if (TryFindResource("SecondaryButtonStyle") is Style style)
            _calendarFilterButton.Style = style;
        _calendarFilterButton.Click += ChooseCalendarFilter_Click;

        var index = panel.Children.IndexOf(EffectiveToggleButton);
        panel.Children.Insert(index >= 0 ? index + 1 : panel.Children.Count, _calendarFilterButton);
    }

    private DutyRosterMonth CurrentMonth
    {
        get
        {
            var key = DutyRosterService.MonthKey(_year, _month);
            if (!_store.Months.TryGetValue(key, out var month))
                _store.Months[key] = month = new DutyRosterMonth();
            return month;
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _store = await _service.LoadAsync() ?? new DutyRosterStore();
        _store.MarkStyles ??= DutyMarkStyle.Defaults();
        if (_store.MarkStyles.Count == 0) _store.MarkStyles = DutyMarkStyle.Defaults();
        EnsureMarkStyles();
        _store.Months ??= new(StringComparer.OrdinalIgnoreCase);
        _store.SelectedMilitaryIds ??= [];
        _store.ExtraPeople ??= [];
        _store.Order ??= [];
        _store.OrderKeys ??= [];
        _store.OperationalCalendarPersonKey ??= string.Empty;
        if (_store.CounterMonths is not (3 or 6 or 12 or 24 or 36)) _store.CounterMonths = 12;
        await App.RedDays.MigrateDutyRosterRedDaysAsync(_store);
        _redDayStore = await App.RedDays.LoadAsync();

        var military = await App.MilitaryRepository.GetAllAsync();
        _all = military.Select(x => new DutyRosterPerson
        {
            MilitaryId = x.Id,
            Key = "M:" + x.Id,
            Rank = x.Rank,
            Name = x.Name,
            WarName = x.WarName
        }).ToList();
        _all.AddRange(_store.ExtraPeople.Where(x => !string.IsNullOrWhiteSpace(x)).Select(name => new DutyRosterPerson
        {
            MilitaryId = 0,
            Key = "E:" + name.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            IsExtra = true
        }));

        // Primeira abertura: somente 3º Sargentos. Depois disso, a escolha do operador é preservada.
        if (!_store.SelectionInitialized)
        {
            _store.SelectedMilitaryIds = military
                .Where(x => MilitaryRankService.GetOrder(x.Rank) == 14)
                .Select(x => x.Id)
                .ToList();
            _store.SelectionInitialized = true;
        }

        var chosen = _all
            .Where(x => x.IsExtra
                ? _store.ExtraPeople.Contains(x.Name, StringComparer.OrdinalIgnoreCase)
                : _store.SelectedMilitaryIds.Contains(x.MilitaryId))
            .OrderBy(GetStoredOrderIndex)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _selected.Clear();
        foreach (var person in chosen) _selected.Add(person);
        SelectCounterMonths(_store.CounterMonths);
        RefreshAvailable();
        RenderLegend();
        RenderMonth();
        _loading = false;
    }

    private int GetStoredOrderIndex(DutyRosterPerson person)
    {
        if (_store.OrderKeys.Count > 0)
        {
            var keyIndex = _store.OrderKeys.FindIndex(x => x.Equals(person.Key, StringComparison.OrdinalIgnoreCase));
            if (keyIndex >= 0) return keyIndex;
        }

        if (!person.IsExtra)
        {
            var legacyIndex = _store.Order.IndexOf(person.MilitaryId);
            if (legacyIndex >= 0) return legacyIndex;
        }

        return int.MaxValue;
    }

    private void SelectCounterMonths(int months)
    {
        foreach (var item in CounterMonthsBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var value) && value == months)
            {
                CounterMonthsBox.SelectedItem = item;
                return;
            }
        }
        CounterMonthsBox.SelectedIndex = 2;
    }

    private void PushUndo()
    {
        var json = JsonSerializer.Serialize(_store.Months);
        if (_undo.Count == 0 || _undo.Peek() != json) _undo.Push(json);
        if (_undo.Count <= 30) return;
        var keep = _undo.Reverse().TakeLast(30).ToArray();
        _undo.Clear();
        foreach (var item in keep) _undo.Push(item);
    }

    private void RenderMonth()
    {
        MonthButton.Content = new DateTime(_year, _month, 1)
            .ToString("MMMM yyyy", new CultureInfo("pt-BR")).ToUpperInvariant();
        _dutyButtons.Clear();
        FrozenNameHost.Children.Clear();
        FrozenNameHost.RowDefinitions.Clear();
        FrozenNameHost.ColumnDefinitions.Clear();
        RosterHost.Children.Clear();
        RosterHost.RowDefinitions.Clear();
        RosterHost.ColumnDefinitions.Clear();
        FrozenNameHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RosterNameColumnWidth) });
        var days = DateTime.DaysInMonth(_year, _month);
        for (var day = 1; day <= days; day++)
            RosterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DutyDayColumnWidth) });
        FrozenNameHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RosterHeaderHeight) });
        RosterHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RosterHeaderHeight) });
        for (var i = 0; i < _selected.Count; i++)
        {
            FrozenNameHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RosterRowHeight) });
            RosterHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RosterRowHeight) });
        }

        AddHeaderCell("MILITAR", 0, false, null);
        for (var day = 1; day <= days; day++)
        {
            var date = new DateTime(_year, _month, day);
            AddHeaderCell($"{day:00}\n{date:ddd}".ToUpperInvariant(), day, DutyRosterService.IsRedDay(CurrentMonth, date, _redDayStore), date);
        }

        for (var row = 0; row < _selected.Count; row++)
        {
            var person = _selected[row];
            var nameBorder = new Border
            {
                BorderBrush = TryBrush("#D9E2EC"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = TryBrush(row % 2 == 0 ? "#F8FAFC" : "#FFFFFF"),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                ToolTip = "Arraste este nome para outro ponto da coluna para reordenar a linha; arraste para um dia para escalar.",
                AllowDrop = true,
                DataContext = person,
                Tag = row
            };
            nameBorder.PreviewMouseLeftButtonDown += (_, e) => PrepareDrag(person, null, e, moveRosterRow: true);
            nameBorder.MouseMove += (sender, e) => StartPreparedDrag(sender as DependencyObject ?? nameBorder, e);
            nameBorder.DragOver += RowHeader_DragOver;
            nameBorder.DragLeave += RowHeader_DragLeave;
            nameBorder.Drop += RowHeader_Drop;
            nameBorder.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = person.ShortRank, FontSize = 8.5, Foreground = TryBrush("#64748B") },
                    new HighlightedNameTextBlock
                    {
                        FullName = person.Name,
                        WarName = person.WarName,
                        FontSize = 10.5,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontWeight = FontWeights.SemiBold
                    }
                }
            };
            Grid.SetRow(nameBorder, row + 1);
            Grid.SetColumn(nameBorder, 0);
            FrozenNameHost.Children.Add(nameBorder);
            for (var day = 1; day <= days; day++) AddDutyCell(person, row + 1, day);
        }

        RefreshCounters();
        NormalizeOperationalCalendarFilter();
        UpdateCalendarFilterButton();
        SelectionSummaryText.Text = $"{_selected.Count:N0} pessoa(s) concorrendo • você pode incluir ou retirar livremente";
        StatusText.Text = $"{_selected.Count:N0} pessoa(s) na escala. A ordem e o efetivo ficam salvos para a próxima abertura.";
    }

    private void AddHeaderCell(string text, int column, bool red, DateTime? date)
    {
        var target = date is null ? FrozenNameHost : RosterHost;
        var button = new Button
        {
            Content = text,
            FontSize = date is null ? 10 : 9,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(2),
            Background = TryBrush(red ? "#FEE2E2" : "#E2E8F0"),
            Foreground = TryBrush(red ? "#991B1B" : "#334155"),
            BorderBrush = TryBrush("#CBD5E1"),
            BorderThickness = new Thickness(0, 0, 1, 1)
        };
        if (date is not null)
        {
            var redInfo = RedDayService.GetInfo(date.Value, _redDayStore);
            button.ToolTip = (redInfo.IsRedDay ? redInfo.TooltipText + "\n" : string.Empty) + "Botão direito: marcar ou desmarcar como dia vermelho";
            var menu = new ContextMenu();
            var item = new MenuItem { Header = redInfo.Kind is RedDayKind.Weekend or RedDayKind.Holiday ? "Dia vermelho automático" : red ? "Desmarcar dia vermelho" : "Marcar dia vermelho" };
            item.IsEnabled = redInfo.Kind is not (RedDayKind.Weekend or RedDayKind.Holiday);
            item.Click += async (_, _) =>
            {
                PushUndo();
                var manualRed = redInfo.Kind is RedDayKind.Manual or RedDayKind.DutyRoster;
                await App.RedDays.SetManualRedDayAsync(date.Value, !manualRed, "Escala Sgt de Dia", "Escala Sgt de Dia");
                _redDayStore = await App.RedDays.LoadAsync();
                RenderMonth();
            };
            menu.Items.Add(item);
            button.ContextMenu = menu;
        }
        Grid.SetRow(button, 0);
        Grid.SetColumn(button, date is null ? 0 : column - 1);
        target.Children.Add(button);
    }

    private void AddDutyCell(DutyRosterPerson person, int row, int day)
    {
        var date = new DateTime(_year, _month, day);
        var button = new Button
        {
            FontWeight = FontWeights.Bold,
            BorderBrush = TryBrush("#E2E8F0"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            AllowDrop = true
        };
        button.Click += (_, _) => ToggleAssignment(person, date);
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var dateKey = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
            var sourceDate = CurrentMonth.Assignments.TryGetValue(dateKey, out var who) &&
                             who.Equals(person.Key, StringComparison.OrdinalIgnoreCase)
                ? date
                : (DateTime?)null;
            PrepareDrag(person, sourceDate, e);
        };
        button.MouseMove += (sender, e) => StartPreparedDrag(sender as DependencyObject ?? button, e);
        button.DragOver += DutyCell_DragOver;
        button.Drop += (_, e) => DutyCell_Drop(e, date);
        var menu = new ContextMenu();
        var markItems = new List<(MenuItem Item, string Mark)>();
        foreach (var markName in _store.MarkStyles.Keys.OrderBy(x => x))
        {
            var item = new MenuItem { Header = "Marcar " + markName };
            item.Click += (_, _) => SetOrChangeMarkedPeriod(person, date, markName);
            markItems.Add((item, markName));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var clearPeriod = new MenuItem { Header = "Desmarcar período completo" };
        clearPeriod.Click += (_, _) => ClearMarkedPeriod(person, date);
        menu.Items.Add(clearPeriod);
        var clearDay = new MenuItem { Header = "Limpar somente este dia" };
        clearDay.Click += (_, _) => SetMark(person, date, string.Empty);
        menu.Items.Add(clearDay);
        menu.Opened += (_, _) => UpdateMarkMenu(markItems, clearPeriod, clearDay, person, date);
        button.ContextMenu = menu;
        Grid.SetRow(button, row);
        Grid.SetColumn(button, day - 1);
        _dutyButtons[CellKey(person.Key, day)] = button;
        UpdateDutyCell(button, person, date);
        RosterHost.Children.Add(button);
    }

    private void UpdateDutyCell(Button button, DutyRosterPerson person, DateTime date)
    {
        var dateKey = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
        var markKey = DutyRosterService.MarkKey(person.Key, date.Year, date.Month, date.Day);
        var assigned = CurrentMonth.Assignments.TryGetValue(dateKey, out var who) &&
                       who.Equals(person.Key, StringComparison.OrdinalIgnoreCase);
        var mark = CurrentMonth.Marks.GetValueOrDefault(markKey) ?? string.Empty;
        var blockingMark = DutyRosterService.IsBlockingMark(mark);
        var red = DutyRosterService.IsRedDay(CurrentMonth, date, _redDayStore);
        var style = !string.IsNullOrWhiteSpace(mark) && _store.MarkStyles.TryGetValue(mark, out var markStyle)
            ? markStyle
            : null;

        // A marcacao aparece somente pela cor. O numero da folga da categoria continua
        // visivel para permitir a conferencia da fila, sem letras cobrindo a contagem.
        button.Content = BuildRunningDutyLabel(person, date, red);
        button.FontSize = assigned ? 11.5 : 9;
        if (assigned)
        {
            button.Background = TryBrush("#020617");
            button.Foreground = Brushes.White;
            button.BorderBrush = TryBrush("#020617");
        }
        else
        {
            button.Background = TryBrush(style?.Background ?? (red ? "#FFF7F7" : "#FFFFFF"));
            button.Foreground = TryBrush(blockingMark
                ? style?.Foreground ?? "#475569"
                : red ? "#B91C1C" : "#475569");
            button.BorderBrush = TryBrush("#E2E8F0");
        }
        button.Opacity = assigned || string.IsNullOrWhiteSpace(mark) || blockingMark ? 1.0 : 0.72;
        button.ToolTip = assigned
            ? $"Escalado: {person.Display}\n{date:dd/MM/yyyy}\n{BuildRunningDutyTooltip(person, date, red)}" + (string.IsNullOrWhiteSpace(mark) ? string.Empty : $"\nMarcação: {mark}")
            : !string.IsNullOrWhiteSpace(mark)
                ? $"{mark} — {person.Display}\nImpede escala automatica. O numero exibido continua sendo a folga da categoria."
                : $"{BuildRunningDutyTooltip(person, date, red)}\nClique ou arraste um nome para escalar em {date:dd/MM/yyyy}.";
    }

    private void RefreshRosterVisuals()
    {
        var days = DateTime.DaysInMonth(_year, _month);
        foreach (var person in _selected)
        for (var day = 1; day <= days; day++)
        {
            if (_dutyButtons.TryGetValue(CellKey(person.Key, day), out var button))
                UpdateDutyCell(button, person, new DateTime(_year, _month, day));
        }

        RefreshCounters();
        SelectionSummaryText.Text = $"{_selected.Count:N0} pessoa(s) concorrendo • arraste nomes para os dias da escala";
        StatusText.Text = $"{CurrentMonth.Assignments.Count:N0} serviço(s) lançado(s). Alterações ainda precisam ser salvas.";
    }

    private string BuildRunningDutyLabel(DutyRosterPerson person, DateTime date, bool red)
    {
        var count = CountCategoryDaysSinceLastDuty(person.Key, date, red);
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private string BuildRunningDutyTooltip(DutyRosterPerson person, DateTime date, bool red)
    {
        var normal = CountCategoryDaysSinceLastDuty(person.Key, date, wantRed: false);
        var redCount = CountCategoryDaysSinceLastDuty(person.Key, date, wantRed: true);
        var current = red ? $"vermelha {redCount}" : $"normal {normal}";
        return $"Contagem {current}. Normal: {normal} · Vermelha: {redCount}. Ao escalar, a contagem da categoria zera em 0.";
    }

    private int CountCategoryDaysSinceLastDuty(string personKey, DateTime endDate, bool wantRed)
    {
        var start = DutyRosterService.CounterStart(endDate.Year, endDate.Month, _store.CounterMonths);
        var count = 0;
        for (var date = start.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            var red = IsRedDutyAcrossStore(date);
            if (red != wantRed) continue;
            var monthKey = DutyRosterService.MonthKey(date.Year, date.Month);
            var dayKey = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
            var assigned = _store.Months.TryGetValue(monthKey, out var month)
                           && month.Assignments.TryGetValue(dayKey, out var who)
                           && who.Equals(personKey, StringComparison.OrdinalIgnoreCase);
            count = assigned ? 0 : count + 1;
        }
        return count;
    }

    private void ToggleAssignment(DutyRosterPerson person, DateTime date)
    {
        var key = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
        var markKey = DutyRosterService.MarkKey(person.Key, date.Year, date.Month, date.Day);
        if (CurrentMonth.Marks.TryGetValue(markKey, out var mark) && DutyRosterService.IsBlockingMark(mark))
        {
            SigfurDialog.Show(this, "Essa pessoa possui impedimento nesse dia. Remova o impedimento antes de escalar.",
                "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CurrentMonth.Assignments.TryGetValue(key, out var current) &&
            current.Equals(person.Key, StringComparison.OrdinalIgnoreCase))
        {
            PushUndo();
            CurrentMonth.Assignments.Remove(key);
        }
        else
        {
            if (!CanAssign(person, date, null)) return;
            PushUndo();
            CurrentMonth.Assignments[key] = person.Key;
        }
        RefreshRosterVisuals();
    }

    private void SetMark(DutyRosterPerson person, DateTime date, string mark)
    {
        PushUndo();
        var key = DutyRosterService.MarkKey(person.Key, date.Year, date.Month, date.Day);
        if (string.IsNullOrWhiteSpace(mark)) CurrentMonth.Marks.Remove(key);
        else
        {
            CurrentMonth.Marks[key] = mark;
            if (DutyRosterService.IsBlockingMark(mark))
            {
                var dayKey = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
                if (CurrentMonth.Assignments.TryGetValue(dayKey, out var assigned) &&
                    assigned.Equals(person.Key, StringComparison.OrdinalIgnoreCase))
                    CurrentMonth.Assignments.Remove(dayKey);
            }
        }
        RefreshRosterVisuals();
    }

    private bool CanAssign(DutyRosterPerson person, DateTime date, DateTime? sourceDate)
    {
        var markKey = DutyRosterService.MarkKey(person.Key, date.Year, date.Month, date.Day);
        if (CurrentMonth.Marks.TryGetValue(markKey, out var mark) && DutyRosterService.IsBlockingMark(mark))
        {
            SigfurDialog.Show(this, "Essa pessoa possui impedimento nesse dia. Remova o impedimento antes de escalar.",
                "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var previous = FindLastDutyExcept(person.Key, date.AddDays(-1), sourceDate);
        if (previous is null || (date.Date - previous.Value.Date).Days >= 3) return true;
        var result = SigfurDialog.Show(this,
            $"O último serviço de {person.Display} foi em {previous.Value:dd/MM/yyyy}. Isso não completa 48 horas de descanso. Deseja lançar mesmo assim?",
            "Descanso mínimo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private DateTime? FindLastDutyExcept(string personKey, DateTime until, DateTime? except)
    {
        DateTime? last = null;
        foreach (var month in _store.Months.Values)
        foreach (var pair in month.Assignments)
        {
            if (!pair.Value.Equals(personKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParse(pair.Key, out var dt) || dt > until) continue;
            if (except is not null && dt.Date == except.Value.Date) continue;
            if (last is null || dt > last) last = dt;
        }
        return last;
    }

    private void AssignFromDrop(DutyRosterPerson person, DateTime targetDate, DateTime? sourceDate)
    {
        if (sourceDate is not null && sourceDate.Value.Date == targetDate.Date)
        {
            var currentKey = DutyRosterService.DayKey(targetDate.Year, targetDate.Month, targetDate.Day);
            if (CurrentMonth.Assignments.TryGetValue(currentKey, out var current) &&
                current.Equals(person.Key, StringComparison.OrdinalIgnoreCase)) return;
        }

        if (!CanAssign(person, targetDate, sourceDate)) return;
        PushUndo();
        if (sourceDate is not null)
        {
            var sourceKey = DutyRosterService.DayKey(sourceDate.Value.Year, sourceDate.Value.Month, sourceDate.Value.Day);
            if (CurrentMonth.Assignments.TryGetValue(sourceKey, out var source) &&
                source.Equals(person.Key, StringComparison.OrdinalIgnoreCase))
                CurrentMonth.Assignments.Remove(sourceKey);
        }
        var targetKey = DutyRosterService.DayKey(targetDate.Year, targetDate.Month, targetDate.Day);
        CurrentMonth.Assignments[targetKey] = person.Key;
        RefreshRosterVisuals();
    }

    private void MarkPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione quem concorre antes de marcar um período.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new DutyRosterPeriodMarkWindow(_selected, _store.MarkStyles.Keys.OrderBy(x => x), _year, _month)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPerson is null || string.IsNullOrWhiteSpace(dialog.SelectedMark)) return;
        if (RejectOutOfCurrentMonthPeriod(dialog, _year, _month))
        {
            SigfurDialog.Show(this, "O período precisa ficar dentro do mês aberto na tela.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PushUndo();
        var person = dialog.SelectedPerson;
        var blocking = DutyRosterService.IsBlockingMark(dialog.SelectedMark);
        for (var date = dialog.StartDate.Date; date <= dialog.EndDate.Date; date = date.AddDays(1))
        {
            SetMarkAcrossMonths(person, date, dialog.SelectedMark, blocking);
        }

        if (dialog.PresentationDate is { } presentationDate)
            SetMarkAcrossMonths(person, presentationDate, VacationPresentationMark, blocking: true);

        RefreshRosterVisuals();
    }

    private static bool RejectOutOfCurrentMonthPeriod(DutyRosterPeriodMarkWindow dialog, int year, int month)
        => false;

    private DutyRosterMonth MonthForDate(DateTime date)
    {
        var monthKey = DutyRosterService.MonthKey(date.Year, date.Month);
        if (!_store.Months.TryGetValue(monthKey, out var month))
            _store.Months[monthKey] = month = new DutyRosterMonth();
        return month;
    }

    private void SetMarkAcrossMonths(DutyRosterPerson person, DateTime date, string mark, bool blocking)
    {
        var month = MonthForDate(date);
        var markKey = DutyRosterService.MarkKey(person.Key, date.Year, date.Month, date.Day);
        month.Marks[markKey] = mark;

        if (!blocking) return;
        var dayKey = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
        if (month.Assignments.TryGetValue(dayKey, out var assigned) &&
            assigned.Equals(person.Key, StringComparison.OrdinalIgnoreCase))
            month.Assignments.Remove(dayKey);
    }

    private void UpdateMarkMenu(
        IReadOnlyList<(MenuItem Item, string Mark)> markItems,
        MenuItem clearPeriod,
        MenuItem clearDay,
        DutyRosterPerson person,
        DateTime date)
    {
        var period = FindMarkedPeriod(person.Key, date);
        foreach (var (item, mark) in markItems)
        {
            item.Header = period is null ? $"Marcar {mark}" : $"Alterar período para {mark}";
            item.IsEnabled = period is null || !period.Mark.Equals(mark, StringComparison.OrdinalIgnoreCase);
        }
        clearPeriod.IsEnabled = period is not null;
        clearDay.IsEnabled = !string.IsNullOrWhiteSpace(GetMark(person.Key, date));
        clearPeriod.Header = period is null
            ? "Desmarcar período completo"
            : $"Desmarcar período {period.Mark} ({FormatPeriod(period.Start, period.End)})";
    }

    private void SetOrChangeMarkedPeriod(DutyRosterPerson person, DateTime clickedDate, string newMark)
    {
        var period = FindMarkedPeriod(person.Key, clickedDate);
        if (period is null)
        {
            SetMark(person, clickedDate, newMark);
            return;
        }
        if (period.Mark.Equals(newMark, StringComparison.OrdinalIgnoreCase)) return;

        PushUndo();
        if (period.PresentationDate is { } oldPresentationDate)
            RemoveMark(person.Key, oldPresentationDate);

        var blocking = DutyRosterService.IsBlockingMark(newMark);
        for (var date = period.Start; date <= period.End; date = date.AddDays(1))
            SetMarkAcrossMonths(person, date, newMark, blocking);

        if (DutyRosterPeriodMarkWindow.IsVacationMark(newMark) && period.End < DateTime.MaxValue.Date)
            SetMarkAcrossMonths(person, period.End.AddDays(1), VacationPresentationMark, blocking: true);

        RefreshRosterVisuals();
        StatusText.Text = $"Período de {person.Display} ({FormatPeriod(period.Start, period.End)}) alterado de {period.Mark} para {newMark}. Alteração ainda precisa ser salva.";
    }

    private void ClearMarkedPeriod(DutyRosterPerson person, DateTime clickedDate)
    {
        var period = FindMarkedPeriod(person.Key, clickedDate);
        if (period is null) return;

        PushUndo();
        for (var date = period.Start; date <= period.End; date = date.AddDays(1))
            RemoveMark(person.Key, date);
        if (period.PresentationDate is { } presentationDate)
            RemoveMark(person.Key, presentationDate);

        RefreshRosterVisuals();
        StatusText.Text = $"Período {period.Mark} de {person.Display} ({FormatPeriod(period.Start, period.End)}) desmarcado. Alteração ainda precisa ser salva.";
    }

    private MarkedPeriod? FindMarkedPeriod(string personKey, DateTime clickedDate)
    {
        var clickedMark = GetMark(personKey, clickedDate);
        if (string.IsNullOrWhiteSpace(clickedMark)) return null;

        var anchor = clickedDate.Date;
        var mark = clickedMark;
        DateTime? presentationDate = null;

        if (mark.Equals(VacationPresentationMark, StringComparison.OrdinalIgnoreCase))
        {
            var previousDate = anchor.AddDays(-1);
            var previousMark = GetMark(personKey, previousDate);
            if (!string.IsNullOrWhiteSpace(previousMark) && DutyRosterPeriodMarkWindow.IsVacationMark(previousMark))
            {
                anchor = previousDate;
                mark = previousMark;
                presentationDate = clickedDate.Date;
            }
        }

        var start = anchor;
        while (start > DateTime.MinValue.Date && MarkEquals(personKey, start.AddDays(-1), mark))
            start = start.AddDays(-1);

        var end = anchor;
        while (end < DateTime.MaxValue.Date && MarkEquals(personKey, end.AddDays(1), mark))
            end = end.AddDays(1);

        if (DutyRosterPeriodMarkWindow.IsVacationMark(mark) && end < DateTime.MaxValue.Date)
        {
            var possiblePresentation = end.AddDays(1);
            if (MarkEquals(personKey, possiblePresentation, VacationPresentationMark))
                presentationDate = possiblePresentation;
        }

        return new MarkedPeriod(mark, start, end, presentationDate);
    }

    private string? GetMark(string personKey, DateTime date)
    {
        var monthKey = DutyRosterService.MonthKey(date.Year, date.Month);
        if (!_store.Months.TryGetValue(monthKey, out var month)) return null;
        var markKey = DutyRosterService.MarkKey(personKey, date.Year, date.Month, date.Day);
        return month.Marks.GetValueOrDefault(markKey);
    }

    private bool MarkEquals(string personKey, DateTime date, string expected)
        => GetMark(personKey, date)?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;

    private void RemoveMark(string personKey, DateTime date)
    {
        var monthKey = DutyRosterService.MonthKey(date.Year, date.Month);
        if (!_store.Months.TryGetValue(monthKey, out var month)) return;
        month.Marks.Remove(DutyRosterService.MarkKey(personKey, date.Year, date.Month, date.Day));
    }

    private static string FormatPeriod(DateTime start, DateTime end)
        => start == end ? start.ToString("dd/MM/yyyy") : $"{start:dd/MM/yyyy} a {end:dd/MM/yyyy}";

    private sealed record MarkedPeriod(string Mark, DateTime Start, DateTime End, DateTime? PresentationDate);

    private void PeopleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        _pendingDragSourceDate = null;
        _pendingDragMovesRosterRow = true;
        _pendingDragPerson = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as DutyRosterPerson
                             ?? SelectedPeopleList.SelectedItem as DutyRosterPerson;
    }

    private void PeopleList_MouseMove(object sender, MouseEventArgs e)
        => StartPreparedDrag(SelectedPeopleList, e);

    private void PrepareDrag(DutyRosterPerson person, DateTime? sourceDate, MouseButtonEventArgs e, bool moveRosterRow = false)
    {
        _dragStartPoint = e.GetPosition(this);
        _pendingDragPerson = person;
        _pendingDragSourceDate = sourceDate;
        _pendingDragMovesRosterRow = moveRosterRow;
    }

    private void StartPreparedDrag(DependencyObject source, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pendingDragPerson is null) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject();
        data.SetData(DragPersonKeyFormat, _pendingDragPerson.Key);
        if (_pendingDragMovesRosterRow)
            data.SetData(DragRosterRowFormat, true);
        if (_pendingDragSourceDate is not null)
            data.SetData(DragSourceDateFormat, _pendingDragSourceDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
        _pendingDragPerson = null;
        _pendingDragSourceDate = null;
        _pendingDragMovesRosterRow = false;
    }

    private void SelectedPeopleList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetRosterRowReorder(e.Data, out var dragged) && dragged is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SelectedPeopleList_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetRosterRowReorder(e.Data, out var dragged) || dragged is null) return;

        var targetItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is DutyRosterPerson target)
        {
            var after = e.GetPosition(targetItem).Y >= targetItem.ActualHeight / 2;
            ReorderSelectedPerson(dragged, target, after);
        }
        else
        {
            MoveSelectedPersonToIndex(dragged, _selected.Count);
        }

        e.Handled = true;
    }

    private static void DutyCell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragPersonKeyFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DutyCell_Drop(DragEventArgs e, DateTime targetDate)
    {
        if (!e.Data.GetDataPresent(DragPersonKeyFormat)) return;
        var key = Convert.ToString(e.Data.GetData(DragPersonKeyFormat), CultureInfo.InvariantCulture) ?? string.Empty;
        var person = _selected.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                     ?? _all.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (person is null) return;
        DateTime? sourceDate = null;
        if (e.Data.GetDataPresent(DragSourceDateFormat) &&
            DateTime.TryParseExact(Convert.ToString(e.Data.GetData(DragSourceDateFormat), CultureInfo.InvariantCulture),
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            sourceDate = parsed;
        AssignFromDrop(person, targetDate, sourceDate);
        e.Handled = true;
    }

    private void RowHeader_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border targetBorder ||
            !TryGetRosterRowReorder(e.Data, out var dragged) ||
            dragged is null ||
            targetBorder.DataContext is not DutyRosterPerson target ||
            dragged.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase))
        {
            if (sender is Border border) ResetNameRowVisual(border);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var after = e.GetPosition(targetBorder).Y >= targetBorder.ActualHeight / 2;
        targetBorder.BorderBrush = TryBrush("#020617");
        targetBorder.BorderThickness = after ? new Thickness(0, 0, 1, 3) : new Thickness(0, 3, 1, 1);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void RowHeader_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) ResetNameRowVisual(border);
    }

    private void RowHeader_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border targetBorder ||
            !TryGetRosterRowReorder(e.Data, out var dragged) ||
            dragged is null ||
            targetBorder.DataContext is not DutyRosterPerson target)
            return;

        var after = e.GetPosition(targetBorder).Y >= targetBorder.ActualHeight / 2;
        ResetNameRowVisual(targetBorder);
        ReorderSelectedPerson(dragged, target, after);
        e.Handled = true;
    }

    private bool TryGetRosterRowReorder(IDataObject data, out DutyRosterPerson? person)
    {
        person = null;
        if (!data.GetDataPresent(DragPersonKeyFormat) || !data.GetDataPresent(DragRosterRowFormat)) return false;

        var key = Convert.ToString(data.GetData(DragPersonKeyFormat), CultureInfo.InvariantCulture) ?? string.Empty;
        person = _selected.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return person is not null;
    }

    private void ReorderSelectedPerson(DutyRosterPerson dragged, DutyRosterPerson target, bool after)
    {
        if (dragged.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase)) return;

        var targetIndex = _selected.IndexOf(target);
        if (targetIndex < 0) return;

        MoveSelectedPersonToIndex(dragged, targetIndex + (after ? 1 : 0));
    }

    private void MoveSelectedPersonToIndex(DutyRosterPerson person, int insertIndex)
    {
        var sourceIndex = _selected.IndexOf(person);
        if (sourceIndex < 0) return;

        insertIndex = Math.Clamp(insertIndex, 0, _selected.Count);
        if (sourceIndex < insertIndex) insertIndex--;
        if (sourceIndex == insertIndex) return;

        _selected.Move(sourceIndex, insertIndex);
        _store.SelectionInitialized = true;
        SelectedPeopleList.SelectedItem = person;
        RenderMonth();
        StatusText.Text = "Ordem da escala alterada. Salve para manter na proxima abertura.";
    }

    private void ResetNameRowVisual(Border border)
    {
        var row = border.Tag is int value ? value : 0;
        border.BorderBrush = TryBrush("#D9E2EC");
        border.BorderThickness = new Thickness(0, 0, 1, 1);
        border.Background = TryBrush(row % 2 == 0 ? "#F8FAFC" : "#FFFFFF");
    }

    private static string CellKey(string personKey, int day) => $"{personKey}|{day:00}";

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void RefreshCounters()
    {
        var nextOpen = DutyRosterService.GetNextOpenDate(_year, _month, CurrentMonth);
        var nextOpenIsRed = IsRedDutyAcrossStore(nextOpen);
        var historyStart = DutyRosterService.CounterStart(nextOpen.Year, nextOpen.Month, _store.CounterMonths);
        var until = nextOpen.AddDays(-1);
        var counters = new List<DutyRosterCounter>();

        foreach (var person in _selected)
        {
            var duties = DutyRosterService.GetDuties(person.Key, _store.Months, historyStart, until);
            var last = DutyRosterService.FindLastDuty(person.Key, until, _store.Months);
            var redCount = duties.Count(IsRedDutyAcrossStore);
            var daysOff = last is null
                ? Math.Max(0, (nextOpen.Date - historyStart.Date).Days)
                : Math.Max(0, (nextOpen.Date - last.Value.Date).Days);
            counters.Add(new DutyRosterCounter
            {
                PersonKey = person.Key,
                Person = person.Display,
                Total = duties.Count,
                RedDays = redCount,
                LastDuty = last,
                DaysOff = daysOff,
                CategoryDaysOff = CountCategoryDaysSinceLastDuty(person.Key, nextOpen, nextOpenIsRed),
                IsEligible = last is null || daysOff >= 3
            });
        }

        var ordered = counters
            .OrderBy(x => x.IsEligible ? 0 : 1)
            .ThenByDescending(x => x.CategoryDaysOff)
            .ThenByDescending(x => x.DaysOff)
            .ThenBy(x => x.LastDuty ?? DateTime.MinValue)
            .ThenBy(x => x.Person, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Priority = i + 1;
        var next = ordered.FirstOrDefault(x => x.IsEligible) ?? ordered.FirstOrDefault();
        if (next is not null) next.IsNextCandidate = true;

        CounterGrid.ItemsSource = ordered;
        NextCandidateText.Text = next?.Person ?? "Nenhuma pessoa selecionada";
        NextCandidateDetailText.Text = next is null
            ? "Adicione pessoas ao efetivo para calcular a prioridade."
            : $"Folga da categoria: {next.CategoryDaysOff} • folga desde qualquer serviço: {next.DaysOff} dia(s) • último: {next.LastDutyText}";
        MonthSummaryText.Text =
            $"{CurrentMonth.Assignments.Count:N0} de {DateTime.DaysInMonth(_year, _month)} dia(s) preenchido(s). " +
            $"Prioridade calculada com {_store.CounterMonths} mês(es) de histórico até {until:dd/MM/yyyy}. " +
            "Na grade principal, a contagem de cada categoria cresce por dia e zera em 0 quando o militar é escalado.";
    }

    private bool IsRedDutyAcrossStore(DateTime date)
    {
        if (_redDayStore is not null) return RedDayService.IsRedDay(date, _redDayStore);
        return _store.Months.TryGetValue(DutyRosterService.MonthKey(date.Year, date.Month), out var month)
            ? DutyRosterService.IsRedDay(month, date, _redDayStore)
            : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    private void RenderLegend()
    {
        LegendHost.Children.Clear();
        foreach (var style in _store.MarkStyles.Values.OrderBy(x => x.Name))
        {
            LegendHost.Children.Add(new Border
            {
                Background = TryBrush(style.Background),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = "Impede escala automática. A cor mostra o motivo e o número da folga permanece visível.",
                Child = new TextBlock
                {
                    Text = style.Name,
                    Foreground = TryBrush(style.Foreground),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold
                }
            });
        }
    }

    private void EnsureMarkStyles()
    {
        var defaults = DutyMarkStyle.Defaults();
        foreach (var pair in defaults)
            if (!_store.MarkStyles.ContainsKey(pair.Key))
                _store.MarkStyles[pair.Key] = pair.Value;

        if (_store.MarkStyles.TryGetValue("DISPENSA", out var dispensa))
        {
            dispensa.Background = "#F3E8FF";
            dispensa.Foreground = "#6B21A8";
        }
    }

    private void RefreshAvailable()
    {
        var query = MilitaryRankService.Normalize(PeopleSearchBox?.Text);
        var selectedKeys = _selected.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _available.Clear();
        foreach (var person in _all
                     .Where(x => !selectedKeys.Contains(x.Key))
                     .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
                     .ThenBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase))
        {
            var hay = MilitaryRankService.Normalize(person.Display);
            if (string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(term => hay.Contains(term, StringComparison.OrdinalIgnoreCase)))
                _available.Add(person);
        }
    }

    private void ChooseCalendarFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0)
        {
            SigfurDialog.Show(this, "Adicione pelo menos uma pessoa ao efetivo da escala antes de escolher o filtro do calendario.",
                "Calendario operacional", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu = new ContextMenu();
        var currentKey = _store.OperationalCalendarPersonKey?.Trim() ?? string.Empty;
        var allItem = new MenuItem
        {
            Header = "Mostrar todos",
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(currentKey)
        };
        allItem.Click += async (_, _) => await SetCalendarFilterAsync(null);
        menu.Items.Add(allItem);
        menu.Items.Add(new Separator());

        foreach (var person in _selected)
        {
            var item = new MenuItem
            {
                Header = person.Display,
                IsCheckable = true,
                IsChecked = person.Key.Equals(currentKey, StringComparison.OrdinalIgnoreCase),
                Tag = person
            };
            item.Click += async (_, _) =>
            {
                if (item.Tag is DutyRosterPerson selectedPerson)
                    await SetCalendarFilterAsync(selectedPerson);
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = _calendarFilterButton ?? sender as UIElement;
        menu.IsOpen = true;
    }

    private async Task SetCalendarFilterAsync(DutyRosterPerson? person)
    {
        _store.OperationalCalendarPersonKey = person?.Key ?? string.Empty;
        UpdateCalendarFilterButton();
        await SaveAsync(false);
        StatusText.Text = person is null
            ? "Calendario operacional mostrando todos os servicos da escala."
            : $"Calendario operacional mostrando somente: {person.Display}. Este filtro nao altera a escala automatica.";
    }

    private void NormalizeOperationalCalendarFilter()
    {
        var key = _store.OperationalCalendarPersonKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            _store.OperationalCalendarPersonKey = string.Empty;
            return;
        }

        if (_selected.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return;
        _store.OperationalCalendarPersonKey = string.Empty;
    }

    private void UpdateCalendarFilterButton()
    {
        if (_calendarFilterButton is null) return;
        var key = _store.OperationalCalendarPersonKey?.Trim() ?? string.Empty;
        var person = string.IsNullOrWhiteSpace(key)
            ? null
            : _selected.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        _calendarFilterButton.Content = person is null
            ? "Filtro calendario: todos"
            : "Filtro calendario: " + CalendarFilterShortName(person);
        _calendarFilterButton.ToolTip = person is null
            ? "Mostra todos os servicos no calendario operacional. Nao altera a prioridade da escala."
            : $"Mostra somente {person.Display} no calendario operacional. Nao altera a prioridade da escala.";
    }

    private static string CalendarFilterShortName(DutyRosterPerson person)
    {
        var rank = person.ShortRank;
        var name = IsBlankNamePart(person.WarName)
            ? person.Name
            : person.WarName;
        var label = string.Join(" ", new[] { rank, name }.Where(x => !IsBlankNamePart(x))).Trim();
        return string.IsNullOrWhiteSpace(label) ? person.Display : label;
    }

    private static bool IsBlankNamePart(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) || text == "-" || text == "\u2014" || text == "\u00E2\u20AC\u201D";
    }

    private void ReplaceSelection(IEnumerable<DutyRosterPerson> people)
    {
        var ordered = people
            .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _selected.Clear();
        foreach (var person in ordered) _selected.Add(person);
        _store.SelectionInitialized = true;
        RefreshAvailable();
        RenderMonth();
    }

    private void SelectThirdSergeants_Click(object sender, RoutedEventArgs e) =>
        ReplaceSelection(_all.Where(x => !x.IsExtra && MilitaryRankService.GetOrder(x.Rank) == 14));

    private void SelectAllSergeants_Click(object sender, RoutedEventArgs e) =>
        ReplaceSelection(_all.Where(x => !x.IsExtra && MilitaryRankService.GetOrder(x.Rank) is 12 or 13 or 14));

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count > 0 && SigfurDialog.Show(this,
                "Retirar todas as pessoas do efetivo que concorre? Os lançamentos já existentes do mês não serão apagados.",
                "Efetivo da escala", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ReplaceSelection([]);
    }

    private void AvailablePeopleList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddPerson_Click(sender, new RoutedEventArgs());

    private void AddPerson_Click(object sender, RoutedEventArgs e)
    {
        var people = AvailablePeopleList.SelectedItems.Cast<DutyRosterPerson>().ToList();
        if (people.Count == 0 && AvailablePeopleList.SelectedItem is DutyRosterPerson one) people.Add(one);
        foreach (var person in people)
            if (!_selected.Any(x => x.Key.Equals(person.Key, StringComparison.OrdinalIgnoreCase))) _selected.Add(person);
        _store.SelectionInitialized = true;
        RefreshAvailable();
        RenderMonth();
    }

    private void RemovePerson_Click(object sender, RoutedEventArgs e)
    {
        var people = SelectedPeopleList.SelectedItems.Cast<DutyRosterPerson>().ToList();
        if (people.Count == 0 && SelectedPeopleList.SelectedItem is DutyRosterPerson one) people.Add(one);
        if (people.Count == 0) return;
        var hasAssignments = people.Any(person => CurrentMonth.Assignments.Values.Any(x =>
            x.Equals(person.Key, StringComparison.OrdinalIgnoreCase)));
        if (hasAssignments && SigfurDialog.Show(this,
                "Uma ou mais pessoas possuem serviço lançado no mês. Retirar do efetivo sem apagar esses lançamentos?",
                "Escala", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var person in people) _selected.Remove(person);
        _store.SelectionInitialized = true;
        RefreshAvailable();
        RenderMonth();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (SelectedPeopleList.SelectedItem is not DutyRosterPerson person) return;
        var index = _selected.IndexOf(person);
        var target = index + delta;
        if (target < 0 || target >= _selected.Count) return;
        _selected.Move(index, target);
        SelectedPeopleList.SelectedItem = person;
        RenderMonth();
    }

    private void AddExtra_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Pessoa externa", "Informe o nome que aparecerá na escala.") { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
        var value = prompt.Value.Trim();
        var key = "E:" + value.ToUpperInvariant();
        if (_all.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return;
        var person = new DutyRosterPerson { Key = key, Name = value, IsExtra = true };
        _store.ExtraPeople.Add(value);
        _all.Add(person);
        _selected.Add(person);
        _store.SelectionInitialized = true;
        RefreshAvailable();
        RenderMonth();
    }

    private void PeopleSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAvailable();

    private void CounterMonthsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CounterMonthsBox.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var months)) return;
        _store.CounterMonths = months;
        RefreshRosterVisuals();
    }

    private async void AutoFill_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione quem concorre antes de preencher.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var refillAll = false;
        if (CurrentMonth.Assignments.Count > 0)
        {
            var choice = SigfurDialog.Show(this,
                $"Este mes ja possui {CurrentMonth.Assignments.Count} servico(s).\n\n" +
                "SIM: refazer toda a escala com a regra correta, preservando impedimentos e dias vermelhos.\n" +
                "NAO: manter os servicos atuais e preencher somente os dias vazios.\n" +
                "CANCELAR: nao alterar nada.",
                "Preenchimento automatico", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            refillAll = choice == MessageBoxResult.Yes;
        }

        PushUndo();
        if (refillAll) CurrentMonth.Assignments.Clear();
        var reference = new DateTime(_year, _month, 1);
        var historyStart = DutyRosterService.CounterStart(reference.Year, reference.Month, _store.CounterMonths);
        CurrentMonth.Assignments = DutyRosterService.AutoFill(
            _year, _month, _selected, CurrentMonth, _store.Months, 1, historyStart, _redDayStore);
        RefreshRosterVisuals();
        await SaveAsync(false);
        var pendingDays = DateTime.DaysInMonth(_year, _month) - CurrentMonth.Assignments.Count;
        StatusText.Text = pendingDays == 0
            ? "Escala automatica concluida e salva. Em cada dia foi escolhida a maior folga da categoria, respeitando as 48 h e os impedimentos."
            : $"Escala salva com {pendingDays} dia(s) vazio(s): nenhum militar estava liberado pela regra de 48 h ou pelos impedimentos nessas datas.";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var restored = JsonSerializer.Deserialize<Dictionary<string, DutyRosterMonth>>(_undo.Pop());
        if (restored is not null) _store.Months = new Dictionary<string, DutyRosterMonth>(restored, StringComparer.OrdinalIgnoreCase);
        RefreshRosterVisuals();
    }

    private void ClearMonth_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Limpar todos os serviços, feriados e impedimentos deste mês?", "Escala",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        PushUndo();
        _store.Months[DutyRosterService.MonthKey(_year, _month)] = new DutyRosterMonth();
        RefreshRosterVisuals();
    }

    private void ToggleEffectivePanel_Click(object sender, RoutedEventArgs e)
    {
        _effectivePanelCollapsed = !_effectivePanelCollapsed;
        EffectivePanel.Visibility = _effectivePanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        EffectiveColumn.Width = _effectivePanelCollapsed ? new GridLength(0) : new GridLength(330);
        EffectiveSpacerColumn.Width = _effectivePanelCollapsed ? new GridLength(0) : new GridLength(12);
        EffectiveToggleButton.Content = _effectivePanelCollapsed ? "Mostrar efetivo" : "Ocultar efetivo";
        EffectiveToggleButton.ToolTip = _effectivePanelCollapsed
            ? "Mostra novamente o efetivo que concorre."
            : "Recolhe o painel de efetivo para abrir mais espaço para a escala.";
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        var date = new DateTime(_year, _month, 1).AddMonths(-1);
        _year = date.Year;
        _month = date.Month;
        RenderMonth();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        var date = new DateTime(_year, _month, 1).AddMonths(1);
        _year = date.Year;
        _month = date.Month;
        RenderMonth();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _year = DateTime.Today.Year;
        _month = DateTime.Today.Month;
        RenderMonth();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        SigfurDialog.Show(this,
            "COMO A PRIORIDADE É CALCULADA\n\n" +
            "1. O quadro usa o período escolhido em Histórico.\n" +
            "2. Em cada dia, entra primeiro quem possui a maior folga acumulada naquela categoria.\n" +
            "3. Dias normais e dias vermelhos possuem contagens independentes. Um serviço vermelho zera apenas a folga vermelha; um serviço normal zera apenas a folga normal.\n" +
            "4. A coluna Folga mostra quantos dias se passaram desde o último serviço até o próximo dia em aberto.\n" +
            "5. O indicador 48 h impede a escala automática antes de três datas corridas de diferença. O programa deixa o dia vazio se ninguem estiver liberado.\n" +
            "6. As marcacoes coloridas bloqueiam a escala automatica, mas mantem o numero da folga visivel na celula.\n" +
            "7. O botao Filtro calendario apenas filtra os nomes mostrados na tela principal; ele nao favorece militar, operador ou usuario marcado.\n\n" +
            "O efetivo é totalmente configurável: use os atalhos para 3º Sgt ou todos os Sgt e depois adicione ou remova qualquer pessoa.",
            "Escala do Sargento de Dia", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task SaveAsync(bool feedback)
    {
        NormalizeOperationalCalendarFilter();
        UpdateCalendarFilterButton();
        _store.SelectionInitialized = true;
        _store.SelectedMilitaryIds = _selected.Where(x => !x.IsExtra).Select(x => x.MilitaryId).Distinct().ToList();
        _store.ExtraPeople = _all.Where(x => x.IsExtra).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _store.Order = _selected.Where(x => !x.IsExtra).Select(x => x.MilitaryId).ToList();
        _store.OrderKeys = _selected.Select(x => x.Key).ToList();
        await _service.SaveAsync(_store);
        if (feedback) StatusText.Text = "Escala salva com segurança em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + ".";
    }

    private static Brush TryBrush(string value)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return Brushes.Transparent; }
    }
}

internal sealed class DutyRosterPeriodMarkWindow : Window
{
    private readonly ComboBox _personBox = new();
    private readonly ComboBox _markBox = new();
    private readonly DatePicker _startPicker = new();
    private readonly DatePicker _endPicker = new();
    private readonly ComboBox _durationBox = new();

    public DutyRosterPerson? SelectedPerson { get; private set; }
    public string SelectedMark { get; private set; } = string.Empty;
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public DateTime? PresentationDate { get; private set; }

    public DutyRosterPeriodMarkWindow(IEnumerable<DutyRosterPerson> people, IEnumerable<string> marks, int year, int month)
    {
        Title = "Marcar período na escala";
        Width = 520;
        Height = 382;
        MinWidth = 460;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var firstDay = new DateTime(year, month, 1);
        var lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        StartDate = firstDay;
        EndDate = lastDay;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddLabel(root, "Militar", 0);
        _personBox.ItemsSource = people.ToList();
        _personBox.ItemTemplate = CreatePersonTemplate();
        _personBox.SelectedIndex = 0;
        AddControl(root, _personBox, 0);

        AddLabel(root, "Tipo", 1);
        _markBox.ItemsSource = marks.ToList();
        _markBox.SelectedIndex = 0;
        AddControl(root, _markBox, 1);

        AddLabel(root, "Início", 2);
        _startPicker.SelectedDate = firstDay;
        AddControl(root, _startPicker, 2);

        AddLabel(root, "Fim", 3);
        _endPicker.SelectedDate = lastDay;
        AddControl(root, _endPicker, 3);

        AddLabel(root, "Dias de ferias", 4);
        foreach (var days in new[] { 10, 15, 30 })
            _durationBox.Items.Add(new ComboBoxItem { Content = $"{days} dias", Tag = days });
        _durationBox.SelectedIndex = 2;
        _durationBox.ToolTip = "Usado quando o tipo selecionado for ferias. A data final e a apresentacao sao calculadas automaticamente.";
        AddControl(root, _durationBox, 4);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "Férias, curso, baixado, missão e outros impedem a escala no período. Dispensa é apenas destaque visual: a contagem continua e o dia fica clareado."
        };
        Grid.SetRow(hint, 5);
        Grid.SetColumnSpan(hint, 2);
        root.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var apply = new Button { Content = "Aplicar", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(16, 6, 16, 6) };
        apply.Click += Apply_Click;
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 6);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        _markBox.SelectionChanged += (_, _) => UpdateVacationMode();
        _startPicker.SelectedDateChanged += (_, _) => UpdateVacationMode();
        _durationBox.SelectionChanged += (_, _) => UpdateVacationMode();
        UpdateVacationMode();
    }

    private void UpdateVacationMode()
    {
        var vacation = _markBox.SelectedItem is string mark && IsVacationMark(mark);
        _durationBox.IsEnabled = vacation;
        _endPicker.IsEnabled = !vacation;
        if (!vacation || _startPicker.SelectedDate is not { } start) return;

        var days = SelectedVacationDays();
        _endPicker.SelectedDate = start.Date.AddDays(days - 1);
        _endPicker.ToolTip = $"Ferias de {days} dias. Apresentacao em {start.Date.AddDays(days):dd/MM/yyyy}; servico somente no dia seguinte.";
    }

    private int SelectedVacationDays()
    {
        if (_durationBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            return days;
        return 30;
    }

    internal static bool IsVacationMark(string mark)
    {
        var normalized = RemoveDiacritics(mark).ToUpperInvariant();
        return normalized.Contains("FERIAS", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
               normalized.Contains("RIAS", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_personBox.SelectedItem is not DutyRosterPerson person)
        {
            SigfurDialog.Show(this, "Selecione o militar.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_markBox.SelectedItem is not string mark || string.IsNullOrWhiteSpace(mark))
        {
            SigfurDialog.Show(this, "Selecione o tipo de marcação.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_startPicker.SelectedDate is not { } start)
        {
            SigfurDialog.Show(this, "Informe início e fim do período.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DateTime end;
        if (IsVacationMark(mark))
        {
            var days = SelectedVacationDays();
            end = start.Date.AddDays(days - 1);
            PresentationDate = end.AddDays(1);
        }
        else
        {
            if (_endPicker.SelectedDate is not { } manualEnd)
            {
                SigfurDialog.Show(this, "Informe a data final.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            end = manualEnd.Date;
            PresentationDate = null;
            if (end < start.Date) (start, end) = (end, start);
        }
        SelectedPerson = person;
        SelectedMark = mark;
        StartDate = start.Date;
        EndDate = end.Date;
        DialogResult = true;
    }

    private static void AddLabel(Grid root, string text, int row)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 10) };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        root.Children.Add(label);
    }

    private static void AddControl(Grid root, Control control, int row)
    {
        control.MinHeight = 32;
        control.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        root.Children.Add(control);
    }

    private static DataTemplate CreatePersonTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var rank = new FrameworkElementFactory(typeof(TextBlock));
        rank.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DutyRosterPerson.ShortRank)));
        rank.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        rank.SetValue(TextBlock.ForegroundProperty, Brushes.DimGray);
        panel.AppendChild(rank);

        var name = new FrameworkElementFactory(typeof(HighlightedNameTextBlock));
        name.SetBinding(HighlightedNameTextBlock.FullNameProperty, new System.Windows.Data.Binding(nameof(DutyRosterPerson.Name)));
        name.SetBinding(HighlightedNameTextBlock.WarNameProperty, new System.Windows.Data.Binding(nameof(DutyRosterPerson.WarName)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(name);

        return new DataTemplate(typeof(DutyRosterPerson)) { VisualTree = panel };
    }
}
