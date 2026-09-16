using Microsoft.Win32;
using System.Windows;

namespace SIGFUR.Wpf.Views.Military;

public partial class PaystubExportWindow : Window
{
    private const string DefaultMessage = "Olá! Seguem anexos os contracheques referentes a {REFERENCIA}.";
    private const string LegacyMessage = "Olá! Segue(m) o(s) contracheque(s) selecionado(s), referente(s) a {REFERENCIA}.";

    public PaystubExportWindow(int count)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        MinWidth = Math.Min(760, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(610, SystemParameters.WorkArea.Height);
        Width = Math.Max(MinWidth, Width);
        Height = Math.Max(MinHeight, Height);
        SummaryText.Text = $"{count} militar(es) serão pesquisados nas pastas de contracheques do SIGFUR.";
        MonthBox.ItemsSource = Enumerable.Range(1, 12).Select(x => x.ToString("00")).ToList();
        MonthBox.SelectedIndex = DateTime.Today.Month - 1;
        YearBox.ItemsSource = Enumerable.Range(DateTime.Today.Year - 5, 8).Reverse().Select(x => x.ToString()).ToList();
        YearBox.Text = DateTime.Today.Year.ToString();
        Loaded += async (_, _) => await LoadMessageSettingsAsync();
    }

    public int Month => int.TryParse(MonthBox.SelectedItem?.ToString(), out var value) ? value : DateTime.Today.Month;
    public int Year => int.TryParse(YearBox.Text, out var value) ? value : DateTime.Today.Year;
    public string Folder => FolderBox.Text.Trim();
    public bool CopyForSharing { get; private set; }
    public bool ShareOnWhatsApp { get; private set; }
    public string ShareMessage => ShareMessageBox.Text
        .Replace("{REFERENCIA}", $"{Month:00}/{Year}", StringComparison.OrdinalIgnoreCase)
        .Trim();

    private async Task LoadMessageSettingsAsync()
    {
        var settings = await App.Json.LoadAsync<SIGFUR.Wpf.Models.WhatsAppShareSettings>(App.Paths.WhatsAppSettingsFile)
                       ?? new SIGFUR.Wpf.Models.WhatsAppShareSettings();
        ShareMessageBox.Text = string.IsNullOrWhiteSpace(settings.MessageTemplate)
                               || settings.MessageTemplate.Equals(LegacyMessage, StringComparison.Ordinal)
            ? DefaultMessage
            : settings.MessageTemplate;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta para exportar os contracheques",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateReference()) return;
        if (string.IsNullOrWhiteSpace(Folder))
        {
            SigfurDialog.Show(this, "Escolha a pasta de destino.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await SaveMessageSettingsAsync();
        CopyForSharing = false;
        ShareOnWhatsApp = false;
        DialogResult = true;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateReference()) return;
        if (string.IsNullOrWhiteSpace(ShareMessage))
        {
            SigfurDialog.Show(this, "Escreva a mensagem que acompanhará os contracheques.", "Copiar contracheques", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await SaveMessageSettingsAsync();
        CopyForSharing = true;
        ShareOnWhatsApp = false;
        DialogResult = true;
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateReference()) return;
        if (string.IsNullOrWhiteSpace(ShareMessage))
        {
            SigfurDialog.Show(this, "Escreva a mensagem que acompanhará os contracheques.", "Compartilhar contracheques", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await SaveMessageSettingsAsync();
        CopyForSharing = false;
        ShareOnWhatsApp = true;
        DialogResult = true;
    }

    private bool ValidateReference()
    {
        if (Month is >= 1 and <= 12 && Year is >= 2000 and <= 2100) return true;
        SigfurDialog.Show(this, "Informe uma referência válida.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private Task SaveMessageSettingsAsync()
        => App.Json.SaveAsync(App.Paths.WhatsAppSettingsFile, new SIGFUR.Wpf.Models.WhatsAppShareSettings
        {
            MessageTemplate = ShareMessageBox.Text.Trim()
        });
}
