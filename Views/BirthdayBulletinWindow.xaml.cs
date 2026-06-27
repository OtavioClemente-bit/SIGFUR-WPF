using System.Text;
using System.Windows;
using System.Windows.Documents;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class BirthdayBulletinWindow : Window
{
    private readonly IReadOnlyList<BirthdayItem> _items;
    private readonly AppPaths _paths;
    private readonly BulletinService _bulletinService;
    private BulletinRenderResult? _lastRender;
    private bool _initializing;
    private const string DefaultTemplate =
        "ASSUNTO: Conferência e atualização das pastas PHPM dos aniversariantes de {MES_ANO}\n\n" +
        "Para conhecimento, conferência e providências administrativas, seguem abaixo os militares aniversariantes do mês de {MES_ANO}:\n\n" +
        "{ANIVERSARIANTES}\n\n" +
        "Os militares relacionados deverão conferir seus dados pessoais e funcionais junto à Furriela, especialmente documentação de identificação, dados de dependentes, contatos, endereço, informações bancárias e demais registros constantes das respectivas pastas PHPM.\n\n" +
        "O Furriel deverá manter o controle atualizado, registrar as pendências identificadas e adotar as providências necessárias para a regularização das pastas, garantindo a fidelidade dos dados administrativos da Subunidade.\n\n" +
        "Em consequência, os interessados tomem conhecimento e providenciem, quando necessário, a atualização documental junto à Furriela.";

    public BirthdayBulletinWindow(IEnumerable<BirthdayItem> items, AppPaths paths)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _items = items.ToList();
        _paths = paths;
        _bulletinService = new BulletinService(paths, App.Json, App.Log);

        _initializing = true;
        Editor.Text = LoadTemplate();
        _initializing = false;
        Loaded += (_, _) =>
        {
            BulletinTabs.SelectedIndex = 0;
            Editor.CaretIndex = 0;
            Editor.ScrollToHome();
        };
    }

    private string LoadTemplate()
    {
        try
        {
            if (File.Exists(_paths.BirthdayBulletinTemplateFile))
            {
                var saved = File.ReadAllText(_paths.BirthdayBulletinTemplateFile, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(saved) && saved.Contains("PHPM", StringComparison.OrdinalIgnoreCase))
                    return saved;
            }
        }
        catch { }
        return DefaultTemplate;
    }

    private BulletinRenderResult FinalRender()
    {
        var month = DateTime.Today.ToString("MMMM 'de' yyyy").ToUpperInvariant();
        var ordered = _items
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var list = ordered.Count == 0
            ? "- Nenhum aniversariante cadastrado para o mês selecionado."
            : string.Join(Environment.NewLine, ordered.Select(x =>
                $"- {x.Day} — {MilitaryRankService.ShortName(x.Rank)} {NameHighlightHelper.PlainDisplay(x.Name, x.WarName)}"));
        var source = Editor.Text.Contains("{ANIVERSARIANTES}", StringComparison.Ordinal)
            ? Editor.Text
            : Editor.Text.TrimEnd() + "\n\n{ANIVERSARIANTES}";
        var text = source.Replace("{MES_ANO}", month).Replace("{ANIVERSARIANTES}", list).Trim();
        return new BulletinRenderResult
        {
            Text = text,
            BoldRanges = BulletinTextFormatter.FindWarNameRanges(text, ordered.Select(item => (item.Name, item.WarName))),
            UnresolvedTokens = []
        };
    }

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_initializing) return;
        _lastRender = null;
        Preview.Document = new FlowDocument();
        CopyButton.IsEnabled = false;
        PreviewStatusText.Text = "Modelo alterado — gere a prévia novamente";
    }

    private void GeneratePreview_Click(object sender, RoutedEventArgs e)
    {
        _lastRender = FinalRender();
        Preview.Document = _bulletinService.BuildDocument(_lastRender);
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastRender.Text);
        PreviewStatusText.Text = $"Prévia gerada às {DateTime.Now:HH:mm} • {_items.Count} aniversariante(s)";
        BulletinTabs.SelectedItem = PreviewTab;
        Preview.Focus();
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.BirthdayBulletinTemplateFile)!);
            File.WriteAllText(_paths.BirthdayBulletinTemplateFile, Editor.Text, new UTF8Encoding(true));
            SigfurDialog.Show(this, "Modelo salvo. Ele será carregado nas próximas aberturas.", "Boletim dos Aniversariantes", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Boletim dos Aniversariantes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        Editor.Text = DefaultTemplate;
        BulletinTabs.SelectedIndex = 0;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRender is null || string.IsNullOrWhiteSpace(_lastRender.Text) || Preview.Document is null)
        {
            SigfurDialog.Show(this, "Gere a prévia e confira o texto antes de copiar.", "Boletim dos Aniversariantes", MessageBoxButton.OK, MessageBoxImage.Information);
            BulletinTabs.SelectedIndex = 0;
            return;
        }

        BulletinService.CopyForWord(Preview.Document, _lastRender.Text);
        PreviewStatusText.Text = $"Texto conferido copiado às {DateTime.Now:HH:mm}";
        SigfurDialog.Show(this, "Texto da prévia copiado com o nome de guerra destacado.", "Boletim dos Aniversariantes", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
