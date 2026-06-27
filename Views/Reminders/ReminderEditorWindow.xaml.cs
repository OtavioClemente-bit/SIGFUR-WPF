using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Reminders;

public partial class ReminderEditorWindow : Window
{
    private readonly ReminderService _service;
    private readonly ReminderRecord _record;
    public int SavedId { get; private set; }

    public ReminderEditorWindow(ReminderService service, ReminderRecord? record = null)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        _record = record?.Clone() ?? CreateBlankRecord();
        PriorityBox.ItemsSource = ReminderRules.Priorities;
        RecurrenceBox.ItemsSource = ReminderRules.Recurrences;
        TitleText.Text = record is null ? "NOVO LEMBRETE" : "EDITAR LEMBRETE";
        LoadRecordIntoForm(record is null);
        Loaded += (_, _) => { ReminderTitleBox.Focus(); ReminderTitleBox.SelectAll(); };
    }

    private static ReminderRecord CreateBlankRecord() => new()
    {
        Title = string.Empty,
        Date = string.Empty,
        Body = string.Empty,
        Completed = false,
        Priority = "Normal",
        Recurrence = "Nenhuma",
        AutoReschedule = true
    };

    private void LoadRecordIntoForm(bool isNew)
    {
        ReminderTitleBox.Text = isNew ? string.Empty : _record.Title;
        DueDatePicker.SelectedDate = isNew ? null : _record.DueDate;
        NoDateBox.IsChecked = isNew || _record.DueDate is null;
        DueDatePicker.IsEnabled = NoDateBox.IsChecked != true;
        PriorityBox.SelectedItem = ReminderRules.Priorities.Contains(_record.Priority) ? _record.Priority : "Normal";
        RecurrenceBox.SelectedItem = ReminderRules.Recurrences.Contains(_record.Recurrence) ? _record.Recurrence : "Nenhuma";
        AutoRescheduleBox.IsChecked = isNew || _record.AutoReschedule;
        CompletedBox.IsChecked = !isNew && _record.Completed;
        BodyBox.Text = isNew ? string.Empty : _record.Body;
        ValidationText.Text = string.Empty;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = ReminderTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) { ValidationText.Text = "Informe o título do lembrete."; ReminderTitleBox.Focus(); return; }
        _record.Title = title;
        _record.Date = NoDateBox.IsChecked == true ? string.Empty : DueDatePicker.SelectedDate?.ToString("dd/MM/yyyy") ?? string.Empty;
        _record.Priority = PriorityBox.SelectedItem?.ToString() ?? "Normal";
        _record.Recurrence = RecurrenceBox.SelectedItem?.ToString() ?? "Nenhuma";
        _record.AutoReschedule = AutoRescheduleBox.IsChecked == true;
        _record.Completed = CompletedBox.IsChecked == true;
        _record.Body = BodyBox.Text.Trim();
        try
        {
            await _service.SaveAsync(_record);
            SavedId = _record.Id;
            DialogResult = true;
        }
        catch (Exception ex) { ValidationText.Text = ex.Message; }
    }

    private void NoDateBox_Changed(object sender, RoutedEventArgs e)
    {
        if (DueDatePicker is null) return;
        DueDatePicker.IsEnabled = NoDateBox.IsChecked != true;
        if (DueDatePicker.IsEnabled && DueDatePicker.SelectedDate is null) DueDatePicker.SelectedDate = DateTime.Today;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Save_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
}
