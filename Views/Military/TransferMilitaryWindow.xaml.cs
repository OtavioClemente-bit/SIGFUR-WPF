using System.Windows;
using System.Windows.Controls;

namespace SIGFUR.Wpf.Views.Military;

public partial class TransferMilitaryWindow : Window
{
    public TransferMilitaryWindow(int count, string summary)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        SummaryText.Text = count == 1 ? summary : $"{count} militares selecionados. {summary}";
    }

    public string Reason
    {
        get
        {
            var reason = (ReasonBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Transferência";
            var detail = DetailBox.Text.Trim();
            return string.IsNullOrWhiteSpace(detail) ? reason : $"{reason} — {detail}";
        }
    }
    public string Destination => DestinationBox.Text.Trim();
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
