using Microsoft.Win32;
using System.Windows;

namespace SIGFUR.Wpf.Views.Military;

public partial class PaystubExportWindow : Window
{
    public PaystubExportWindow(int count)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        SummaryText.Text = $"{count} militar(es) serão pesquisados nas pastas de contracheques do SIGFUR e do usuário.";
        MonthBox.ItemsSource = Enumerable.Range(1, 12).Select(x => x.ToString("00")).ToList();
        MonthBox.SelectedIndex = DateTime.Today.Month - 1;
        YearBox.ItemsSource = Enumerable.Range(DateTime.Today.Year - 5, 8).Reverse().Select(x => x.ToString()).ToList();
        YearBox.Text = DateTime.Today.Year.ToString();
    }
    public int Month => int.TryParse(MonthBox.SelectedItem?.ToString(), out var value) ? value : DateTime.Today.Month;
    public int Year => int.TryParse(YearBox.Text, out var value) ? value : DateTime.Today.Year;
    public string Folder => FolderBox.Text.Trim();
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Escolha a pasta para exportar os contracheques", Multiselect = false };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Month is < 1 or > 12 || Year is < 2000 or > 2100) { SigfurDialog.Show(this, "Informe uma referência válida.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (string.IsNullOrWhiteSpace(Folder)) { SigfurDialog.Show(this, "Escolha a pasta de destino.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
