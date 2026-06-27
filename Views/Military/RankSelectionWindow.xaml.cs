using System.Windows;

namespace SIGFUR.Wpf.Views.Military;

public partial class RankSelectionWindow : Window
{
    public RankSelectionWindow(IEnumerable<string> ranks, int count)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        SummaryText.Text = count == 1 ? "1 militar selecionado." : $"{count} militares selecionados.";
        RankBox.ItemsSource = ranks.ToList();
        RankBox.SelectedIndex = 0;
    }
    public string SelectedRank => RankBox.SelectedItem?.ToString() ?? string.Empty;
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedRank)) { SigfurDialog.Show(this, "Selecione o novo Posto/Graduação.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
