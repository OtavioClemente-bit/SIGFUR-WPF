using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Finance;

public partial class BizurometroSpedWindow : Window
{
    private readonly string _stateFile = Path.Combine(App.Paths.DataDirectory, "bizurometro_sped_estado_wpf.json");
    private readonly string _processesFile = Path.Combine(App.Paths.DataDirectory, "bizurometro_sped_processos_wpf.json");
    private readonly ObservableCollection<CheckRow> _checks = [];
    private readonly ObservableCollection<BizuMilitaryOption> _relation = [];
    private readonly ObservableCollection<SavedProcess> _savedProcesses = [];
    private readonly List<BizuMilitaryOption> _allMilitary = [];

    private string _diexFile = string.Empty;
    private string _bulletinFile = string.Empty;
    private string _reportFile = string.Empty;
    private string _pfFile = string.Empty;
    private string _financialStatementFiles = string.Empty;
    private string _paystubFiles = string.Empty;
    private string _otherFile = string.Empty;
    private string _spedProcessFile = string.Empty;
    private string _lastGeneratedDiex = string.Empty;
    private string _lastGeneratedDispatch = string.Empty;
    private DateTimeOffset? _processAutuatedAt;
    private bool _loading = true;
    private bool _forceGenerateTexts;
    private string? _currentProcessId;
    private bool _dirty;
    private string _configuredFlow = string.Empty;
    private bool _configuringFlow;

    private static readonly string[] Steps =
    [
        "1. DEFINIR FINALIDADE, DESTINATÁRIO E ORIGEM\nEscolha a finalidade, informe para quem o DIEx será enviado e confira os documentos aplicáveis. O remetente será o próprio usuário autenticado.",
        "2. CRIAR O DIEx NO SPED 3.0 DA 4ª RM\nAcesse http://sped3.4rm.eb.mil.br/#/, preencha destinatário, assunto, classificação 002.01, anexos e corpo do texto.",
        "3. SALVAR SEM FINALIZAR\nClique em Salvar. O SPED deve abrir a minuta do documento; não despache nem encaminhe o DIEx isoladamente.",
        "4. ABRIR ASSINAR/PROTOCOLAR\nNa visualização da minuta, clique em Assinar/Protocolar.",
        "5. ESCOLHER ASSINATURA ELETRÔNICA\nSelecione Assinatura Eletrônica e marque Não encaminhar automaticamente.",
        "6. ASSINAR COM A SENHA ELETRÔNICA\nClique em Assinar, informe a senha eletrônica própria de assinatura e confirme.",
        "7. CRIAR O PROCESSO\nUse o botão 2. Criar processo; o SIGFUR entrará novamente no SPED e abrirá a criação do processo.",
        "8. INCLUIR O DIEx PELO NÚMERO\nO SIGFUR pesquisará o documento de origem usando exatamente o número guardado ao criar o DIEx.",
        "9. SALVAR E AUTUAR\nO processo será salvo, confirmado e autuado conforme o fluxo ensinado no ZIP.",
        "10. REDIGIR O DESPACHO\nAntes do encaminhamento, o SIGFUR criará uma única minuta, preencherá assunto e corpo e indicará o assinante escolhido.",
        "11. ENCAMINHAR E CONFIRMAR\nO SIGFUR encaminhará ao destinatário escolhido, diferente do assinante do despacho, usará um motivo curto, salvará e confirmará."
    ];

