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

    private readonly AssistantDocumentService _documents = App.AssistantDocuments;
    private AssistantSettings _settings = new();
    private AssistantConversationStore _conversation = new();
    private CancellationTokenSource? _requestCts;
    private bool _operatorCanceledRequest;
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
        Closed += (_, _) => _requestCts?.Cancel();
    }

    public ObservableCollection<AssistantMessageView> Messages { get; } = [];
    public ObservableCollection<AssistantAttachmentItem> Attachments { get; } = [];
    public ObservableCollection<AssistantPendingAction> PendingActions { get; } = [];
    public ObservableCollection<AssistantOperationalItem> OperationalItems { get; } = [];
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
            AddAssistantMessage("Sou o Assistente SIGFUR. Posso auditar boletins com o manual SIPPES, explicar divergências entre publicações e contracheques, preparar justificativas de exercícios anteriores e organizar pendências. As análises usam IA por API e apresentam fontes e dados faltantes. Abra Pendências para ver sua fila de trabalho.", save: false);
        }
        ApplySettingsToUi();
        await LoadInitialContextAsync();
        await RefreshUsageAsync();
        await RefreshOperationsAsync();
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
        var provider = AssistantCredentialService.DisplayName(_settings.Provider);
        var apiConfigured = _credentials.HasApiKey(provider);
        ConnectionText.Text = apiConfigured ? $"● {provider} • {_settings.Model}" : $"○ Configure a chave da {provider}";
        ConnectionText.Foreground = (System.Windows.Media.Brush)FindResource(apiConfigured ? "SuccessBrush" : "WarningBrush");
        DataPermissionText.Text = _settings.EnableLocalDataTools ? "Dados do SIGFUR disponíveis para consulta" : "Consultas aos módulos desativadas";
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
        UsageTokensText.Text = $"{usage.UncachedInputTokens:N0} entrada normal • {usage.CachedInputTokens:N0} entrada em cache • {usage.OutputTokens:N0} saída • {usage.WebSearchCalls} pesquisas • US$ {usage.PaidToolsCostUsd:0.0000} em ferramentas";
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    public void ShowOperations() => WorkspaceTabs.SelectedItem = OperationsTab;

    private void ShowOperations_Click(object sender, RoutedEventArgs e) => ShowOperations();

    private async void RefreshOperations_Click(object sender, RoutedEventArgs e) => await RefreshOperationsAsync();

    private async Task RefreshOperationsAsync()
    {
        try
        {
            RefreshOperationsButton.IsEnabled = false;
            var snapshot = await App.AssistantOperations.BuildSnapshotAsync();
            OperationalItems.Clear();
            foreach (var item in snapshot.Items) OperationalItems.Add(item);
            var attention = AssistantOperationsService.SelectAttention(snapshot.Items, DateTime.Today, _settings.DeadlineLookaheadDays);
            OperationsSummaryText.Text = $"{snapshot.Items.Count} pendências • {attention.Count} com atenção nos próximos {_settings.DeadlineLookaheadDays} dias • Atualizado às {snapshot.GeneratedAt:HH:mm}";
            OperationsWarningText.Text = string.Join(Environment.NewLine, snapshot.Warnings);
        }
        catch (Exception ex) { OperationsWarningText.Text = "Não foi possível atualizar a fila: " + ex.Message; }
        finally { RefreshOperationsButton.IsEnabled = true; }
    }

    private async void PlanOperations_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_settings.EnableLocalDataTools)
        {
            OperationsWarningText.Text = "Habilite as consultas aos módulos nas configurações para analisar a fila com IA.";
            return;
        }
        WorkspaceTabs.SelectedIndex = 0;
        PromptBox.Text = $"Consulte minhas pendências operacionais atuais, incluindo lembretes, pagamento e exercícios anteriores. Organize um plano de trabalho por prazo e urgência, com atenção aos próximos {_settings.DeadlineLookaheadDays} dias. Use consultar_pendencias_operacionais, informe o alcance da consulta e busque outras páginas se necessário. Não invente prazos para itens sem data nem marque tarefas como concluídas. Não é necessário pesquisar legislação para apenas ordenar os prazos cadastrados.";
        await SendCurrentAsync();
    }

    private void OpenOperationalItem_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is not AssistantOperationalItem item) return;
        Window module = item.Module switch
        {
            "Pagamento" => new PaymentRemindersWindow(App.Paths, App.Json),
            "Exercícios anteriores" => new Finance.ExercisePreviousWindow(App.MilitaryRepository, App.Paths, App.Log),
            _ => new Reminders.ReminderWindow(App.Reminders, int.TryParse(item.Id.Split(':').Last(), out var id) ? id : 0)
        };
        module.Owner = this;
        module.Closed += async (_, _) => await RefreshOperationsAsync();
        module.Show();
    }

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
        SetBusy(true, "Preparando consulta à IA...");

        try
        {
            _operatorCanceledRequest = false;
            _requestCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            SetBusy(true, "Analisando com IA e consultando as fontes necessárias...");
            var result = await _api.SendAsync(historySnapshot, prompt, Attachments.ToList(), _settings, _requestCts.Token);
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
                : "Consultas realizadas: " + string.Join(" • ", result.ToolSummaries);
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
            AddErrorMessage(_operatorCanceledRequest
                ? "Solicitação cancelada pelo operador."
                : "A solicitação passou do tempo limite e foi cancelada para não travar o SIGFUR. Tente novamente com uma pergunta menor ou verifique a internet/API.");
        }
        catch (Exception ex)
        {
            AddErrorMessage(BuildUserFriendlyAssistantError(ex));
            await App.Log.WriteAsync("Falha no Assistente SIGFUR.", ex);
        }
        finally
        {
            SetBusy(false);
            _requestCts?.Dispose();
            _requestCts = null;
            _operatorCanceledRequest = false;
            await SaveHistoryIfEnabledAsync();
            await RefreshUsageAsync();
        }
    }

    private static string BuildUserFriendlyAssistantError(Exception ex)
    {
        if (ex is OperationCanceledException or TaskCanceledException)
            return "A solicitação passou do tempo limite e foi cancelada para não travar o SIGFUR. Tente novamente com uma pergunta menor ou verifique a internet.";

        if (ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message))
            return ex.Message;

        return OpenAiAssistantService.FriendlyNetworkMessage(ex);
    }

    private void SetBusy(bool value, string? text = null)
    {
        _busy = value;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !value;
        AttachButton.IsEnabled = !value && _settings.EnableAttachments;
        PromptBox.IsEnabled = !value;
        if (!string.IsNullOrWhiteSpace(text)) BusyOverlay.Message = text;
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
        if (_busy) return;
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
    private void CancelRequest_Click(object sender, RoutedEventArgs e)
    {
        _operatorCanceledRequest = true;
        _requestCts?.Cancel();
    }

    private async void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
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
        if (_busy) return;
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
            _requestCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var revised = await _api.RewriteTextAsync(source, instruction, _settings, _requestCts.Token);
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
            _requestCts?.Dispose();
            _requestCts = null;
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
        if (action.Type.Equals("create_reminder", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (action.Payload.ContainsKey("saved_id"))
                {
                    ShowOperations();
                    return;
                }
                var record = JsonSerializer.Deserialize<ReminderRecord>(action.Payload["record"])
                             ?? throw new InvalidOperationException("Lembrete preparado inválido.");
                var editor = new Reminders.ReminderEditorWindow(App.Reminders, record) { Owner = this };
                if (editor.ShowDialog() == true)
                {
                    action.Payload["saved_id"] = editor.SavedId.ToString();
                    PendingActions.Remove(action);
                    await SaveHistoryIfEnabledAsync();
                    await RefreshOperationsAsync();
                    ConversationSubtitle.Text = "Lembrete salvo após revisão.";
                }
            }
            catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Lembrete", MessageBoxButton.OK, MessageBoxImage.Warning); }
            return;
        }
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
            var page = action.Payload.TryGetValue("page", out var pageText) && int.TryParse(pageText, out var parsedPage) ? parsedPage : 0;
            if (!FileOpenService.TryOpenFileAtPage(path, page, out var error))
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
