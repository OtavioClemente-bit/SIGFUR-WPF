using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public sealed class ExercisePreviousWindow : Window
{
    private readonly AppPaths _paths;
    private readonly ExercisePreviousRepository _repository;
    private readonly ExercisePreviousAssetsService _assets;
    private readonly ExercisePreviousExcelService _excel;
    private readonly ExercisePreviousImportService _importer;
    private readonly ExercisePreviousDocumentService _documents;
    private readonly CpexExerciseAutomationService _cpex;
    private readonly ExercisePreviousProtocolService _protocols;
    private readonly ObservableCollection<ExercisePreviousProcess> _processes = [];
    private readonly ObservableCollection<string> _situationPresets = new(ExercisePreviousDefaults.Situations);
    private readonly ObservableCollection<string> _bankPresets = [];
    private readonly ObservableCollection<string> _indicativePresets = new(ExercisePreviousDefaults.Indicatives);
    private readonly ObservableCollection<string> _previousTypePresets = new(ExercisePreviousDefaults.PreviousExerciseTypes);
    private readonly ObservableCollection<string> _yesNoPresets = new(ExercisePreviousDefaults.YesNo);
    private readonly ObservableCollection<string> _researchPresets = new(new[] { "Sim", "Não" });
    private readonly Dictionary<string, ObservableCollection<string>> _additionalPresets = new(StringComparer.OrdinalIgnoreCase);
    private ExercisePreviousProcess _current = new();
    private DataGrid _processGrid = null!;
    private DataGrid _codesGrid = null!;
    private DataGrid _entriesGrid = null!;
    private TextBlock _processLabel = null!;
    private TextBlock _status = null!;
    private TextBlock _summary = null!;
    private TextBox _preview = null!;
    private TextBox _rightMaterializationBox = null!;
    private TextBox _nonPaymentExplanationBox = null!;
    private ComboBox _archiveFilter = null!;
    private ProgressBar _busy = null!;
    private string _lastWorkbook = string.Empty;
    private bool _loading;

    public ExercisePreviousWindow(MilitaryRepository militaryRepository, AppPaths paths, LogService log)
    {
        _paths = paths;
        _repository = new ExercisePreviousRepository(paths, log);
        _assets = new ExercisePreviousAssetsService(paths);
        _excel = new ExercisePreviousExcelService(_assets, _repository);
        _importer = new ExercisePreviousImportService(_repository, log);
        _documents = new ExercisePreviousDocumentService(_assets);
        _cpex = new CpexExerciseAutomationService(paths, _repository);
        _protocols = new ExercisePreviousProtocolService(paths, _repository);

        Title = "SIGFUR — Exercício Anterior (EA) | IPCA-E";
        Width = 1500; Height = 900; MinWidth = 1080; MinHeight = 690;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        App.UiState.Attach(this);
        Content = BuildUi();
        DataContext = _current;
        Loaded += async (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            await InitializeAsync();
        };
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel();
        var top = BuildToolbar(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = BuildStatusBar(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);

        var body = new Grid { Margin = new Thickness(16, 8, 16, 12) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = BuildProcessPanel(); Grid.SetColumn(left, 0); body.Children.Add(left);
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 0, 2, 0) };
        splitter.SetResourceReference(BackgroundProperty, "BorderBrush");
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var tabs = BuildTabs(); Grid.SetColumn(tabs, 2); body.Children.Add(tabs);
        root.Children.Add(body);
        return root;
    }

    private UIElement BuildToolbar()
    {
        var outer = new Border { Padding = new Thickness(18, 14, 18, 12), BorderThickness = new Thickness(0, 0, 0, 1) };
        outer.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        outer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var panel = new DockPanel(); outer.Child = panel;
        var title = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 20, 0) };
        var titleText = new TextBlock { Text = "Exercício Anterior — IPCA-E", FontSize = 24, FontWeight = FontWeights.SemiBold };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        title.Children.Add(titleText);
        _processLabel = new TextBlock { Text = "Processo não salvo", Margin = new Thickness(0, 3, 0, 0) };
        _processLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        title.Children.Add(_processLabel); DockPanel.SetDock(title, Dock.Left); panel.Children.Add(title);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AddButton(buttons, "Novo", async () => await NewAsync(), EaButtonKind.Ghost);
        AddButton(buttons, "Salvar", async () => await SaveAsync(), EaButtonKind.Primary);
        AddButton(buttons, "Buscar militar", SearchMilitaryAsync, EaButtonKind.Secondary);
        AddButton(buttons, "Importar Excel", ImportExcelAsync, EaButtonKind.Secondary);
        AddSeparator(buttons);
        AddButton(buttons, "IPCA-E", ManageIpcaAsync, EaButtonKind.Ghost);
        AddButton(buttons, "Gerar Excel", GenerateWorkbookAsync, EaButtonKind.Primary, "Cria uma cópia XLSM sem proteção, preservando macros, fórmulas e formatação.");
        AddButton(buttons, "Gerar PDF", GeneratePdfAsync, EaButtonKind.Primary);
        AddButton(buttons, "Imprimir", PrintAsync, EaButtonKind.Secondary);
        AddSeparator(buttons);
        AddButton(buttons, "Capa", GenerateCoverAsync, EaButtonKind.Secondary);
        AddButton(buttons, "Requerimento", GenerateRequestAsync, EaButtonKind.Secondary);
        AddButton(buttons, "Capa + requerimento", GenerateBothAsync, EaButtonKind.Secondary);
        AddSeparator(buttons);
        AddButton(buttons, "CPEX Online", OpenCpexAsync, EaButtonKind.Primary);
        panel.Children.Add(buttons);
        return outer;
    }

    private UIElement BuildStatusBar()
    {
        var grid = new Grid { Height = 38, Margin = new Thickness(0) };
        grid.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock { Margin = new Thickness(16, 10, 8, 0), Text = "Preparando Exercício Anterior..." };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _busy = new ProgressBar { Width = 140, Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(8, 15, 16, 0) };
        Grid.SetColumn(_busy, 1); grid.Children.Add(_status); grid.Children.Add(_busy); return grid;
    }

    private UIElement BuildProcessPanel()
    {
        var card = new Border { Padding = new Thickness(0) };
        ApplyStyle(card, "CardStyle");
        var panel = new DockPanel();
        card.Child = panel;
        var header = new StackPanel { Margin = new Thickness(14) };
        var heading = new TextBlock { Text = "Processos salvos", FontSize = 16, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        header.Children.Add(heading);
        _archiveFilter = new ComboBox { Margin = new Thickness(0, 9, 0, 0), ItemsSource = new[] { "Em andamento", "Arquivados / pagos", "Todos" }, SelectedIndex = 0 };
        _archiveFilter.SelectionChanged += async (_, _) => { if (!_loading) await RefreshProcessesAsync(); };
        header.Children.Add(_archiveFilter);
        var processButtons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        AddSmallButton(processButtons, "Abrir", OpenSelectedProcessAsync);
        AddSmallButton(processButtons, "Duplicar", DuplicateSelectedAsync);
        AddSmallButton(processButtons, "Arquivar", ArchiveSelectedAsync);
        AddSmallButton(processButtons, "Excluir", DeleteSelectedAsync, true);
        header.Children.Add(processButtons); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);

        _processGrid = new DataGrid
        {
            ItemsSource = _processes, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 8, 8), RowHeaderWidth = 0, MinRowHeight = 46
        };
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(ExercisePreviousProcess.Id)) { StringFormat = "0000" }, Width = 62 });
        var processNameFactory = new FrameworkElementFactory(typeof(HighlightedNameTextBlock));
        processNameFactory.SetBinding(HighlightedNameTextBlock.FullNameProperty, new Binding(nameof(ExercisePreviousProcess.FullName)));
        processNameFactory.SetBinding(HighlightedNameTextBlock.WarNameProperty, new Binding(nameof(ExercisePreviousProcess.WarName)));
        processNameFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        processNameFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
        _processGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Militar",
            CellTemplate = new DataTemplate { VisualTree = processNameFactory },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 230
        });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "Período", Binding = new Binding(nameof(ExercisePreviousProcess.PeriodStart)), Width = 92 });
        _processGrid.MouseDoubleClick += async (_, _) => await OpenSelectedProcessAsync();
        panel.Children.Add(_processGrid); return card;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl();
        tabs.Items.Add(Tab("Dados do processo", BuildDataTab()));
        tabs.Items.Add(Tab("Lançamentos", BuildEntriesTab()));
        tabs.Items.Add(Tab("Códigos", BuildCodesTab()));
        tabs.Items.Add(Tab("Documentos / Requerimento", BuildDocumentsTab()));
        tabs.Items.Add(Tab("CPEX Online", BuildCpexTab()));
        tabs.Items.Add(Tab("Prévia e totais", BuildPreviewTab()));
        return tabs;
    }

    private UIElement BuildDataTab()
    {
        var stack = FormStack();
        stack.Children.Add(Section("Militar", new[]
        {
            Row(Field("Posto/graduação", nameof(ExercisePreviousProcess.Rank)), Field("Nome completo", nameof(ExercisePreviousProcess.FullName), 2), Field("Nome de guerra", nameof(ExercisePreviousProcess.WarName))),
            Row(Field("CPF", nameof(ExercisePreviousProcess.Cpf)), Field("PREC-CP", nameof(ExercisePreviousProcess.PrecCp)), Field("Identidade", nameof(ExercisePreviousProcess.Identity)), PresetCombo("Situação", nameof(ExercisePreviousProcess.Situation), "situacao", _situationPresets)),
            Row(PresetCombo("Banco", nameof(ExercisePreviousProcess.Bank), "banco", _bankPresets), Field("Agência", nameof(ExercisePreviousProcess.Agency)), Field("Conta", nameof(ExercisePreviousProcess.Account)), DateField("Nascimento", nameof(ExercisePreviousProcess.BirthDate)))
        }));
        stack.Children.Add(Section("Processo e período", new[]
        {
            Row(Field("Número do processo", nameof(ExercisePreviousProcess.ProcessNumber)), Field("Ano", nameof(ExercisePreviousProcess.ProcessYear)), DateField("Início da dívida", nameof(ExercisePreviousProcess.PeriodStart)), DateField("Fim da dívida", nameof(ExercisePreviousProcess.PeriodEnd))),
            Row(Field("Atualizado até (AAAA-MM)", nameof(ExercisePreviousProcess.UpdatedThrough)), Field("Espécie da dívida", nameof(ExercisePreviousProcess.DebtType), 2), Field("Data da solicitação por extenso", nameof(ExercisePreviousProcess.RequestDateInWords), 2))
        }));
        stack.Children.Add(Section("Organização Militar", new[]
        {
            Row(PresetCombo("OM / Sigla", nameof(ExercisePreviousProcess.OrganizationName), "om_nome", AdditionalPresets("om_nome"), 2), Combo("RM", nameof(ExercisePreviousProcess.MilitaryRegion), ExercisePreviousDefaults.MilitaryRegions), PresetCombo("UG", nameof(ExercisePreviousProcess.ManagementUnit), "ug", AdditionalPresets("ug")), PresetCombo("CODOM", nameof(ExercisePreviousProcess.Codom), "codom", AdditionalPresets("codom"))),
            Row(PresetCombo("OD — nome e posto", nameof(ExercisePreviousProcess.OdNameRank), "od_nome_posto", AdditionalPresets("od_nome_posto"), 2), PresetCombo("Função do OD", nameof(ExercisePreviousProcess.OdFunction), "od_funcao", AdditionalPresets("od_funcao")), PresetCombo("Cidade/UF", nameof(ExercisePreviousProcess.CityState), "cidade_estado", AdditionalPresets("cidade_estado"))),
            Row(PresetCombo("Chefe da 1ª Seção — nome e posto", nameof(ExercisePreviousProcess.PersonnelChiefNameRank), "chefe_pessoal_nome_posto", AdditionalPresets("chefe_pessoal_nome_posto"), 2), PresetCombo("Função", nameof(ExercisePreviousProcess.PersonnelChiefFunction), "chefe_pessoal_funcao", AdditionalPresets("chefe_pessoal_funcao"))),
            Row(PresetCombo("Fiscal administrativo — nome e posto", nameof(ExercisePreviousProcess.AdministrativeInspectorNameRank), "fiscal_adm_nome_posto", AdditionalPresets("fiscal_adm_nome_posto"), 2), PresetCombo("Função", nameof(ExercisePreviousProcess.AdministrativeInspectorFunction), "fiscal_adm_funcao", AdditionalPresets("fiscal_adm_funcao")))
        }));
        stack.Children.Add(Section("Publicação", new[]
        {
            Row(DateField("Data do requerimento", nameof(ExercisePreviousProcess.RequestDate)), Field("BI / ADT (somente número)", nameof(ExercisePreviousProcess.BulletinNumber)), DateField("Data do BI", nameof(ExercisePreviousProcess.BulletinDate)))
        }));
        var dataActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AddButton(dataActions, "Gerar data da solicitação por extenso", GenerateDateInWordsAsync);
        stack.Children.Add(dataActions);
        return Scroll(stack);
    }

    private UIElement BuildDocumentsTab()
    {
        var stack = FormStack();
        stack.Children.Add(Section("Capa e expediente", new[]
        {
            Row(PresetCombo("Protocolo geral", nameof(ExercisePreviousProcess.GeneralProtocol), "protocolo_geral", AdditionalPresets("protocolo_geral")), PresetCombo("Seção", nameof(ExercisePreviousProcess.Section), "secao", AdditionalPresets("secao")), Field("Assunto nº", nameof(ExercisePreviousProcess.SubjectNumber)), Field("Anexos / folhas", nameof(ExercisePreviousProcess.AttachmentSheets))),
            Row(PresetCombo("Assunto", nameof(ExercisePreviousProcess.SubjectText), "assunto_texto", AdditionalPresets("assunto_texto"), 2), PresetCombo("Destinatário", nameof(ExercisePreviousProcess.Recipient), "destinatario", AdditionalPresets("destinatario"), 2)),
            Row(PresetCombo("Objeto", nameof(ExercisePreviousProcess.Object), "objeto", AdditionalPresets("objeto"), 2), Field("Telefone", nameof(ExercisePreviousProcess.Phone)))
        }));
        stack.Children.Add(Section("Motivo do requerimento", new[]
        {
            MultiField("Motivo do pagamento / requerimento", nameof(ExercisePreviousProcess.PaymentReason), 105)
        }));
        stack.Children.Add(Section("Requerimento de EA", new[]
        {
            Row(Field("EB do requerimento", nameof(ExercisePreviousProcess.EbRequest)), Field("EB da informação", nameof(ExercisePreviousProcess.EbInformation)), PresetCombo("Referente a", nameof(ExercisePreviousProcess.RefersTo), "referente_a", AdditionalPresets("referente_a"), 2), Field("Valor requerido", nameof(ExercisePreviousProcess.RequestedValue))),
            Row(PresetCombo("OD à época — nome", nameof(ExercisePreviousProcess.FormerOdName), "od_epoca_nome", AdditionalPresets("od_epoca_nome"), 2), PresetCombo("Identidade", nameof(ExercisePreviousProcess.FormerOdIdentity), "od_epoca_idt", AdditionalPresets("od_epoca_idt")), PresetCombo("CPF", nameof(ExercisePreviousProcess.FormerOdCpf), "od_epoca_cpf", AdditionalPresets("od_epoca_cpf"))),
            Row(PresetCombo("Cmt Companhia", nameof(ExercisePreviousProcess.CompanyCommander), "cmt_companhia", AdditionalPresets("cmt_companhia"), 2), Field("Representante — nome", nameof(ExercisePreviousProcess.RepresentativeName), 2)),
            Row(Field("Representante — CPF", nameof(ExercisePreviousProcess.RepresentativeCpf)), Field("Representante — identidade", nameof(ExercisePreviousProcess.RepresentativeIdentity)))
        }));
        var representativeActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AddButton(representativeActions, "Buscar representante no cadastro", SearchRepresentativeAsync);
        stack.Children.Add(representativeActions);
        var materializationField = MultiField("Documento que materializou o direito", nameof(ExercisePreviousProcess.RightMaterializationDocument), 120, out _rightMaterializationBox);
        var nonPaymentField = MultiField("Explicação do não pagamento", nameof(ExercisePreviousProcess.NonPaymentExplanation), 120, out _nonPaymentExplanationBox);
        stack.Children.Add(Section("Fundamentação da Solicitação Verso", new[]
        {
            materializationField,
            MultiField("Boletim que averbou", nameof(ExercisePreviousProcess.BulletinThatRecorded), 65),
            nonPaymentField
        }));
        var aiActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        AddButton(aiActions, "✦ Gerar documento que materializou o direito", async () => await GenerateEaFieldWithAiAsync(_rightMaterializationBox, "documento que materializou o direito"), EaButtonKind.Secondary);
        AddButton(aiActions, "✦ Gerar explicação do não pagamento", async () => await GenerateEaFieldWithAiAsync(_nonPaymentExplanationBox, "explicação objetiva do não pagamento"), EaButtonKind.Primary);
        stack.Children.Add(aiActions);
        return Scroll(stack);
    }

    private UIElement BuildCpexTab()
    {
        var stack = FormStack();
        stack.Children.Add(Section("Classificação no CPEX", new[]
        {
            Row(PresetCombo("Indicativo", nameof(ExercisePreviousProcess.EaIndicative), "ea_indicativo", _indicativePresets, 2), PresetCombo("Tipo de Exercício Anterior", nameof(ExercisePreviousProcess.PreviousExerciseType), "tipo_exercicio_anterior", _previousTypePresets, 3)),
            Row(PresetCombo("Possui pensão judiciária", nameof(ExercisePreviousProcess.HasJudicialPension), "possui_pensao_judiciaria", _yesNoPresets), PresetCombo("Ficha cadastro", nameof(ExercisePreviousProcess.RegistrationFileResearch), "pesquisa_ficha_cadastro", _researchPresets), PresetCombo("Ficha financeira", nameof(ExercisePreviousProcess.FinancialFileResearch), "pesquisa_ficha_financeira", _researchPresets), PresetCombo("Levantamento SIAFI", nameof(ExercisePreviousProcess.SiafiResearch), "pesquisa_levantamento_siafi", _researchPresets)),
            Row(PresetCombo("Documento de remessa", nameof(ExercisePreviousProcess.RemittanceDocument), "documento_remessa", AdditionalPresets("documento_remessa"), 3))
        }));
        stack.Children.Add(Section("Protocolo e controle interno", new[]
        {
            Row(Field("Protocolo CPEX", nameof(ExercisePreviousProcess.CpexProtocol)), Field("Página de impressão", nameof(ExercisePreviousProcess.CpexPrintPage)), Field("Protocolado em", nameof(ExercisePreviousProcess.CpexProtocolledAt)), Field("Status", nameof(ExercisePreviousProcess.CpexStatus))),
            MultiField("Observações CPEX", nameof(ExercisePreviousProcess.CpexNotes), 90),
            MultiField("Observação de pagamento/arquivamento", nameof(ExercisePreviousProcess.PaidNotes), 70)
        }));
        var help = new Border { Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)), Padding = new Thickness(14), Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(6) };
        help.Child = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "A automação abre primeiro a Área da UA, preenche login quando configurado, aguarda captcha/login manual, entra em Exercícios Anteriores, preenche os dados e para antes de enviar ou protocolar. Pensão Judiciária é forçada para NÃO e as três pesquisas para SIM, conforme a regra do módulo original." };
        stack.Children.Add(help);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        AddButton(buttons, "Configurar navegador e operador", ConfigureCpexAsync);
        AddButton(buttons, "Abrir e preencher CPEX", OpenCpexAsync, true);
        AddButton(buttons, "Salvar protocolo / PDF", SaveCpexProtocolAsync);
        AddButton(buttons, "Abrir PDF salvo", OpenCpexPdfAsync);
        AddButton(buttons, "Boletim EA CPEx", OpenCpexBulletinAsync);
        stack.Children.Add(buttons);
        return Scroll(stack);
    }

    private UIElement BuildCodesTab()
    {
        var panel = new DockPanel { Margin = new Thickness(10) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "Os 17 códigos são carregados do próprio XLSM e podem ser ajustados por processo.", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
        AddButton(header, "Recarregar do modelo", LoadDefaultCodesAsync); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        _codesGrid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, ItemsSource = _current.Codes, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        _codesGrid.Columns.Add(new DataGridTextColumn { Header = "Ordem", Binding = new Binding(nameof(ExercisePreviousCode.Order)) { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = 70 });
        _codesGrid.Columns.Add(new DataGridTextColumn { Header = "Código / descrição", Binding = new Binding(nameof(ExercisePreviousCode.Description)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _codesGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Tipo", ItemsSource = new[] { "Receita", "Despesa", "-" }, SelectedItemBinding = new Binding(nameof(ExercisePreviousCode.Type)) { Mode = BindingMode.TwoWay }, Width = 150 });
        panel.Children.Add(_codesGrid); return panel;
    }

    private UIElement BuildEntriesTab()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerCard = new Border { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
        ApplyStyle(headerCard, "CardStyle");
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        var title = new TextBlock { Text = "Lançamentos por competência", FontSize = 18, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        heading.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "Cadastre cada valor em uma janela própria. O código aparece com nome e tipo, e o SIGFUR lança automaticamente nas abas Recebido e Devido do Excel.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 18, 0)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(subtitle);
        headerGrid.Children.Add(heading);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AddButton(actions, "Novo lançamento", AddEntryAsync, EaButtonKind.Primary);
        AddButton(actions, "Editar", EditEntryAsync, EaButtonKind.Secondary);
        AddButton(actions, "Duplicar", DuplicateEntryAsync, EaButtonKind.Ghost);
        AddButton(actions, "Excluir", RemoveEntryAsync, EaButtonKind.Danger);
        AddButton(actions, "Atualizar cálculo", RefreshPreviewAsync, EaButtonKind.Ghost);
        Grid.SetColumn(actions, 1);
        headerGrid.Children.Add(actions);
        headerCard.Child = headerGrid;
        root.Children.Add(headerCard);

        var gridCard = new Border { Padding = new Thickness(0) };
        ApplyStyle(gridCard, "CardStyle");
        Grid.SetRow(gridCard, 1);
        root.Children.Add(gridCard);

        _entriesGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            ItemsSource = _current.Entries,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            BorderThickness = new Thickness(0),
            RowHeight = 44
        };
        _entriesGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Competência",
            Binding = new Binding(nameof(ExercisePreviousEntry.Competence)),
            Width = 110
        });
        _entriesGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Código / descrição",
            Binding = new Binding(nameof(ExercisePreviousEntry.CodeOrder))
            {
                Converter = new ExercisePreviousCodeDisplayConverter(() => _current.Codes, includeType: false)
            },
            Width = new DataGridLength(2.2, DataGridLengthUnitType.Star)
        });
        _entriesGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Tipo",
            Binding = new Binding(nameof(ExercisePreviousEntry.CodeOrder))
            {
                Converter = new ExercisePreviousCodeDisplayConverter(() => _current.Codes, includeType: true)
            },
            Width = 105
        });
        _entriesGrid.Columns.Add(MoneyColumn("Recebido", nameof(ExercisePreviousEntry.Received)));
        _entriesGrid.Columns.Add(MoneyColumn("Devido", nameof(ExercisePreviousEntry.Due)));
        _entriesGrid.Columns.Add(new DataGridTextColumn { Header = "Líquido", Binding = new Binding(nameof(ExercisePreviousEntry.Net)) { StringFormat = "N2" }, IsReadOnly = true, Width = 120 });
        _entriesGrid.Columns.Add(new DataGridTextColumn { Header = "Fator IPCA-E", Binding = new Binding(nameof(ExercisePreviousEntry.Factor)) { StringFormat = "N8" }, IsReadOnly = true, Width = 120 });
        _entriesGrid.Columns.Add(new DataGridTextColumn { Header = "Líquido corrigido", Binding = new Binding(nameof(ExercisePreviousEntry.CorrectedNet)) { StringFormat = "N2" }, IsReadOnly = true, Width = 145 });
        _entriesGrid.MouseDoubleClick += async (_, _) => await EditEntryAsync();
        gridCard.Child = _entriesGrid;
        return root;
    }

    private UIElement BuildPreviewTab()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AddButton(top, "Atualizar prévia", RefreshPreviewAsync);
        AddButton(top, "Abrir pasta do processo", OpenProcessFolderAsync);
        _summary = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 8, 0, 0) };
        top.Children.Add(_summary); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        _preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(12) };
        panel.Children.Add(_preview); return panel;
    }

    private async Task InitializeAsync()
    {
        await BusyAsync("Validando banco, modelos e IPCA-E...", async () =>
        {
            // A cópia/validação dos modelos pode envolver OneDrive ou antivírus.
            // Executa fora da thread da interface para a janela abrir imediatamente.
            await Task.Run(_assets.EnsureInstalled);
            await _repository.EnsureSchemaAsync();
            await ReloadPresetsAsync();
            await RefreshProcessesAsync();
            await NewAsync(false);
            var maxIpca = await _repository.GetMaxIpcaCompetenceAsync();
            _status.Text = string.IsNullOrWhiteSpace(maxIpca) ? "IPCA-E ainda não importado. Use Atualizar IPCA." : $"IPCA-E disponível até {maxIpca}.";
        });
    }

    private async Task NewAsync(bool announce = true)
    {
        var process = new ExercisePreviousProcess
        {
            ProcessYear = DateTime.Today.Year,
            RequestDate = DateTime.Today.ToString("yyyy-MM-dd"),
            RequestDateInWords = DateInWords(DateTime.Today),
            BulletinDate = DateTime.Today.ToString("yyyy-MM-dd")
        };
        try { foreach (var code in await _excel.ReadDefaultCodesAsync()) process.Codes.Add(code); }
        catch { for (var i = 1; i <= 17; i++) process.Codes.Add(new ExercisePreviousCode { Order = i }); }
        SetCurrent(process);
        if (announce) SetStatus("Novo processo iniciado. Preencha os dados e salve.");
    }

    private void SetCurrent(ExercisePreviousProcess process)
    {
        _current = process; DataContext = null; DataContext = _current;
        if (_codesGrid is not null) _codesGrid.ItemsSource = _current.Codes;
        if (_entriesGrid is not null)
        {
            _entriesGrid.ItemsSource = _current.Entries;
            _entriesGrid.Items.Refresh();
        }
        _processLabel.Text = process.Id <= 0 ? "Processo não salvo" : $"Processo {process.Id:0000} — {process.ArchiveStatus}";
        _lastWorkbook = string.Empty;
        _ = RefreshPreviewAsync();
    }

    private async Task SaveAsync()
    {
        await CommitGridsAsync();
        await BusyAsync("Salvando processo EA...", async () =>
        {
            var id = await _repository.SaveAsync(_current);
            _processLabel.Text = $"Processo {id:0000} — {_current.ArchiveStatus}";
            await RefreshProcessesAsync();
            SetStatus($"Processo {id:0000} salvo com códigos e lançamentos.");
        });
    }

    private async Task RefreshProcessesAsync()
    {
        _loading = true;
        try
        {
            bool? paid = _archiveFilter?.SelectedIndex switch { 0 => false, 1 => true, _ => null };
            var list = await _repository.ListAsync(paid);
            _processes.Clear(); foreach (var item in list) _processes.Add(item);
        }
        finally { _loading = false; }
    }

    private async Task OpenSelectedProcessAsync()
    {
        if (_processGrid.SelectedItem is not ExercisePreviousProcess selected) return;
        await BusyAsync("Abrindo processo salvo...", async () =>
        {
            var process = await _repository.GetAsync(selected.Id) ?? throw new InvalidOperationException("Processo não encontrado.");
            if (process.Codes.Count == 0) foreach (var code in await _excel.ReadDefaultCodesAsync()) process.Codes.Add(code);
            SetCurrent(process); SetStatus($"Processo {process.Id:0000} carregado.");
        });
    }

    private async Task DuplicateSelectedAsync()
    {
        if (_processGrid.SelectedItem is not ExercisePreviousProcess selected) return;
        await BusyAsync("Duplicando processo...", async () =>
        {
            var id = await _repository.DuplicateAsync(selected.Id);
            await RefreshProcessesAsync();
            var process = await _repository.GetAsync(id) ?? throw new InvalidOperationException("A cópia foi criada, mas não pôde ser reaberta.");
            SetCurrent(process); SetStatus($"Cópia criada como processo {id:0000}; protocolo e arquivamento foram limpos.");
        });
    }

    private async Task ArchiveSelectedAsync()
    {
        if (_processGrid.SelectedItem is not ExercisePreviousProcess selected) return;
        var archive = !selected.Paid;
        await _repository.SetPaidAsync(selected.Id, archive, selected.PaidNotes);
        await RefreshProcessesAsync();
        SetStatus(archive ? "Processo marcado como pago e arquivado." : "Processo restaurado para em andamento.");
    }

    private async Task DeleteSelectedAsync()
    {
        if (_processGrid.SelectedItem is not ExercisePreviousProcess selected) return;
        if (SigfurDialog.Show(this, $"Excluir definitivamente o processo {selected.Id:0000}?\n\nCódigos e lançamentos também serão removidos.", "Excluir EA", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _repository.DeleteAsync(selected.Id); await RefreshProcessesAsync();
        if (_current.Id == selected.Id) await NewAsync(false);
        SetStatus("Processo excluído.");
    }

    private async Task ImportExcelAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar planilha de Exercicio Anterior",
            Filter = "Planilhas suportadas|*.xlsx;*.ods;*.csv;*.txt|Excel moderno (*.xlsx)|*.xlsx|ODS (*.ods)|*.ods|CSV/TXT (*.csv;*.txt)|*.csv;*.txt|Excel antigo (*.xls)|*.xls|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        ExercisePreviousImportResult? result = null;
        try
        {
            await BusyAsync("Lendo planilha...", async () =>
            {
                var defaultCodes = await _excel.ReadDefaultCodesAsync();
                var progress = new Progress<string>(SetStatus);
                result = await _importer.ImportAsync(dialog.FileName, defaultCodes, progress);
                await RefreshProcessesAsync();
            });
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Importacao EA", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result is null) return;
        _preview.Text = result.Details + Environment.NewLine + Environment.NewLine + _preview.Text;
        new ExercisePreviousImportSummaryWindow(result) { Owner = this }.ShowDialog();
        SetStatus($"Importacao concluida: {result.Imported} importado(s), {result.Linked} vinculado(s), {result.Pending} pendente(s).");
    }

    private async Task SearchMilitaryAsync()
    {
        var dialog = new ExercisePreviousMilitarySearchWindow(_repository) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected is null) return;
        var m = dialog.Selected;
        ApplyMilitarySnapshot(_current, m);
        DataContext = null; DataContext = _current;
        SetStatus($"Dados de {m.FullName} copiados do banco ({m.Source}).");
        await RefreshPreviewAsync();
    }

    private async Task SearchRepresentativeAsync()
    {
        var dialog = new ExercisePreviousMilitarySearchWindow(_repository) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected is null) return;
        _current.RepresentativeName = dialog.Selected.FullName;
        _current.RepresentativeCpf = dialog.Selected.Cpf;
        _current.RepresentativeIdentity = dialog.Selected.Identity;
        DataContext = null; DataContext = _current;
        SetStatus($"Representante {dialog.Selected.FullName} copiado do cadastro.");
        await Task.CompletedTask;
    }

    private static void ApplyMilitarySnapshot(ExercisePreviousProcess process, ExercisePreviousMilitarySearchResult m)
    {
        process.MilitaryId = m.ActiveMilitaryId;
        process.Rank = Prefer(process.Rank, m.Rank);
        process.FullName = Prefer(process.FullName, m.FullName);
        process.WarName = Prefer(process.WarName, m.WarName);
        process.Cpf = Prefer(process.Cpf, m.Cpf);
        process.PrecCp = Prefer(process.PrecCp, m.PrecCp);
        process.Identity = Prefer(process.Identity, m.Identity);
        process.BirthDate = Prefer(process.BirthDate, m.BirthDate);
        process.Phone = Prefer(process.Phone, m.Phone);
        process.Bank = Prefer(process.Bank, m.Bank);
        process.Agency = Prefer(process.Agency, m.Agency);
        process.Account = Prefer(process.Account, m.Account);

        static string Prefer(string current, string incoming)
            => string.IsNullOrWhiteSpace(incoming) ? current : incoming;
    }

    private Task GenerateDateInWordsAsync()
    {
        if (!DateTime.TryParseExact(_current.RequestDate, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date))
        {
            SigfurDialog.Show(this, "Informe primeiro a data do requerimento.", "Data por extenso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }
        _current.RequestDateInWords = DateInWords(date);
        DataContext = null; DataContext = _current;
        SetStatus("Data da solicitação preenchida por extenso.");
        return Task.CompletedTask;
    }

    private async Task ManageIpcaAsync()
    {
        var dialog = new ExercisePreviousIpcaWindow(_repository, _excel) { Owner = this };
        dialog.ShowDialog();
        var max = await _repository.GetMaxIpcaCompetenceAsync();
        SetStatus(string.IsNullOrWhiteSpace(max) ? "IPCA-E ainda não importado." : $"IPCA-E disponível até {max}.");
        await RefreshPreviewAsync();
    }

    private async Task GenerateWorkbookAsync()
    {
        await SaveAsync();
        await BusyAsync("Preenchendo XLSM sem remover macros ou fórmulas...", async () =>
        {
            _lastWorkbook = await _excel.GenerateWorkbookAsync(_current, true);
            SetStatus("XLSM gerado e aberto: " + _lastWorkbook);
        });
    }

    private async Task EnsureWorkbookAsync()
    {
        await CommitGridsAsync();
        // PDF e impressão também salvam silenciosamente o estado atual. Assim, nenhum
        // campo digitado fica apenas na tela caso o usuário feche depois de gerar a saída.
        await _repository.SaveAsync(_current);
        await RefreshProcessesAsync();
        _processLabel.Text = $"Processo {_current.Id:0000} — {_current.ArchiveStatus}";

        // Sempre cria uma cópia nova para garantir que PDF/impressão usem exatamente
        // os dados atualmente exibidos, sem reaproveitar uma prévia antiga.
        _lastWorkbook = await _excel.GenerateWorkbookAsync(_current, false);
    }

    private async Task GeneratePdfAsync()
    {
        await BusyAsync("Gerando PDF das abas obrigatórias...", async () =>
        {
            await EnsureWorkbookAsync(); var pdf = await _excel.ExportPdfAsync(_lastWorkbook, _current); SetStatus("PDF gerado: " + pdf);
        });
    }

    private async Task PrintAsync()
    {
        await BusyAsync("Enviando abas válidas para impressão...", async () =>
        {
            await EnsureWorkbookAsync(); await _excel.PrintAsync(_lastWorkbook, _current); SetStatus("Impressão enviada pelo Microsoft Excel.");
        });
    }

    private async Task GenerateCoverAsync()
    {
        await SaveAsync(); var file = _documents.GenerateCover(_current); Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); SetStatus("CAPA gerada: " + file);
    }
    private async Task GenerateRequestAsync()
    {
        await SaveAsync(); var file = _documents.GenerateRequest(_current); Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); SetStatus("Requerimento gerado: " + file);
    }
    private async Task GenerateBothAsync()
    {
        await SaveAsync(); var cover = _documents.GenerateCover(_current); var request = _documents.GenerateRequest(_current);
        Process.Start(new ProcessStartInfo(cover) { UseShellExecute = true }); Process.Start(new ProcessStartInfo(request) { UseShellExecute = true });
        SetStatus("CAPA e requerimento gerados.");
    }

    private async Task ConfigureCpexAsync()
    {
        var settings = _cpex.LoadSettings(); var dialog = new ExercisePreviousCpexSettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() == true) { _cpex.SaveSettings(dialog.Settings); SetStatus("Configuração local do CPEX salva."); }
        await Task.CompletedTask;
    }

    private async Task OpenCpexAsync()
    {
        await SaveAsync();
        var settings = _cpex.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.LoginCpf))
        {
            var dialog = new ExercisePreviousCpexSettingsWindow(settings) { Owner = this };
            if (dialog.ShowDialog() != true) return; settings = dialog.Settings; _cpex.SaveSettings(settings);
        }
        await BusyAsync("Abrindo Área da UA e preenchendo CPEX...", async () =>
        {
            var log = await _cpex.OpenAndFillAsync(_current, settings);
            _preview.Text = log + Environment.NewLine + Environment.NewLine + _preview.Text;
            SetStatus("CPEX preenchido até a conferência manual; nenhum protocolo foi enviado automaticamente.");
        });
    }

    private async Task SaveCpexProtocolAsync()
    {
        await SaveAsync();
        var dialog = new ExercisePreviousProtocolWindow(_current, _protocols) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await SaveAsync();
        DataContext = null; DataContext = _current;
        SetStatus($"Protocolo CPEx {_current.CpexProtocol} salvo e processo marcado como OK.");
    }

    private Task OpenCpexPdfAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_current.CpexPrintPage) || !File.Exists(_current.CpexPrintPage))
                throw new FileNotFoundException("Nenhum PDF de protocolo válido está vinculado a este processo.", _current.CpexPrintPage);
            Process.Start(new ProcessStartInfo(_current.CpexPrintPage) { UseShellExecute = true });
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "PDF CPEx", MessageBoxButton.OK, MessageBoxImage.Error); }
        return Task.CompletedTask;
    }

    private async Task OpenCpexBulletinAsync()
    {
        await SaveAsync();
        try
        {
            var text = await _protocols.BuildBulletinTextAsync(_current);
            new ExercisePreviousBulletinWindow(_current, _protocols, text) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Boletim EA CPEx", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task LoadDefaultCodesAsync()
    {
        if (SigfurDialog.Show(this, "Substituir os 17 códigos atuais pelos códigos do XLSM?", "Recarregar códigos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var codes = await _excel.ReadDefaultCodesAsync(); _current.Codes.Clear(); foreach (var code in codes) _current.Codes.Add(code);
        _entriesGrid?.Items.Refresh();
        SetStatus("Códigos recarregados do modelo XLSM.");
    }

    private async Task AddEntryAsync()
    {
        var dialog = new ExercisePreviousEntryEditorWindow(_current.Codes) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        dialog.Entry.ProcessId = _current.Id;
        var existing = FindMatchingEntry(dialog.Entry);
        if (existing is not null)
        {
            var codeName = _current.Codes.FirstOrDefault(x => x.Order == existing.CodeOrder)?.Description ?? $"Código {existing.CodeOrder:00}";
            if (SigfurDialog.Show(this,
                    $"Já existe um lançamento para {existing.Competence} no código {existing.CodeOrder:00} — {codeName}.\n\nDeseja substituir os valores existentes?",
                    "Lançamento já cadastrado", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CopyEntryValues(dialog.Entry, existing);
            _entriesGrid.SelectedItem = existing;
            _entriesGrid.ScrollIntoView(existing);
            await RefreshPreviewAsync();
            _entriesGrid.Items.Refresh();
            SetStatus($"Lançamento {existing.Competence} do código {existing.CodeOrder:00} substituído.");
            return;
        }

        _current.Entries.Add(dialog.Entry);
        _entriesGrid.SelectedItem = dialog.Entry;
        _entriesGrid.ScrollIntoView(dialog.Entry);
        await RefreshPreviewAsync();
        SetStatus($"Lançamento {dialog.Entry.Competence} incluído no código {dialog.Entry.CodeOrder:00}.");
    }

    private async Task EditEntryAsync()
    {
        if (_entriesGrid.SelectedItem is not ExercisePreviousEntry selected)
        {
            SigfurDialog.Show(this, "Selecione um lançamento para editar.", "Lançamentos EA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ExercisePreviousEntryEditorWindow(_current.Codes, selected, "Editar lançamento") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var edited = dialog.Entry;
        var conflict = FindMatchingEntry(edited, selected);
        if (conflict is not null)
        {
            SigfurDialog.Show(this,
                $"Já existe outro lançamento para {edited.Competence} no código {edited.CodeOrder:00}.\n\nEdite o lançamento existente ou escolha outra competência/código.",
                "Lançamento duplicado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CopyEntryValues(edited, selected);
        await RefreshPreviewAsync();
        _entriesGrid.Items.Refresh();
        SetStatus($"Lançamento {selected.Competence} atualizado.");
    }

    private async Task DuplicateEntryAsync()
    {
        if (_entriesGrid.SelectedItem is not ExercisePreviousEntry selected)
        {
            SigfurDialog.Show(this, "Selecione um lançamento para duplicar.", "Lançamentos EA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var copy = new ExercisePreviousEntry
        {
            ProcessId = _current.Id,
            CodeOrder = selected.CodeOrder,
            Year = selected.Year,
            Month = selected.Month,
            Received = selected.Received,
            Due = selected.Due,
            Factor = selected.Factor
        };
        var dialog = new ExercisePreviousEntryEditorWindow(_current.Codes, copy, "Duplicar lançamento") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        dialog.Entry.Id = 0;
        dialog.Entry.ProcessId = _current.Id;
        var conflict = FindMatchingEntry(dialog.Entry);
        if (conflict is not null)
        {
            SigfurDialog.Show(this,
                $"Já existe um lançamento para {dialog.Entry.Competence} no código {dialog.Entry.CodeOrder:00}.\n\nEscolha outra competência ou outro código para a cópia.",
                "Lançamento duplicado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _current.Entries.Add(dialog.Entry);
        _entriesGrid.SelectedItem = dialog.Entry;
        _entriesGrid.ScrollIntoView(dialog.Entry);
        await RefreshPreviewAsync();
        SetStatus($"Cópia incluída em {dialog.Entry.Competence}.");
    }

    private ExercisePreviousEntry? FindMatchingEntry(ExercisePreviousEntry candidate, ExercisePreviousEntry? except = null)
        => _current.Entries.FirstOrDefault(x => !ReferenceEquals(x, except)
            && x.CodeOrder == candidate.CodeOrder
            && x.Year == candidate.Year
            && x.Month == candidate.Month);

    private static void CopyEntryValues(ExercisePreviousEntry source, ExercisePreviousEntry target)
    {
        target.CodeOrder = source.CodeOrder;
        target.Year = source.Year;
        target.Month = source.Month;
        target.Received = source.Received;
        target.Due = source.Due;
        target.Factor = source.Factor;
    }

    private async Task RemoveEntryAsync()
    {
        if (_entriesGrid.SelectedItem is not ExercisePreviousEntry entry) return;
        var code = _current.Codes.FirstOrDefault(x => x.Order == entry.CodeOrder)?.Description ?? $"Código {entry.CodeOrder:00}";
        if (SigfurDialog.Show(this, $"Excluir o lançamento de {entry.Competence}?\n\n{entry.CodeOrder:00} — {code}", "Excluir lançamento", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _current.Entries.Remove(entry);
        await RefreshPreviewAsync();
        SetStatus("Lançamento excluído.");
    }

    private async Task RefreshPreviewAsync()
    {
        if (_preview is null) return;
        await CommitGridsAsync();
        var total = await _repository.CalculateSummaryAsync(_current);
        _summary.Text = $"Líquido: {total.Net:N2}  |  Corrigido: {total.CorrectedNet:N2}";
        var sb = new StringBuilder();
        sb.AppendLine($"PROCESSO EA: {(_current.Id > 0 ? _current.Id.ToString("0000") : "NÃO SALVO")}");
        sb.AppendLine($"MILITAR: {_current.Rank} {_current.FullName} — {_current.WarName}");
        sb.AppendLine($"CPF: {_current.Cpf}   PREC-CP: {_current.PrecCp}   IDT: {_current.Identity}");
        sb.AppendLine($"PERÍODO: {_current.PeriodStart} a {_current.PeriodEnd}   ATUALIZADO ATÉ: {_current.UpdatedThrough}");
        sb.AppendLine(new string('─', 95));
        foreach (var group in _current.Entries.OrderBy(x => x.Year).ThenBy(x => x.Month).ThenBy(x => x.CodeOrder).GroupBy(x => x.Year))
        {
            sb.AppendLine($"ANO {group.Key}");
            foreach (var item in group)
            {
                var code = _current.Codes.FirstOrDefault(x => x.Order == item.CodeOrder)?.Description ?? $"Código {item.CodeOrder}";
                sb.AppendLine($"  {ExercisePreviousDefaults.Months[Math.Clamp(item.Month, 1, 12) - 1]} | {item.CodeOrder:00} {code,-35} | Rec {item.Received,12:N2} | Dev {item.Due,12:N2} | Líq corr {item.CorrectedNet,12:N2}");
            }
        }
        sb.AppendLine(new string('─', 95));
        sb.AppendLine($"TOTAL RECEBIDO:            {total.Received,15:N2}");
        sb.AppendLine($"TOTAL DEVIDO:              {total.Due,15:N2}");
        sb.AppendLine($"TOTAL LÍQUIDO:             {total.Net,15:N2}");
        sb.AppendLine($"TOTAL RECEBIDO CORRIGIDO:  {total.CorrectedReceived,15:N2}");
        sb.AppendLine($"TOTAL DEVIDO CORRIGIDO:    {total.CorrectedDue,15:N2}");
        sb.AppendLine($"TOTAL LÍQUIDO CORRIGIDO:   {total.CorrectedNet,15:N2}");
        _preview.Text = sb.ToString();
    }

    private Task OpenProcessFolderAsync()
    {
        var folder = _assets.GetProcessFolder(_current); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); return Task.CompletedTask;
    }

    private async Task CommitGridsAsync()
    {
        _codesGrid?.CommitEdit(DataGridEditingUnit.Cell, true); _codesGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        _entriesGrid?.CommitEdit(DataGridEditingUnit.Cell, true); _entriesGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        await Task.CompletedTask;
    }

    private async Task GenerateEaFieldWithAiAsync(TextBox target, string purpose)
    {
        target.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var prompt = $"""
Você está auxiliando no preenchimento de um processo de Exercício Anterior do SIGFUR.
Gere somente o texto destinado ao campo “{purpose}”, sem título adicional e sem inventar fatos.

DADOS DO PROCESSO
Militar: {_current.Rank} {_current.FullName} — nome de guerra: {_current.WarName}
Período da dívida: {_current.PeriodStart} a {_current.PeriodEnd}
Espécie da dívida: {_current.DebtType}
Assunto: {_current.SubjectText}
Objeto: {_current.Object}
Motivo informado: {_current.PaymentReason}
BI/ADT: {_current.BulletinNumber}, de {_current.BulletinDate}
Documento já informado: {_current.RightMaterializationDocument}
Explicação já informada: {_current.NonPaymentExplanation}

Use as ferramentas locais para consultar documentos cadastrados do militar e legislação indexada quando houver informação suficiente. Diferencie o que foi encontrado do que precisa ser confirmado. Não invente número de documento, BI, norma, data, valor ou motivo. Quando faltar dado indispensável, use [CONFIRMAR ...]. O texto deve ser administrativo, claro, conciso e pronto para colar no formulário.
""";

        await BusyAsync($"Gerando {purpose} com o Assistente SIGFUR...", async () =>
        {
            var settings = await App.AssistantStorage.LoadSettingsAsync();
            var result = await App.Assistant.SendAsync(
                Array.Empty<AssistantConversationMessage>(),
                prompt,
                Array.Empty<AssistantAttachmentItem>(),
                settings);
            target.Text = result.Text.Trim();
            target.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            SetStatus($"Campo “{purpose}” preenchido pela IA. Revise antes de salvar ou protocolar.");
        });
    }

    private async Task BusyAsync(string message, Func<Task> action)
    {
        if (_busy.Visibility == Visibility.Visible) return;
        _busy.Visibility = Visibility.Visible; IsEnabled = false; _status.Text = message;
        try { await action(); }
        catch (Exception ex)
        {
            _status.Text = "Erro: " + ex.Message;
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Exercício Anterior", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; _busy.Visibility = Visibility.Collapsed; }
    }

    private void SetStatus(string text) => _status.Text = text;

    // ---------- construção de formulário ----------
    private static StackPanel FormStack() => new() { Margin = new Thickness(12), Orientation = Orientation.Vertical };
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = content };
    private static Border Section(string title, IEnumerable<UIElement> rows)
    {
        var panel = new StackPanel();
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        panel.Children.Add(heading);
        foreach (var row in rows) panel.Children.Add(row);
        var border = new Border { Child = panel, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
        ApplyStyle(border, "CardStyle");
        return border;
    }
    private static Grid Row(params FrameworkElement[] fields)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        var column = 0;
        foreach (var field in fields)
        {
            var weight = Math.Max(1, Convert.ToInt32(field.Tag ?? 1, CultureInfo.InvariantCulture));
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            Grid.SetColumn(field, column++); field.Margin = new Thickness(0, 0, 8, 0); grid.Children.Add(field);
        }
        return grid;
    }
    private static FrameworkElement Field(string label, string property, int weight = 1)
    {
        var box = new TextBox { MinHeight = 30, Padding = new Thickness(7, 4, 7, 4) };
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        if (property == nameof(ExercisePreviousProcess.RequestedValue))
        {
            box.ToolTip = "Digite o valor em reais. Ao sair do campo, o SIGFUR completa o valor por extenso.";
            box.LostFocus += (_, _) => box.Text = FormatMoneyWithWords(box.Text);
        }
        return Labeled(label, box, weight);
    }

    private static string FormatMoneyWithWords(string text)
    {
        if (!TryParseMoney(text, out var value)) return text?.Trim() ?? string.Empty;
        value = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        return $"{value.ToString("C", culture)} ({MoneyToWords(value)})";
    }

    private static bool TryParseMoney(string? text, out decimal value)
    {
        value = 0m;
        var source = (text ?? string.Empty).Split('(', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(source)) return false;
        var match = Regex.Match(source, @"-?\s*(?:R\$\s*)?\d[\d.,]*", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var raw = match.Value.Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty);
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        if (raw.Contains('.', StringComparison.Ordinal) && !raw.Contains(',', StringComparison.Ordinal))
        {
            var digitsAfterLastDot = raw.Length - raw.LastIndexOf('.') - 1;
            if (digitsAfterLastDot == 3)
                return decimal.TryParse(raw, NumberStyles.Number, pt, out value)
                       || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
                   || decimal.TryParse(raw, NumberStyles.Number, pt, out value);
        }
        return decimal.TryParse(raw, NumberStyles.Number, pt, out value)
               || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string MoneyToWords(decimal value)
    {
        var negative = value < 0;
        var totalCents = (long)Math.Round(Math.Abs(value) * 100m, 0, MidpointRounding.AwayFromZero);
        var reais = totalCents / 100;
        var cents = (int)(totalCents % 100);
        var parts = new List<string>();
        if (reais > 0) parts.Add($"{NumberToWords(reais)} {(reais == 1 ? "real" : "reais")}");
        if (cents > 0) parts.Add($"{NumberToWords(cents)} {(cents == 1 ? "centavo" : "centavos")}");
        if (parts.Count == 0) parts.Add("zero real");
        return (negative ? "menos " : string.Empty) + string.Join(" e ", parts);
    }

    private static string NumberToWords(long value)
    {
        if (value == 0) return "zero";
        if (value < 0) return "menos " + NumberToWords(Math.Abs(value));
        if (value < 1000) return HundredsToWords((int)value);

        var millions = value / 1_000_000;
        var thousands = value % 1_000_000 / 1000;
        var remainder = value % 1000;
        var parts = new List<string>();
        if (millions > 0) parts.Add(millions == 1 ? "um milh\u00e3o" : $"{NumberToWords(millions)} milh\u00f5es");
        if (thousands > 0) parts.Add(thousands == 1 ? "mil" : $"{HundredsToWords((int)thousands)} mil");
        if (remainder > 0) parts.Add(HundredsToWords((int)remainder));
        return string.Join(" e ", parts);
    }

    private static string HundredsToWords(int value)
    {
        string[] units = ["", "um", "dois", "tr\u00eas", "quatro", "cinco", "seis", "sete", "oito", "nove"];
        string[] teens = ["dez", "onze", "doze", "treze", "quatorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove"];
        string[] tens = ["", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"];
        string[] hundreds = ["", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos"];
        if (value == 100) return "cem";
        if (value < 10) return units[value];
        if (value < 20) return teens[value - 10];
        if (value < 100)
        {
            var unit = value % 10;
            return unit == 0 ? tens[value / 10] : $"{tens[value / 10]} e {units[unit]}";
        }
        var rest = value % 100;
        return rest == 0 ? hundreds[value / 100] : $"{hundreds[value / 100]} e {HundredsToWords(rest)}";
    }
    private static FrameworkElement MultiField(string label, string property, double height)
        => MultiField(label, property, height, out _);

    private static FrameworkElement MultiField(string label, string property, double height, out TextBox box)
    {
        box = new TextBox
        {
            Height = height,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(7),
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("pt-BR")
        };
        SpellCheck.SetIsEnabled(box, true);
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return Labeled(label, box, 1);
    }
    private FrameworkElement PresetCombo(string label, string property, string presetField, ObservableCollection<string> values, int weight = 1)
    {
        var outer = new StackPanel { Tag = weight };
        var labelText = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        outer.Children.Add(labelText);
        var row = new DockPanel();
        var add = new Button { Content = "+", Width = 36, MinHeight = 36, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Salvar o valor digitado para reutilizar em outros processos" };
        ApplyStyle(add, "GhostButtonStyle");
        DockPanel.SetDock(add, Dock.Right); row.Children.Add(add);
        var combo = new ComboBox { MinHeight = 36, IsEditable = true, ItemsSource = values };
        combo.SetBinding(ComboBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        row.Children.Add(combo); outer.Children.Add(row);
        add.Click += async (_, _) =>
        {
            var value = combo.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return;
            await _repository.AddPresetAsync(presetField, value);
            if (!values.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase))) values.Add(value);
            SetStatus($"Valor ‘{value}’ salvo na lista {label}.");
        };
        return outer;
    }

    private ObservableCollection<string> AdditionalPresets(string field)
    {
        if (_additionalPresets.TryGetValue(field, out var values)) return values;
        values = [];
        _additionalPresets[field] = values;
        return values;
    }

    private async Task ReloadPresetsAsync()
    {
        ReplaceValues(_situationPresets, await _repository.GetPresetsAsync("situacao"));
        ReplaceValues(_indicativePresets, await _repository.GetPresetsAsync("ea_indicativo"));
        ReplaceValues(_previousTypePresets, await _repository.GetPresetsAsync("tipo_exercicio_anterior"));
        ReplaceValues(_yesNoPresets, await _repository.GetPresetsAsync("possui_pensao_judiciaria"));
        var research = (await _repository.GetPresetsAsync("pesquisa_ficha_cadastro"))
            .Concat(await _repository.GetPresetsAsync("pesquisa_ficha_financeira"))
            .Concat(await _repository.GetPresetsAsync("pesquisa_levantamento_siafi"));
        ReplaceValues(_researchPresets, research);
        var banks = (await _repository.ListBanksAsync()).Concat(await _repository.GetPresetsAsync("banco"));
        ReplaceValues(_bankPresets, banks);
        foreach (var pair in _additionalPresets)
            ReplaceValues(pair.Value, await _repository.GetPresetsAsync(pair.Key));
    }

    private static void ReplaceValues(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var normalized = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        target.Clear(); foreach (var value in normalized) target.Add(value);
    }

    private static FrameworkElement Combo(string label, string property, IEnumerable<string> values, int weight = 1)
    {
        var combo = new ComboBox { MinHeight = 30, IsEditable = true, ItemsSource = values.ToArray() };
        combo.SetBinding(ComboBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return Labeled(label, combo, weight);
    }
    private static FrameworkElement DateField(string label, string property, int weight = 1)
    {
        var picker = new DatePicker { MinHeight = 30 };
        picker.SetBinding(DatePicker.SelectedDateProperty, new Binding(property) { Mode = BindingMode.TwoWay, Converter = new IsoDateConverter(), UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return Labeled(label, picker, weight);
    }
    private static FrameworkElement Labeled(string label, Control control, int weight)
    {
        var panel = new StackPanel { Tag = weight };
        var labelText = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(labelText);
        panel.Children.Add(control); return panel;
    }
    private static DataGridTextColumn MoneyColumn(string header, string property)
        => new() { Header = header, Binding = new Binding(property) { Mode = BindingMode.OneWay, StringFormat = "N2" }, Width = 120, IsReadOnly = true };

    private static void AddButton(Panel panel, string text, Func<Task> action, bool primary = false)
        => AddButton(panel, text, action, primary ? EaButtonKind.Primary : EaButtonKind.Secondary);

    private static void AddButton(Panel panel, string text, Func<Task> action, EaButtonKind kind, string? toolTip = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), ToolTip = toolTip };
        ApplyStyle(button, kind switch
        {
            EaButtonKind.Primary => "PrimaryButtonStyle",
            EaButtonKind.Ghost => "GhostButtonStyle",
            EaButtonKind.Danger => "DangerButtonStyle",
            _ => "SecondaryButtonStyle"
        });
        button.Click += async (_, _) => await action();
        panel.Children.Add(button);
    }

    private static void AddSmallButton(Panel panel, string text, Func<Task> action, bool danger = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(9, 5, 9, 5), MinHeight = 32 };
        ApplyStyle(button, danger ? "DangerButtonStyle" : "GhostButtonStyle");
        button.Click += async (_, _) => await action();
        panel.Children.Add(button);
    }

    private static void AddSeparator(Panel panel)
    {
        var separator = new Separator { Width = 1, Height = 30, Margin = new Thickness(7, 5, 7, 5) };
        separator.SetResourceReference(BackgroundProperty, "DividerBrush");
        panel.Children.Add(separator);
    }

    private static void ApplyStyle(FrameworkElement element, string resourceKey)
    {
        if (Application.Current.TryFindResource(resourceKey) is Style style) element.Style = style;
    }

    private enum EaButtonKind
    {
        Primary,
        Secondary,
        Ghost,
        Danger
    }

    private static string DateInWords(DateTime d)
    {
        var months = new[] { "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" };
        return $"{d.Day:00} de {months[d.Month - 1]} de {d.Year}";
    }
}

internal sealed class IsoDateConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        return DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date) ? date : null;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
}

internal sealed class ExercisePreviousCodeDisplayConverter : IValueConverter
{
    private readonly Func<IEnumerable<ExercisePreviousCode>> _codes;
    private readonly bool _includeType;

    public ExercisePreviousCodeDisplayConverter(Func<IEnumerable<ExercisePreviousCode>> codes, bool includeType)
    {
        _codes = codes;
        _includeType = includeType;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order))
            return string.Empty;
        var code = _codes().FirstOrDefault(x => x.Order == order);
        if (_includeType) return code?.Type ?? "-";
        var description = string.IsNullOrWhiteSpace(code?.Description) ? "Sem descrição" : code!.Description;
        return $"{order:00} — {description}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

internal sealed class ExercisePreviousImportSummaryWindow : Window
{
    public ExercisePreviousImportSummaryWindow(ExercisePreviousImportResult result)
    {
        Title = "Resumo da importacao EA";
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Fechar", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(4) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var title = new TextBlock
        {
            Text = "Importacao concluida",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var details = new TextBox
        {
            Text = result.Details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(10)
        };
        root.Children.Add(details);
        Content = root;
    }
}

internal sealed class ExercisePreviousMilitarySearchWindow : Window
{
    private readonly ExercisePreviousRepository _repository;
    private readonly ObservableCollection<ExercisePreviousMilitarySearchResult> _items = [];
    private readonly TextBox _query = new() { MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _hint = new() { Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private int _searchVersion;
    public ExercisePreviousMilitarySearchResult? Selected { get; private set; }

    public ExercisePreviousMilitarySearchWindow(ExercisePreviousRepository repository)
    {
        _repository = repository; Title = "Pesquisar militar — ativos e licenciados/transferidos"; Width = 920; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _query.ToolTip = "Digite nome, nome de guerra, CPF, Prec-CP ou identidade.";
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new DockPanel(); var search = new Button { Content = "Pesquisar", Padding = new Thickness(12, 5, 12, 5) }; search.Click += async (_, _) => await SearchAsync(); DockPanel.SetDock(search, Dock.Right); top.Children.Add(search); top.Children.Add(_query); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(_hint, Dock.Top); root.Children.Add(_hint);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var use = new Button { Content = "Usar selecionado", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4) }; use.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4) }; cancel.Click += (_, _) => DialogResult = false;
        bottom.Children.Add(use); bottom.Children.Add(cancel); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        _grid.ItemsSource = _items;
        _grid.Columns.Add(new DataGridTextColumn { Header = "Conf.", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.DisplayConfidence)), Width = 62 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Critério", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.MatchKind)), Width = 155 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Origem", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.Source)), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Posto", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.Rank)), Width = 100 });
        var nameFactory = new FrameworkElementFactory(typeof(HighlightedNameTextBlock));
        nameFactory.SetBinding(HighlightedNameTextBlock.FullNameProperty, new Binding(nameof(ExercisePreviousMilitarySearchResult.FullName)));
        nameFactory.SetBinding(HighlightedNameTextBlock.WarNameProperty, new Binding(nameof(ExercisePreviousMilitarySearchResult.WarName)));
        nameFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Nome completo",
            CellTemplate = new DataTemplate { VisualTree = nameFactory },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "CPF", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.Cpf)), Width = 125 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Prec-CP", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.PrecCp)), Width = 115 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Identidade", Binding = new Binding(nameof(ExercisePreviousMilitarySearchResult.Identity)), Width = 115 });
        _grid.MouseDoubleClick += (_, _) => Accept(); root.Children.Add(_grid); Content = root;
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await SearchAsync(); };
        _query.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { _searchTimer.Stop(); await SearchAsync(); } };
        Loaded += async (_, _) => { _query.Focus(); await SearchAsync(); };
    }
    private async Task SearchAsync()
    {
        var query = _query.Text;
        var version = ++_searchVersion;
        _hint.Text = string.IsNullOrWhiteSpace(query) ? "Digite para filtrar por nome, CPF, Prec-CP ou identidade." : "Pesquisando no banco...";
        var list = await _repository.SearchMilitaryAsync(query);
        if (version != _searchVersion) return;
        _items.Clear(); foreach (var item in list) _items.Add(item);
        _hint.Text = list.Count == 0
            ? "Nenhum militar encontrado. Confira CPF, Prec-CP, identidade ou nome completo."
            : $"{list.Count} resultado(s). CPF e Prec-CP têm prioridade; nome de guerra aparece apenas como sugestão.";
    }
    private void Accept() { if (_grid.SelectedItem is not ExercisePreviousMilitarySearchResult item) return; Selected = item; DialogResult = true; }
}

internal sealed class ExercisePreviousCpexSettingsWindow : Window
{
    private readonly ComboBox _browser = new() { ItemsSource = new[] { "edge", "chrome" }, IsEditable = false, MinWidth = 180 };
    private readonly TextBox _cpf = new(); private readonly PasswordBox _password = new(); private readonly TextBox _driver = new(); private readonly TextBox _timeout = new();
    private readonly CheckBox _keep = new() { Content = "Manter navegador aberto ao terminar" }; private readonly CheckBox _headless = new() { Content = "Executar oculto (não recomendado para captcha)" };
    private readonly TextBox _operatorName = new(); private readonly TextBox _operatorCpf = new(); private readonly TextBox _operatorEmail = new(); private readonly TextBox _operatorPhone = new();
    public CpexExerciseSettings Settings { get; }

    public ExercisePreviousCpexSettingsWindow(CpexExerciseSettings settings)
    {
        Settings = settings; Title = "Configuração CPEX Online"; Width = 630; Height = 660; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var root = new DockPanel { Margin = new Thickness(16) }; var form = new StackPanel();
        form.Children.Add(Label("Navegador", _browser)); form.Children.Add(Label("CPF de login da Área da UA", _cpf)); form.Children.Add(Label("Senha", _password));
        form.Children.Add(Label("Pasta do msedgedriver/chromedriver — deixe vazio para Selenium Manager", _driver)); form.Children.Add(Label("Tempo máximo para concluir login/captcha (segundos)", _timeout)); form.Children.Add(_keep); form.Children.Add(_headless);
        form.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 14) }); form.Children.Add(new TextBlock { Text = "Informações do operador", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        form.Children.Add(Label("Nome", _operatorName)); form.Children.Add(Label("CPF", _operatorCpf)); form.Children.Add(Label("E-mail OM", _operatorEmail)); form.Children.Add(Label("Celular", _operatorPhone));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Salvar", Padding = new Thickness(15, 6, 15, 6), Margin = new Thickness(4) }; save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(15, 6, 15, 6), Margin = new Thickness(4) }; cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(save); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _browser.SelectedItem = settings.Browser; _cpf.Text = settings.LoginCpf; _password.Password = settings.GetPassword(); _driver.Text = settings.DriverDirectory; _timeout.Text = settings.ManualLoginTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _keep.IsChecked = settings.KeepBrowserOpen; _headless.IsChecked = settings.Headless; _operatorName.Text = settings.OperatorName; _operatorCpf.Text = settings.OperatorCpf; _operatorEmail.Text = settings.OperatorEmail; _operatorPhone.Text = settings.OperatorPhone;
    }
    private static FrameworkElement Label(string title, Control control) { control.MinHeight = 30; var p = new StackPanel { Margin = new Thickness(0, 0, 0, 9) }; p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) }); p.Children.Add(control); return p; }
    private void Save()
    {
        Settings.Browser = _browser.SelectedItem?.ToString() ?? "edge"; Settings.LoginCpf = _cpf.Text; Settings.SetPassword(_password.Password); Settings.DriverDirectory = _driver.Text; Settings.ManualLoginTimeoutSeconds = int.TryParse(_timeout.Text, out var timeout) ? Math.Clamp(timeout, 30, 1800) : 300;
        Settings.KeepBrowserOpen = _keep.IsChecked == true; Settings.Headless = _headless.IsChecked == true; Settings.OperatorName = _operatorName.Text; Settings.OperatorCpf = _operatorCpf.Text; Settings.OperatorEmail = _operatorEmail.Text; Settings.OperatorPhone = _operatorPhone.Text; DialogResult = true;
    }
}
