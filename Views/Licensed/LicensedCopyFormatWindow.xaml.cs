using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views.Licensed;

public partial class LicensedCopyFormatWindow : Window
{
    private const string DefaultTemplate = "{posto} {nome_upper}\nPrec-CP {prec_cp}  CPF {cpf}";
    private readonly IReadOnlyList<LicensedTransferredRecord> _records;
    private readonly LicensedTransferredPreferences _preferences;

    public LicensedCopyFormatWindow(IReadOnlyList<LicensedTransferredRecord> records, LicensedTransferredPreferences preferences)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _records = records;
        _preferences = preferences;
        TemplateBox.Text = string.IsNullOrWhiteSpace(preferences.CopyTemplate) ? DefaultTemplate : preferences.CopyTemplate;
        RefreshPreview();
    }

    private void TemplateBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();
    private void RefreshPreview() => PreviewBox.Text = string.Join(Environment.NewLine + Environment.NewLine, _records.Select(x => Render(TemplateBox.Text, x)));

    private static string Render(string template, LicensedTransferredRecord r)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["posto"] = r.ShortRank, ["nome"] = r.Name, ["nome_upper"] = r.Name.ToUpperInvariant(), ["nome_guerra"] = r.WarName,
            ["cpf"] = r.FormattedCpf, ["cpf_limpo"] = MilitaryFormatting.Digits(r.Cpf), ["prec_cp"] = r.FormattedPrecCp, ["idt"] = r.MilitaryId,
            ["banco"] = r.Bank, ["agencia"] = r.Agency, ["conta"] = r.Account, ["endereco"] = r.Address, ["cep"] = r.ZipCode,
            ["ano_formacao"] = r.FormationYear, ["data_nascimento"] = r.BirthDate, ["data_praca"] = r.EnlistmentDate, ["pnr"] = r.HasPnr,
            ["motivo"] = r.Reason, ["destino"] = r.Destination, ["telefone"] = r.Phone, ["email"] = r.Email, ["escolaridade"] = r.Education
        };
        var result = template ?? string.Empty;
        foreach (var (key, value) in values) result = result.Replace("{" + key + "}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => TemplateBox.Text = DefaultTemplate;
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _preferences.CopyTemplate = TemplateBox.Text;
        await App.Json.SaveAsync(App.Paths.LicensedTransferredSettingsFile, _preferences);
        StatusText.Text = "Modelo salvo.";
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        RefreshPreview();
        if (string.IsNullOrWhiteSpace(PreviewBox.Text)) return;
        Clipboard.SetText(PreviewBox.Text);
        _preferences.CopyTemplate = TemplateBox.Text;
        await App.Json.SaveAsync(App.Paths.LicensedTransferredSettingsFile, _preferences);
        StatusText.Text = $"{_records.Count} registro(s) copiado(s).";
    }
}
