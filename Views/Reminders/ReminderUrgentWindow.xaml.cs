using System.Globalization;
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
        CountText.Text = records.Count.ToString(CultureInfo.InvariantCulture);
        SummaryText.Text = $"Revise antes de iniciar a rotina. Marcadas como urgentes: {records.Count(x => x.Urgent)}  •  Vencidas: {summary.Overdue}  •  Para hoje: {summary.Today}";
    }
    private void Open_Click(object sender, RoutedEventArgs e) { OpenModule = true; DialogResult = true; }
}
