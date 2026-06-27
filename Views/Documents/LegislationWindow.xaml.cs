using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Documents;

public partial class LegislationWindow : Window
{
    private readonly LegislationService _service;
    private readonly ObservableCollection<LegislationSearchHit> _results = [];
    private readonly ObservableCollection<LegislationDocument> _visibleDocuments = [];
    private List<LegislationDocument> _documents = [];
    private bool _busy;

    public LegislationWindow(LegislationService service)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        ResultsGrid.ItemsSource = _results;
        DocumentList.ItemsSource = _visibleDocuments;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _service.InitializeAsync();
            await RefreshDocumentsAsync();
            SearchBox.Focus();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, "Não foi possível preparar a biblioteca offline.\n\n" + ex.Message, "Legislação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SetBusyAsync(string message, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        IndexStatusText.Text = message;
        FooterStatusText.Text = message;
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Legislação", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            _busy = false;
            FooterStatusText.Text = "Pesquisa local pronta. A IA só recebe os arquivos anexados e depende da API configurada.";
        }
    }

    private async Task RefreshDocumentsAsync()
    {
        _documents = await _service.ListDocumentsAsync();
        ApplyDocumentFilter();
        var stats = await _service.GetStatsAsync();
        DocumentsMetric.Text = stats.Documents.ToString("N0");
        PagesMetric.Text = stats.Pages.ToString("N0");
        IndexStatusText.Text = stats.Display;
    }

    private void ApplyDocumentFilter()
    {
        var query = Normalize(DocumentFilterBox.Text);
        _visibleDocuments.Clear();
        foreach (var document in _documents)
        {
            var hay = Normalize(document.Title + " " + document.FileName);
            if (string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => hay.Contains(term, StringComparison.OrdinalIgnoreCase)))
                _visibleDocuments.Add(document);
        }
    }

    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchBox.Focus();
            return;
        }
        await SetBusyAsync("Pesquisando na biblioteca offline…", async () =>
        {
            var hits = await _service.SearchAsync(query);
            _results.Clear();
            foreach (var hit in hits) _results.Add(hit);
            ResultCountText.Text = $"{hits.Count:N0} resultado(s)";
            PreviewBox.Text = hits.Count == 0
                ? "Nenhum resultado encontrado. Tente termos mais específicos, o número da norma, a rubrica, um percentual ou importe o documento correspondente."
                : "Selecione um resultado para ler o texto completo da página, ou clique em “Responder pela biblioteca” para obter uma síntese com referências.";
            IndexStatusText.Text = hits.Count == 0 ? "Nenhuma correspondência" : $"Pesquisa concluída: {hits.Count:N0} resultado(s)";
        });
    }

    private async Task ShowSelectedResultAsync()
    {
        if (ResultsGrid.SelectedItem is not LegislationSearchHit hit) return;
        ReferenceText.Text = hit.Reference;
        PreviewBox.Text = await _service.GetPageTextAsync(hit.DocumentId, hit.Page);
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { }
    private void DocumentFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyDocumentFilter();

    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        var question = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(question)) { SearchBox.Focus(); return; }
        await SetBusyAsync("Construindo resposta técnica com as fontes locais…", async () =>
        {
            PreviewBox.Text = await _service.AnswerAsync(question, _results.ToList());
            ReferenceText.Text = "Síntese offline com páginas de conferência";
            IndexStatusText.Text = "Resposta montada a partir da biblioteca local";
        });
    }

    private async void OpenAiResearch_Click(object sender, RoutedEventArgs e)
    {
        var question = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            SearchBox.Focus();
            return;
        }
        if (_results.Count == 0) await SearchAsync();
        var paths = _results.Select(x => x.Path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        var prompt = $"""
            Analise a pergunta abaixo com prioridade absoluta para os documentos oficiais anexados. Cite o nome do documento e a página quando a informação estiver disponível. Diferencie claramente o que está comprovado pelos PDFs locais do que for orientação geral. Para assuntos de pagamento do Exército, indique também quais dados precisam ser conferidos em fonte oficial atualizada.

            PERGUNTA: {question}
            """;
        var window = new SIGFUR.Wpf.Views.AssistantChatWindow(prompt, paths) { Owner = this };
        window.Show();
        window.Activate();
        FooterStatusText.Text = $"Assistente aberto com {paths.Count} documento(s) local(is) relevante(s).";
    }

    private void OpenOfficialPaymentSearch_Click(object sender, RoutedEventArgs e)
    {
        var query = string.IsNullOrWhiteSpace(SearchBox.Text) ? "pagamento pessoal Exército Brasileiro remuneração militar" : SearchBox.Text.Trim();
        OpenOfficialSearch($"site:sgex.eb.mil.br OR site:planalto.gov.br OR site:gov.br Exército pagamento remuneração militar {query}", "Pesquisa externa aberta em fontes oficiais de pagamento. Confira data, órgão e vigência antes de aplicar.");
    }

    private void OpenSgexSearch_Click(object sender, RoutedEventArgs e)
    {
        var query = string.IsNullOrWhiteSpace(SearchBox.Text) ? "portaria pagamento pessoal Exército SIPPES CPEx" : SearchBox.Text.Trim();
        OpenOfficialSearch($"site:sgex.eb.mil.br Exército Portaria DGP C Ex SIPPES CPEx pagamento {query}", "Pesquisa aberta no SGEx/EB. Priorize portarias, boletins e normas vigentes do Exército.");
    }

    private void OpenPlanaltoSearch_Click(object sender, RoutedEventArgs e)
    {
        var query = string.IsNullOrWhiteSpace(SearchBox.Text) ? "militares remuneração férias pagamento" : SearchBox.Text.Trim();
        OpenOfficialSearch($"site:planalto.gov.br militares remuneração férias pagamento MP 2215 decreto {query}", "Pesquisa aberta no Planalto. Confira lei, decreto, medida provisória e redação vigente.");
    }

    private void OpenOfficialSearch(string query, string status)
    {
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        ShellService.OpenPath(url);
        FooterStatusText.Text = status;
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Documentos oficiais e listas de links|*.pdf;*.html;*.htm;*.docx;*.odt;*.txt;*.url;*.csv;*.json;*.md|Documentos oficiais|*.pdf;*.html;*.htm;*.docx;*.odt;*.txt|Atalhos/listas para baixar|*.url;*.csv;*.json;*.md|PDF|*.pdf|HTML oficial|*.html;*.htm|Word|*.docx|OpenDocument|*.odt|Texto|*.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        await SetBusyAsync("Importando e indexando documentos…", async () =>
        {
            var count = await _service.ImportFilesAsync(dialog.FileNames);
            await RefreshDocumentsAsync();
            var message = count > 0
                ? $"{count:N0} documento(s) oficial(is) importado(s)/baixado(s) e indexado(s)."
                : "Nenhum documento oficial foi indexado. Se você importou apenas atalhos (.url) ou manifesto, o SIGFUR tentou baixar os documentos oficiais pela internet. Confira a conexão, proxy/rede do quartel e tente novamente, ou salve os PDFs/HTML oficiais na pasta e clique em Atualizar índice.";
            SigfurDialog.Show(this, message, "Legislação", MessageBoxButton.OK, count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async void ImportZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Pacotes ZIP|*.zip", Title = "Selecione um pacote com normas e manuais" };
        if (dialog.ShowDialog(this) != true) return;
        await SetBusyAsync("Extraindo e indexando o pacote…", async () =>
        {
            var count = await _service.ImportZipAsync(dialog.FileName);
            await RefreshDocumentsAsync();
            var message = count > 0
                ? $"{count:N0} documento(s) oficial(is) importado(s)/baixado(s) do ZIP."
                : "Nenhum documento oficial foi indexado no ZIP. O pacote pode conter somente .url/.md/manifesto. O SIGFUR agora tenta baixar os links oficiais automaticamente; se ainda ficar 0, a internet/rede bloqueou o download. Abra a pasta da biblioteca, salve os PDFs/HTML oficiais nela e clique em Reindexar tudo.";
            SigfurDialog.Show(this, message, "Legislação", MessageBoxButton.OK, count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione uma pasta com normas, manuais e orientações", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await SetBusyAsync("Importando a pasta e atualizando o índice…", async () =>
        {
            var count = await _service.ImportFolderAsync(dialog.FolderName);
            await RefreshDocumentsAsync();
            var message = count > 0
                ? $"{count:N0} documento(s) oficial(is) importado(s)/baixado(s)."
                : "Nenhum documento oficial foi indexado. Se a pasta contém apenas .url/.md/CSV, o SIGFUR tentou baixar os links oficiais; confira conexão. Se preferir, baixe/salve os PDFs ou páginas HTML oficiais nessa pasta e clique em Atualizar índice.";
            SigfurDialog.Show(this, message, "Legislação", MessageBoxButton.OK, count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async void Index_Click(object sender, RoutedEventArgs e) => await IndexAsync(false);
    private async void ForceIndex_Click(object sender, RoutedEventArgs e) => await IndexAsync(true);

    private async Task IndexAsync(bool force)
    {
        await SetBusyAsync(force ? "Reindexando toda a biblioteca…" : "Atualizando documentos novos ou alterados…", async () =>
        {
            var progress = new Progress<string>(text => IndexStatusText.Text = text);
            await _service.IndexAllAsync(force, progress);
            await RefreshDocumentsAsync();
        });
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.LegislationDocumentsDirectory);
    private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ShowSelectedResultAsync();
    private async void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { await ShowSelectedResultAsync(); OpenSelectedResult(); }
    private async void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentList.SelectedItem is not LegislationDocument document) return;
        ReferenceText.Text = document.Title;
        PreviewBox.Text = $"Arquivo: {document.FileName}\nPáginas indexadas: {document.PageCount:N0}\nTamanho: {document.SizeText}\nIndexado em: {document.IndexedAtText}\n\nDê duplo clique para abrir o documento original.";
        await Task.CompletedTask;
    }
    private void DocumentList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedDocument();

    private void OpenSelectedResult_Click(object sender, RoutedEventArgs e) => OpenSelectedResult();
    private void OpenSelectedResult()
    {
        if (ResultsGrid.SelectedItem is LegislationSearchHit hit) ShellService.OpenPath(hit.Path);
        else if (DocumentList.SelectedItem is LegislationDocument document) ShellService.OpenPath(document.Path);
    }
    private void OpenSelectedDocument_Click(object sender, RoutedEventArgs e) => OpenSelectedDocument();
    private void OpenSelectedDocument() { if (DocumentList.SelectedItem is LegislationDocument document) ShellService.OpenPath(document.Path); }
    private void OpenSelectedDocumentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is LegislationDocument document) ShellService.OpenPath(Path.GetDirectoryName(document.Path) ?? App.Paths.LegislationDocumentsDirectory);
    }
    private void CopyDocumentPath_Click(object sender, RoutedEventArgs e) { if (DocumentList.SelectedItem is LegislationDocument document) Clipboard.SetText(document.Path); }
    private void CopyReference_Click(object sender, RoutedEventArgs e) { if (ResultsGrid.SelectedItem is LegislationSearchHit hit) Clipboard.SetText(hit.Reference); }
    private void CopySnippet_Click(object sender, RoutedEventArgs e) { if (ResultsGrid.SelectedItem is LegislationSearchHit hit) Clipboard.SetText(hit.Snippet); }
    private void CopyPreview_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(PreviewBox.Text)) Clipboard.SetText(PreviewBox.Text); }

    private async void ReadWholeDocument_Click(object sender, RoutedEventArgs e)
    {
        var documentId = ResultsGrid.SelectedItem is LegislationSearchHit hit
            ? hit.DocumentId
            : DocumentList.SelectedItem is LegislationDocument document ? document.Id : 0;
        if (documentId <= 0)
        {
            SigfurDialog.Show(this, "Selecione um documento ou resultado.", "Legislação", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await SetBusyAsync("Montando leitura integral do documento…", async () =>
        {
            PreviewBox.Text = await _service.GetDocumentTextAsync(documentId);
            ReferenceText.Text = "Leitura integral da fonte selecionada";
        });
    }

    private void ExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreviewBox.Text)) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Texto UTF-8|*.txt|HTML oficial|*.html;*.htm",
            FileName = "RESPOSTA_LEGISLACAO_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            InitialDirectory = App.Paths.LegislationDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, PreviewBox.Text, new UTF8Encoding(true));
        IndexStatusText.Text = "Resposta exportada: " + dialog.FileName;
    }

    private async void DeleteDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is not LegislationDocument document) return;
        var answer = SigfurDialog.Show(this,
            $"Remover “{document.Title}” da biblioteca e apagar o arquivo armazenado pelo SIGFUR?",
            "Legislação", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await SetBusyAsync("Removendo documento e índice…", async () =>
        {
            await _service.DeleteDocumentAsync(document, deleteFile: true);
            _results.Clear();
            PreviewBox.Clear();
            ReferenceText.Text = string.Empty;
            await RefreshDocumentsAsync();
        });
    }


    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
    }
}
