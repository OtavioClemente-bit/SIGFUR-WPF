using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;
using SIGFUR.Wpf.Views.Tools;

namespace SIGFUR.Wpf.Views;

public partial class AssistantChatWindow : Window, INotifyPropertyChanged
{
    private readonly AssistantStorageService _storage = App.AssistantStorage;
    private readonly AssistantCredentialService _credentials = App.AssistantCredentials;
    private readonly AssistantAttachmentService _attachmentService = App.AssistantAttachments;
    private readonly OpenAiAssistantService _api = App.Assistant;
    private readonly SigfurAssistantService _localAssistant = App.AssistantLocal;
    private readonly AssistantDocumentService _documents = App.AssistantDocuments;
    private AssistantSettings _settings = new();
    private AssistantConversationStore _conversation = new();
    private CancellationTokenSource? _requestCts;
    private bool _busy;
    private readonly string _initialPrompt;
    private readonly IReadOnlyList<string> _initialAttachmentPaths;

    public AssistantChatWindow(string initialPrompt = "", IReadOnlyList<string>? initialAttachmentPaths = null)
    {
        _initialPrompt = initialPrompt ?? string.Empty;
        _initialAttachmentPaths = initialAttachmentPaths ?? Array.Empty<string>();
        InitializeComponent();
        DataContext = this;
        App.UiState.Attach(this);
        Loaded += async (_, _) => await InitializeAsync();
    }

