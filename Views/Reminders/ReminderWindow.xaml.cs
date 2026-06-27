using Microsoft.Win32;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Reminders;

public partial class ReminderWindow : Window
{
    private readonly ReminderService _service;
    private readonly ObservableCollection<ReminderRecord> _records = [];
    private ListCollectionView? _view;
    private ReminderSettings _settings = new();
    private string _sortProperty = nameof(ReminderRecord.DueDate);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private readonly int _initialId;
    private readonly bool _createNew;
    private bool _initializing = true;

    public ReminderWindow(ReminderService service, int initialId = 0, bool createNew = false)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        _initialId = initialId;
        _createNew = createNew;
        ReminderGrid.ItemsSource = _records;
    }

    private ReminderRecord? Selected => ReminderGrid.SelectedItem as ReminderRecord;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _service.LoadSettingsAsync();
        SearchBox.Text = _settings.Search;
        UpcomingDaysBox.Text = Math.Max(0, _settings.UpcomingDays).ToString(CultureInfo.InvariantCulture);
        ShowCompletedBox.IsChecked = _settings.ShowCompleted;
        GroupBox.IsChecked = _settings.GroupByPriority;
        AutoPriorityBox.IsChecked = _settings.AutoClassify;
        OrderBox.SelectedIndex = _settings.Order switch { "Data (mais distante)" => 1, "Título (A→Z)" => 2, "Status" => 3, "Prioridade" => 4, _ => 0 };
        _initializing = false;
        if (_settings.AutoClassify) await _service.ApplyAutomaticPrioritiesAsync();
        await LoadAsync(_initialId);
        if (_createNew) New_Click(this, new RoutedEventArgs());
        SearchBox.Focus();
    }

    private async Task LoadAsync(int selectId = 0)
    {
        var items = await _service.LoadAsync();
        _records.Clear();
        foreach (var item in items) _records.Add(item);
        _view = CollectionViewSource.GetDefaultView(_records) as ListCollectionView;
        ApplyView();
        if (selectId > 0)
        {
            var selected = _records.FirstOrDefault(x => x.Id == selectId);
            if (selected is not null) { ReminderGrid.SelectedItem = selected; ReminderGrid.ScrollIntoView(selected); }
        }
        UpdateBadges();
        UpdateSelection();
        StatusText.Text = $"{_view?.Cast<object>().Count() ?? _records.Count} lembrete(s) exibido(s).";
    }

    private int UpcomingDays => int.TryParse(UpcomingDaysBox.Text, out var days) ? Math.Max(0, days) : 2;

    private void ApplyView()
    {
        if (_view is null) return;
        _view.Filter = item => item is ReminderRecord reminder && MatchesFilter(reminder);
        _view.GroupDescriptions.Clear();
        if (GroupBox.IsChecked == true)
            _view.GroupDescriptions.Add(new PropertyGroupDescription(AutoPriorityBox.IsChecked == true ? nameof(ReminderRecord.EffectivePriority) : nameof(ReminderRecord.Priority)));
        _view.CustomSort = new ReminderComparer(_sortProperty, _sortDirection, AutoPriorityBox.IsChecked == true);
        _view.Refresh();
        UpdateBadges();
    }

    private bool MatchesFilter(ReminderRecord item)
    {
        if (ShowCompletedBox.IsChecked != true && item.Completed) return false;
        var search = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        var haystack = string.Join(" ", item.Title, item.Body, item.Priority, item.EffectivePriority, item.Recurrence, item.FormattedDate, item.Status);
        return CultureInfo.GetCultureInfo("pt-BR").CompareInfo.IndexOf(haystack, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    private void UpdateBadges()
    {
        var visible = _view?.Cast<object>().OfType<ReminderRecord>().ToList() ?? _records.ToList();
        var summary = ReminderService.Summarize(visible, UpcomingDays);
        OverdueText.Text = summary.Overdue.ToString(CultureInfo.InvariantCulture);
        TodayText.Text = summary.Today.ToString(CultureInfo.InvariantCulture);
        UpcomingText.Text = summary.Upcoming.ToString(CultureInfo.InvariantCulture);
        NoDateText.Text = summary.NoDate.ToString(CultureInfo.InvariantCulture);
        CompletedText.Text = summary.Completed.ToString(CultureInfo.InvariantCulture);
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        ApplyView();
        await SaveSettingsAsync();
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded && !_initializing) { ApplyView(); _ = SaveSettingsAsync(); } }
    private async void AutoPriority_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        if (AutoPriorityBox.IsChecked == true)
        {
            await _service.ApplyAutomaticPrioritiesAsync();
            await LoadAsync(Selected?.Id ?? 0);
        }
        else ApplyView();
        await SaveSettingsAsync();
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        _sortProperty = OrderBox.SelectedIndex switch { 1 => nameof(ReminderRecord.DueDate), 2 => nameof(ReminderRecord.Title), 3 => nameof(ReminderRecord.Status), 4 => nameof(ReminderRecord.EffectivePriority), _ => nameof(ReminderRecord.DueDate) };
        _sortDirection = OrderBox.SelectedIndex == 1 ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ApplyView();
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        _settings.Search = SearchBox.Text;
        _settings.Order = (OrderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Data (mais próximo)";
        _settings.UpcomingDays = UpcomingDays;
        _settings.ShowCompleted = ShowCompletedBox.IsChecked == true;
        _settings.GroupByPriority = GroupBox.IsChecked == true;
        _settings.AutoClassify = AutoPriorityBox.IsChecked == true;
        await _service.SaveSettingsAsync(_settings);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ReminderEditorWindow(_service) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(editor.SavedId);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione um lembrete."; return; }
        var editor = new ReminderEditorWindow(_service, Selected) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(editor.SavedId);
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione um lembrete."; return; }
        var id = Selected.Id;
        await _service.SetCompletedAsync(id, !Selected.Completed);
        await LoadAsync(id);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione um lembrete."; return; }
        if (SigfurDialog.Show(this, $"Excluir o lembrete “{Selected.Title}”?", "Excluir lembrete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _service.DeleteAsync(Selected.Id);
        await LoadAsync();
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        var count = await _service.ArchiveCompletedAsync();
        StatusText.Text = count == 0 ? "Nenhum lembrete concluído para arquivar." : $"{count} lembrete(s) arquivado(s).";
        await LoadAsync();
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        var history = new ReminderHistoryWindow(_service) { Owner = this };
        history.ShowDialog();
        if (history.Changed) await LoadAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var rows = _view?.Cast<object>().OfType<ReminderRecord>().ToList() ?? _records.ToList();
        if (rows.Count == 0) { StatusText.Text = "Não há lembretes para exportar."; return; }
        var dialog = new SaveFileDialog { Title = "Exportar lembretes", Filter = "Texto|*.txt", FileName = "lembretes.txt" };
        if (dialog.ShowDialog(this) != true) return;
        await _service.ExportTextAsync(dialog.FileName, rows);
        StatusText.Text = "Lembretes exportados.";
    }

    private void ReminderGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (Selected is null) { SelectedTitleText.Text = "Nenhum lembrete selecionado"; SelectedInfoText.Text = string.Empty; SelectedBodyText.Text = "Selecione um item para ver a descrição."; return; }
        SelectedTitleText.Text = Selected.Title;
        var displayedPriority = AutoPriorityBox.IsChecked == true ? Selected.EffectivePriority : Selected.Priority;
        SelectedInfoText.Text = $"{Selected.FormattedDate}  •  {Selected.DaysText}\n{Selected.Status}  •  {displayedPriority}  •  {Selected.Recurrence}";
        SelectedBodyText.Text = string.IsNullOrWhiteSpace(Selected.Body) ? "Sem descrição adicional." : Selected.Body;
    }
    private void ReminderGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, new RoutedEventArgs());

    private void ReminderGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var property = string.IsNullOrWhiteSpace(e.Column.SortMemberPath) ? nameof(ReminderRecord.DueDate) : e.Column.SortMemberPath;
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortProperty = property;
        foreach (var column in ReminderGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortDirection;
        ApplyView();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { New_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Enter && Selected is not null) { Edit_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Space && Selected is not null) { Toggle_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Delete && Selected is not null) { Delete_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { ReminderGrid.UnselectAll(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private sealed class ReminderComparer(string property, ListSortDirection direction, bool autoPriority) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ReminderRecord left || y is not ReminderRecord right) return 0;
            var result = property switch
            {
                nameof(ReminderRecord.Title) => CompareText(left.Title, right.Title),
                nameof(ReminderRecord.Status) => StatusOrder(left.Status).CompareTo(StatusOrder(right.Status)),
                nameof(ReminderRecord.EffectivePriority) => PriorityOrder(autoPriority ? left.EffectivePriority : left.Priority).CompareTo(PriorityOrder(autoPriority ? right.EffectivePriority : right.Priority)),
                nameof(ReminderRecord.Recurrence) => CompareText(left.Recurrence, right.Recurrence),
                nameof(ReminderRecord.Completed) => left.Completed.CompareTo(right.Completed),
                nameof(ReminderRecord.DaysRemaining) => (left.DaysRemaining ?? int.MaxValue).CompareTo(right.DaysRemaining ?? int.MaxValue),
                _ => (left.DueDate ?? DateTime.MaxValue).CompareTo(right.DueDate ?? DateTime.MaxValue)
            };
            if (result == 0) result = CompareText(left.Title, right.Title);
            return direction == ListSortDirection.Descending ? -result : result;
        }
        private static int CompareText(string? left, string? right) => string.Compare(left, right, CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        private static int StatusOrder(string status) => status switch { "Atrasado" => 0, "Hoje" => 1, "Pendente" => 2, "Sem data" => 3, "Concluído" => 4, _ => 9 };
        private static int PriorityOrder(string priority) => priority switch { "Urgentíssimo" => 0, "Urgente" => 1, "Normal" => 2, "Baixa" => 3, _ => 9 };
    }
}
