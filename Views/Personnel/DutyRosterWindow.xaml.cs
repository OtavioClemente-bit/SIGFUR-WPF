using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly DutyRosterService _service = new(App.Paths, App.Json);
    private DutyRosterStore _store = new();
    private readonly ObservableCollection<DutyRosterPerson> _selected = [];
    private readonly ObservableCollection<DutyRosterPerson> _available = [];
    private List<DutyRosterPerson> _all = [];
    private readonly Stack<string> _undo = new();
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;
    private bool _loading;

    public DutyRosterWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        SelectedPeopleList.ItemsSource = _selected;
        AvailablePeopleList.ItemsSource = _available;
        Loaded += async (_, _) => await LoadAsync();
        Closing += async (_, _) => await SaveAsync(false);
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
        _store.Months ??= new(StringComparer.OrdinalIgnoreCase);
        _store.SelectedMilitaryIds ??= [];
        _store.ExtraPeople ??= [];
        _store.Order ??= [];
        if (_store.CounterMonths is not (3 or 6 or 12 or 24 or 36)) _store.CounterMonths = 12;

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
            .OrderBy(x =>
            {
                var index = _store.Order.IndexOf(x.MilitaryId);
                return index < 0 ? int.MaxValue : index;
            })
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
        var json = JsonSerializer.Serialize(CurrentMonth);
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
        RosterHost.Children.Clear();
        RosterHost.RowDefinitions.Clear();
        RosterHost.ColumnDefinitions.Clear();
        RosterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        var days = DateTime.DaysInMonth(_year, _month);
        for (var day = 1; day <= days; day++)
            RosterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        RosterHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        for (var i = 0; i < _selected.Count; i++)
            RosterHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

        AddHeaderCell("MILITAR", 0, false, null);
        for (var day = 1; day <= days; day++)
        {
            var date = new DateTime(_year, _month, day);
            AddHeaderCell($"{day:00}\n{date:ddd}".ToUpperInvariant(), day, DutyRosterService.IsRedDay(CurrentMonth, date), date);
        }

        for (var row = 0; row < _selected.Count; row++)
        {
            var person = _selected[row];
            var nameBorder = new Border
            {
                BorderBrush = TryBrush("#D9E2EC"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = TryBrush(row % 2 == 0 ? "#F8FAFC" : "#FFFFFF"),
                Padding = new Thickness(8, 4, 8, 4)
            };
            nameBorder.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = person.ShortRank, FontSize = 10, Foreground = TryBrush("#64748B") },
                    new HighlightedNameTextBlock
                    {
                        FullName = person.Name,
                        WarName = person.WarName,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontWeight = FontWeights.SemiBold
                    }
                }
            };
            Grid.SetRow(nameBorder, row + 1);
            Grid.SetColumn(nameBorder, 0);
            RosterHost.Children.Add(nameBorder);
            for (var day = 1; day <= days; day++) AddDutyCell(person, row + 1, day);
        }

        RefreshCounters();
        SelectionSummaryText.Text = $"{_selected.Count:N0} pessoa(s) concorrendo • você pode incluir ou retirar livremente";
        StatusText.Text = $"{_selected.Count:N0} pessoa(s) na escala. A ordem e o efetivo ficam salvos para a próxima abertura.";
    }

    private void AddHeaderCell(string text, int column, bool red, DateTime? date)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(2),
            Background = TryBrush(red ? "#FEE2E2" : "#E2E8F0"),
            Foreground = TryBrush(red ? "#991B1B" : "#334155"),
            BorderBrush = TryBrush("#CBD5E1"),
            BorderThickness = new Thickness(0, 0, 1, 1)
        };
        if (date is not null)
        {
            button.ToolTip = "Botão direito: marcar ou desmarcar como dia vermelho";
            var menu = new ContextMenu();
            var item = new MenuItem { Header = red ? "Desmarcar dia vermelho" : "Marcar dia vermelho" };
            item.Click += (_, _) =>
            {
                PushUndo();
                if (!CurrentMonth.RedDays.Add(date.Value.Day)) CurrentMonth.RedDays.Remove(date.Value.Day);
                RenderMonth();
            };
            menu.Items.Add(item);
            button.ContextMenu = menu;
        }
        Grid.SetRow(button, 0);
        Grid.SetColumn(button, column);
        RosterHost.Children.Add(button);
    }

    private void AddDutyCell(DutyRosterPerson person, int row, int day)
    {
        var date = new DateTime(_year, _month, day);
        var dateKey = DutyRosterService.DayKey(_year, _month, day);
        var markKey = DutyRosterService.MarkKey(person.Key, _year, _month, day);
        var assigned = CurrentMonth.Assignments.TryGetValue(dateKey, out var who) &&
                       who.Equals(person.Key, StringComparison.OrdinalIgnoreCase);
        var mark = CurrentMonth.Marks.GetValueOrDefault(markKey) ?? string.Empty;
        var red = DutyRosterService.IsRedDay(CurrentMonth, date);
        var style = !string.IsNullOrWhiteSpace(mark) && _store.MarkStyles.TryGetValue(mark, out var markStyle)
            ? markStyle
            : null;
        var button = new Button
        {
            Content = !string.IsNullOrWhiteSpace(mark) ? Abbreviate(mark) : BuildRunningDutyLabel(person, date, red),
            FontSize = assigned ? 12 : 10,
            FontWeight = FontWeights.Bold,
            Background = TryBrush(style?.Background ?? (assigned ? "#DBEAFE" : red ? "#FFF7F7" : "#FFFFFF")),
            Foreground = TryBrush(style?.Foreground ?? (assigned ? "#1D4ED8" : red ? "#B91C1C" : "#475569")),
            BorderBrush = TryBrush("#E2E8F0"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            ToolTip = assigned
                ? $"Escalado: {person.Display}\n{date:dd/MM/yyyy}\n{BuildRunningDutyTooltip(person, date, red)}"
                : !string.IsNullOrWhiteSpace(mark)
                    ? $"{mark} — {person.Display}"
                    : $"{BuildRunningDutyTooltip(person, date, red)}\nClique para escalar {person.Display} em {date:dd/MM/yyyy}."
        };
        button.Click += (_, _) => ToggleAssignment(person, date);
        var menu = new ContextMenu();
        foreach (var markName in _store.MarkStyles.Keys.OrderBy(x => x))
        {
            var item = new MenuItem { Header = "Marcar " + markName };
            item.Click += (_, _) => SetMark(person, date, markName);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Limpar impedimento" };
        clear.Click += (_, _) => SetMark(person, date, string.Empty);
        menu.Items.Add(clear);
        button.ContextMenu = menu;
        Grid.SetRow(button, row);
        Grid.SetColumn(button, day);
        RosterHost.Children.Add(button);
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
        if (CurrentMonth.Marks.ContainsKey(markKey))
        {
            SigfurDialog.Show(this, "Essa pessoa possui impedimento nesse dia. Remova o impedimento antes de escalar.",
                "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PushUndo();
        if (CurrentMonth.Assignments.TryGetValue(key, out var current) &&
            current.Equals(person.Key, StringComparison.OrdinalIgnoreCase))
        {
            CurrentMonth.Assignments.Remove(key);
        }
        else
        {
            var previous = DutyRosterService.FindLastDuty(person.Key, date.AddDays(-1), _store.Months);
            if (previous is not null && (date.Date - previous.Value.Date).Days < 3)
            {
                var result = SigfurDialog.Show(this,
                    $"O último serviço de {person.Display} foi em {previous.Value:dd/MM/yyyy}. Isso não completa 48 horas de descanso. Deseja lançar mesmo assim?",
                    "Descanso mínimo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
            CurrentMonth.Assignments[key] = person.Key;
        }
        RenderMonth();
    }

    private void SetMark(DutyRosterPerson person, DateTime date, string mark)
    {
        PushUndo();
        var key = DutyRosterService.MarkKey(person.Key, date.Year, date.Month, date.Day);
        if (string.IsNullOrWhiteSpace(mark)) CurrentMonth.Marks.Remove(key);
        else
        {
            CurrentMonth.Marks[key] = mark;
            var dayKey = DutyRosterService.DayKey(date.Year, date.Month, date.Day);
            if (CurrentMonth.Assignments.TryGetValue(dayKey, out var assigned) &&
                assigned.Equals(person.Key, StringComparison.OrdinalIgnoreCase))
                CurrentMonth.Assignments.Remove(dayKey);
        }
        RenderMonth();
    }

    private void RefreshCounters()
    {
        var nextOpen = DutyRosterService.GetNextOpenDate(_year, _month, CurrentMonth);
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
                IsEligible = last is null || daysOff >= 3
            });
        }

        var ordered = counters
            .OrderBy(x => x.IsEligible ? 0 : 1)
            .ThenBy(x => x.Total)
            .ThenBy(x => x.RedDays)
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
            : $"{next.Total} serviço(s) no período • {next.DaysOff} dia(s) de folga • último: {next.LastDutyText}";
        MonthSummaryText.Text =
            $"{CurrentMonth.Assignments.Count:N0} de {DateTime.DaysInMonth(_year, _month)} dia(s) preenchido(s). " +
            $"Prioridade calculada com {_store.CounterMonths} mês(es) de histórico até {until:dd/MM/yyyy}. " +
            "Na grade principal, a contagem de cada categoria cresce por dia e zera em 0 quando o militar é escalado.";
    }

    private bool IsRedDutyAcrossStore(DateTime date)
    {
        return _store.Months.TryGetValue(DutyRosterService.MonthKey(date.Year, date.Month), out var month)
            ? DutyRosterService.IsRedDay(month, date)
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
        RefreshCounters();
    }

    private void AutoFill_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione quem concorre antes de preencher.", "Escala", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PushUndo();
        var reference = new DateTime(_year, _month, 1);
        var historyStart = DutyRosterService.CounterStart(reference.Year, reference.Month, _store.CounterMonths);
        CurrentMonth.Assignments = DutyRosterService.AutoFill(_year, _month, _selected, CurrentMonth, _store.Months, 1, historyStart);
        RenderMonth();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var restored = JsonSerializer.Deserialize<DutyRosterMonth>(_undo.Pop());
        if (restored is not null) _store.Months[DutyRosterService.MonthKey(_year, _month)] = restored;
        RenderMonth();
    }

    private void ClearMonth_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Limpar todos os serviços, feriados e impedimentos deste mês?", "Escala",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        PushUndo();
        _store.Months[DutyRosterService.MonthKey(_year, _month)] = new DutyRosterMonth();
        RenderMonth();
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
            "2. Quem tem menos serviços fica à frente.\n" +
            "3. Em empate, considera serviços em dias vermelhos, mais dias de folga e a data do último serviço.\n" +
            "4. A coluna Folga mostra quantos dias se passaram desde o último serviço até o próximo dia em aberto.\n" +
            "5. O indicador 48 h impede a escala automática antes de três datas corridas de diferença.\n\n" +
            "O efetivo é totalmente configurável: use os atalhos para 3º Sgt ou todos os Sgt e depois adicione ou remova qualquer pessoa.",
            "Escala do Sargento de Dia", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task SaveAsync(bool feedback)
    {
        _store.SelectionInitialized = true;
        _store.SelectedMilitaryIds = _selected.Where(x => !x.IsExtra).Select(x => x.MilitaryId).Distinct().ToList();
        _store.ExtraPeople = _all.Where(x => x.IsExtra).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _store.Order = _selected.Where(x => !x.IsExtra).Select(x => x.MilitaryId).ToList();
        await _service.SaveAsync(_store);
        if (feedback) StatusText.Text = "Escala salva com segurança em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + ".";
    }

    private static string Abbreviate(string value)
    {
        if (value.Length <= 4) return value;
        var initials = string.Concat(value.Split('/', ' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x[0])).ToUpperInvariant();
        return initials[..Math.Min(4, initials.Length)];
    }

    private static Brush TryBrush(string value)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return Brushes.Transparent; }
    }
}