    public ObservableCollection<AssistantMessageView> Messages { get; } = [];
    public ObservableCollection<AssistantAttachmentItem> Attachments { get; } = [];
    public ObservableCollection<AssistantPendingAction> PendingActions { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task InitializeAsync()
    {
        _settings = await _storage.LoadSettingsAsync();
        RitexBox.Text = _settings.DiexRitex;
        _conversation = _settings.SaveHistoryLocally
            ? await _storage.LoadHistoryAsync()
            : new AssistantConversationStore();
        Messages.Clear();
        foreach (var message in _conversation.Messages) Messages.Add(new AssistantMessageView { Message = message });
        if (Messages.Count == 0)
        {
            AddAssistantMessage("Olá. Sou o Assistente SIGFUR. Posso consultar dados locais autorizados mesmo sem API: militares, carteira, contracheques, boletins, Índice por Pessoa, pastas, impressão e rotas. Quando precisar de texto livre ou interpretação avançada, uso a API configurada.", save: false);
        }
        ApplySettingsToUi();
        await LoadInitialContextAsync();
        await RefreshUsageAsync();
        ScrollToEnd();
    }

    private async Task LoadInitialContextAsync()
    {
        if (!string.IsNullOrWhiteSpace(_initialPrompt)) PromptBox.Text = _initialPrompt.Trim();
        foreach (var path in _initialAttachmentPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 25 * 1024 * 1024) continue;
                var item = new AssistantAttachmentItem { Path = path, SizeBytes = info.Length, Status = "Lendo conteúdo..." };
                Attachments.Add(item);
                item.ExtractedText = await _attachmentService.ExtractAsync(path, _settings.MaxAttachmentCharacters);
                item.Status = $"Pronto • {item.ExtractedText.Length:N0} caracteres";
            }
            catch (Exception ex)
            {
                var item = Attachments.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (item is not null) item.Status = "Erro: " + ex.Message;
            }
        }
        ApplySettingsToUi();
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    private void ApplySettingsToUi()
    {
        var configured = _credentials.HasApiKey();
        ConnectionText.Text = configured ? $"● Modo híbrido • API pronta • {_settings.Model}" : "● Modo local pronto • API opcional";
        ConnectionText.Foreground = (System.Windows.Media.Brush)FindResource(configured ? "SuccessBrush" : "PrimaryDarkBrush");
        DataPermissionText.Text = _settings.EnableLocalDataTools ? "Consultas locais: permitidas (somente leitura)" : "Consultas locais: desativadas";
        AttachButton.IsEnabled = _settings.EnableAttachments;
        NoAttachmentsText.Visibility = Attachments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoActionsText.Visibility = PendingActions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshUsageAsync()
    {
        var usage = await _storage.GetCurrentMonthUsageAsync(_settings);
        UsageValueText.Text = usage.Display;
        UsageRequestText.Text = $"{usage.Requests:N0} solicitação(ões)";
        UsageProgress.Value = usage.Percent;
        UsageTokensText.Text = $"{usage.InputTokens:N0} tokens de entrada • {usage.OutputTokens:N0} de saída";
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async Task SendCurrentAsync()
    {
        if (_busy) return;
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        var notReady = Attachments.Where(x => !x.IsReady).ToList();
        if (notReady.Count > 0)
        {
            SigfurDialog.Show(this, "Aguarde a leitura dos anexos ou remova os arquivos que apresentaram erro.", "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var historySnapshot = _conversation.Messages.ToList();
        var userMessage = new AssistantConversationMessage
        {
            Role = "user",
            Content = prompt,
            CreatedAt = DateTime.Now,
            AttachmentNames = Attachments.Select(x => x.FileName).ToList()
        };
        _conversation.Messages.Add(userMessage);
        Messages.Add(new AssistantMessageView { Message = userMessage });
        PromptBox.Clear();
        ScrollToEnd();
        SetBusy(true, "Consultando base local...");

        try
        {
            _requestCts = new CancellationTokenSource();
            AssistantApiResult? result = null;

            var hasApiKey = _credentials.HasApiKey();

            // Perguntas operacionais de BI/ADT/contracheque/carteira passam primeiro pela base local.
            // A IA fica para texto livre e interpretação complementar, sem inventar resultado local.
            if (_settings.EnableLocalDataTools && Attachments.Count == 0 && (!hasApiKey || ShouldUseDeterministicLocalMode(prompt)))
                result = await _localAssistant.TryHandleAsync(prompt, _settings, _requestCts.Token);

            if (result is null)
            {
                if (!hasApiKey)
                    result = _localAssistant.BuildNoApiGuidance(prompt);
                else
                {
                    SetBusy(true, "Consultando IA...");
                    result = await _api.SendAsync(historySnapshot, prompt, Attachments.ToList(), _settings, _requestCts.Token);
                }
            }

            SetBusy(true, "Montando resposta...");
            if (_settings.EnableLocalDataTools)
                await _localAssistant.EnrichConversationLinksAsync(prompt, result, _requestCts.Token);

            var safeActions = result.PendingActions
                .Where(x => x is not null)
                .GroupBy(x => string.Join("|", x.Type, x.ConversationLinkLabel, string.Join(";", x.FilePaths), string.Join(";", x.Payload.Select(kv => kv.Key + "=" + kv.Value))))
                .Select(x => x.First())
                .ToList();

            var assistantMessage = new AssistantConversationMessage
            {
                Role = "assistant",
                Content = result.Text,
                CreatedAt = DateTime.Now,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                EstimatedCostBrl = result.EstimatedCostBrl,
                Actions = safeActions
            };
            _conversation.Messages.Add(assistantMessage);
            Messages.Add(new AssistantMessageView { Message = assistantMessage });
            foreach (var action in safeActions) PendingActions.Add(action);
            LastToolText.Text = result.ToolSummaries.Count == 0
                ? "Resposta gerada sem consulta adicional aos módulos locais."
                : "Fontes locais: " + string.Join(" • ", result.ToolSummaries);
            Attachments.Clear();
            ApplySettingsToUi();
            await SaveHistoryIfEnabledAsync();
            await RefreshUsageAsync();
            ScrollToEnd();

            // O assistente nunca abre PDF, carteira, pasta ou rota automaticamente.
            // Ele apenas deixa as ações como links/botões para o operador abrir se precisar.
        }
        catch (OperationCanceledException)
        {
            AddErrorMessage("Solicitação cancelada pelo operador.");
        }
        catch (Exception ex)
        {
            AddErrorMessage(ex.Message);
            await App.Log.WriteAsync("Falha no Assistente SIGFUR.", ex);
        }
        finally
        {
            SetBusy(false);
            _requestCts?.Dispose();
            _requestCts = null;
            await SaveHistoryIfEnabledAsync();
        }
    }

    private static bool ShouldUseDeterministicLocalMode(string prompt)
    {
        var normalized = AssistantIntentDetector.Normalize(prompt);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (AssistantIntentDetector.ContainsAny(normalized,
                "crie", "criar", "redija", "redigir", "escreva", "monta texto", "montar texto",
                "mensagem", "diex", "oficio", "ofício", "minuta", "melhore o texto"))
            return false;

        // Perguntas gerais não podem virar "pesquisa de militar" só porque têm "qual", "tem" ou "sobre".
        // O modo determinístico fica para ações e consultas operacionais do SIGFUR.
        if (AssistantIntentDetector.ContainsAny(normalized,
                "lei", "legislacao", "legislação", "portaria", "decreto", "norma", "artigo", "base legal", "fundamento",
                "como funciona", "quem tem direito", "faz jus", "pode receber", "deve receber"))
            return false;

        return AssistantIntentDetector.ContainsAny(normalized,
            "nota", "boletim", "bi", "adt", "aditamento", "furriel", "indice", "índice",
            "contracheque", "contra cheque", "contra-cheque", "ficha financeira", "carteira", "pasta", "rota", "maps",
            "cpf", "prec", "identidade", "militar", "abre", "abrir", "imprime", "imprimir",
            "despesa a anular", "auxilio transporte", "auxílio transporte", "ferias", "férias", "gratificacao", "gratificação");
    }

    private void SetBusy(bool value, string? text = null)
    {
        _busy = value;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !value;
        AttachButton.IsEnabled = !value && _settings.EnableAttachments;
        PromptBox.IsEnabled = !value;
        if (!string.IsNullOrWhiteSpace(text)) BusyText.Text = text;
    }

    private async Task SaveHistoryIfEnabledAsync()
    {
        if (_settings.SaveHistoryLocally) await _storage.SaveHistoryAsync(_conversation);
    }

    private void AddAssistantMessage(string text, bool save)
    {
        var message = new AssistantConversationMessage { Role = "assistant", Content = text, CreatedAt = DateTime.Now };
        Messages.Add(new AssistantMessageView { Message = message });
        if (save) _conversation.Messages.Add(message);
    }

    private void AddErrorMessage(string text)
    {
        var message = new AssistantConversationMessage { Role = "assistant", Content = text, CreatedAt = DateTime.Now, IsError = true };
        Messages.Add(new AssistantMessageView { Message = message });
        _conversation.Messages.Add(message);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Messages.Count > 0) ConversationList.ScrollIntoView(Messages[^1]);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _ = SendCurrentAsync();
        }
    }

    private void QuickPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt }) return;
        PromptBox.Text = prompt;
        PromptBox.Focus();
        PromptBox.Select(prompt.IndexOf('[') >= 0 ? prompt.IndexOf('[') : prompt.Length, 0);
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.EnableAttachments) return;
        var dialog = new OpenFileDialog { Filter = AssistantAttachmentService.Filter, Multiselect = true, Title = "Anexar documentos ao Assistente SIGFUR" };
        if (dialog.ShowDialog(this) != true) return;
        var availableSlots = Math.Max(0, 5 - Attachments.Count);
        var selectedPaths = dialog.FileNames
            .Where(path => !Attachments.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(availableSlots)
            .ToList();
        if (selectedPaths.Count < dialog.FileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            SigfurDialog.Show(this, "O Assistente SIGFUR aceita até 5 anexos por solicitação. Os arquivos excedentes não foram adicionados.", "Assistente SIGFUR — Anexos", MessageBoxButton.OK, MessageBoxImage.Information);

        foreach (var path in selectedPaths)
        {
            var info = new FileInfo(path);
            if (info.Length > 25 * 1024 * 1024)
            {
                SigfurDialog.Show(this, $"O arquivo {info.Name} excede o limite local de 25 MB.", "Assistente SIGFUR — Anexos", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            var item = new AssistantAttachmentItem { Path = path, SizeBytes = info.Length, Status = "Lendo conteúdo..." };
            Attachments.Add(item);
            NoAttachmentsText.Visibility = Visibility.Collapsed;
            try
            {
                item.ExtractedText = await _attachmentService.ExtractAsync(path, _settings.MaxAttachmentCharacters);
                item.Status = $"Pronto • {item.ExtractedText.Length:N0} caracteres";
            }
            catch (Exception ex) { item.Status = "Erro: " + ex.Message; }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Attachments)));
            ConversationList.Items.Refresh();
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AssistantAttachmentItem item }) Attachments.Remove(item);
        ApplySettingsToUi();
    }

    private void ClearAttachments_Click(object sender, RoutedEventArgs e)
    {
        Attachments.Clear();
        ApplySettingsToUi();
    }

    private void ClearPrompt_Click(object sender, RoutedEventArgs e) => PromptBox.Clear();
    private void CancelRequest_Click(object sender, RoutedEventArgs e) => _requestCts?.Cancel();

    private async void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation.Messages.Count > 0 && SigfurDialog.Show(this, "Iniciar uma nova conversa? O histórico atual será limpo deste computador.", "Assistente SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _conversation = new AssistantConversationStore();
        Messages.Clear();
        Attachments.Clear();
        PendingActions.Clear();
        await _storage.ClearHistoryAsync();
        AddAssistantMessage("Nova conversa iniciada. O que você precisa consultar ou preparar?", save: false);
        ApplySettingsToUi();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private async void OpenSettings()
    {
        var window = new AssistantSettingsWindow(_storage, _credentials, _api) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings = await _storage.LoadSettingsAsync();
            ApplySettingsToUi();
            await RefreshUsageAsync();
        }
    }

    private void CopyLastAnswer_Click(object sender, RoutedEventArgs e)
    {
        var last = _conversation.Messages.LastOrDefault(x => x.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && !x.IsError);
        if (last is null) return;
        Clipboard.SetText(last.Content);
        ConversationSubtitle.Text = "Última resposta copiada para a área de transferência.";
    }

    private async void ExportDiex_Click(object sender, RoutedEventArgs e)
    {
        var last = _conversation.Messages.LastOrDefault(x => x.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && !x.IsError);
        var content = string.IsNullOrWhiteSpace(DraftEditorBox.Text) ? last?.Content ?? string.Empty : DraftEditorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            SigfurDialog.Show(this, "Ainda não existe texto para exportar.", "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var suggested = _documents.GetSuggestedPath("DIEx");
        var dialog = new SaveFileDialog
        {
            Filter = "Documento Word|*.docx",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested),
            Title = "Exportar resposta como minuta de DIEx"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var path = await _documents.ExportDocxAsync(dialog.FileName, "MINUTA DE DIEx", content);
            if (SigfurDialog.Show(this, $"Minuta salva com sucesso:\n{path}\n\nDeseja abrir o documento?", "Assistente SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                ShellService.OpenPath(path);
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Exportar DIEx", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RitexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = RitexBox.Text.Trim().ToUpperInvariant();
        if (string.Equals(_settings.DiexRitex, value, StringComparison.Ordinal)) return;
        _settings.DiexRitex = value;
        await _storage.SaveSettingsAsync(_settings);
    }

    private string LastAssistantText()
        => _conversation.Messages.LastOrDefault(x => x.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && !x.IsError)?.Content ?? string.Empty;

    private void InsertIntoEditor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (DraftEditorBox.SelectionLength > 0)
        {
            DraftEditorBox.SelectedText = value;
        }
        else
        {
            var index = Math.Clamp(DraftEditorBox.CaretIndex, 0, DraftEditorBox.Text.Length);
            DraftEditorBox.Text = DraftEditorBox.Text.Insert(index, value);
            DraftEditorBox.CaretIndex = index + value.Length;
        }
        DraftEditorBox.Focus();
    }

    private void PasteDraft_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText()) InsertIntoEditor(Clipboard.GetText());
    }

    private void InsertLastAnswer_Click(object sender, RoutedEventArgs e)
    {
        var text = LastAssistantText();
        if (string.IsNullOrWhiteSpace(text))
        {
            SigfurDialog.Show(this, "Ainda não existe resposta do assistente para inserir.", "Editor de texto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        InsertIntoEditor(text);
        WorkspaceTabs.SelectedIndex = 1;
    }

    private async void ReviewDraftSelection_Click(object sender, RoutedEventArgs e)
        => await ReviewEditorAsync("Corrija integralmente o português brasileiro: ortografia, acentuação, concordância, regência e pontuação. Preserve rigorosamente fatos, nomes, números, siglas, datas e referências.");

    private async void FormalizeDraftSelection_Click(object sender, RoutedEventArgs e)
        => await ReviewEditorAsync("Reescreva em linguagem administrativa militar formal, clara, objetiva e respeitosa. Corrija ortografia, acentuação e pontuação sem alterar os fatos, nomes, números, datas, siglas e referências.");

    private async Task ReviewEditorAsync(string instruction)
    {
        if (_busy) return;
        var selectionOnly = DraftEditorBox.SelectionLength > 0;
        var source = selectionOnly ? DraftEditorBox.SelectedText : DraftEditorBox.Text;
        if (string.IsNullOrWhiteSpace(source))
        {
            SigfurDialog.Show(this, "Cole ou digite um texto. Para alterar apenas uma parte, selecione o trecho antes de clicar.", "Editor de texto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, selectionOnly ? "Revisando somente o trecho selecionado..." : "Revisando o texto completo...");
        DraftEditorBox.IsEnabled = false;
        try
        {
            var revised = await _api.RewriteTextAsync(source, instruction, _settings);
            if (selectionOnly) DraftEditorBox.SelectedText = revised;
            else
            {
                DraftEditorBox.Text = revised;
                DraftEditorBox.CaretIndex = DraftEditorBox.Text.Length;
            }
            await RefreshUsageAsync();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Revisão de texto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DraftEditorBox.IsEnabled = true;
            SetBusy(false);
            DraftEditorBox.Focus();
        }
    }

    private async void AddDiexClosing_Click(object sender, RoutedEventArgs e)
    {
        var profile = await App.Settings.LoadProfileAsync();
        var operatorName = string.Join(" ", new[] { profile.Rank, profile.Operator }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(operatorName)) operatorName = "[POSTO/GRADUAÇÃO E NOME DO OPERADOR]";
        var ritex = string.IsNullOrWhiteSpace(RitexBox.Text) ? "[RITEX]" : RitexBox.Text.Trim().ToUpperInvariant();
        var closing = $"\n\nColoco-me à disposição para eventuais esclarecimentos por intermédio do {operatorName}, pelo RITEX {ritex}.";
        InsertIntoEditor(closing);
    }


    private void ClearActions_Click(object sender, RoutedEventArgs e)
    {
        PendingActions.Clear();
        ApplySettingsToUi();
    }

    private void CopyActionPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AssistantPendingAction action }) return;
        var path = action.FirstFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SigfurDialog.Show(this, "Esta ação não possui caminho local para copiar.", "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Clipboard.SetText(path);
        ConversationSubtitle.Text = "Caminho copiado para a área de transferência.";
    }

    private void ExecuteAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AssistantPendingAction action }) ExecutePendingAction(action, alreadyConfirmed: false);
    }

    private async void ExecutePendingAction(AssistantPendingAction action, bool alreadyConfirmed)
    {
        if (action.Type.Equals("print", StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.ConfirmOperationalActions && !alreadyConfirmed && SigfurDialog.Show(this,
                    $"Abrir a fila de impressão com {action.FilePaths.Count} arquivo(s)?\n\nA fila permitirá escolher impressora, cópias e revisar tudo antes de imprimir.",
                    "Assistente SIGFUR — Impressão", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var files = action.FilePaths.Where(File.Exists).ToList();
            if (files.Count == 0)
            {
                SigfurDialog.Show(this, "Os arquivos preparados não estão mais disponíveis.", "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
                PendingActions.Remove(action);
                return;
            }
            var window = new PrintQueueWindow(files, action.Copies) { Owner = this };
            window.Show();
            window.Activate();
            PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }

        if (action.Type.Equals("open_file", StringComparison.OrdinalIgnoreCase))
        {
            var path = action.FilePaths.FirstOrDefault() ?? string.Empty;
            if (!FileOpenService.TryOpenFile(path, out var error))
                SigfurDialog.Show(this, error, "Assistente SIGFUR — Abrir arquivo", MessageBoxButton.OK, MessageBoxImage.Warning);
            else PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }

        if (action.Type.Equals("open_folder", StringComparison.OrdinalIgnoreCase))
        {
            var path = action.FilePaths.FirstOrDefault() ?? string.Empty;
            if (!FileOpenService.TryOpenFolder(path, out var error))
                SigfurDialog.Show(this, error, "Assistente SIGFUR — Abrir pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
            else PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }

        if (action.Type.Equals("reveal_file", StringComparison.OrdinalIgnoreCase))
        {
            var path = action.FilePaths.FirstOrDefault() ?? string.Empty;
            if (!FileOpenService.TryReveal(path, out var error))
                SigfurDialog.Show(this, error, "Assistente SIGFUR — Mostrar arquivo", MessageBoxButton.OK, MessageBoxImage.Warning);
            else PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }

        if (action.Type.Equals("open_wallet", StringComparison.OrdinalIgnoreCase))
        {
            if (!action.Payload.TryGetValue("military_id", out var idText) || !int.TryParse(idText, out var id))
            {
                SigfurDialog.Show(this, "A ação não trouxe o ID do militar.", "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var military = await App.MilitaryRepository.GetByIdAsync(id);
            if (military is null)
            {
                SigfurDialog.Show(this, "Militar não localizado no banco.", "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new MilitaryWalletWindow(App.MilitaryRepository, App.Paystubs, military) { Owner = this };
            window.Show();
            window.Activate();
            PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }

        if (action.Type.Equals("open_url", StringComparison.OrdinalIgnoreCase))
        {
            var url = action.Payload.TryGetValue("url", out var value) ? value : string.Empty;
            if (!FileOpenService.TryOpenUrl(url, out var error))
                SigfurDialog.Show(this, error, "Assistente SIGFUR — Abrir rota", MessageBoxButton.OK, MessageBoxImage.Warning);
            else PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }

        if (action.Type.Equals("copy_text", StringComparison.OrdinalIgnoreCase))
        {
            var text = action.Payload.TryGetValue("text", out var value) ? value : string.Empty;
            Clipboard.SetText(text);
            PendingActions.Remove(action);
            ApplySettingsToUi();
            return;
        }
    }

    private void OpenExports_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.GeneratedDocumentsDirectory);
}
