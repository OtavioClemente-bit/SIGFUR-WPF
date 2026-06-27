using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views.Military;

public partial class CopyFormatWindow : Window
{
    public const string DefaultTemplate = "{PG} {NOME}\nPrec-CP {PREC} CPF {CPF}";
    private readonly IReadOnlyList<MilitaryRecord> _rows;
    private CopyFormatSettings _settings = new();
    private bool _ready;

    public CopyFormatWindow(IReadOnlyList<MilitaryRecord> rows)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _rows = rows;
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await App.Json.LoadAsync<CopyFormatSettings>(App.Paths.CopyFormatFile) ?? new CopyFormatSettings();
        TemplateBox.Text = string.IsNullOrWhiteSpace(_settings.Template) ? DefaultTemplate : _settings.Template;
        SelectionText.Text = $"{_rows.Count} militar(es) selecionado(s) • o texto será separado por uma linha em branco";
        _ready = true;
        RefreshPreview();
    }

    private void Template_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_ready) RefreshPreview();
    }

    private void RefreshPreview()
    {
        try { PreviewBox.Text = BuildText(TemplateBox.Text); }
        catch (Exception ex) { PreviewBox.Text = $"[ERRO NO MODELO]\n{ex.Message}"; }
    }

    private string BuildText(string template)
    {
        template = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        return string.Join(Environment.NewLine + Environment.NewLine, _rows.Select(row => Apply(template, row)));
    }

    private static string Apply(string template, MilitaryRecord row)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PG"] = row.ShortRank,
            ["NOME"] = (row.Name ?? string.Empty).Trim().ToUpperInvariant(),
            ["NOME_GUERRA"] = (row.WarName ?? string.Empty).Trim().ToUpperInvariant(),
            ["CPF"] = row.FormattedCpf,
            ["CPF_DIGITOS"] = MilitaryFormatting.Digits(row.Cpf),
            ["PREC"] = MilitaryFormatting.Digits(row.PrecCp),
            ["IDT"] = row.MilitaryId ?? string.Empty,
            ["ANO"] = row.FormationYear ?? string.Empty,
            ["TELEFONE"] = row.Phone ?? string.Empty,
            ["EMAIL"] = row.Email ?? string.Empty,
            ["ENDERECO"] = row.Address ?? string.Empty,
            ["CEP"] = row.ZipCode ?? string.Empty,
            ["TEMPO_SERVICO"] = row.ServiceTimeText,
        };
        var output = template;
        foreach (var pair in replacements) output = output.Replace("{" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
        return output;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Template = string.IsNullOrWhiteSpace(TemplateBox.Text) ? DefaultTemplate : TemplateBox.Text;
        await App.Json.SaveAsync(App.Paths.CopyFormatFile, _settings);
        SigfurDialog.Show(this, "Modelo salvo. Ele será usado nas próximas cópias.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = BuildText(TemplateBox.Text);
            Clipboard.SetText(text);
            _settings.Template = string.IsNullOrWhiteSpace(TemplateBox.Text) ? DefaultTemplate : TemplateBox.Text;
            await App.Json.SaveAsync(App.Paths.CopyFormatFile, _settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Modelo inválido", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => TemplateBox.Text = DefaultTemplate;
}
