using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Reminders;

public partial class ReminderUrgentWindow : Window
{
    public bool OpenModule { get; private set; }
    public ReminderUrgentWindow(IReadOnlyList<ReminderRecord> records)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        UrgentGrid.ItemsSource = records;
        var summary = ReminderService.Summarize(records, 36500);
        SummaryText.Text = $"Atrasados: {summary.Overdue}  •  Hoje: {summary.Today}  •  Próximos: {Math.Max(0, records.Count - summary.Overdue - summary.Today)}";
    }
    private void Open_Click(object sender, RoutedEventArgs e) { OpenModule = true; DialogResult = true; }
}
