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
    private readonly string _initialQuery;
    private readonly LegislationSearchScope _initialScope;
    private readonly bool _showInitialSummary;
    private List<LegislationDocument> _documents = [];
    private bool _busy;
    private long _currentDocumentId;
    private string _currentDocumentTitle = string.Empty;
    private string _currentDocumentPath = string.Empty;
    private int _currentPage;
    private int _currentPageCount;

    public LegislationWindow(
        LegislationService service,
        string? initialQuery = null,
        LegislationSearchScope initialScope = LegislationSearchScope.All,
        bool showInitialSummary = false)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        _initialQuery = initialQuery?.Trim() ?? string.Empty;
        _initialScope = initialScope;
        _showInitialSummary = showInitialSummary;
        ResultsGrid.ItemsSource = _results;
        DocumentList.ItemsSource = _visibleDocuments;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public static LegislationWindow ShowTopic(
        Window owner,
        string topic,
        bool manualOnly = false,
        bool showSummary = false)
    {
        var window = new LegislationWindow(
            App.Legislation,
            topic,
            manualOnly ? LegislationSearchScope.ManualSippes : LegislationSearchScope.All,
            showSummary)
        {
            Owner = owner,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Show();
        window.Activate();
        return window;
    }

    private async Task InitializeAsync()
    {
        SelectScope(_initialScope);
        await SetBusyAsync("Preparando e indexando a biblioteca instalada…", async () =>
        {
            await _service.InitializeAsync();
            var progress = new Progress<string>(text =>
            {
                IndexStatusText.Text = text;
                FooterStatusText.Text = text;
            });
            await _service.IndexAllAsync(force: false, progress);
            await RefreshDocumentsAsync();
        });

        if (!string.IsNullOrWhiteSpace(_initialQuery))
        {
            SearchBox.Text = _initialQuery;
            await SearchAsync();
            if (_showInitialSummary)
                await ShowAnswerAsync();
        }
        else
        {
            PreviewBox.Text = "Escolha um documento da biblioteca ou pesquise um assunto. O texto será aberto aqui, página por página, sem sair do SIGFUR.";
            SearchBox.Focus();
        }
    }

    private async Task SetBusyAsync(string message, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Cursor = Cursors.Wait;
        IndexStatusText.Text = message;
        FooterStatusText.Text = message;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha na Central Técnica Offline.", ex);
            SigfurDialog.Show(this, ex.Message, "Central Técnica Offline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = null;
            _busy = false;
            FooterStatusText.Text = "Consulta 100% offline. Confira a página citada e a vigência da norma antes de efetuar lançamento ou pagamento.";
        }
    }

    private async Task RefreshDocumentsAsync()
    {
        _documents = (await _service.ListDocumentsAsync())
            .OrderBy(document => document.DocumentKind.Equals("Manual técnico", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(document => document.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(document => document.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyDocumentFilter();
        var stats = await _service.GetStatsAsync();
        DocumentsMetric.Text = stats.Documents.ToString("N0");
        PagesMetric.Text = stats.Pages.ToString("N0");
        BuiltInCountText.Text = $"{stats.BuiltInDocuments:N0} incluído(s)";
        IndexStatusText.Text = stats.Display;
    }

    private void ApplyDocumentFilter()
    {
        var query = Normalize(DocumentFilterBox.Text);
        _visibleDocuments.Clear();
        foreach (var document in _documents)
        {
            var haystack = Normalize($"{document.Title} {document.FileName} {document.Category} {document.DocumentKind}");
            if (string.IsNullOrWhiteSpace(query)
                || query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)))
                _visibleDocuments.Add(document);
        }
    }

    private LegislationSearchScope SelectedScope()
    {
        var tag = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<LegislationSearchScope>(tag, out var scope)
            ? scope
            : LegislationSearchScope.All;
    }

    private void SelectScope(LegislationSearchScope scope)
    {
        ScopeBox.SelectedItem = ScopeBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), scope.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? ScopeBox.Items[0];
    }

    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchBox.Focus();
            return;
        }

        await SetBusyAsync("Pesquisando no conteúdo offline…", async () =>
        {
            var hits = await _service.SearchAsync(query, 160, SelectedScope());
            _results.Clear();
            foreach (var hit in hits) _results.Add(hit);
            ResultCountText.Text = $"{hits.Count:N0} resultado(s)";
            IndexStatusText.Text = hits.Count == 0
                ? "Nenhuma correspondência encontrada"
                : $"Pesquisa concluída: {hits.Count:N0} resultado(s)";
            if (hits.Count == 0)
            {
                PreviewBox.Text = "Nenhum resultado encontrado. Tente o nome do direito, uma rubrica, o número da norma ou mude o filtro entre Manual SIPPES e Normas/Portarias.";
                ReferenceText.Text = string.Empty;
                ClearPagePosition();
            }
            else
            {
                ResultsGrid.SelectedIndex = 0;
                ResultsGrid.ScrollIntoView(ResultsGrid.SelectedItem);
            }
        });
    }

    private async Task ShowAnswerAsync()
    {
        var question = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            SearchBox.Focus();
            return;
        }
        if (_results.Count == 0)
            await SearchAsync();
        if (_results.Count == 0) return;

        await SetBusyAsync("Montando resumo com referências locais…", async () =>
        {
            PreviewBox.Text = await _service.AnswerAsync(question, _results.ToList(), SelectedScope());
            ReferenceText.Text = "Resumo offline — confirme as páginas indicadas";
            PagePositionText.Text = "Síntese técnica baseada nas fontes instaladas";
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            IndexStatusText.Text = "Resumo montado sem internet e sem IA externa";
        });
    }

    private async Task ShowDocumentPageAsync(
        long documentId,
        string title,
        string path,
        int page,
        int pageCount)
    {
        if (documentId <= 0) return;
        _currentDocumentId = documentId;
        _currentDocumentTitle = title;
        _currentDocumentPath = path;
        _currentPageCount = Math.Max(1, pageCount);
        _currentPage = Math.Clamp(page, 1, _currentPageCount);

        var text = await _service.GetPageTextAsync(_currentDocumentId, _currentPage);
        ReferenceText.Text = $"{_currentDocumentTitle}, p. {_currentPage}";
        PreviewBox.Text = string.IsNullOrWhiteSpace(text)
            ? "Esta página não possui texto extraível. Use “Abrir PDF original” para conferir a imagem da página."
            : text;
        PreviewBox.ScrollToHome();
        PagePositionText.Text = $"Página {_currentPage:N0} de {_currentPageCount:N0}";
        PreviousPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < _currentPageCount;
    }

    private void ClearPagePosition()
    {
        _currentDocumentId = 0;
        _currentDocumentPath = string.Empty;
        _currentDocumentTitle = string.Empty;
        _currentPage = 0;
        _currentPageCount = 0;
        PagePositionText.Text = "Nenhuma página aberta";
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async void QuickTopic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var topic = button.Tag?.ToString() ?? string.Empty;
        if (topic.Equals("__manual__", StringComparison.Ordinal))
        {
            SelectScope(LegislationSearchScope.ManualSippes);
            var manual = _documents.FirstOrDefault(document => document.DocumentKind.Equals("Manual técnico", StringComparison.OrdinalIgnoreCase));
            if (manual is not null)
            {
                DocumentList.SelectedItem = _visibleDocuments.FirstOrDefault(document => document.Id == manual.Id);
                await ShowDocumentPageAsync(manual.Id, manual.Title, manual.Path, 1, manual.PageCount);
                SearchBox.Text = "Manual Técnico do SIPPES";
            }
            return;
        }

        SelectScope(LegislationSearchScope.All);
        SearchBox.Text = topic;
        await SearchAsync();
    }

    private async void Answer_Click(object sender, RoutedEventArgs e) => await ShowAnswerAsync();
    private void DocumentFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyDocumentFilter();

    private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not LegislationSearchHit hit) return;
        await ShowDocumentPageAsync(hit.DocumentId, hit.Title, hit.Path, hit.Page, hit.DocumentPageCount);
    }

    private async void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is LegislationSearchHit hit)
            await ShowDocumentPageAsync(hit.DocumentId, hit.Title, hit.Path, hit.Page, hit.DocumentPageCount);
    }

    private async void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentList.SelectedItem is not LegislationDocument document) return;
        ResultsGrid.SelectedItem = null;
        await ShowDocumentPageAsync(document.Id, document.Title, document.Path, 1, document.PageCount);
    }

    private async void DocumentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DocumentList.SelectedItem is LegislationDocument document)
            await ShowDocumentPageAsync(document.Id, document.Title, document.Path, 1, document.PageCount);
    }

    private async void ReadSelectedResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is LegislationSearchHit hit)
            await ShowDocumentPageAsync(hit.DocumentId, hit.Title, hit.Path, hit.Page, hit.DocumentPageCount);
    }

    private async void ReadSelectedDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is LegislationDocument document)
            await ShowDocumentPageAsync(document.Id, document.Title, document.Path, 1, document.PageCount);
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocumentId > 0 && _currentPage > 1)
            await ShowDocumentPageAsync(_currentDocumentId, _currentDocumentTitle, _currentDocumentPath, _currentPage - 1, _currentPageCount);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocumentId > 0 && _currentPage < _currentPageCount)
            await ShowDocumentPageAsync(_currentDocumentId, _currentDocumentTitle, _currentDocumentPath, _currentPage + 1, _currentPageCount);
    }

    private void OpenSelectedResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is LegislationSearchHit hit) ShellService.OpenPath(hit.Path);
    }

    private void OpenSelectedDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is LegislationDocument document) ShellService.OpenPath(document.Path);
    }

    private void OpenCurrentSource_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_currentDocumentPath)) ShellService.OpenPath(_currentDocumentPath);
        else if (ResultsGrid.SelectedItem is LegislationSearchHit hit) ShellService.OpenPath(hit.Path);
        else if (DocumentList.SelectedItem is LegislationDocument document) ShellService.OpenPath(document.Path);
    }

    private void OpenSelectedDocumentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is LegislationDocument document)
            ShellService.OpenPath(Path.GetDirectoryName(document.Path) ?? App.Paths.LegislationDocumentsDirectory);
    }

    private void CopyDocumentPath_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is LegislationDocument document) Clipboard.SetText(document.Path);
    }

    private void CopyReference_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is LegislationSearchHit hit) Clipboard.SetText(hit.Reference);
        else if (!string.IsNullOrWhiteSpace(ReferenceText.Text)) Clipboard.SetText(ReferenceText.Text);
    }

    private void CopySnippet_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is LegislationSearchHit hit) Clipboard.SetText(hit.Snippet);
    }

    private void CopyPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(PreviewBox.Text)) Clipboard.SetText(PreviewBox.Text);
    }

    private async void ReadWholeDocument_Click(object sender, RoutedEventArgs e)
    {
        var documentId = _currentDocumentId;
        if (documentId <= 0 && ResultsGrid.SelectedItem is LegislationSearchHit hit) documentId = hit.DocumentId;
        if (documentId <= 0 && DocumentList.SelectedItem is LegislationDocument document) documentId = document.Id;
        if (documentId <= 0)
        {
            SigfurDialog.Show(this, "Selecione um documento ou resultado.", "Central Técnica Offline", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await SetBusyAsync("Montando leitura integral do documento…", async () =>
        {
            PreviewBox.Text = await _service.GetDocumentTextAsync(documentId, 400_000);
            ReferenceText.Text = string.IsNullOrWhiteSpace(_currentDocumentTitle)
                ? "Leitura integral da fonte selecionada"
                : _currentDocumentTitle;
            PagePositionText.Text = "Documento inteiro — use a barra de rolagem";
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            PreviewBox.ScrollToHome();
        });
    }

    private void ExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreviewBox.Text)) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Texto UTF-8|*.txt",
            FileName = "CONSULTA_TECNICA_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            InitialDirectory = App.Paths.LegislationDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, PreviewBox.Text, new UTF8Encoding(true));
        IndexStatusText.Text = "Texto exportado: " + dialog.FileName;
    }

    private async void DeleteDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is not LegislationDocument document) return;
        if (document.IsBuiltIn)
        {
            SigfurDialog.Show(this,
                "Este documento já vem instalado com o SIGFUR e faz parte da biblioteca essencial. Ele não pode ser removido.",
                "Biblioteca essencial", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = SigfurDialog.Show(this,
            $"Remover “{document.Title}” da biblioteca e apagar o arquivo importado?",
            "Central Técnica Offline", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await SetBusyAsync("Removendo documento importado…", async () =>
        {
            await _service.DeleteDocumentAsync(document, deleteFile: true);
            _results.Clear();
            PreviewBox.Clear();
            ReferenceText.Text = string.Empty;
            ClearPagePosition();
            await RefreshDocumentsAsync();
        });
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Documentos oficiais e listas de links|*.pdf;*.html;*.htm;*.docx;*.odt;*.txt;*.url;*.csv;*.json;*.md|PDF|*.pdf|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync("Importando e indexando documentos…", () => _service.ImportFilesAsync(dialog.FileNames));
    }

    private async void ImportZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Pacotes ZIP|*.zip", Title = "Selecione um pacote com normas e manuais" };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync("Extraindo e indexando o pacote…", () => _service.ImportZipAsync(dialog.FileName));
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione uma pasta com normas, manuais e orientações", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync("Importando a pasta e atualizando o índice…", () => _service.ImportFolderAsync(dialog.FolderName));
    }

    private async Task ImportAsync(string status, Func<Task<int>> import)
    {
        await SetBusyAsync(status, async () =>
        {
            var count = await import();
            await RefreshDocumentsAsync();
            SigfurDialog.Show(this,
                count > 0
                    ? $"{count:N0} documento(s) importado(s) e indexado(s)."
                    : "Nenhum documento compatível foi encontrado ou baixado.",
                "Central Técnica Offline", MessageBoxButton.OK,
                count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async void Index_Click(object sender, RoutedEventArgs e) => await IndexAsync(false);
    private async void ForceIndex_Click(object sender, RoutedEventArgs e) => await IndexAsync(true);

    private async Task IndexAsync(bool force)
    {
        await SetBusyAsync(force ? "Reindexando toda a biblioteca…" : "Atualizando o índice…", async () =>
        {
            var progress = new Progress<string>(text => IndexStatusText.Text = text);
            await _service.IndexAllAsync(force, progress);
            await RefreshDocumentsAsync();
        });
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
        => ShellService.OpenPath(App.Paths.LegislationDocumentsDirectory);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