    public BizurometroSpedWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);

        StepsList.ItemsSource = Steps.Select((text, index) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 9),
            FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal
        });
        ChecklistList.ItemsSource = _checks;
        RelationGrid.ItemsSource = _relation;
        SavedProcessBox.ItemsSource = _savedProcesses;

        FlowBox.SelectedIndex = 0;
        BeneficiaryModeBox.SelectedIndex = 0;
        BulletinDatePicker.SelectedDate = DateTime.Today;
        ConfigureFlow(resetReport: true);
        BuildChecks();

        Loaded += async (_, _) =>
        {
            await LoadMilitaryOptionsAsync();
            await LoadProcessesAsync();
            _loading = false;
            Refresh();
            _dirty = false;
            RefreshProcessHeader();
        };
        Closing += (_, args) =>
        {
            if (!_dirty) return;
            var answer = SigfurDialog.Show(this,
                "Há alterações neste processo que ainda não foram salvas.\n\nDeseja sair e descartar essas alterações?",
                "Alterações não salvas", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) args.Cancel = true;
        };
    }

    private string Flow => string.IsNullOrWhiteSpace(EditableComboValue(FlowBox))
        ? "Inconsistência Bancária"
        : EditableComboValue(FlowBox);

    private static string EditableComboValue(ComboBox combo)
    {
        if (combo.IsEditable)
        {
            combo.ApplyTemplate();
            if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor
                && !string.IsNullOrWhiteSpace(editor.Text))
                return editor.Text.Trim();
        }
        if (combo.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString()?.Trim() ?? string.Empty;
        return combo.Text?.Trim() ?? string.Empty;
    }

    private static void SetEditableComboValue(ComboBox combo, string? value, bool selectMatchingItem = false)
    {
        var text = value?.Trim() ?? string.Empty;
        combo.SelectedItem = selectMatchingItem
            ? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            : null;
        if (combo.SelectedItem is null) combo.SelectedIndex = -1;
        combo.Text = text;
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
        {
            editor.Text = text;
            editor.CaretIndex = text.Length;
        }
    }

    private void OpenSpedLearning_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Gravador de caminho aberto. Faça as etapas manualmente e finalize gerando o ZIP.";
        var window = new SpedProcessLearningWindow { Owner = this };
        window.ShowDialog();
        StatusText.Text = "Gravador fechado. Se você finalizou a gravação, o ZIP está pronto para envio.";
    }

    private async void CreateDiex_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecipientBox.Text))
        {
            SigfurDialog.Show(this, "Informe para quem o DIEx será enviado.", "Destinatário do DIEx", MessageBoxButton.OK, MessageBoxImage.Information);
            RecipientBox.Focus();
            return;
        }
        _forceGenerateTexts = true;
        try { Refresh(); }
        finally { _forceGenerateTexts = false; }

        var attachments = ActiveAttachmentPaths(GetDocumentProfile());
        var missingAttachments = attachments.Where(path => !File.Exists(path)).ToList();
        if (missingAttachments.Count > 0)
        {
            SigfurDialog.Show(this, "Um ou mais anexos selecionados não foram encontrados. Selecione novamente os arquivos antes de continuar.",
                "Anexos do SPED", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var attachmentTotal = attachments.Sum(path => new FileInfo(path).Length);
        if (attachmentTotal > GratificationService.SpedAttachmentLimitBytes)
        {
            SigfurDialog.Show(this,
                $"Os anexos somam {attachmentTotal / 1_000_000d:0.00} MB. O SPED aceita no máximo 10 MB no total; reduza ou substitua os arquivos antes de continuar.",
                "Limite de anexos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var draft = new SpedDiexDraft
        {
            Subject = GeneratedSubject(),
            BodyHtml = PlainTextToHtml(DiexText.Text),
            AttachmentPaths = attachments,
            FillDocumentPurpose = false
        };
        SavedProcess savedProcess;
        try { savedProcess = await SaveCurrentProcessAsync(); }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Não foi possível preparar a pasta local antes de criar o DIEx.", ex);
            SigfurDialog.Show(this, "Não foi possível salvar o registro local. O DIEx não será iniciado.", "SPED Processos", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var storageDirectory = Path.Combine(App.Paths.SpedProcessesDirectory, savedProcess.Id, "documentos");
        var window = new SpedProcessAutomationWindow(
            SpedProcessAutomationMode.CreateDiex,
            draft, RecipientBox.Text.Trim(), ProcessInterestedBox.Text.Trim(),
            storageDirectory: storageDirectory) { Owner = this };
        window.ShowDialog();
        var result = window.AutomationResult;
        if (result is null) return;

        if (!string.IsNullOrWhiteSpace(result.DocumentNumber))
        {
            DiexNumberBox.Text = result.DocumentNumber;
            _processAutuatedAt = null;
            _dirty = true;
        }
        if (!string.IsNullOrWhiteSpace(result.DocumentPdfPath) && File.Exists(result.DocumentPdfPath))
        {
            _diexFile = result.DocumentPdfPath;
            DiexFileText.Text = FileLabel(_diexFile);
            _dirty = true;
        }

        try { await SaveCurrentProcessAsync(); }
        catch (Exception ex) { await App.Log.WriteAsync("O DIEx foi tratado no SPED, mas o processo local não pôde ser salvo.", ex); }
        if (result.Signed && File.Exists(_diexFile))
        {
            StatusText.Text = $"DIEx nº {result.DocumentNumber} criado e salvo em PDF. Confira o arquivo antes de usar 2. Criar processo.";
            ShellService.OpenPath(_diexFile);
            SigfurDialog.Show(this,
                "O PDF do DIEx foi salvo e aberto para conferência. Verifique o documento antes de criar o processo.",
                "Conferir DIEx", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusText.Text = result.Message;
        }
    }

    private async void CreateProcess_Click(object sender, RoutedEventArgs e)
    {
        var documentNumber = DiexNumberBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            SigfurDialog.Show(this,
                "Crie primeiro o DIEx ou informe o número do DIEx que o SPED gerou.",
                "Número do DIEx", MessageBoxButton.OK, MessageBoxImage.Information);
            DiexNumberBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_diexFile) || !File.Exists(_diexFile))
        {
            SigfurDialog.Show(this,
                "O PDF do DIEx ainda não está salvo neste processo. Crie o DIEx ou vincule o PDF e confira-o antes de continuar.",
                "Conferência obrigatória", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(ProcessInterestedBox.Text))
        {
            SigfurDialog.Show(this, "Informe o interessado que aparecerá na capa do processo.", "Interessado do processo", MessageBoxButton.OK, MessageBoxImage.Information);
            ProcessInterestedBox.Focus();
            return;
        }

        _forceGenerateTexts = true;
        try { Refresh(); }
        finally { _forceGenerateTexts = false; }

        var processDraft = new SpedDiexDraft
        {
            Subject = GeneratedSubject(),
            DispatchSubject = $"Despacho — {SanitizeSubjectText(Flow)}",
            DispatchBodyHtml = PlainTextToHtml(DispatchText.Text)
        };
        SavedProcess savedProcess;
        try { savedProcess = await SaveCurrentProcessAsync(); }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Não foi possível preparar a pasta local antes de criar o processo no SPED.", ex);
            SigfurDialog.Show(this, "Não foi possível salvar o registro local. O processo no SPED não será iniciado.", "SPED Processos", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var storageDirectory = Path.Combine(App.Paths.SpedProcessesDirectory, savedProcess.Id, "documentos");
        var window = new SpedProcessAutomationWindow(
            SpedProcessAutomationMode.CreateProcess,
            processDraft, RecipientBox.Text.Trim(), ProcessInterestedBox.Text.Trim(), documentNumber, Flow,
            storageDirectory) { Owner = this };
        window.ShowDialog();
        var result = window.AutomationResult;
        if (result is null) return;

        if (result.Autuated)
            _processAutuatedAt = DateTimeOffset.Now;
        if (!string.IsNullOrWhiteSpace(result.ProcessPdfPath) && File.Exists(result.ProcessPdfPath))
        {
            _spedProcessFile = result.ProcessPdfPath;
            ProcessFileText.Text = FileLabel(_spedProcessFile);
            _dirty = true;
        }
        try { await SaveCurrentProcessAsync(); }
        catch (Exception ex) { await App.Log.WriteAsync("O processo foi tratado no SPED, mas o registro local não pôde ser salvo.", ex); }

        StatusText.Text = result.Forwarded
            ? $"Processo criado, autuado e encaminhado com o DIEx nº {documentNumber}. Finalizado."
            : result.Message;
    }

    private void CopyDiexNumber_Click(object sender, RoutedEventArgs e)
    {
        var number = DiexNumberBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(number))
        {
            StatusText.Text = "Ainda não há número de DIEx salvo.";
            return;
        }
        Clipboard.SetText(number);
        StatusText.Text = $"Número do DIEx {number} copiado.";
    }

    private static string PlainTextToHtml(string text)
    {
        var encoded = WebUtility.HtmlEncode(text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join(string.Empty, encoded.Split("\n\n", StringSplitOptions.None)
            .Select(paragraph => $"<p>{paragraph.Replace("\n", "<br>")}</p>"));
    }

    private bool IsRelationMode => BeneficiaryModeBox.SelectedIndex == 1;

    private async Task LoadMilitaryOptionsAsync()
    {
        try
        {
            var activeTask = App.MilitaryRepository.GetAllAsync();
            var licensedTask = App.LicensedTransferred.GetAllAsync(true);
            await Task.WhenAll(activeTask, licensedTask);

            _allMilitary.Clear();
            _allMilitary.AddRange(activeTask.Result.Select(item => BizuMilitaryOption.FromMilitary(item, "Ativo")));
            _allMilitary.AddRange(licensedTask.Result.Select(item => BizuMilitaryOption.FromLicensed(item, "Licenciado/Transferido")));

            var unique = _allMilitary
                .GroupBy(item => string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(item.Cpf))
                    ? $"{Normalize(item.Name)}|{Normalize(item.Source)}"
                    : $"{MilitaryFormatting.Digits(item.Cpf)}|{Normalize(item.Source)}")
                .Select(group => group.First())
                .OrderBy(item => MilitaryRankService.GetOrder(item.Rank))
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _allMilitary.Clear();
            _allMilitary.AddRange(unique);
            MilitaryPickerBox.ItemsSource = _allMilitary;
        }
        catch (Exception ex)
        {
            _ = App.Log.WriteAsync("Não foi possível carregar o efetivo no SPED Processos.", ex);
            StatusText.Text = "O efetivo não pôde ser carregado. Ainda é possível preencher a identificação manualmente.";
        }
    }

    private void ConfigureFlow(bool resetReport)
    {
        _configuringFlow = true;
        try
        {
            var profile = GetDocumentProfile();
            var previousReport = EditableComboValue(ReportBox);
            var previousPayment = EditableComboValue(PfBox);
            ReportBox.ItemsSource = profile.OriginChoices;
            SetEditableComboValue(ReportBox,
                resetReport || string.IsNullOrWhiteSpace(previousReport) ? profile.DefaultOrigin : previousReport);

            OriginReferenceLabel.Text = profile.OriginReferenceLabel;
            PublicationReferencePanel.Visibility = profile.UsePublication ? Visibility.Visible : Visibility.Collapsed;
            PaymentReferencePanel.Visibility = profile.UsePaymentDocument ? Visibility.Visible : Visibility.Collapsed;
            PublicationFilePanel.Visibility = profile.UsePublication ? Visibility.Visible : Visibility.Collapsed;
            PaymentFilePanel.Visibility = profile.UsePaymentDocument ? Visibility.Visible : Visibility.Collapsed;
            PublicationReferenceLabel.Text = profile.PublicationReferenceLabel;
            PaymentReferenceLabel.Text = profile.PaymentReferenceLabel;
            PfBox.ItemsSource = new[]
            {
                "GRU",
                "Comprovante de depósito",
                "Forma de ressarcimento",
                "Comprovante de recolhimento",
                "PF / consulta financeira",
                "Outro documento financeiro"
            };
            SetEditableComboValue(PfBox, previousPayment);
            UpdateOriginFileUi(profile);
            PickPublicationFileButton.Content = profile.PublicationFileButton + " (um ou mais)";
            UpdatePaymentFileUi(profile);
            DocumentsHelpText.Text = profile.Guidance + " Você pode selecionar vários arquivos em cada categoria.";
            _configuredFlow = Normalize(Flow);
        }
        finally { _configuringFlow = false; }
    }

    private void UpdateOriginFileUi(DocumentProfile? profile = null)
    {
        profile ??= GetDocumentProfile();
        var kind = OriginAttachmentKind();
        PickOriginFileButton.Content = string.IsNullOrWhiteSpace(EditableComboValue(ReportBox))
            ? profile.OriginFileButton + " (um ou mais)"
            : $"Adicionar {kind.ToLowerInvariant()} (um ou mais)";
        ReportFileText.ToolTip = $"Arquivo(s) de: {OriginDocumentDescription()}";
    }

    private string OriginAttachmentKind()
    {
        var selected = (ReportBox.SelectedItem as string ?? EditableComboValue(ReportBox)).Trim();
        if (string.IsNullOrWhiteSpace(selected)) return "Documento principal";
        if (selected.Equals("Boletim", StringComparison.OrdinalIgnoreCase)) return "Boletim Interno";
        if (selected.Contains("ficha financeira", StringComparison.OrdinalIgnoreCase)) return "Ficha financeira";
        if (selected.Contains("contracheque", StringComparison.OrdinalIgnoreCase)) return "Contracheque";
        return selected;
    }

    private string OriginDocumentDescription()
    {
        var kind = OriginAttachmentKind();
        var reference = OriginDocumentReferenceBox.Text.Trim();
        return string.IsNullOrWhiteSpace(reference) ? kind : $"{kind}: {reference}";
    }

    private void UpdatePaymentFileUi(DocumentProfile? profile = null)
    {
        profile ??= GetDocumentProfile();
        var selected = EditableComboValue(PfBox);
        PickPaymentFileButton.Content = string.IsNullOrWhiteSpace(selected)
            ? profile.PaymentFileButton + " (um ou mais)"
            : $"Adicionar {PaymentAttachmentKind().ToLowerInvariant()} (um ou mais)";
        PfFileText.ToolTip = $"Arquivo(s) de: {PaymentAttachmentKind()}";
    }

    private string PaymentAttachmentKind()
    {
        var selected = (PfBox.SelectedItem as string ?? EditableComboValue(PfBox)).Trim();
        if (string.IsNullOrWhiteSpace(selected)) return "Documento financeiro / comprovante";
        if (selected.Equals("GRU", StringComparison.OrdinalIgnoreCase)) return "GRU";
        if (selected.Contains("depósito", StringComparison.OrdinalIgnoreCase)) return "Comprovante de depósito";
        if (selected.Contains("ressarcimento", StringComparison.OrdinalIgnoreCase)) return "Documento da forma de ressarcimento";
        if (selected.Contains("recolhimento", StringComparison.OrdinalIgnoreCase)) return "Comprovante de recolhimento";
        if (selected.Contains("PF", StringComparison.OrdinalIgnoreCase)) return "PF / consulta financeira";
        return selected;
    }

    private DocumentProfile GetDocumentProfile()
    {
        if (Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase))
            return new("Consulta ou relatório que determinou o valor", ["TDBLOQ", "Consulta SIAFI", "Relatório bancário"], "TDBLOQ",
                true, true, "Boletim que autorizou o pagamento / data", "PF ou referência do pagamento",
                "Selecionar consulta ou relatório TDBLOQ", "Selecionar boletim", "Selecionar PF / consulta financeira",
                "Para TDBLOQ, confira consulta/relatório, boletim e PF. Anexe somente os documentos realmente usados no cálculo.");
        if (Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase))
            return new("Relatório que determinou o valor", ["PP760", "Relatório CPEx", "Relatório de pagamento"], "PP760",
                true, true, "Boletim / data (quando houver)", "PF ou referência financeira (quando houver)",
                "Selecionar relatório PP760", "Selecionar boletim", "Selecionar PF / referência",
                "Para PP760, o relatório é o documento principal. Boletim e PF só entram quando forem aplicáveis.");
        if (Flow.Contains("Inconsistência", StringComparison.OrdinalIgnoreCase))
            return new("Relatório que determinou o valor", ["TDINC", "TDCNAB", "Relatório de Inconsistências Bancárias"], "TDINC",
                true, true, "Boletim / data (quando houver)", "PF ou referência financeira (quando houver)",
                "Selecionar relatório de inconsistência", "Selecionar boletim", "Selecionar PF / consulta",
                "Para inconsistência bancária, use o relatório que contém o beneficiário e o valor. Boletim e PF são anexados somente quando existirem no caso.");
        if (Flow.Contains("dano ao erário", StringComparison.OrdinalIgnoreCase))
            return new("Tipo do documento que apurou ou determinou o valor", ["Boletim Interno", "Aditamento ao Boletim Interno", "Solução de sindicância", "Relatório de apuração", "IPM", "Termo de reconhecimento de dívida", "Planilha de cálculo", "Outro"], "",
                false, true, "", "GRU, depósito ou forma de ressarcimento (se houver)",
                "Selecionar documento que determinou o valor", "", "Selecionar GRU / comprovante / depósito",
                "Para dano ao erário, o documento principal pode ser boletim, solução, relatório, termo ou cálculo. PF não é exigida. Inclua GRU ou comprovante somente se já existir.");
        if (Flow.Contains("Reposição ao erário", StringComparison.OrdinalIgnoreCase)
            || Flow.Contains("Cobrança", StringComparison.OrdinalIgnoreCase)
            || Flow.Contains("recolhimento", StringComparison.OrdinalIgnoreCase))
            return new("Tipo do documento que originou a cobrança e determinou o valor", ["Boletim Interno", "Aditamento ao Boletim Interno", "Nota de débito", "Termo de reconhecimento de dívida", "Planilha de cálculo", "Relatório de apuração", "Outro"], "",
                false, true, "", "GRU, depósito ou comprovante de recolhimento (se houver)",
                "Selecionar documento de origem", "", "Selecionar GRU / comprovante",
                "Use o documento que fundamentou a cobrança ou reposição. Anexe GRU e comprovante apenas quando fizerem parte desta etapa.");
        if (Flow.Contains("Restituição", StringComparison.OrdinalIgnoreCase)
            || Flow.Contains("devolução", StringComparison.OrdinalIgnoreCase))
            return new("Tipo do documento que determinou o valor a restituir", ["Boletim Interno", "Aditamento ao Boletim Interno", "Contracheque", "Ficha financeira", "Planilha de cálculo", "Relatório", "Requerimento", "Outro"], "",
                false, true, "", "Comprovante bancário ou de devolução (se houver)",
                "Selecionar documento que determinou o valor", "", "Selecionar comprovante",
                "Em restituição ou devolução de salário, use o documento que demonstra a origem e o valor. Não será exigida PF.");
        if (Flow.Contains("direito reconhecido", StringComparison.OrdinalIgnoreCase))
            return new("Tipo do documento que reconheceu o direito e determinou o valor", ["Boletim Interno", "Aditamento ao Boletim Interno", "Processo de reconhecimento de direito", "Despacho decisório", "Planilha de cálculo", "Outro"], "",
                false, true, "", "Documento financeiro ou comprovante (se houver)",
                "Selecionar documento de reconhecimento", "", "Selecionar documento financeiro",
                "Use o ato que reconheceu o direito e o documento que demonstra o valor. Outros anexos são opcionais e devem ter relação direta com o pedido.");
        if (Flow.Contains("Regularização", StringComparison.OrdinalIgnoreCase))
            return new("Tipo do documento que apontou a necessidade de regularização", ["Boletim Interno", "Aditamento ao Boletim Interno", "Relatório", "Contracheque", "Ficha financeira", "Mensagem CPEx", "Outro"], "",
                true, true, "Boletim / data (quando houver)", "Documento financeiro ou referência (se houver)",
                "Selecionar documento de origem", "Selecionar boletim", "Selecionar documento financeiro",
                "Na regularização, escolha o documento que identificou o problema. Boletim e documento financeiro são opcionais conforme o caso.");
        return new("Tipo do documento que originou o pedido ou determinou o valor", ["Boletim Interno", "Aditamento ao Boletim Interno", "Relatório", "Requerimento", "Contracheque", "Ficha financeira", "Planilha de cálculo", "Termo", "Outro"], "",
            true, true, "Boletim / data (se aplicável)", "Documento financeiro, GRU ou comprovante (se aplicável)",
            "Selecionar documento principal", "Selecionar boletim", "Selecionar documento financeiro",
            "Escolha somente os documentos que fundamentam esta finalidade. O documento principal pode ser boletim, relatório, requerimento, cálculo ou outro ato válido.");
    }

    private void BuildChecks(IReadOnlyDictionary<string, bool>? previous = null)
    {
        previous ??= _checks.ToDictionary(item => item.Text, item => item.Checked, StringComparer.OrdinalIgnoreCase);
        string[] items;

        if (Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase))
        {
            items =
            [
                "Consultei a conta TDBLOQ no SIAFI.",
                "Conferi o valor que chegou na conta TDBLOQ.",
                "Publiquei ou localizei a publicação em boletim.",
                "Consultei a PF.",
                "Anexei ou mencionei a PF no DIEx.",
                "Conferi o percentual de pagamento ao CPF.",
                "Conferi o percentual de reversão ao CPEx.",
                "O valor do DIEx está igual ao valor do boletim.",
                "Tenho o boletim salvo em PDF.",
                "Tenho o relatório/consulta geradora salvo em PDF.",
                "O chefe fará o despacho para o Ordenador de Despesas."
            ];
        }
        else if (Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase))
        {
            items =
            [
                "Consultei o relatório PP760.",
                "Conferi se o caso realmente pertence ao PP760.",
                "Conferi o militar ou a relação de militares.",
                "Publiquei ou localizei a publicação em boletim.",
                "O valor do DIEx está igual ao valor do boletim.",
                "O valor do boletim está igual ao relatório PP760.",
                "Mencionei a PF ou PFs no texto do DIEx, quando houver.",
                "Tenho o boletim salvo em PDF.",
                "Tenho o relatório PP760 salvo em PDF.",
                "O chefe fará o despacho para o Ordenador de Despesas."
            ];
        }
        else if (Flow.Contains("Inconsistência", StringComparison.OrdinalIgnoreCase))
        {
            items =
            [
                "Consultei o relatório TDINC/TDCNAB.",
                "Conferi o militar ou a relação de militares.",
                "Conferi o valor no relatório gerador.",
                "Publiquei ou localizei a publicação em boletim.",
                "O valor do DIEx está igual ao valor do boletim.",
                "O valor do boletim está igual ao relatório gerador.",
                "Mencionei a PF ou PFs no texto do DIEx, quando houver.",
                "Tenho o boletim salvo em PDF.",
                "Tenho o relatório gerador salvo em PDF.",
                "O chefe fará o despacho para o Ordenador de Despesas."
            ];
        }
        else
        {
            items =
            [
                "Conferi a finalidade e o assunto do processo.",
                "Conferi o interessado ou a relação de interessados.",
                "Conferi o valor, quando houver.",
                "Conferi o documento ou relatório que fundamenta o pedido, quando houver.",
                "Conferi a publicação em boletim, quando exigida para este caso.",
                "Separei todos os documentos que devem ser anexados ao DIEx.",
                "Anexei ao DIEx o boletim, o relatório e os demais documentos aplicáveis.",
                "Conferi se os valores e referências são iguais em todos os documentos aplicáveis.",
                "Salvei o DIEx com seus anexos antes de iniciar o processo.",
                "Conferi o despacho e o destinatário antes do encaminhamento."
            ];
        }

        _checks.Clear();
        foreach (var text in items)
        {
            _checks.Add(new CheckRow
            {
                Text = text,
                Checked = previous.TryGetValue(text, out var isChecked) && isChecked
            });
        }
    }

    private void Form_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
            return;

        if (ReferenceEquals(sender, FlowBox))
        {
            var currentFlow = Normalize(Flow);
            if (!string.Equals(currentFlow, _configuredFlow, StringComparison.Ordinal))
            {
                var previous = _checks.ToDictionary(item => item.Text, item => item.Checked, StringComparer.OrdinalIgnoreCase);
                ConfigureFlow(resetReport: true);
                BuildChecks(previous);
            }
        }

        MarkDirty();
        Refresh();
    }

    private void ReportBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _configuringFlow || !IsLoaded) return;
        UpdateOriginFileUi();
        MarkDirty();
        Refresh();
    }

    private void PaymentReference_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _configuringFlow || !IsLoaded) return;
        UpdatePaymentFileUi();
        MarkDirty();
        Refresh();
    }

    private void Checklist_Changed(object sender, RoutedEventArgs e)
    {
        MarkDirty();
        Refresh();
    }

    private void ProcessTitle_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        MarkDirty();
    }

    private void GeneratedText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        RefreshProcessHeader();
    }

    private void RefreshProcessHeader()
    {
        if (CurrentProcessText is null || SaveStateText is null) return;
        var current = _savedProcesses.FirstOrDefault(item => item.Id == _currentProcessId);
        CurrentProcessText.Text = current is null
            ? "NOVO PROCESSO — AINDA NÃO SALVO"
            : $"PROCESSO SALVO — {current.Title.ToUpperInvariant()}";
        SaveStateText.Text = _dirty
            ? "Alterações não salvas"
            : current is null
                ? "Preencha os dados e salve quando desejar."
                : $"Salvo em {current.UpdatedAt.ToLocalTime():dd/MM/yyyy 'às' HH:mm}";
        SaveStateText.Foreground = _dirty
            ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
            : (Brush)FindResource("MutedBrush");
    }

    private string FlowWarning => Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
        ? "No TDBLOQ, PF e percentuais são obrigatórios na conferência."
        : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase)
            ? "PP760 tem que nascer do relatório PP760. Não chute a origem."
            : Flow.Contains("Inconsistência", StringComparison.OrdinalIgnoreCase)
                ? "Se o valor não bater em todos os documentos aplicáveis, não autue."
                : "Use somente os documentos e requisitos que realmente se aplicam à finalidade informada.";

    private string GeneratedSubject()
    {
        var beneficiary = SubjectBeneficiary();
        if (Flow.Contains("Inconsistência", StringComparison.OrdinalIgnoreCase))
        {
            var kind = SubjectBeneficiaryKind();
            return $"DEPÓSITO DE TERCEIROS - {OrganizationIdentity.UpperName} / 4ª RM - PAGAMENTO DE INCONSISTÊNCIA BANCÁRIA ({kind} / {beneficiary})";
        }

        var reason = Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase)
            ? "REGULARIZAÇÃO DE BLOQUEIO BANCÁRIO - TDBLOQ"
            : Flow.Contains("PP760", StringComparison.OrdinalIgnoreCase)
                ? "PAGAMENTO DE INCONSISTÊNCIA BANCÁRIA - PP760/CPEx"
                : SanitizeSubjectText(Flow);
        return $"DEPÓSITO DE TERCEIROS - {OrganizationIdentity.UpperName} / 4ª RM - {reason.ToUpperInvariant()} ({beneficiary})";
    }

    private string SubjectBeneficiaryKind()
    {
        var context = Normalize($"{Flow} {ReportBox.Text} {ObservationBox.Text}");
        if (context.Contains("pensao judicial", StringComparison.Ordinal)
            || context.Contains("pensao alimenticia", StringComparison.Ordinal))
            return "PENSÃO JUDICIAL";

        if (IsRelationMode)
        {
            var hasRegisteredMilitary = _relation.Any(item =>
                item.Source.Contains("Ativo", StringComparison.OrdinalIgnoreCase)
                || item.Source.Contains("Licenciado", StringComparison.OrdinalIgnoreCase));
            return hasRegisteredMilitary ? "PG DE MILITARES" : "PENSÕES JUDICIAIS";
        }

        return MilitaryPickerBox.SelectedItem is BizuMilitaryOption
            ? "PG DE MILITAR"
            : "PENSÃO JUDICIAL";
    }

    private void Refresh()
    {
        if (_loading)
            return;

        var beneficiaries = BeneficiariesText();
        var documentProfile = GetDocumentProfile();
        UpdateOriginFileUi(documentProfile);
        UpdatePaymentFileUi(documentProfile);
        var bulletinDate = BulletinDatePicker.SelectedDate is DateTime date
            ? date.ToString("dd/MM/yyyy")
            : string.Empty;
        var observation = ObservationBox.Text.Trim();

        var requestParts = new List<string> { $"Solicito a adoção das providências relativas a {Flow}" };
        if (!string.IsNullOrWhiteSpace(ValueBox.Text))
            requestParts.Add($"no valor de {FormatValueWithWords(ValueBox.Text)}");
        if (IsRelationMode || _relation.Count > 0)
            requestParts.Add("conforme relação anexa");
        var generatedDiex = "1. " + string.Join(", ", requestParts) + ".\n\n" +
                            $"2. O pedido refere-se ao(s) seguinte(s) interessado(s):\n{beneficiaries}";

        var references = new List<string>();
        if (!string.IsNullOrWhiteSpace(EditableComboValue(ReportBox)))
            references.Add($"Documento que fundamenta o pedido e o valor: {OriginDocumentDescription()}.");
        if (documentProfile.UsePublication && !string.IsNullOrWhiteSpace(BulletinBox.Text))
        {
            var dateSuffix = string.IsNullOrWhiteSpace(bulletinDate) ? string.Empty : $", de {bulletinDate}";
            references.Add($"Publicação: {BulletinBox.Text.Trim()}{dateSuffix}.");
        }
        var paymentReference = EditableComboValue(PfBox);
        if (documentProfile.UsePaymentDocument && !string.IsNullOrWhiteSpace(paymentReference))
            references.Add($"Referência financeira ou comprovante: {paymentReference}.");
        if (references.Count > 0)
            generatedDiex += "\n\n3. Referências que fundamentam o pedido:\n" + string.Join("\n", references);

        var attachmentLines = new List<string>();
        AddAttachmentLines(attachmentLines, OriginDocumentDescription(), _reportFile);
        if (documentProfile.UsePublication) AddAttachmentLines(attachmentLines, "Publicação/boletim", _bulletinFile);
        if (documentProfile.UsePaymentDocument) AddAttachmentLines(attachmentLines, PaymentAttachmentKind(), _pfFile);
        AddAttachmentLines(attachmentLines, "Ficha financeira", _financialStatementFiles);
        AddAttachmentLines(attachmentLines, "Contracheque", _paystubFiles);
        AddAttachmentLines(attachmentLines, "Documento complementar", _otherFile);
        var selectedAttachmentPaths = ActiveAttachmentPaths(documentProfile);
        var existingAttachmentTotal = selectedAttachmentPaths.Where(File.Exists).Sum(path => new FileInfo(path).Length);
        var attachmentOverLimit = existingAttachmentTotal > GratificationService.SpedAttachmentLimitBytes;
        AttachmentsLimitText.Text = selectedAttachmentPaths.Count == 0
            ? "Limite do SPED: 10 MB no total. Nenhum anexo selecionado."
            : $"{selectedAttachmentPaths.Count} anexo(s) • {existingAttachmentTotal / 1_000_000d:0.00} MB de 10 MB" +
              (attachmentOverLimit ? " — reduza os arquivos antes de continuar." : string.Empty);
        AttachmentsLimitText.Foreground = attachmentOverLimit
            ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
            : (Brush)FindResource("MutedBrush");
        RefreshSavedFiles(documentProfile);
        generatedDiex += "\n\n4. Documentos anexados ao DIEx:\n" +
                         (attachmentLines.Count > 0
                             ? string.Join("\n", attachmentLines).TrimEnd(';') + "."
                             : "- Selecionar os documentos que fundamentam o pedido.");

        generatedDiex += "\n\n5. Diante do exposto, solicito a análise e a adoção das medidas cabíveis, observada a coerência entre os valores, as referências e os documentos anexos.";

        if (Flow.Contains("TDBLOQ", StringComparison.OrdinalIgnoreCase))
        {
            generatedDiex +=
                "\n\nObservação obrigatória para TDBLOQ: conferir a PF, o percentual de pagamento ao CPF " +
                "e o percentual de reversão ao CPEx antes da autuação.";
        }

        if (!string.IsNullOrWhiteSpace(observation))
            generatedDiex += $"\n\nObservação complementar: {observation}";

        var generatedDispatch = $"1. Após análise da documentação constante do presente processo, manifesto-me favoravelmente ao prosseguimento das providências relativas a {Flow}.\n\n" +
                                "2. Encaminhe-se ao Comandante da Organização Militar para ciência e demais providências cabíveis.";

        ReplaceGeneratedText(DiexText, generatedDiex, ref _lastGeneratedDiex, _forceGenerateTexts);
        ReplaceGeneratedText(DispatchText, generatedDispatch, ref _lastGeneratedDispatch, _forceGenerateTexts);

        var beneficiaryReady = IsRelationMode ? _relation.Count > 0 : !string.IsNullOrWhiteSpace(MilitaryBox.Text);
        var essentialReady = !string.IsNullOrWhiteSpace(FlowBox.Text) && beneficiaryReady;
        var mainDocuments = SplitPaths(_reportFile);
        var sourceAttachmentsReady = mainDocuments.Count > 0 && mainDocuments.All(File.Exists);
        sourceAttachmentsReady &= !attachmentOverLimit;
        var diexReady = !string.IsNullOrWhiteSpace(_diexFile);
        var checkedCount = _checks.Count(item => item.Checked);
        var allChecked = _checks.Count > 0 && checkedCount == _checks.Count;

        if (essentialReady && sourceAttachmentsReady && diexReady && allChecked)
        {
            StatusTitle.Text = "PRONTO PARA REVISÃO FINAL";
            StatusText.Text = $"DIEx concluído, documentos selecionados e checklist finalizado. Faça a leitura final antes de autuar. {FlowWarning}";
            StatusCard.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231));
            StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
        }
        else if (essentialReady)
        {
            StatusTitle.Text = "CONFERÊNCIA EM ANDAMENTO";
            StatusText.Text = $"{checkedCount}/{_checks.Count} conferências marcadas. Documentos do DIEx: {(sourceAttachmentsReady ? "selecionados" : "pendentes")}. DIEx concluído: {(diexReady ? "sim" : "ainda não")}. {FlowWarning}";
            StatusCard.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
            StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(161, 98, 7));
        }
        else
        {
            StatusTitle.Text = "PREENCHIMENTO INCOMPLETO";
            StatusText.Text = $"Informe a finalidade e o interessado ou relação para iniciar a conferência. Os demais campos são usados quando aplicáveis ao caso. {FlowWarning}";
            StatusCard.Background = new SolidColorBrush(Color.FromRgb(254, 226, 226));
            StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
        }
    }

    private static void ReplaceGeneratedText(TextBox box, string generated, ref string previousGenerated, bool force = false)
    {
        if (force || string.IsNullOrWhiteSpace(box.Text) || string.Equals(box.Text, previousGenerated, StringComparison.Ordinal))
            box.Text = generated;
        previousGenerated = generated;
    }

    private void GenerateTexts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _forceGenerateTexts = true;
            Refresh();
            StatusText.Text = "DIEx e despacho gerados. Os campos continuam editáveis antes de copiar/salvar.";
        }
        finally
        {
            _forceGenerateTexts = false;
        }
    }

    private string BeneficiariesText()
    {
        if (IsRelationMode)
        {
            if (_relation.Count == 0)
                return "[RELAÇÃO ANEXA A PREENCHER]";
            return string.Join("\n", _relation.Select((item, index) => $"{index + 1}. {BeneficiaryIdentity(item)}"));
        }

        var identity = MilitaryPickerBox.SelectedItem is BizuMilitaryOption selected
            ? BeneficiaryIdentity(selected)
            : Placeholder(MilitaryBox.Text, "[INFORMAR BENEFICIÁRIO OU RELAÇÃO ANEXA]");
        var line = $"1. {identity}";
        if (!string.IsNullOrWhiteSpace(ValueBox.Text)) line += $" | Valor: {FormatValueWithWords(ValueBox.Text)}";
        return line;
    }

    private static string BeneficiaryIdentity(BizuMilitaryOption item)
    {
        var rank = MilitaryRankService.ShortName(item.Rank);
        var war = item.WarName?.Trim().ToUpperInvariant() ?? string.Empty;
        var name = item.Name?.Trim().ToUpperInvariant() ?? string.Empty;
        var prefix = string.Join(" ", new[] { rank, war }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(name))
            return string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix} - {name}";
        return string.IsNullOrWhiteSpace(prefix) ? "[MILITAR]" : prefix;
    }

    private string SubjectBeneficiary()
    {
        if (IsRelationMode || _relation.Count > 1)
        {
            if (_relation.Count == 1)
                return SanitizeSubjectPersonName(_relation[0].Name);
            return "RELAÇÃO ANEXA";
        }

        if (MilitaryPickerBox.SelectedItem is BizuMilitaryOption selected)
            return SanitizeSubjectPersonName(selected.Name);

        var firstLine = MilitaryBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return SanitizeSubjectPersonName(firstLine);
    }

    private static string SanitizeSubjectPersonName(string? value)
    {
        var text = value ?? string.Empty;
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\b(?:CPF|PREC(?:-CP)?|VALOR|PF)\b.*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", string.Empty);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"^\s*(?:SD|CB|3[º°]?\s*SGT|2[º°]?\s*SGT|1[º°]?\s*SGT|ST|ASP|2[º°]?\s*TEN|1[º°]?\s*TEN|CAP|MAJ|TC|CEL)\s+",
            string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = SanitizeSubjectText(text);
        return string.IsNullOrWhiteSpace(text) ? "INTERESSADO" : text.ToUpperInvariant();
    }

    private static string SanitizeSubjectText(string? value)
    {
        var text = value ?? string.Empty;
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\b(?:CPF|PREC(?:-CP)?)\s*[:\-]?\s*[0-9.\-/]+", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '/', '–');
        return text;
    }

    private static string Placeholder(string? value, string placeholder)
        => string.IsNullOrWhiteSpace(value) ? placeholder : value.Trim();

    private void MilitaryPickerBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (_loading || e.Key is Key.Up or Key.Down or Key.Enter or Key.Tab or Key.Escape)
            return;

        var query = MilitaryPickerBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allMilitary
            : _allMilitary.Where(item => item.SearchText.Contains(Normalize(query), StringComparison.Ordinal)).Take(80).ToList();

        MilitaryPickerBox.ItemsSource = filtered;
        MilitaryPickerBox.IsDropDownOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MilitaryPickerBox.Text = query;
        }));
    }

    private void UseMilitary_Click(object sender, RoutedEventArgs e)
    {
        if (MilitaryPickerBox.SelectedItem is not BizuMilitaryOption selected)
        {
            SigfurDialog.Show(this, "Selecione um militar na pesquisa.", "SPED Processos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MilitaryBox.Text = selected.IdentityText;
        Refresh();
    }

    private void AddRelation_Click(object sender, RoutedEventArgs e)
    {
        BizuMilitaryOption? item = MilitaryPickerBox.SelectedItem as BizuMilitaryOption;
        if (item is null && !string.IsNullOrWhiteSpace(MilitaryBox.Text))
            item = BizuMilitaryOption.FromManual(MilitaryBox.Text);

        if (item is null || string.IsNullOrWhiteSpace(item.Name))
        {
            SigfurDialog.Show(this, "Selecione um militar ou preencha uma identificação manual antes de adicionar.", "Relação anexa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cpf = MilitaryFormatting.Digits(item.Cpf);
        var exists = _relation.Any(existing =>
            (!string.IsNullOrWhiteSpace(cpf) && MilitaryFormatting.Digits(existing.Cpf) == cpf)
            || (string.IsNullOrWhiteSpace(cpf) && Normalize(existing.Name) == Normalize(item.Name)));
        if (!exists)
            _relation.Add(item.Clone());

        BeneficiaryModeBox.SelectedIndex = 1;
        MarkDirty();
        Refresh();
    }

    private void RemoveRelation_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in RelationGrid.SelectedItems.Cast<BizuMilitaryOption>().ToList())
            _relation.Remove(item);
        MarkDirty();
        Refresh();
    }

    private void ClearRelation_Click(object sender, RoutedEventArgs e)
    {
        if (_relation.Count == 0)
            return;
        if (SigfurDialog.Show(this, "Remover todos os beneficiários da relação?", "Relação anexa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _relation.Clear();
        MarkDirty();
        Refresh();
    }

    private void PickDiex_Click(object sender, RoutedEventArgs e) => PickSingle(ref _diexFile, DiexFileText);
    private void PickBulletin_Click(object sender, RoutedEventArgs e) => PickMultiple(ref _bulletinFile, BulletinFileText);
    private void PickReport_Click(object sender, RoutedEventArgs e) => PickMultiple(ref _reportFile, ReportFileText);
    private void PickPf_Click(object sender, RoutedEventArgs e) => PickMultiple(ref _pfFile, PfFileText);
    private void PickFinancialStatement_Click(object sender, RoutedEventArgs e) => PickMultiple(ref _financialStatementFiles, FinancialStatementFileText);
    private void PickPaystub_Click(object sender, RoutedEventArgs e) => PickMultiple(ref _paystubFiles, PaystubFileText);

    private void PickOther_Click(object sender, RoutedEventArgs e)
        => PickMultiple(ref _otherFile, OtherFileText);

    private void PickMultiple(ref string field, TextBlock label)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Documentos|*.pdf;*.doc;*.docx;*.odt;*.xlsx;*.xls;*.ods;*.png;*.jpg;*.jpeg|Todos os arquivos|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var combined = SplitPaths(field)
            .Concat(dialog.FileNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        field = string.Join("|", combined);
        label.Text = FilesLabel(field);
        MarkDirty();
        Refresh();
    }

    private void PickSingle(ref string field, TextBlock label)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Documentos|*.pdf;*.doc;*.docx;*.odt;*.xlsx;*.xls;*.ods;*.png;*.jpg;*.jpeg|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        field = dialog.FileName;
        label.Text = Path.GetFileName(field);
        MarkDirty();
        Refresh();
    }

    private List<string> ActiveAttachmentPaths(DocumentProfile profile)
    {
        var paths = new List<string>();
        paths.AddRange(SplitPaths(_reportFile));
        if (profile.UsePublication) paths.AddRange(SplitPaths(_bulletinFile));
        if (profile.UsePaymentDocument) paths.AddRange(SplitPaths(_pfFile));
        paths.AddRange(SplitPaths(_financialStatementFiles));
        paths.AddRange(SplitPaths(_paystubFiles));
        paths.AddRange(SplitPaths(_otherFile));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RefreshSavedFiles(DocumentProfile profile)
    {
        var rows = new List<SavedFileRow>();
        AddSavedFiles(rows, OriginDocumentDescription(), _reportFile, true);
        AddSavedFiles(rows, "Publicação / boletim", _bulletinFile, profile.UsePublication);
        AddSavedFiles(rows, PaymentAttachmentKind(), _pfFile, profile.UsePaymentDocument);
        AddSavedFiles(rows, "Ficha financeira", _financialStatementFiles, true);
        AddSavedFiles(rows, "Contracheque", _paystubFiles, true);
        AddSavedFiles(rows, "Anexo complementar", _otherFile, true);
        AddSavedFile(rows, "DIEx salvo pelo SPED", _diexFile, false, isDiex: true);
        AddSavedFile(rows, "Processo salvo pelo SPED", _spedProcessFile, false);
        SavedFilesList.ItemsSource = rows;
        NoSavedFilesText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AddSavedFiles(ICollection<SavedFileRow> rows, string kind, string paths, bool active)
    {
        foreach (var path in SplitPaths(paths)) AddSavedFile(rows, kind, path, active);
    }

    private static void AddSavedFile(ICollection<SavedFileRow> rows, string kind, string path, bool active, bool isDiex = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var exists = File.Exists(path);
        var size = exists ? new FileInfo(path).Length / 1_000_000d : 0d;
        var detail = !exists
            ? "Arquivo não encontrado — selecione novamente ou remova."
            : isDiex
                ? $"{size:0.00} MB • referência local; não será anexado ao novo DIEx."
                : active
                    ? $"{size:0.00} MB • será anexado ao DIEx."
                    : $"{size:0.00} MB • guardado, mas não será usado nesta finalidade.";
        rows.Add(new SavedFileRow
        {
            Kind = kind,
            Path = path,
            Display = Path.GetFileName(path),
            Detail = detail
        });
    }

    private void OpenSavedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            SigfurDialog.Show(this, "Este arquivo não está mais no local salvo. Selecione novamente ou remova a referência.",
                "Arquivo não encontrado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShellService.OpenPath(path);
    }

    private void RemoveSavedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        _reportFile = RemovePath(_reportFile, path);
        _bulletinFile = RemovePath(_bulletinFile, path);
        _pfFile = RemovePath(_pfFile, path);
        _financialStatementFiles = RemovePath(_financialStatementFiles, path);
        _paystubFiles = RemovePath(_paystubFiles, path);
        if (string.Equals(_diexFile, path, StringComparison.OrdinalIgnoreCase)) _diexFile = string.Empty;
        if (string.Equals(_spedProcessFile, path, StringComparison.OrdinalIgnoreCase)) _spedProcessFile = string.Empty;
        _otherFile = RemovePath(_otherFile, path);
        DiexFileText.Text = string.IsNullOrWhiteSpace(_diexFile) ? "Ainda não criado" : FileLabel(_diexFile);
        ProcessFileText.Text = string.IsNullOrWhiteSpace(_spedProcessFile) ? "Ainda não criado" : FileLabel(_spedProcessFile);
        BulletinFileText.Text = FilesLabel(_bulletinFile);
        ReportFileText.Text = FilesLabel(_reportFile);
        PfFileText.Text = FilesLabel(_pfFile);
        FinancialStatementFileText.Text = FilesLabel(_financialStatementFiles);
        PaystubFileText.Text = FilesLabel(_paystubFiles);
        OtherFileText.Text = FilesLabel(_otherFile);
        MarkDirty();
        Refresh();
    }

    private void MarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _checks)
            item.Checked = true;
        ChecklistList.Items.Refresh();
        MarkDirty();
        Refresh();
    }

    private void UnmarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _checks)
            item.Checked = false;
        ChecklistList.Items.Refresh();
        MarkDirty();
        Refresh();
    }

    private void CopyDiex_Click(object sender, RoutedEventArgs e) => CopyText(DiexText.Text, "DIEx copiado.");
    private void CopyDispatch_Click(object sender, RoutedEventArgs e) => CopyText(DispatchText.Text, "Despacho copiado.");
    private void CopyChecklist_Click(object sender, RoutedEventArgs e) => CopyText(BuildChecklistText(), "Checklist copiado.");

    private void CopyText(string text, string confirmation)
    {
        Clipboard.SetText(text ?? string.Empty);
        StatusText.Text = confirmation;
    }

    private string BuildChecklistText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CHECKLIST FINAL — SPED PROCESSOS {OrganizationIdentity.UpperName}");
        builder.AppendLine();
        builder.AppendLine($"Tipo: {Flow}");
        builder.AppendLine($"Documento que fundamenta o pedido e o valor: {OriginDocumentDescription()}");
        builder.AppendLine($"Assunto: {GeneratedSubject()}");
        builder.AppendLine($"Beneficiário(s): {BeneficiariesText()}");
        builder.AppendLine($"Regra crítica: {FlowWarning}");
        builder.AppendLine();
        foreach (var item in _checks)
            builder.AppendLine($"[{(item.Checked ? "OK" : "PENDENTE")}] {item.Text}");
        return builder.ToString().TrimEnd();
    }

    private async void SaveState_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = await SaveCurrentProcessAsync();
            SigfurDialog.Show(this, $"Processo “{saved.Title}” salvo com sucesso.", "SPED Processos", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, $"Não foi possível salvar o processo.\n\n{ex.Message}", "SPED Processos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<SavedProcess> SaveCurrentProcessAsync()
    {
        var state = CaptureState();
        var now = DateTimeOffset.Now;
        var title = string.IsNullOrWhiteSpace(ProcessTitleBox.Text)
            ? BuildDefaultProcessTitle(state)
            : ProcessTitleBox.Text.Trim();
        var saved = _savedProcesses.FirstOrDefault(item => item.Id == _currentProcessId);
        var createdNew = saved is null;
        if (saved is null)
        {
            saved = new SavedProcess
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = now
            };
            _savedProcesses.Add(saved);
        }

        state = PreserveProcessFiles(state, saved.Id);
        saved.Title = title;
        saved.UpdatedAt = now;
        saved.State = state;
        _currentProcessId = saved.Id;
        try
        {
            await PersistProcessesAsync();
        }
        catch
        {
            if (createdNew)
            {
                _savedProcesses.Remove(saved);
                _currentProcessId = null;
            }
            throw;
        }
        await WriteJsonAtomicAsync(_stateFile, state); // cópia de recuperação e compatibilidade com versões anteriores

        ApplyManagedFilePaths(state);

        SortSavedProcesses(saved.Id);
        _loading = true;
        ProcessTitleBox.Text = title;
        _loading = false;
        _dirty = false;
        RefreshProcessHeader();
        return saved;
    }

    private static State PreserveProcessFiles(State state, string processId)
    {
        var directory = Path.Combine(App.Paths.SpedProcessesDirectory, processId, "documentos");
        Directory.CreateDirectory(directory);
        state.ReportFile = CopyProcessFiles(state.ReportFile, directory, "documento_principal");
        state.BulletinFile = CopyProcessFiles(state.BulletinFile, directory, "publicacao_boletim");
        state.PfFile = CopyProcessFiles(state.PfFile, directory, "documento_financeiro");
        state.FinancialStatementFiles = CopyProcessFiles(state.FinancialStatementFiles, directory, "ficha_financeira");
        state.PaystubFiles = CopyProcessFiles(state.PaystubFiles, directory, "contracheque");
        state.DiexFile = CopyProcessFile(state.DiexFile, directory, "diex");
        state.SpedProcessFile = CopyProcessFile(state.SpedProcessFile, directory, "processo_sped");
        state.OtherFile = CopyProcessFiles(state.OtherFile, directory, "anexo");
        return state;
    }

    private static string CopyProcessFiles(string paths, string directory, string prefix)
        => string.Join("|", SplitPaths(paths)
            .Select((path, index) => CopyProcessFile(path, directory, $"{prefix}_{index + 1:00}"))
            .Where(path => !string.IsNullOrWhiteSpace(path)));

    private static string CopyProcessFile(string path, string directory, string prefix)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (!File.Exists(path)) return path;
        var source = Path.GetFullPath(path);
        var managedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (source.StartsWith(managedDirectory, StringComparison.OrdinalIgnoreCase)) return source;
        var extension = Path.GetExtension(source);
        var originalName = Path.GetFileNameWithoutExtension(source);
        var safeName = string.Concat(originalName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        if (safeName.Length > 80) safeName = safeName[..80].Trim();
        var destination = Path.Combine(directory, $"{prefix}_{safeName}{extension}");
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private void ApplyManagedFilePaths(State state)
    {
        _reportFile = state.ReportFile;
        _bulletinFile = state.BulletinFile;
        _pfFile = state.PfFile;
        _financialStatementFiles = state.FinancialStatementFiles;
        _paystubFiles = state.PaystubFiles;
        _diexFile = state.DiexFile;
        _spedProcessFile = state.SpedProcessFile;
        _otherFile = state.OtherFile;
        ReportFileText.Text = FilesLabel(_reportFile);
        BulletinFileText.Text = FilesLabel(_bulletinFile);
        PfFileText.Text = FilesLabel(_pfFile);
        FinancialStatementFileText.Text = FilesLabel(_financialStatementFiles);
        PaystubFileText.Text = FilesLabel(_paystubFiles);
        DiexFileText.Text = string.IsNullOrWhiteSpace(_diexFile) ? "Ainda não criado" : FileLabel(_diexFile);
        ProcessFileText.Text = string.IsNullOrWhiteSpace(_spedProcessFile) ? "Ainda não criado" : FileLabel(_spedProcessFile);
        OtherFileText.Text = FilesLabel(_otherFile);
        RefreshSavedFiles(GetDocumentProfile());
    }

    private State CaptureState() => new()
    {
        Flow = Flow,
        Recipient = RecipientBox.Text,
        ProcessInterested = ProcessInterestedBox.Text,
        Report = EditableComboValue(ReportBox),
        OriginDocumentReference = OriginDocumentReferenceBox.Text,
        BeneficiaryMode = IsRelationMode ? "Relação anexa" : "Um militar",
        Military = MilitaryBox.Text,
        SelectedMilitary = CaptureSelectedMilitary(),
        Value = ValueBox.Text,
        Bulletin = BulletinBox.Text,
        BulletinDate = BulletinDatePicker.SelectedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
        Pf = EditableComboValue(PfBox),
        Observation = ObservationBox.Text,
        Checks = _checks.Select(item => new CheckState { Text = item.Text, Checked = item.Checked }).ToList(),
        LegacyChecks = _checks.Select(item => item.Checked).ToList(),
        Relation = _relation.Select(item => item.Clone()).ToList(),
        DiexFile = _diexFile,
        SpedProcessFile = _spedProcessFile,
        BulletinFile = _bulletinFile,
        ReportFile = _reportFile,
        PfFile = _pfFile,
        FinancialStatementFiles = _financialStatementFiles,
        PaystubFiles = _paystubFiles,
        OtherFile = _otherFile,
        DiexNumber = DiexNumberBox.Text.Trim(),
        ProcessAutuatedAt = _processAutuatedAt,
        DiexText = DiexText.Text,
        DispatchText = DispatchText.Text
    };

    private BizuMilitaryOption? CaptureSelectedMilitary()
    {
        if (MilitaryPickerBox.SelectedItem is BizuMilitaryOption selected) return selected.Clone();
        return FindMilitaryForState(new State { Military = MilitaryBox.Text })?.Clone();
    }

    private BizuMilitaryOption? FindMilitaryForState(State state)
    {
        if (state.SelectedMilitary is { } snapshot)
        {
            var snapshotCpf = MilitaryFormatting.Digits(snapshot.Cpf);
            var existing = _allMilitary.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(snapshotCpf) && MilitaryFormatting.Digits(item.Cpf) == snapshotCpf)
                || string.Equals(Normalize(item.Name), Normalize(snapshot.Name), StringComparison.Ordinal));
            return existing ?? snapshot;
        }

        var relationPerson = state.Relation.FirstOrDefault();
        if (relationPerson is not null) return relationPerson;
        var digits = MilitaryFormatting.Digits(state.Military);
        var byCpf = digits.Length >= 11
            ? _allMilitary.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(item.Cpf))
                && digits.Contains(MilitaryFormatting.Digits(item.Cpf), StringComparison.Ordinal))
            : null;
        if (byCpf is not null) return byCpf;
        var normalized = Normalize(state.Military);
        return _allMilitary.FirstOrDefault(item => normalized.Contains(Normalize(item.Name), StringComparison.Ordinal));
    }

    private async Task LoadProcessesAsync()
    {
        try
        {
            if (File.Exists(_processesFile))
            {
                var loaded = JsonSerializer.Deserialize<List<SavedProcess>>(await File.ReadAllTextAsync(_processesFile)) ?? [];
                foreach (var process in loaded.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
                {
                    process.State ??= new State();
                    process.State.SelectedMilitary ??= FindMilitaryForState(process.State)?.Clone();
                    _savedProcesses.Add(process);
                }
            }

            // Migra automaticamente o antigo preenchimento único para a nova carteira.
            if (_savedProcesses.Count == 0 && File.Exists(_stateFile))
            {
                var legacy = DeserializeState(await File.ReadAllTextAsync(_stateFile));
                if (legacy is not null)
                {
                    var timestamp = File.GetLastWriteTimeUtc(_stateFile);
                    _savedProcesses.Add(new SavedProcess
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = BuildDefaultProcessTitle(legacy),
                        CreatedAt = timestamp,
                        UpdatedAt = timestamp,
                        State = legacy
                    });
                    await PersistProcessesAsync();
                }
            }

            SortSavedProcesses();
            var latest = _savedProcesses.FirstOrDefault();
            if (latest is not null)
                ApplyProcess(latest);
        }
        catch (Exception ex)
        {
            _ = App.Log.WriteAsync("Não foi possível carregar os processos salvos do SPED Processos.", ex);
            StatusText.Text = "Os processos salvos não puderam ser carregados. Um novo preenchimento continua disponível.";
        }
    }

    private void ApplyProcess(SavedProcess process)
    {
        _loading = true;
        try
        {
            var state = process.State ?? new State();
            var savedFlow = string.IsNullOrWhiteSpace(state.Flow) ? "Inconsistência Bancária" : state.Flow.Trim();
            SetEditableComboValue(FlowBox, savedFlow, selectMatchingItem: true);
            ConfigureFlow(resetReport: false);
            RecipientBox.Text = state.Recipient;
            ProcessInterestedBox.Text = string.IsNullOrWhiteSpace(state.ProcessInterested)
                ? OrganizationIdentity.Treasury
                : state.ProcessInterested;
            SetEditableComboValue(ReportBox, state.Report);
            OriginDocumentReferenceBox.Text = state.OriginDocumentReference;
            BeneficiaryModeBox.SelectedIndex = state.BeneficiaryMode.Contains("Relação", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var selectedMilitary = FindMilitaryForState(state);
            MilitaryPickerBox.SelectedItem = selectedMilitary is null
                ? null
                : _allMilitary.FirstOrDefault(item => ReferenceEquals(item, selectedMilitary)
                    || (!string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(selectedMilitary.Cpf))
                        && MilitaryFormatting.Digits(item.Cpf) == MilitaryFormatting.Digits(selectedMilitary.Cpf)))
                    ?? selectedMilitary;
            MilitaryPickerBox.Text = selectedMilitary?.Display ?? string.Empty;
            MilitaryBox.Text = state.Military;
            ValueBox.Text = state.Value;
            BulletinBox.Text = state.Bulletin;
            BulletinDatePicker.SelectedDate = DateTime.TryParse(state.BulletinDate, out var bulletinDate) ? bulletinDate : DateTime.Today;
            SetEditableComboValue(PfBox, state.Pf, selectMatchingItem: true);
            ObservationBox.Text = state.Observation;

            var checkMap = state.Checks.ToDictionary(item => item.Text, item => item.Checked, StringComparer.OrdinalIgnoreCase);
            BuildChecks(checkMap);
            if (state.Checks.Count == 0 && state.LegacyChecks.Count > 0)
            {
                for (var index = 0; index < Math.Min(state.LegacyChecks.Count, _checks.Count); index++)
                    _checks[index].Checked = state.LegacyChecks[index];
            }

            _relation.Clear();
            foreach (var item in state.Relation)
                _relation.Add(item.Clone());

            _diexFile = state.DiexFile;
            _spedProcessFile = state.SpedProcessFile;
            _bulletinFile = state.BulletinFile;
            _reportFile = state.ReportFile;
            _pfFile = state.PfFile;
            _financialStatementFiles = state.FinancialStatementFiles;
            _paystubFiles = state.PaystubFiles;
            _otherFile = state.OtherFile;
            DiexNumberBox.Text = state.DiexNumber;
            _processAutuatedAt = state.ProcessAutuatedAt;
            DiexFileText.Text = FileLabel(_diexFile);
            ProcessFileText.Text = FileLabel(_spedProcessFile);
            BulletinFileText.Text = FilesLabel(_bulletinFile);
            ReportFileText.Text = FilesLabel(_reportFile);
            PfFileText.Text = FilesLabel(_pfFile);
            FinancialStatementFileText.Text = FilesLabel(_financialStatementFiles);
            PaystubFileText.Text = FilesLabel(_paystubFiles);
            OtherFileText.Text = FilesLabel(_otherFile);
            DiexText.Text = state.DiexText;
            DispatchText.Text = state.DispatchText;
            // Texto salvo pode ter sido revisado manualmente; a abertura nunca deve sobrescrevê-lo.
            _lastGeneratedDiex = string.Empty;
            _lastGeneratedDispatch = string.Empty;
            ProcessTitleBox.Text = process.Title;
            _currentProcessId = process.Id;
            SavedProcessBox.SelectedItem = process;
            ChecklistList.Items.Refresh();
            _dirty = false;
        }
        finally
        {
            _loading = false;
        }
        Refresh();
        _dirty = false;
        RefreshProcessHeader();
    }

    private async Task PersistProcessesAsync()
    {
        var ordered = _savedProcesses.OrderByDescending(item => item.UpdatedAt).ToList();
        await WriteJsonAtomicAsync(_processesFile, ordered);
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, json);
        File.Move(temporary, path, true);
    }

    private void SortSavedProcesses(string? selectId = null)
    {
        selectId ??= _currentProcessId;
        var ordered = _savedProcesses.OrderByDescending(item => item.UpdatedAt).ToList();
        _savedProcesses.Clear();
        foreach (var item in ordered) _savedProcesses.Add(item);
        SavedProcessBox.SelectedItem = _savedProcesses.FirstOrDefault(item => item.Id == selectId);
    }

    private static string BuildDefaultProcessTitle(State state)
    {
        var interested = state.SelectedMilitary?.WarName;
        if (string.IsNullOrWhiteSpace(interested)) interested = state.SelectedMilitary?.Name;
        if (string.IsNullOrWhiteSpace(interested)) interested = state.Relation.FirstOrDefault()?.WarName;
        if (string.IsNullOrWhiteSpace(interested)) interested = state.Relation.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(interested))
            interested = state.Military.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var title = string.IsNullOrWhiteSpace(interested) ? state.Flow : $"{state.Flow} — {interested}";
        title = string.IsNullOrWhiteSpace(title) ? "Processo SPED" : title.Trim();
        return title.Length <= 110 ? title : title[..110].TrimEnd();
    }

    private void OpenProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_savedProcesses.Count == 0)
        {
            SigfurDialog.Show(this, "Ainda não há processos salvos.", "Processos salvos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picker = new BizurometroSavedProcessesWindow(_savedProcesses, _currentProcessId) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedProcess is not SavedProcess selected) return;
        if (!ConfirmDiscardChanges(selected.Id == _currentProcessId ? "recarregar este processo" : "abrir outro processo")) return;
        ApplyProcess(selected);
    }

    private void NewProcess_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("iniciar um novo processo")) return;
        _currentProcessId = null;
        ResetForm();
        _dirty = false;
        SavedProcessBox.SelectedItem = null;
        RefreshProcessHeader();
        StatusText.Text = "Novo processo iniciado. Preencha os dados e clique em Salvar processo.";
    }

    private async void DuplicateProcess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sourceTitle = string.IsNullOrWhiteSpace(ProcessTitleBox.Text)
                ? BuildDefaultProcessTitle(CaptureState())
                : ProcessTitleBox.Text.Trim();
            var now = DateTimeOffset.Now;
            var copyId = Guid.NewGuid().ToString("N");
            var copy = new SavedProcess
            {
                Id = copyId,
                Title = $"Cópia de {sourceTitle}",
                CreatedAt = now,
                UpdatedAt = now,
                State = PreserveProcessFiles(CaptureState(), copyId)
            };
            _savedProcesses.Add(copy);
            _currentProcessId = copy.Id;
            await PersistProcessesAsync();
            SortSavedProcesses(copy.Id);
            ApplyProcess(copy);
            StatusText.Text = $"Cópia criada como “{copy.Title}”. Edite e salve normalmente.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, $"Não foi possível duplicar o processo.\n\n{ex.Message}", "Duplicar processo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteProcess_Click(object sender, RoutedEventArgs e)
    {
        var selected = SavedProcessBox.SelectedItem as SavedProcess
                       ?? _savedProcesses.FirstOrDefault(item => item.Id == _currentProcessId);
        if (selected is null)
        {
            SigfurDialog.Show(this, "Selecione um processo salvo para excluir.", "Excluir processo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SigfurDialog.Show(this,
                $"Excluir definitivamente o processo “{selected.Title}”?\n\nO preenchimento salvo será removido, mas os arquivos originais selecionados como anexos não serão apagados.",
                "Excluir processo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var deletingCurrent = selected.Id == _currentProcessId;
            _savedProcesses.Remove(selected);
            try
            {
                await PersistProcessesAsync();
            }
            catch
            {
                _savedProcesses.Add(selected);
                SortSavedProcesses(selected.Id);
                throw;
            }
            SortSavedProcesses();
            if (deletingCurrent)
            {
                _currentProcessId = null;
                ResetForm();
                _dirty = false;
            }
            SavedProcessBox.SelectedItem = _savedProcesses.FirstOrDefault();
            RefreshProcessHeader();
            StatusText.Text = "Processo excluído. Os arquivos originais dos anexos foram preservados.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, $"Não foi possível excluir o processo.\n\n{ex.Message}", "Excluir processo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ConfirmDiscardChanges(string action)
    {
        if (!_dirty) return true;
        return SigfurDialog.Show(this,
                   $"Há alterações ainda não salvas. Deseja descartá-las e {action}?",
                   "Alterações não salvas", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static State? DeserializeState(string json)
    {
        try { return JsonSerializer.Deserialize<State>(json); }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            static string Get(JsonElement element, string name)
                => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
            var state = new State
            {
                Flow = Get(root, "Flow"), Recipient = Get(root, "Recipient"), ProcessInterested = Get(root, "ProcessInterested"), Report = Get(root, "Report"), OriginDocumentReference = Get(root, "OriginDocumentReference"), Military = Get(root, "Military"),
                Value = Get(root, "Value"), Bulletin = Get(root, "Bulletin"), Pf = Get(root, "Pf"),
                Observation = Get(root, "Observation"), DiexFile = Get(root, "DiexFile"), SpedProcessFile = Get(root, "SpedProcessFile"), DiexNumber = Get(root, "DiexNumber"),
                BulletinFile = Get(root, "BulletinFile"), ReportFile = Get(root, "ReportFile"), PfFile = Get(root, "PfFile"),
                FinancialStatementFiles = Get(root, "FinancialStatementFiles"), PaystubFiles = Get(root, "PaystubFiles"), OtherFile = Get(root, "OtherFile")
            };
            if (root.TryGetProperty("Checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
                state.LegacyChecks = checks.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.True or JsonValueKind.False).Select(x => x.GetBoolean()).ToList();
            return state;
        }
    }

    private void ExportSummary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar resumo do SPED Processos",
            Filter = "Arquivo de texto|*.txt",
            DefaultExt = ".txt",
            FileName = $"resumo_sped_processos_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var content = new StringBuilder()
            .AppendLine("SPED PROCESSOS — RESUMO DO PROCESSO")
            .AppendLine(new string('=', 54))
            .AppendLine()
            .AppendLine(DiexText.Text)
            .AppendLine()
            .AppendLine(new string('-', 54))
            .AppendLine(BuildChecklistText())
            .AppendLine()
            .AppendLine(new string('-', 54))
            .AppendLine("DESPACHO")
            .AppendLine(DispatchText.Text)
            .AppendLine()
            .AppendLine(new string('-', 54))
            .AppendLine("ANEXOS SELECIONADOS")
            .AppendLine($"Número do DIEx no SPED: {(string.IsNullOrWhiteSpace(DiexNumberBox.Text) ? "Não informado" : DiexNumberBox.Text.Trim())}")
            .AppendLine($"DIEx: {FileLabel(_diexFile)}")
            .AppendLine($"Processo no SPED: {FileLabel(_spedProcessFile)}")
            .AppendLine($"{OriginDocumentDescription()}: {FilesLabel(_reportFile)}")
            .AppendLine($"Publicação/boletim: {FilesLabel(_bulletinFile)}")
            .AppendLine($"{PaymentAttachmentKind()}: {FilesLabel(_pfFile)}")
            .AppendLine($"Ficha financeira: {FilesLabel(_financialStatementFiles)}")
            .AppendLine($"Contracheque: {FilesLabel(_paystubFiles)}")
            .AppendLine($"Outros: {FilesLabel(_otherFile)}")
            .ToString();

        File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
        SigfurDialog.Show(this, "Resumo exportado com sucesso.", "SPED Processos", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Limpar todo o preenchimento atual?", "SPED Processos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ResetForm();
        MarkDirty();
    }

    private void ResetForm()
    {
        _loading = true;
        try
        {
            FlowBox.SelectedIndex = 0;
            RecipientBox.Clear();
            ProcessInterestedBox.Text = OrganizationIdentity.Treasury;
            BeneficiaryModeBox.SelectedIndex = 0;
            ConfigureFlow(resetReport: true);
            OriginDocumentReferenceBox.Clear();
            MilitaryPickerBox.SelectedItem = null;
            MilitaryPickerBox.Text = string.Empty;
            MilitaryBox.Clear();
            ValueBox.Clear();
            BulletinBox.Clear();
            BulletinDatePicker.SelectedDate = DateTime.Today;
            SetEditableComboValue(PfBox, string.Empty);
            ObservationBox.Clear();
            ProcessTitleBox.Clear();
            _relation.Clear();
            _diexFile = _bulletinFile = _reportFile = _pfFile = _financialStatementFiles = _paystubFiles = _otherFile = _spedProcessFile = string.Empty;
            _processAutuatedAt = null;
            DiexNumberBox.Clear();
            DiexFileText.Text = "Ainda não criado";
            ProcessFileText.Text = "Ainda não criado";
            BulletinFileText.Text = ReportFileText.Text = PfFileText.Text = FinancialStatementFileText.Text = PaystubFileText.Text = OtherFileText.Text = "Não selecionado";
            BuildChecks();
            _lastGeneratedDiex = _lastGeneratedDispatch = string.Empty;
            DiexText.Clear();
            DispatchText.Clear();
        }
        finally
        {
            _loading = false;
        }
        Refresh();
    }

    private static string FileLabel(string path)
        => string.IsNullOrWhiteSpace(path) ? "Não selecionado" : Path.GetFileName(path);

    private static string FilesLabel(string paths)
    {
        var files = SplitPaths(paths);
        if (files.Count == 0) return "Não selecionado";
        return files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} arquivos selecionados";
    }

    private static List<string> SplitPaths(string? paths)
        => (paths ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string RemovePath(string paths, string path)
        => string.Join("|", SplitPaths(paths).Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)));

    private static void AddAttachmentLines(ICollection<string> lines, string kind, string paths)
    {
        foreach (var path in SplitPaths(paths)) lines.Add($"- {kind} — arquivo: {Path.GetFileName(path)};");
    }

    private static string FormatValueWithWords(string? input)
    {
        var raw = (input ?? string.Empty).Trim();
        if (!NumberToWordsService.TryParse(raw, out var value)) return raw;
        return $"{NumberToWordsService.FormatNumber(value, true)} ({NumberToWordsService.Convert(value, true)})";
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }
        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public sealed class CheckRow
    {
        public string Text { get; set; } = string.Empty;
        public bool Checked { get; set; }
    }

    private sealed record DocumentProfile(
        string OriginReferenceLabel,
        string[] OriginChoices,
        string DefaultOrigin,
        bool UsePublication,
        bool UsePaymentDocument,
        string PublicationReferenceLabel,
        string PaymentReferenceLabel,
        string OriginFileButton,
        string PublicationFileButton,
        string PaymentFileButton,
        string Guidance);

    private sealed class SavedFileRow
    {
        public string Kind { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class BizuMilitaryOption
    {
        public string Rank { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string WarName { get; set; } = string.Empty;
        public string Cpf { get; set; } = string.Empty;
        public string PrecCp { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string FormattedCpf => MilitaryFormatting.FormatCpf(Cpf);
        public string Display => $"{MilitaryRankService.ShortName(Rank)} {(string.IsNullOrWhiteSpace(WarName) ? Name : WarName)} — {Name} | CPF {MilitaryFormatting.FormatCpf(Cpf)} | {Source}".Trim();
        public string SearchText => Normalize($"{Rank} {Name} {WarName} {Cpf} {PrecCp} {Source}");
        public string IdentityText
        {
            get
            {
                var rank = MilitaryRankService.ShortName(Rank);
                var firstLine = $"{rank} {Name}".Trim();
                var prec = MilitaryFormatting.Digits(PrecCp);
                var cpf = MilitaryFormatting.FormatCpf(Cpf);
                return $"{firstLine}\nPrec-CP {(string.IsNullOrWhiteSpace(prec) ? "—" : prec)} CPF {(string.IsNullOrWhiteSpace(cpf) ? "—" : cpf)}";
            }
        }

        public BizuMilitaryOption Clone() => new()
        {
            Rank = Rank,
            Name = Name,
            WarName = WarName,
            Cpf = Cpf,
            PrecCp = PrecCp,
            Source = Source
        };

        public static BizuMilitaryOption FromMilitary(MilitaryRecord item, string source) => new()
        {
            Rank = item.Rank,
            Name = item.Name,
            WarName = item.WarName,
            Cpf = item.Cpf,
            PrecCp = item.PrecCp,
            Source = source
        };

        public static BizuMilitaryOption FromLicensed(LicensedTransferredRecord item, string source) => new()
        {
            Rank = item.Rank,
            Name = item.Name,
            WarName = item.WarName,
            Cpf = item.Cpf,
            PrecCp = item.PrecCp,
            Source = source
        };

        public static BizuMilitaryOption FromManual(string text)
        {
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var name = lines.FirstOrDefault() ?? text.Trim();
            var cpfMatch = System.Text.RegularExpressions.Regex.Match(text, @"CPF\s*([0-9.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var precMatch = System.Text.RegularExpressions.Regex.Match(text, @"PREC(?:-CP)?\s*([0-9.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return new BizuMilitaryOption
            {
                Name = name,
                Cpf = cpfMatch.Success ? cpfMatch.Groups[1].Value : string.Empty,
                PrecCp = precMatch.Success ? precMatch.Groups[1].Value : string.Empty,
                Source = "Manual"
            };
        }
    }

    public sealed class SavedProcess
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public State State { get; set; } = new();
        [JsonIgnore]
        public string DisplayName => $"{Title}  •  {UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm}";
        [JsonIgnore]
        public BizuMilitaryOption? PrimaryMilitary => State.SelectedMilitary ?? State.Relation.FirstOrDefault();
        [JsonIgnore]
        public string PersonFullName => PrimaryMilitary?.Name ?? FirstMilitaryLine(State.Military);
        [JsonIgnore]
        public string PersonWarName => PrimaryMilitary?.WarName ?? string.Empty;
        [JsonIgnore]
        public string PersonRank => PrimaryMilitary?.Rank ?? string.Empty;
        [JsonIgnore]
        public string PersonCpf => PrimaryMilitary?.Cpf ?? string.Empty;
        [JsonIgnore]
        public string UpdatedAtText => UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        [JsonIgnore]
        public string SearchText => Normalize($"{Title} {State.Flow} {State.Report} {State.OriginDocumentReference} {State.Recipient} {State.DiexNumber} {PersonRank} {PersonFullName} {PersonWarName} {PersonCpf} {State.Military} " +
                                              string.Join(' ', State.Relation.Select(item => $"{item.Rank} {item.Name} {item.WarName} {item.Cpf} {item.PrecCp}")));

        private static string FirstMilitaryLine(string? value)
            => (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    public sealed class CheckState
    {
        public string Text { get; set; } = string.Empty;
        public bool Checked { get; set; }
    }

    public sealed class State
    {
        public string Flow { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public string ProcessInterested { get; set; } = string.Empty;
        public string Report { get; set; } = string.Empty;
        public string OriginDocumentReference { get; set; } = string.Empty;
        [JsonIgnore]
        public string OriginDocumentDisplay => string.IsNullOrWhiteSpace(OriginDocumentReference)
            ? Report
            : $"{Report}: {OriginDocumentReference}";
        public string BeneficiaryMode { get; set; } = "Um militar";
        public string Military { get; set; } = string.Empty;
        public BizuMilitaryOption? SelectedMilitary { get; set; }
        public string Value { get; set; } = string.Empty;
        public string Bulletin { get; set; } = string.Empty;
        public string BulletinDate { get; set; } = string.Empty;
        public string Pf { get; set; } = string.Empty;
        public string Observation { get; set; } = string.Empty;
        public List<CheckState> Checks { get; set; } = [];
        public List<bool> LegacyChecks { get; set; } = [];
        public List<BizuMilitaryOption> Relation { get; set; } = [];
        public string DiexFile { get; set; } = string.Empty;
        public string SpedProcessFile { get; set; } = string.Empty;
        public string BulletinFile { get; set; } = string.Empty;
        public string ReportFile { get; set; } = string.Empty;
        public string PfFile { get; set; } = string.Empty;
        public string FinancialStatementFiles { get; set; } = string.Empty;
        public string PaystubFiles { get; set; } = string.Empty;
        public string OtherFile { get; set; } = string.Empty;
        public string DiexNumber { get; set; } = string.Empty;
        public DateTimeOffset? ProcessAutuatedAt { get; set; }
        public string DiexText { get; set; } = string.Empty;
        public string DispatchText { get; set; } = string.Empty;
    }
}
