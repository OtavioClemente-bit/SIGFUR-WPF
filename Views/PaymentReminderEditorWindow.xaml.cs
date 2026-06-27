using System.Windows;
using System.Windows.Controls;

namespace SIGFUR.Wpf.Views;

public partial class PaymentReminderEditorWindow : Window
{
    private readonly PaymentReminderRow? _source;
    private readonly string _id;
    private readonly string _createdAt;

    public PaymentReminderEditorWindow(PaymentReminderRow? source, string defaultCompetence, DateTime defaultDeadline)
    {
        InitializeComponent(); App.UiState.Attach(this); _source = source;
        _id = source?.Id ?? $"pag_{DateTime.Now:yyyyMMdd_HHmmss_ffffff}";
        _createdAt = source?.CreatedAt ?? DateTime.Now.ToString("O");
        CategoryBox.Text = source?.Category ?? "Outro";
        DescriptionBox.Text = source?.Description ?? string.Empty;
        CompetenceBox.Text = source?.Competence ?? defaultCompetence;
        DeadlinePicker.SelectedDate = ParseDate(source?.Deadline) ?? defaultDeadline;
        ObservationBox.Text = source?.Observation ?? string.Empty;
        PermanentBox.IsChecked = source?.IsPermanent ?? false;
        var priority = source?.Priority ?? "Normal";
        PriorityBox.SelectedIndex = priority.Equals("Alta", StringComparison.OrdinalIgnoreCase) ? 0 : priority.Equals("Baixa", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        Loaded += (_, _) => { DescriptionBox.Focus(); DescriptionBox.SelectAll(); };
    }

    public PaymentReminderRow? Result { get; private set; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var description = DescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(description)) { SigfurDialog.Show(this, "Informe o que precisa ser conferido ou pago.", "Controle de pagamento", MessageBoxButton.OK, MessageBoxImage.Warning); DescriptionBox.Focus(); return; }
        var priority = (PriorityBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? PriorityBox.Text ?? "Normal";
        Result = new PaymentReminderRow
        {
            Id = _id, Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? "Outro" : CategoryBox.Text.Trim(), Description = description,
            Competence = CompetenceBox.Text.Trim(), Deadline = DeadlinePicker.SelectedDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            Priority = priority, Observation = ObservationBox.Text.Trim(), IsPermanent = PermanentBox.IsChecked == true,
            Status = _source?.Status ?? "pendente", CompletedAt = _source?.CompletedAt ?? string.Empty,
            CreatedAt = _createdAt, UpdatedAt = DateTime.Now.ToString("O")
        };
        DialogResult = true;
    }
    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var date) ? date : null;
}
