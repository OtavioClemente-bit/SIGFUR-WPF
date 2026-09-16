using Microsoft.Win32;
using System.Collections.ObjectModel;
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
        ShowCompletedBox.IsChecked = _settings.ShowCompleted;
        _initializing = false;
        await LoadAsync(_initialId);
        if (_createNew) New_Click(this, new RoutedEventArgs());
        QuickTaskBox.Focus();
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
            if (selected is not null)
            {
                ReminderGrid.SelectedItem = selected;
                ReminderGrid.ScrollIntoView(selected);
            }
        }
        UpdateMetrics();
        UpdateSelection();
        StatusText.Text = $"{_records.Count(x => !x.Completed)} tarefa(s) ativa(s), organizadas em ordem de execução.";
    }

    private void ApplyView()
    {
        if (_view is null) return;
        _view.Filter = item => item is ReminderRecord reminder && MatchesFilter(reminder);
        _view.Refresh();
        UpdateMetrics();
    }

    private bool MatchesFilter(ReminderRecord item)
    {
        if (ShowCompletedBox.IsChecked != true && item.Completed) return false;
        var search = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        var haystack = string.Join(" ", item.Title, item.Body, item.Priority, item.Recurrence, item.FormattedDate, item.Status, item.UrgentText);
        return CultureInfo.GetCultureInfo("pt-BR").CompareInfo.IndexOf(haystack, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    private void UpdateMetrics()
    {
        ActiveText.Text = _records.Count(x => !x.Completed).ToString(CultureInfo.InvariantCulture);
        UrgentText.Text = _records.Count(x => !x.Completed && x.Urgent).ToString(CultureInfo.InvariantCulture);
        TodayText.Text = _records.Count(x => !x.Completed && x.DaysRemaining is <= 0).ToString(CultureInfo.InvariantCulture);
        CompletedText.Text = _records.Count(x => x.Completed).ToString(CultureInfo.InvariantCulture);
    }

    private async void QuickAdd_Click(object sender, RoutedEventArgs e) => await AddQuickTaskAsync();

    private async void QuickTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddQuickTaskAsync();
    }

    private async Task AddQuickTaskAsync()
    {
        var title = QuickTaskBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText.Text = "Escreva o que precisa ser feito antes de adicionar.";
            QuickTaskBox.Focus();
            return;
        }

        var record = new ReminderRecord
        {
            Title = title,
            Urgent = QuickUrgentBox.IsChecked == true,
            Priority = QuickUrgentBox.IsChecked == true ? "Urgente" : "Normal",
            Recurrence = "Nenhuma",
            AutoReschedule = true
        };
        await _service.SaveAsync(record);
        QuickTaskBox.Clear();
        QuickUrgentBox.IsChecked = false;
        await LoadAsync(record.Id);
        QuickTaskBox.Focus();
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        ApplyView();
        await SaveSettingsAsync();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        ApplyView();
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        _settings.Search = SearchBox.Text;
        _settings.ShowCompleted = ShowCompletedBox.IsChecked == true;
        _settings.Order = "Ordem definida";
        _settings.GroupByPriority = false;
        _settings.AutoClassify = false;
        await _service.SaveSettingsAsync(_settings);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ReminderEditorWindow(_service) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(editor.SavedId);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione uma tarefa."; return; }
        var editor = new ReminderEditorWindow(_service, Selected) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(editor.SavedId);
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await MoveSelectedAsync(-1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await MoveSelectedAsync(1);

    private async Task MoveSelectedAsync(int direction)
    {
        if (Selected is null) { StatusText.Text = "Selecione uma tarefa para reorganizar."; return; }
        if (Selected.Completed) { StatusText.Text = "Reabra a tarefa antes de alterar sua posição."; return; }
        var id = Selected.Id;
        await _service.MoveAsync(id, direction);
        await LoadAsync(id);
    }

    private async void ToggleUrgent_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione uma tarefa."; return; }
        var id = Selected.Id;
        await _service.SetUrgentAsync(id, !Selected.Urgent);
        await LoadAsync(id);
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione uma tarefa."; return; }
        var id = Selected.Id;
        await _service.SetCompletedAsync(id, !Selected.Completed);
        await LoadAsync(id);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "Selecione uma tarefa."; return; }
        if (SigfurDialog.Show(this, $"Excluir a tarefa “{Selected.Title}”?", "Excluir tarefa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _service.DeleteAsync(Selected.Id);
        await LoadAsync();
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        var count = await _service.ArchiveCompletedAsync();
        await LoadAsync();
        StatusText.Text = count == 0 ? "Nenhuma tarefa concluída para arquivar." : $"{count} tarefa(s) arquivada(s).";
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
        if (rows.Count == 0) { StatusText.Text = "Não há tarefas para exportar."; return; }
        var dialog = new SaveFileDialog { Title = "Exportar prioridades", Filter = "Texto|*.txt", FileName = "painel_de_prioridades.txt" };
        if (dialog.ShowDialog(this) != true) return;
        await _service.ExportTextAsync(dialog.FileName, rows);
        StatusText.Text = "Lista exportada com sucesso.";
    }

    private void ReminderGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (Selected is null)
        {
            SelectedTitleText.Text = "Nenhuma tarefa selecionada";
            SelectedInfoText.Text = string.Empty;
            SelectedBodyText.Text = "Selecione uma tarefa para consultar as anotações.";
            return;
        }
        SelectedTitleText.Text = Selected.Title;
        var urgent = Selected.Urgent ? "  •  URGENTE" : string.Empty;
        SelectedInfoText.Text = $"{Selected.PositionText}{urgent}\nPrazo: {Selected.FormattedDate}  •  {Selected.DaysText}  •  {Selected.Status}";
        SelectedBodyText.Text = string.IsNullOrWhiteSpace(Selected.Body) ? "Sem anotações adicionais." : Selected.Body;
    }

    private void ReminderGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, new RoutedEventArgs());

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { New_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Up && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { MoveUp_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { MoveDown_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Enter && Selected is not null && !QuickTaskBox.IsKeyboardFocusWithin) { Edit_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Space && Selected is not null && !QuickTaskBox.IsKeyboardFocusWithin) { Toggle_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Delete && Selected is not null && !QuickTaskBox.IsKeyboardFocusWithin) { Delete_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
}
