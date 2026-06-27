using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;
using SIGFUR.Wpf.Views.ExternalBulletins;
using SIGFUR.Wpf.Views;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class BulletinWindow : Window
{
    private readonly BulletinService _service;
    private readonly BulletinKnowledgeService _knowledge;
    private readonly ObservableCollection<BulletinTemplate> _templates = [];
    private readonly ObservableCollection<MilitaryRecord> _available = [];
    private readonly ObservableCollection<BulletinSelectedMilitary> _selected = [];
    private readonly Dictionary<string, FrameworkElement> _fieldControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _fieldStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _sisbolTimer;
    private readonly DispatcherTimer _fieldChangeTimer;
    private ICollectionView? _templateView;
    private ICollectionView? _availableView;
    private BulletinPreferences _preferences = new();
    private Dictionary<string, string> _globalKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Dictionary<string, string>> _documentKeysByMilitary = [];
    private readonly Dictionary<int, Dictionary<string, string>> _exerciseKeysByMilitary = [];
    private List<MilitaryRecord> _allMilitary = [];
    private IReadOnlyList<int> _customOrder = [];
    private BulletinRenderResult? _lastRender;
    private BulletinKnowledgeRule? _currentKnowledgeRule;
    private BulletinComplianceReport? _lastCompliance;
    private Point _templateDragStart;
    private Point _selectedDragStart;
    private BulletinTemplate? _draggedTemplate;
    private BulletinSelectedMilitary? _draggedSelected;
    private string _currentTemplateName = string.Empty;
    private bool _loading;
    private readonly SemaphoreSlim _saveStateGate = new(1, 1);
    private bool _sisbolStatusRunning;
    private bool _buildingFields;
    private bool _previewRunning;
    private bool _switchingTemplate;
    private bool _sisbolSending;

    public BulletinWindow()
    {
        InitializeComponent();
        _service = new BulletinService(App.Paths, App.Json, App.Log);
        _knowledge = App.BulletinKnowledge;
        App.UiState.Attach(this);
        Loaded += OnLoaded;
        Closing += async (_, _) => await SaveCurrentStateAsync();
        _sisbolTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _sisbolTimer.Tick += async (_, _) =>
        {
            if (_sisbolStatusRunning || !IsVisible || !IsActive || WindowState == WindowState.Minimized) return;
            await RefreshSisbolStatusAsync();
        };
        _fieldChangeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _fieldChangeTimer.Tick += async (_, _) =>
        {
            _fieldChangeTimer.Stop();
            await ApplyPendingFieldChangesAsync();
        };
        _sisbolTimer.Start();
        Closed += (_, _) => { _sisbolTimer.Stop(); _fieldChangeTimer.Stop(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _preferences = await _service.LoadPreferencesAsync();
            LockBulletinOrderCheck.IsChecked = _preferences.OrderLocked;
            ApplyBulletinOrderLockState();
            _globalKeys = await _service.LoadGlobalKeysAsync();
            _customOrder = await App.MilitaryPreferences.LoadCustomOrderAsync();
            _allMilitary = await App.MilitaryRepository.GetAllAsync();
            await App.MilitaryPreferences.ApplyAsync(_allMilitary);

            foreach (var template in await _service.LoadTemplatesAsync()) _templates.Add(template);
            foreach (var military in _allMilitary) _available.Add(military);

            _templateView = CollectionViewSource.GetDefaultView(_templates);
            _templateView.Filter = FilterTemplate;
            TemplateList.ItemsSource = _templateView;

            _availableView = CollectionViewSource.GetDefaultView(_available);
            _availableView.Filter = FilterMilitary;
            AvailableGrid.ItemsSource = _availableView;
            SelectedGrid.ItemsSource = _selected;

            SelectSortMode(_preferences.AvailableSortMode);
            ApplyAvailableSort();
            var wanted = _templates.FirstOrDefault(x => x.Name.Equals(_preferences.LastTemplate, StringComparison.OrdinalIgnoreCase)) ?? _templates.FirstOrDefault();
            _loading = false;
            if (wanted is not null) TemplateList.SelectedItem = wanted;
            RefreshCounters();
            await RefreshSisbolStatusAsync();
            StatusText.Text = $"{_templates.Count} modelo(s) e {_allMilitary.Count} militar(es) carregados do banco oficial.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao abrir módulo Boletim C#.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Boletim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Activate();
            Topmost = true;
            await Dispatcher.InvokeAsync(() =>
            {
                Topmost = false;
                Activate();
                Focus();
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    private BulletinTemplate? CurrentTemplate => TemplateList.SelectedItem as BulletinTemplate;

    private bool FilterTemplate(object item)
    {
        if (item is not BulletinTemplate template) return false;
        var query = Normalize(TemplateSearchBox.Text);
        return string.IsNullOrWhiteSpace(query) || Normalize(template.SearchText).Contains(query);
    }

    private bool FilterMilitary(object item)
    {
        if (item is not MilitaryRecord military || _selected.Any(x => x.Military.Id == military.Id)) return false;
        var query = Normalize(MilitarySearchBox.Text);
        return string.IsNullOrWhiteSpace(query) || Normalize($"{military.Rank} {military.Name} {military.WarName} {military.Cpf} {military.PrecCp}").Contains(query);
    }

    private async void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _switchingTemplate) return;
        var requested = e.AddedItems.OfType<BulletinTemplate>().LastOrDefault() ?? CurrentTemplate;
        if (requested is null) return;

        _switchingTemplate = true;
        TemplateList.IsEnabled = false;
        _fieldChangeTimer.Stop();
        try
        {
            // O item selecionado do ListBox muda antes deste evento. Portanto,
            // salvamos explicitamente os controles e a seleção do modelo que ainda
            // está exibido, sem consultar CurrentTemplate depois de um await.
            if (!string.IsNullOrWhiteSpace(_currentTemplateName))
            {
                SaveFieldValues(_currentTemplateName);
                await SaveCurrentStateAsync(_currentTemplateName);
            }

            _currentTemplateName = requested.Name;
            _preferences.LastTemplate = requested.Name;
            RawTemplateBox.Text = requested.Text;
            ApplyConsequencesFromTemplate(requested);
            _currentKnowledgeRule = await _knowledge.FindRuleAsync(requested.Name);
            await LoadSelectionForTemplateAsync(requested.Name);
            BuildFields(requested);
            GeneratePreview(requested);
            await SavePreferencesOnlyAsync();
            StatusText.Text = $"Modelo carregado: {requested.Name}.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync($"Falha ao trocar o modelo do Boletim para {requested.Name}.", ex);
            StatusText.Text = "Não foi possível trocar o modelo. A nota anterior foi preservada.";
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Boletim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TemplateList.IsEnabled = true;
            _switchingTemplate = false;
        }
    }

    private void ApplyConsequencesFromTemplate(BulletinTemplate template)
    {
        if (ConsequencesTextBox is null) return;
        ConsequencesTextBox.Text = SisbolTexts.ForTemplate(template.Name, template.Text);
    }

    private string CurrentDefaultConsequences()
    {
        var template = CurrentTemplate;
        return template is null
            ? SisbolTexts.BulletinConsequencesText
            : SisbolTexts.ForTemplate(template.Name, template.Text);
    }

    private async Task LoadSelectionForTemplateAsync(string templateName)
    {
        _selected.Clear();
        var ids = _preferences.SelectionByTemplate.TryGetValue(templateName, out var stored) ? stored : _preferences.SelectedMilitaryIds;
        foreach (var id in ids)
        {
            var military = _allMilitary.FirstOrDefault(x => x.Id == id);
            if (military is null) continue;
            _selected.Add(new BulletinSelectedMilitary { Military = military });
            await EnsureDocumentKeysAsync(military.Id);
        }
        RenumberSelected();
        _availableView?.Refresh();
    }

    private async Task EnsureDocumentKeysAsync(int militaryId)
    {
        if (!_documentKeysByMilitary.ContainsKey(militaryId))
            _documentKeysByMilitary[militaryId] = await App.MilitaryRepository.GetLatestCertificateKeysAsync(militaryId);
        if (!_exerciseKeysByMilitary.ContainsKey(militaryId))
            _exerciseKeysByMilitary[militaryId] = await LoadExercisePreviousKeysAsync(militaryId);
    }

    private static async Task<Dictionary<string, string>> LoadExercisePreviousKeysAsync(int militaryId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repository = new ExercisePreviousRepository(App.Paths, App.Log);
        var process = await repository.GetLatestForMilitaryAsync(militaryId);
        if (process is null) return result;
        var summary = await repository.CalculateSummaryAsync(process);
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        result["DOC_MATERIALIZOU_DIREITO"] = process.RightMaterializationDocument;
        result["PROTOCOLO_CPEX"] = string.IsNullOrWhiteSpace(process.CpexProtocol) ? process.GeneralProtocol : process.CpexProtocol;
        result["TIPO_EXERCICIO_ANTERIOR"] = process.PreviousExerciseType;
        result["PERIODO_DIVIDA"] = $"{process.PeriodStart} a {process.PeriodEnd}";
        result["RUBRICAS_VALORES"] = string.Join("; ", process.Codes.Select(code =>
        {
            var rows = process.Entries.Where(x => x.CodeOrder == code.Order).ToList();
            var value = rows.Sum(x => x.Net);
            return $"{code.Description} ({code.Type}): R$ {value.ToString("N2", ptBr)}";
        }));
        result["VALOR_BRUTO"] = summary.Net.ToString("N2", ptBr);
        result["VALOR_TOTAL"] = summary.CorrectedNet.ToString("N2", ptBr);
        return result;
    }

    private void BuildFields(BulletinTemplate template)
    {
        _buildingFields = true;
        try
        {
        FieldsPanel.Children.Clear();
        _fieldControls.Clear();
        _fieldStatus.Clear();
        FieldsSummaryText.Text = template.Name;
        AddSmartTemplateHints(template);
        var fields = _service.DetectFields(template.Text);
        foreach (var publicationField in _service.DetectFields(template.Text, includeAutomatic: true)
                     .Where(item => ClassifyPublicationField(item.Key) != PublicationFieldKind.None
                                    && fields.All(existing => !Normalize(existing.Key).Equals(Normalize(item.Key), StringComparison.OrdinalIgnoreCase))))
            fields.Add(publicationField);
        var normalizedTemplate = Normalize($"{template.Name} {template.Text}");
        if (normalizedTemplate.Contains("auxilio alimentacao", StringComparison.OrdinalIgnoreCase))
        {
            var calculated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "VALORDIA", "VALORTOTAL", "VALORTOTALINDIVIDUAL", "VALORREFERENCIADIA",
                "MULTIPLICADORETAPA", "ETAPACOMUM", "VALORETAPACOMUM"
            };
            fields = fields.Where(field => !calculated.Contains(Normalize(field.Key).Replace("_", string.Empty, StringComparison.Ordinal))).ToList();
        }
        var values = _preferences.FormValues.TryGetValue(template.Name, out var saved)
            ? saved
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (fields.Count == 0)
        {
            FieldsPanel.Children.Add(new TextBlock { Text = "Este modelo não possui campos manuais. As chaves dos militares serão preenchidas automaticamente.", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(4) });
            FieldsProgressText.Text = "Preenchimento automático";
            return;
        }

        foreach (var field in fields)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)FindResource("SoftCardStyle")
            };
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            var information = new StackPanel();
            var knowledgeField = _currentKnowledgeRule?.RequiredFields.FirstOrDefault(item => Normalize(item.Key) == Normalize(field.Key));
            information.Children.Add(new TextBlock
            {
                Text = FriendlyKey(field.Key),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 3),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            });
            information.Children.Add(new TextBlock
            {
                Text = "Campo editável usado na prévia",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap
            });
            var initialValue = ResolveInitialFieldValue(field.Key, values, out var sourceLabel);
            var control = CreateFieldControl(field, initialValue, knowledgeField);
            if (!string.IsNullOrWhiteSpace(knowledgeField?.Description))
            {
                information.Children.Add(new TextBlock
                {
                    Text = "Sugestão SIPPES: " + AdvisoryText(knowledgeField.Description),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 7, 0, 0)
                });
            }
            if (!string.IsNullOrWhiteSpace(sourceLabel))
            {
                var automaticHint = new TextBlock
                {
                    Text = sourceLabel,
                    Foreground = (Brush)FindResource("SuccessBrush"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 5, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                information.Children.Add(automaticHint);
            }
            if (field.Type == "money")
            {
                var hint = new TextBlock { Text = "Use VALOR=AMBOS no modelo para número + valor por extenso.", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
                information.Children.Add(hint);
            }
            panel.Children.Add(information);

            var editor = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(editor, 2);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            editor.Children.Add(control);
            AddContextualFieldActions(editor, field.Key);
            var status = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 6, 0, 0)
            };
            editor.Children.Add(status);
            panel.Children.Add(editor);
            card.Child = panel;
            FieldsPanel.Children.Add(card);
            _fieldControls[field.Key] = control;
            _fieldStatus[field.Key] = status;
        }
        UpdateFieldProgress();
        }
        finally
        {
            _buildingFields = false;
        }
    }

    private string ResolveInitialFieldValue(string key, IReadOnlyDictionary<string, string> saved, out string sourceLabel)
    {
        sourceLabel = string.Empty;
        if (_selected.Count == 1 && Normalize(CurrentTemplate?.Name).Contains("exercicios anteriores", StringComparison.OrdinalIgnoreCase)
            && _exerciseKeysByMilitary.TryGetValue(_selected[0].Military.Id, out var exerciseKeys))
        {
            var fromExercise = FindKeyValue(exerciseKeys, key);
            if (!string.IsNullOrWhiteSpace(fromExercise))
            {
                sourceLabel = "Preenchido pelo processo salvo em Exercícios Anteriores deste militar.";
                return fromExercise;
            }
        }
        if (_selected.Count == 1 && _documentKeysByMilitary.TryGetValue(_selected[0].Military.Id, out var documentKeys))
        {
            var fromDocument = FindKeyValue(documentKeys, key);
            if (!string.IsNullOrWhiteSpace(fromDocument))
            {
                sourceLabel = "Preenchido pela certidão vinculada a este militar.";
                return fromDocument;
            }
        }

        var stored = FindKeyValue(saved, key);
        if (BulletinService.IsMeaningfulFieldValue(key, stored)) return stored;

        var global = FindKeyValue(_globalKeys, key);
        if (BulletinService.IsMeaningfulFieldValue(key, global))
        {
            sourceLabel = "Preenchido por uma chave salva do SIGFUR.";
            return global;
        }
        return string.Empty;
    }

    private static string FindKeyValue(IReadOnlyDictionary<string, string> values, string key)
    {
        var wanted = Normalize(key).Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var pair in values)
        {
            var current = Normalize(pair.Key).Replace("_", string.Empty, StringComparison.Ordinal);
            if (current.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
        }
        return string.Empty;
    }

    private void AddSmartTemplateHints(BulletinTemplate template)
    {
        var text = Normalize($"{template.Name} {template.Text}");
        if (text.Contains("auxilio natalidade", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Conferência recomendada antes do Auxílio-Natalidade",
                "Antes de pedir o auxílio-natalidade, confira/gere a DEPENDÊNCIA ECONÔMICA do dependente para fins de Salário-Família, FUSEx e Imposto de Renda. Os dados da certidão podem vir do OCR/chaves salvas, mas também ficam editáveis manualmente.",
                "WarningBrush");
        if (text.Contains("assistencia pre escolar", StringComparison.OrdinalIgnoreCase) || text.Contains("pre escolar", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Pré-escolar vinculado à certidão",
                "Use as chaves da certidão de nascimento quando houver OCR salvo: nome do dependente, CPF, data de nascimento, matrícula e cartório. Se o OCR não estiver salvo, preencha manualmente.",
                "InfoBrush");
        if (text.Contains("dependencia economica", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Dependência econômica",
                "Marque corretamente a finalidade: Salário-Família, FUSEx e/ou Imposto de Renda. Parentesco e dados do dependente usam lista para evitar erro de digitação, mas aceitam edição manual.",
                "InfoBrush");
        if (text.Contains("pensao alimenticia", StringComparison.OrdinalIgnoreCase) || text.Contains("pensao", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Pensão alimentícia — dados críticos do SIPPES",
                "A leitura por OCR ficará configurável quando você enviar o modelo. Por enquanto, o SIGFUR organiza os pontos de conferência: processo, origem, decisão, alimentante, alimentado, detentor da guarda, banco, regra de cálculo, incidências e pensões anteriores.",
                "WarningBrush");
        if (text.Contains("exercicios anteriores", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Exercício anterior",
                "Prefira trazer protocolo, rubricas, período e valores do módulo Exercício Anterior/CPEx. Não digite valores de memória; o modelo deixa os campos evidentes para conferência.",
                "WarningBrush");
        if (text.Contains("transferencia de pagamento", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Transferência de pagamento",
                "Selecione a OM quando possível e confira o CODOM antes de enviar. O campo aceita preenchimento manual quando a OM não estiver cadastrada.",
                "InfoBrush");
        if (text.Contains("auxilio alimentacao", StringComparison.OrdinalIgnoreCase))
            AddFieldHintCard("Cálculo automático do auxílio-alimentação",
                "Informe o código e a quantidade de dias. O SIGFUR calcula Valor por dia e Valor total com base na etapa comum: A58/A48 = 1x, A53/A43 = 5x e A52/A42 = 10x.",
                "SuccessBrush");
    }

    private void AddFieldHintCard(string title, string text, string brushKey)
    {
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Style = (Style)FindResource("SoftCardStyle")
        };
        card.SetResourceReference(Border.BorderBrushProperty, brushKey);
        var panel = new StackPanel();
        var titleBlock = new TextBlock { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        panel.Children.Add(titleBlock);
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush") });
        card.Child = panel;
        FieldsPanel.Children.Add(card);
    }

    private FrameworkElement CreateFieldControl(BulletinFieldDefinition field, string value, BulletinKnowledgeField? knowledgeField = null)
    {
        var options = (knowledgeField?.Options ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var normalizedFieldKey = Normalize(field.Key).Replace("_", string.Empty, StringComparison.Ordinal);
        if (normalizedFieldKey is "omorigem" or "omdestino" or "ompagamento" or "codomorigem" or "codomdestino" or "codompagamento")
            options = _service.SuggestOptions(field.Key, CurrentTemplate?.Name).ToList();
        else if (options.Count == 0)
            options = _service.SuggestOptions(field.Key, CurrentTemplate?.Name)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        if (options.Count > 0)
        {
            field.Type = "select";
            field.Options = options;
        }

        if (field.Type == "select")
        {
            var combo = new ComboBox { IsEditable = true, ItemsSource = field.Options, Text = value, MinWidth = 260, MinHeight = 30, FontSize = 12.5, Tag = field.Key };
            combo.SelectionChanged += FieldChanged;
            combo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(FieldChanged));
            ConfigureReplaceOnFirstEdit(combo);
            return combo;
        }
        if (field.Type == "date")
        {
            var picker = new DatePicker { SelectedDateFormat = DatePickerFormat.Short, MinWidth = 260, MinHeight = 30, FontSize = 12.5, Tag = field.Key };
            if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)) picker.SelectedDate = date;
            picker.SelectedDateChanged += FieldChanged;
            return picker;
        }
        if (field.Type == "month")
        {
            var combo = new ComboBox { IsEditable = true, MinWidth = 260, MinHeight = 30, FontSize = 12.5, Tag = field.Key };
            var baseDate = DateTime.Today.AddYears(-2);
            combo.ItemsSource = Enumerable.Range(0, 61)
                .Select(i => baseDate.AddMonths(i).ToString("MMM yy", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")))
                .ToList();
            combo.Text = value;
            combo.SelectionChanged += FieldChanged;
            combo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(FieldChanged));
            ConfigureReplaceOnFirstEdit(combo);
            return combo;
        }
        if (field.Type == "money")
        {
            var money = new BulletinValueInputControl(value, field.MoneyFormat) { Tag = field.Key };
            money.ValueChanged += FieldChanged;
            return money;
        }
        var text = new TextBox { Text = value, MinWidth = 260, MinHeight = 30, FontSize = 12.5, Tag = field.Key };
        text.TextChanged += FieldChanged;
        ConfigureReplaceOnFirstEdit(text);
        return text;
    }

    private static void ConfigureReplaceOnFirstEdit(TextBox textBox)
    {
        textBox.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (textBox.IsKeyboardFocusWithin) return;
            e.Handled = true;
            textBox.Focus();
            textBox.SelectAll();
        };
        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
    }

    private static void ConfigureReplaceOnFirstEdit(ComboBox comboBox)
    {
        comboBox.Loaded += (_, _) =>
        {
            if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox editor)
                ConfigureReplaceOnFirstEdit(editor);
        };
    }

    private void AddContextualFieldActions(Panel editor, string fieldKey)
    {
        var kind = ClassifyPublicationField(fieldKey);
        if (kind == PublicationFieldKind.None) return;

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (kind is PublicationFieldKind.Bi or PublicationFieldKind.Generic)
            row.Children.Add(CreatePublicationButton("Usar BI salvo", fieldKey, PublicationFieldKind.Bi));
        if (kind is PublicationFieldKind.Adt or PublicationFieldKind.Generic)
            row.Children.Add(CreatePublicationButton("Usar ADT salvo", fieldKey, PublicationFieldKind.Adt));
        editor.Children.Add(row);
    }

    private Button CreatePublicationButton(string caption, string fieldKey, PublicationFieldKind requiredKind)
    {
        var button = new Button
        {
            Content = caption,
            Tag = new PublicationFieldAction(fieldKey, requiredKind),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 3, 8, 3),
            MinWidth = 92,
            FontSize = 11,
            Style = (Style)FindResource("GhostButtonStyle"),
            ToolTip = "Preenche somente este campo. Você ainda pode editar manualmente depois."
        };
        button.Click += PublicationFieldButton_Click;
        return button;
    }

    private async void PublicationFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTemplate is null || sender is not Button { Tag: PublicationFieldAction action }) return;
        var dialog = new SavedBulletinPickerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedReference is not { } reference) return;
        if (!ReferenceMatchesRequestedKind(reference, action.RequiredKind))
        {
            StatusText.Text = action.RequiredKind == PublicationFieldKind.Bi
                ? "Selecione um Boletim Interno para este campo."
                : "Selecione um Aditamento do Furriel para este campo.";
            return;
        }

        var value = PublicationValue(action.FieldKey, reference);
        if (string.IsNullOrWhiteSpace(value)) value = FormatPublicationReference(reference);
        if (_fieldControls.TryGetValue(action.FieldKey, out var control)) SetControlValue(control, value);
        SaveFieldValues(CurrentTemplate.Name);
        GeneratePreview();
        await SavePreferencesOnlyAsync();
        StatusText.Text = $"{FormatPublicationReference(reference)} aplicado somente em {FriendlyKey(action.FieldKey)}.";
    }

    private static bool ReferenceMatchesRequestedKind(SavedBulletinReference reference, PublicationFieldKind requiredKind)
    {
        if (requiredKind == PublicationFieldKind.Generic) return true;
        var isAdt = IsAdtReference(reference);
        return requiredKind == PublicationFieldKind.Adt ? isAdt : !isAdt;
    }

    private enum PublicationFieldKind { None, Bi, Adt, Generic }
    private sealed record PublicationFieldAction(string FieldKey, PublicationFieldKind RequiredKind);

    private static PublicationFieldKind ClassifyPublicationField(string? key)
    {
        var normalized = Normalize(key).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return PublicationFieldKind.None;
        if (normalized is "bar" or "numerobar" or "barreferencia") return PublicationFieldKind.Adt;
        var hasAdt = normalized.Contains("adt", StringComparison.Ordinal) || normalized.Contains("aditamento", StringComparison.Ordinal);
        var hasBi = normalized == "bi" || normalized.StartsWith("bi", StringComparison.Ordinal) || normalized.EndsWith("bi", StringComparison.Ordinal)
                    || normalized.Contains("boletim", StringComparison.Ordinal);
        var hasPublication = normalized.Contains("publicacao", StringComparison.Ordinal) || normalized.Contains("referencia", StringComparison.Ordinal) || normalized.Contains("docreferencia", StringComparison.Ordinal);
        if (hasAdt && !hasBi) return PublicationFieldKind.Adt;
        if (hasBi && !hasAdt) return PublicationFieldKind.Bi;
        if (hasBi && hasAdt) return PublicationFieldKind.Generic;
        return hasPublication ? PublicationFieldKind.Generic : PublicationFieldKind.None;
    }

    private static bool IsAdtReference(SavedBulletinReference reference)
        => reference.Kind.Equals("Aditamento do Furriel", StringComparison.OrdinalIgnoreCase);

    private static void SetControlValue(FrameworkElement control, string value)
    {
        switch (control)
        {
            case TextBox text:
                text.Text = value;
                break;
            case ComboBox combo:
                combo.Text = value;
                break;
            case DatePicker picker when DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AllowWhiteSpaces, out var date):
                picker.SelectedDate = date;
                break;
            case BulletinValueInputControl:
                break;
        }
    }

    private void FieldChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _buildingFields || CurrentTemplate is null) return;
        ApplyCodomSelection(sender as FrameworkElement);
        _fieldChangeTimer.Stop();
        _fieldChangeTimer.Start();
        UpdateFieldProgress();
        StatusText.Text = "Atualizando a prévia…";
    }

    private void ApplyCodomSelection(FrameworkElement? source)
    {
        var key = source?.Tag?.ToString() ?? string.Empty;
        var normalized = Normalize(key).Replace("_", string.Empty, StringComparison.Ordinal);
        if (normalized is not ("omorigem" or "omdestino" or "ompagamento")) return;
        var value = source switch { ComboBox combo => combo.Text, TextBox text => text.Text, _ => string.Empty };
        if (!_service.TryResolveCodom(value, out var codom)) return;
        var targetKey = normalized switch
        {
            "omorigem" => "CODOM_ORIGEM",
            "omdestino" => "CODOM_DESTINO",
            _ => "CODOM_PAGAMENTO"
        };
        if (!_fieldControls.TryGetValue(targetKey, out var target)) return;
        _buildingFields = true;
        try
        {
            if (target is ComboBox combo) combo.Text = codom;
            else if (target is TextBox text) text.Text = codom;
        }
        finally { _buildingFields = false; }
    }

    private async Task ApplyPendingFieldChangesAsync()
    {
        if (_loading || _buildingFields || CurrentTemplate is null) return;
        try
        {
            SaveFieldValues(CurrentTemplate.Name);
            GeneratePreview();
            await SavePreferencesOnlyAsync();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao atualizar campos do Boletim.", ex);
            StatusText.Text = "Não foi possível atualizar este campo. O formulário permaneceu aberto.";
        }
    }

    private void SaveFieldValues(string templateName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _fieldControls) values[pair.Key] = ReadControlValue(pair.Value);
        ResolveCodomPair(values, "OM_ORIGEM", "CODOM_ORIGEM");
        ResolveCodomPair(values, "OM_DESTINO", "CODOM_DESTINO");
        ResolveCodomPair(values, "OM_PAGAMENTO", "CODOM_PAGAMENTO");
        _preferences.FormValues[templateName] = values;
    }

    private void ResolveCodomPair(IDictionary<string, string> values, string organizationKey, string codomKey)
    {
        var organization = values.FirstOrDefault(x => Normalize(x.Key).Replace("_", string.Empty, StringComparison.Ordinal)
            == Normalize(organizationKey).Replace("_", string.Empty, StringComparison.Ordinal)).Value;
        if (string.IsNullOrWhiteSpace(organization) || !_service.TryResolveCodom(organization, out var codom)) return;
        values[codomKey] = codom;
        if (_fieldControls.TryGetValue(codomKey, out var control))
        {
            if (control is ComboBox combo) combo.Text = codom;
            else if (control is TextBox text) text.Text = codom;
        }
    }

    private static string ReadControlValue(FrameworkElement control) => control switch
    {
        TextBox text => text.Text.Trim(),
        ComboBox combo => (combo.Text ?? combo.SelectedItem?.ToString() ?? string.Empty).Trim(),
        DatePicker picker => picker.SelectedDate?.ToString("dd/MM/yyyy") ?? string.Empty,
        BulletinValueInputControl money => money.Value,
        _ => string.Empty
    };

    private void UpdateFieldProgress()
    {
        var filled = 0;
        foreach (var pair in _fieldControls)
        {
            var hasValue = BulletinService.IsMeaningfulFieldValue(pair.Key, ReadControlValue(pair.Value));
            if (hasValue) filled++;
            if (!_fieldStatus.TryGetValue(pair.Key, out var status)) continue;
            status.Text = hasValue ? "Preenchido e aplicado à prévia" : "Pendente: informe para completar o texto";
            status.SetResourceReference(TextBlock.ForegroundProperty, hasValue ? "SuccessBrush" : "WarningBrush");
        }

        FieldsProgressText.Text = _fieldControls.Count == 0
            ? "Preenchimento automático"
            : $"{filled} de {_fieldControls.Count} preenchido(s)";
        FieldsProgressText.SetResourceReference(TextBlock.ForegroundProperty,
            filled == _fieldControls.Count ? "SuccessBrush" : "WarningBrush");
    }

    private void GeneratePreview(BulletinTemplate? templateOverride = null)
    {
        var template = templateOverride ?? CurrentTemplate;
        if (template is null || _previewRunning) return;
        _previewRunning = true;
        try
        {
            SaveFieldValues(template.Name);
            var rawValues = _preferences.FormValues.GetValueOrDefault(template.Name) ?? new Dictionary<string, string>();
            var values = _service.EnrichFormValues(template.Name, rawValues);
            _lastRender = _service.Render(template, _selected.Select(x => x.Military).ToList(), values, _globalKeys, _documentKeysByMilitary);
            _lastCompliance = _knowledge.Validate(_currentKnowledgeRule, template, _selected.Select(x => x.Military).ToList(), values, _globalKeys, _lastRender);
            var previewRender = _lastRender;
            if (string.IsNullOrWhiteSpace(_lastRender.Text))
            {
                previewRender = new BulletinRenderResult
                {
                    Text = "Selecione pelo menos um militar na lista acima para montar este modelo de boletim.",
                    BoldRanges = [],
                    UnresolvedTokens = []
                };
            }
            PreviewDocumentViewer.Document = _service.BuildDocument(previewRender);
            UpdateFieldProgress();
            RenderCompliance();
            UnresolvedText.Text = _lastCompliance.IsBlocked
                ? $"Modelo: {_lastRender.UnresolvedTokens.Count} campo(s) pendente(s)"
                : _lastCompliance.Rule is null
                    ? "Conferência: regra não cadastrada • revisão opcional"
                    : $"Conferência: {_lastCompliance.Score}/100 • sem bloqueio da IA";
            UnresolvedText.SetResourceReference(TextBlock.ForegroundProperty,
                _lastCompliance.IsBlocked ? "DangerBrush" : _lastCompliance.Rule is null ? "WarningBrush" : "SuccessBrush");
            StatusText.Text = _selected.Count == 0
                ? "Modelo carregado. Adicione os militares para gerar o texto completo."
                : $"Prévia gerada com {_selected.Count} militar(es). O nome de guerra está em negrito real.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Erro ao gerar a prévia. Corrija o campo destacado e tente novamente.";
            _ = App.Log.WriteAsync("Falha controlada ao gerar prévia do Boletim.", ex);
        }
        finally
        {
            _previewRunning = false;
        }
    }

    private void RenderCompliance()
    {
        CompliancePanel.Children.Clear();
        var report = _lastCompliance;
        if (report is null) return;
        var manualReview = report.Rule is null;

        var header = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 14),
            BorderThickness = new Thickness(1)
        };
        header.SetResourceReference(Border.BackgroundProperty, report.IsBlocked ? "DangerSoftBrush" : manualReview || report.Warnings.Count > 0 ? "WarningSoftBrush" : "SuccessSoftBrush");
        header.SetResourceReference(Border.BorderBrushProperty, report.IsBlocked ? "DangerBrush" : manualReview || report.Warnings.Count > 0 ? "WarningBrush" : "SuccessBrush");
        var headerPanel = new StackPanel();
        headerPanel.Children.Add(new TextBlock
        {
            Text = report.IsBlocked ? "PREENCHIMENTO DO MODELO INCOMPLETO" : manualReview ? "REVISÃO MANUAL RECOMENDADA" : "CONFERÊNCIA SIPPES DISPONÍVEL",
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Foreground = (Brush)FindResource(report.IsBlocked ? "DangerBrush" : manualReview || report.Warnings.Count > 0 ? "WarningBrush" : "SuccessBrush")
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = $"Nota {report.Score}/100 • Regra: {report.Rule?.Title ?? "revisão manual"}",
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        header.Child = headerPanel;
        CompliancePanel.Children.Add(header);

        AddComplianceSection("CAMPOS DO MODELO PENDENTES", report.Errors, "✖", "DangerBrush");
        AddComplianceSection("SUGESTÕES PARA CONFERÊNCIA", report.Warnings, "⚠", "WarningBrush");
        AddComplianceSection("CHECKLIST PROFISSIONAL", report.Recommendations, "•", "PrimaryBrush");
        AddComplianceSection("DADOS CONFERIDOS", report.Passed, "✓", "SuccessBrush");

        if (report.Rule?.SourceReferences.Count > 0)
        {
            CompliancePanel.Children.Add(new TextBlock
            {
                Text = "Base aprendida: " + string.Join(" • ", report.Rule.SourceReferences),
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 12, 4, 4)
            });
        }
    }

    private void AddComplianceSection(string title, IReadOnlyList<string> items, string icon, string brushKey)
    {
        if (items.Count == 0) return;
        CompliancePanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource(brushKey),
            Margin = new Thickness(4, 8, 4, 6)
        });
        foreach (var item in items)
        {
            var border = new Border { Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(8) };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
            border.Child = new TextBlock { Text = $"{icon}  {item}", TextWrapping = TextWrapping.Wrap };
            CompliancePanel.Children.Add(border);
        }
    }

    private async void AuditWithAi_Click(object sender, RoutedEventArgs e)
    {
        GeneratePreview();
        if (_lastRender is null || string.IsNullOrWhiteSpace(_lastRender.Text) || CurrentTemplate is null) return;
        try
        {
            AuditAiButton.IsEnabled = false;
            AuditAiButton.Content = "Auditando…";
            StatusText.Text = "A IA está cruzando o boletim com a base SIPPES aprendida…";
            var settings = await App.AssistantStorage.LoadSettingsAsync();
            var bulletinText = settings.RedactSensitiveData
                ? AssistantAttachmentService.RedactSensitiveData(_lastRender.Text)
                : _lastRender.Text;
            var prompt = $"""
                Audite o boletim abaixo como revisor especializado em SIPPES e Aditamento do Furriel.
                Consulte a base local de regras de boletim antes de responder.
                Modelo selecionado: {CurrentTemplate.Name}

                Entregue exatamente estes blocos:
                RESULTADO: APROVADO, APROVADO COM RESSALVAS ou BLOQUEADO.
                IMPEDIMENTOS: campos obrigatórios ausentes ou incoerências objetivas.
                CORREÇÕES: texto pontual que deve ser ajustado, sem inventar dado algum.
                CHECKLIST FINAL: conferências humanas antes do SisBol.

                Não invente BI, data, valor, norma, pessoa, rubrica ou motivo. Quando faltar dado, escreva [PREENCHER].

                BOLETIM PARA AUDITORIA:
                {bulletinText}
                """;
            var result = await App.Assistant.SendAsync([], prompt, [], settings);
            ShowAiAuditResult(result.Text);
            StatusText.Text = "Auditoria IA concluída. O resultado é uma conferência e não bloqueia o envio.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha na auditoria IA do Boletim.", ex);
            SigfurDialog.Show(this, ex.Message, "Auditoria IA", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "A auditoria IA não foi concluída; a conferência SIPPES local continua disponível.";
        }
        finally
        {
            AuditAiButton.IsEnabled = true;
            AuditAiButton.Content = "✦ Auditar com IA";
        }
    }

    private void ShowAiAuditResult(string text)
    {
        var window = new Window
        {
            Title = "SIGFUR — Auditoria IA do Boletim",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 900,
            Height = 680,
            MinWidth = 680,
            MinHeight = 500,
            Background = (Brush)FindResource("AppBackgroundBrush"),
            Icon = Icon
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "AUDITORIA IA — CONFERÊNCIA COMPLEMENTAR",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("PrimaryDarkBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        var resultBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(14),
            FontFamily = new FontFamily("Arial"),
            FontSize = 13
        };
        Grid.SetRow(resultBox, 1);
        root.Children.Add(resultBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copiar auditoria", Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(text);
        var close = new Button { Content = "Fechar", Style = (Style)FindResource("PrimaryButtonStyle") };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        window.Content = root;
        window.ShowDialog();
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e) => AddMilitary(AvailableGrid.SelectedItems.Cast<MilitaryRecord>());
    private void AddMarked_Click(object sender, RoutedEventArgs e) => AddMilitary(_allMilitary.Where(x => x.IsMarkedForBatch));

    private void SelectAllAvailable_Click(object sender, RoutedEventArgs e)
    {
        try { AvailableGrid.SelectAll(); } catch { }
        StatusText.Text = $"{AvailableGrid.SelectedItems.Count} militar(es) selecionado(s) na lista disponível.";
    }

    private void ClearAvailableSelection_Click(object sender, RoutedEventArgs e)
    {
        try { AvailableGrid.UnselectAll(); } catch { }
        StatusText.Text = "Seleção dos disponíveis limpa.";
    }

    private void CopyAvailableSelection_Click(object sender, RoutedEventArgs e)
    {
        var lines = AvailableGrid.SelectedItems.Cast<MilitaryRecord>()
            .Select(x => $"{x.ShortRank} {x.Name} | CPF {x.FormattedCpf} | PREC-CP {x.PrecCp}")
            .ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusText.Text = lines.Count == 0 ? "Nenhum militar selecionado para copiar." : $"{lines.Count} militar(es) copiado(s).";
    }

    private void SelectAllSelected_Click(object sender, RoutedEventArgs e)
    {
        try { SelectedGrid.SelectAll(); } catch { }
        StatusText.Text = $"{SelectedGrid.SelectedItems.Count} militar(es) selecionado(s) no boletim.";
    }

    private void ClearSelectedSelection_Click(object sender, RoutedEventArgs e)
    {
        try { SelectedGrid.UnselectAll(); } catch { }
        StatusText.Text = "Seleção do boletim limpa.";
    }

    private void CopySelectedSelection_Click(object sender, RoutedEventArgs e)
    {
        var lines = SelectedGrid.SelectedItems.OfType<BulletinSelectedMilitary>()
            .Select(x => $"{x.Position}. {x.Military.ShortRank} {x.Military.Name} | CPF {x.Military.FormattedCpf} | PREC-CP {x.Military.PrecCp}")
            .ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusText.Text = lines.Count == 0 ? "Nenhum militar selecionado para copiar." : $"{lines.Count} militar(es) do boletim copiado(s).";
    }

    private void AvailableGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AvailableGrid.SelectedItem is MilitaryRecord military) AddMilitary([military]);
    }

    private async void AddMilitary(IEnumerable<MilitaryRecord> source)
    {
        var count = 0;
        foreach (var military in source.DistinctBy(x => x.Id))
        {
            if (_selected.Any(x => x.Military.Id == military.Id)) continue;
            _selected.Add(new BulletinSelectedMilitary { Military = military });
            await EnsureDocumentKeysAsync(military.Id);
            count++;
        }
        RenumberSelected();
        _availableView?.Refresh();
        if (CurrentTemplate is not null) BuildFields(CurrentTemplate);
        GeneratePreview();
        await SaveCurrentStateAsync();
        StatusText.Text = $"{count} militar(es) adicionado(s). Arraste as linhas para definir a ordem do boletim.";
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedGrid.SelectedItems.OfType<BulletinSelectedMilitary>().Distinct().ToList();
        if (items.Count == 0 && SelectedGrid.SelectedItem is BulletinSelectedMilitary current) items.Add(current);
        if (items.Count == 0) return;

        try
        {
            _draggedSelected = null;
            var nextIndex = items
                .Select(item => _selected.IndexOf(item))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
            foreach (var item in items.Where(_selected.Contains).ToList()) _selected.Remove(item);
            RenumberSelected();
            _availableView?.Refresh();
            if (CurrentTemplate is not null) BuildFields(CurrentTemplate);
            GeneratePreview();
            await SaveCurrentStateAsync();
            if (_selected.Count > 0)
                SelectedGrid.SelectedItem = _selected[Math.Min(nextIndex, _selected.Count - 1)];
            StatusText.Text = $"{items.Count} militar(es) removido(s) do boletim.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao remover militar do boletim.", ex);
            SigfurDialog.Show(this, "Não foi possível remover o militar do boletim.\n\n" + ex.Message,
                "SIGFUR — Boletim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearSelected_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear(); RenumberSelected(); _availableView?.Refresh(); if (CurrentTemplate is not null) BuildFields(CurrentTemplate); GeneratePreview(); await SaveCurrentStateAsync();
    }

    private void MoveSelectedUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveSelectedDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private async void MoveSelected(int delta)
    {
        if (_preferences.OrderLocked) { NotifyBulletinOrderLocked(); return; }
        if (SelectedGrid.SelectedItem is not BulletinSelectedMilitary item) return;
        var index = _selected.IndexOf(item);
        var target = index + delta;
        if (target < 0 || target >= _selected.Count) return;
        _selected.Move(index, target); RenumberSelected(); SelectedGrid.SelectedItem = item; GeneratePreview(); await SaveCurrentStateAsync();
    }

    private void RenumberSelected()
    {
        for (var index = 0; index < _selected.Count; index++) _selected[index].Position = index + 1;
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        AvailableCountText.Text = $"{_availableView?.Cast<object>().Count() ?? _available.Count} disponível(is)";
        SelectedCountText.Text = $"{_selected.Count} selecionado(s)";
    }

    private void MilitarySearch_TextChanged(object sender, TextChangedEventArgs e) { _availableView?.Refresh(); RefreshCounters(); }
    private void TemplateSearch_TextChanged(object sender, TextChangedEventArgs e) => _templateView?.Refresh();

    private async void AvailableSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.AvailableSortMode = (AvailableSortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ordem salva (Listar Militares)";
        ApplyAvailableSort();
        await SavePreferencesOnlyAsync();
    }

    private void SelectSortMode(string mode)
    {
        AvailableSortBox.SelectedItem = AvailableSortBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Content?.ToString(), mode, StringComparison.OrdinalIgnoreCase)) ?? AvailableSortBox.Items[0];
    }

    private void ApplyAvailableSort()
    {
        if (_availableView is null) return;
        var mode = (AvailableSortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        var order = _customOrder.Select((id, index) => (id, index))
            .GroupBy(x => x.id)
            .ToDictionary(x => x.Key, x => x.Min(y => y.index));
        var sorted = mode switch
        {
            "Somente Nome (A→Z)" => _allMilitary.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            "ID (cadastro)" => _allMilitary.OrderBy(x => x.Id),
            "Posto/Graduação + Nome" => _allMilitary.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => _allMilitary.OrderBy(x => order.GetValueOrDefault(x.Id, int.MaxValue)).ThenBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        _available.Clear();
        foreach (var military in sorted) _available.Add(military);
        _availableView.Refresh(); RefreshCounters();
    }

    private async Task SaveCurrentStateAsync(string? templateName = null)
    {
        await _saveStateGate.WaitAsync();
        try
        {
            var name = templateName ?? CurrentTemplate?.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (name == CurrentTemplate?.Name) SaveFieldValues(name);
                _preferences.SelectionByTemplate[name] = _selected.Select(x => x.Military.Id).Distinct().ToList();
            }
            _preferences.SelectedMilitaryIds = _selected.Select(x => x.Military.Id).Distinct().ToList();
            _preferences.TemplateOrder = _templates.OrderBy(x => x.Order).Select(x => x.Name).ToList();
            await _service.SavePreferencesAsync(_preferences);
        }
        finally { _saveStateGate.Release(); }
    }

    private async Task SavePreferencesOnlyAsync()
    {
        await _saveStateGate.WaitAsync();
        try { await _service.SavePreferencesAsync(_preferences); }
        finally { _saveStateGate.Release(); }
    }

    private async void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        var template = new BulletinTemplate { Name = "NOVO MODELO - Geral", Text = "[[ITEM]]\n[[POSTO_ABREV]] [[NOME]]\nPrec-CP [[PREC_CP]] CPF [[CPF]]\n[[/ITEM]]", Order = _templates.Count };
        var dialog = new BulletinTemplateEditorWindow(template) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        template.Name = BulletinService.NormalizeTemplateName(template.Name);
        if (_templates.Any(x => x.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase))) template.Name = UniqueTemplateName(template.Name + " cópia");
        _templates.Add(template); RenumberTemplates(); await _service.SaveTemplatesAsync(_templates); TemplateList.SelectedItem = template;
    }

    private async void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTemplate is null) return;
        var oldName = CurrentTemplate.Name;
        var dialog = new BulletinTemplateEditorWindow(CurrentTemplate) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        CurrentTemplate.Name = BulletinService.NormalizeTemplateName(CurrentTemplate.Name);
        if (!oldName.Equals(CurrentTemplate.Name, StringComparison.OrdinalIgnoreCase) && _templates.Any(x => x != CurrentTemplate && x.Name.Equals(CurrentTemplate.Name, StringComparison.OrdinalIgnoreCase)))
        {
            SigfurDialog.Show(this, "Já existe outro modelo com esse nome.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            CurrentTemplate.Name = oldName;
            return;
        }
        if (_preferences.FormValues.Remove(oldName, out var form)) _preferences.FormValues[CurrentTemplate.Name] = form;
        if (_preferences.SelectionByTemplate.Remove(oldName, out var ids)) _preferences.SelectionByTemplate[CurrentTemplate.Name] = ids;
        _currentTemplateName = CurrentTemplate.Name;
        RawTemplateBox.Text = CurrentTemplate.Text;
        BuildFields(CurrentTemplate); GeneratePreview(); await _service.SaveTemplatesAsync(_templates);
    }

    private async void DuplicateTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTemplate is null) return;
        var name = UniqueTemplateName(CurrentTemplate.Name + " cópia");
        var clone = CurrentTemplate.Clone(name); clone.Order = _templates.Count; _templates.Add(clone); RenumberTemplates(); await _service.SaveTemplatesAsync(_templates); TemplateList.SelectedItem = clone;
    }

    private async void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTemplate is null) return;
        if (SigfurDialog.Show(this, $"Excluir o modelo ‘{CurrentTemplate.Name}’?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var item = CurrentTemplate; _templates.Remove(item); _preferences.FormValues.Remove(item.Name); _preferences.SelectionByTemplate.Remove(item.Name); RenumberTemplates(); await _service.SaveTemplatesAsync(_templates); TemplateList.SelectedItem = _templates.FirstOrDefault();
    }

    private string UniqueTemplateName(string seed)
    {
        var baseName = BulletinService.NormalizeTemplateName(seed);
        var name = baseName;
        var index = 2;
        while (_templates.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = BulletinService.NormalizeTemplateName($"{baseName} {index++}");
        return name;
    }

    private void RenumberTemplates()
    {
        for (var index = 0; index < _templates.Count; index++) _templates[index].Order = index;
        _templateView?.Refresh();
    }

    private void MoveTemplateUp_Click(object sender, RoutedEventArgs e) => MoveTemplate(-1);
    private void MoveTemplateDown_Click(object sender, RoutedEventArgs e) => MoveTemplate(1);
    private async void MoveTemplate(int delta)
    {
        if (_preferences.OrderLocked) { NotifyBulletinOrderLocked(); return; }
        if (CurrentTemplate is null) return;
        var index = _templates.IndexOf(CurrentTemplate); var target = index + delta;
        if (target < 0 || target >= _templates.Count) return;
        _templates.Move(index, target); RenumberTemplates(); await _service.SaveTemplatesAsync(_templates); TemplateList.SelectedItem = CurrentTemplate;
    }

    private async void SavedKeys_Click(object sender, RoutedEventArgs e)
    {
        var manualKeys = await _service.LoadManualKeysAsync();
        var dialog = new BulletinKeysWindow(manualKeys) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await _service.SaveGlobalKeysAsync(dialog.Values); _globalKeys = await _service.LoadGlobalKeysAsync(); GeneratePreview(); StatusText.Text = "Chaves manuais salvas e aplicadas à prévia.";
    }

    private async void SavedBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTemplate is null) return;
        var dialog = new SavedBulletinPickerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedReference is not { } reference) return;

        SaveFieldValues(CurrentTemplate.Name);
        var values = _preferences.FormValues.GetValueOrDefault(CurrentTemplate.Name)
                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var referenceKeys = BuildPublicationKeys(reference);
        foreach (var pair in referenceKeys) _globalKeys[pair.Key] = pair.Value;

        var selectedKind = IsAdtReference(reference) ? PublicationFieldKind.Adt : PublicationFieldKind.Bi;
        var applied = 0;
        foreach (var field in _service.DetectFields(CurrentTemplate.Text, includeAutomatic: true))
        {
            var fieldKind = ClassifyPublicationField(field.Key);
            if (fieldKind == PublicationFieldKind.None) continue;
            if (fieldKind != PublicationFieldKind.Generic && fieldKind != selectedKind) continue;

            var value = PublicationValue(field.Key, reference);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (BulletinService.IsAutomaticKey(field.Key)) _globalKeys[field.Key] = value;
            else values[field.Key] = value;
            applied++;
        }

        _preferences.FormValues[CurrentTemplate.Name] = values;
        await _service.SaveBulletinReferenceKeysAsync(referenceKeys);
        BuildFields(CurrentTemplate);
        GeneratePreview();
        await SavePreferencesOnlyAsync();
        StatusText.Text = applied > 0
            ? $"{FormatPublicationReference(reference)} aplicado em {applied} campo(s) compatível(is), sem ocupar BI e ADT juntos."
            : $"{FormatPublicationReference(reference)} salvo como referência global, mas este modelo não possui campo compatível.";
    }

    private static Dictionary<string, string> BuildPublicationKeys(SavedBulletinReference reference)
    {
        var isAdt = IsAdtReference(reference);
        var abbreviatedDate = AbbreviatedDate(reference.Date);
        var formatted = FormatPublicationReference(reference);
        var publicationNumber = PublicationNumberWithoutYear(reference.Number);
        var completeNumber = string.IsNullOrWhiteSpace(reference.Bar) ? publicationNumber : $"{publicationNumber} BAR {reference.Bar}";
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BI_REFERENCIA"] = isAdt ? string.Empty : formatted,
            ["REFERENCIA_BOLETIM"] = formatted,
            ["PUBLICACAO_BI"] = isAdt ? string.Empty : formatted,
            ["BOLETIM_REFERENCIA"] = isAdt ? string.Empty : formatted,
            ["BI_TIPO"] = isAdt ? "ADT FURRIEL" : "BOLETIM INTERNO",
            ["BI_ORIGEM"] = reference.Kind,
            ["BI_NUMERO"] = isAdt ? string.Empty : publicationNumber,
            ["NUM_BI"] = isAdt ? string.Empty : publicationNumber,
            ["BI_NUMERO_COMPLETO"] = isAdt ? string.Empty : completeNumber,
            ["DATA_BI"] = isAdt ? string.Empty : reference.Date,
            ["DATA_PUBLICACAO_BI"] = isAdt ? string.Empty : reference.Date,
            ["DATA_PUBLICACAO"] = reference.Date,
            ["DATA_BI_ABREV"] = isAdt ? string.Empty : abbreviatedDate,
            ["DATA_PUBLICACAO_BI_ABREV"] = isAdt ? string.Empty : abbreviatedDate,
            ["ADT_REFERENCIA"] = isAdt ? formatted : string.Empty,
            ["NUM_ADT"] = isAdt ? publicationNumber : string.Empty,
            ["DATA_ADT"] = isAdt ? reference.Date : string.Empty,
            ["DATA_ADT_ABREV"] = isAdt ? abbreviatedDate : string.Empty,
            ["BAR"] = isAdt ? reference.Bar : string.Empty
        };
    }

    private static string PublicationValue(string key, SavedBulletinReference reference)
    {
        var normalized = Normalize(key).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var isAdt = IsAdtReference(reference);
        var fieldKind = ClassifyPublicationField(key);
        if (fieldKind == PublicationFieldKind.Bi && isAdt) return string.Empty;
        if (fieldKind == PublicationFieldKind.Adt && !isAdt) return string.Empty;

        var formatted = FormatPublicationReference(reference);
        var abbreviatedDate = AbbreviatedDate(reference.Date);
        var publicationNumber = PublicationNumberWithoutYear(reference.Number);
        if (normalized is "bar" or "numerobar" or "barreferencia") return isAdt ? reference.Bar : string.Empty;
        if (normalized.Contains("tipo", StringComparison.Ordinal) && fieldKind != PublicationFieldKind.None)
            return isAdt ? "ADT FURRIEL" : "BOLETIM INTERNO";
        if (normalized.Contains("origem", StringComparison.Ordinal) && fieldKind != PublicationFieldKind.None) return reference.Kind;
        if (normalized.Contains("data", StringComparison.Ordinal) && fieldKind != PublicationFieldKind.None)
            return normalized.Contains("abrev", StringComparison.Ordinal) ? abbreviatedDate : reference.Date;
        if (normalized.Contains("numerocompleto", StringComparison.Ordinal) && fieldKind != PublicationFieldKind.None)
            return string.IsNullOrWhiteSpace(reference.Bar) ? publicationNumber : $"{publicationNumber} BAR {reference.Bar}";
        if ((normalized.Contains("numero", StringComparison.Ordinal) || normalized.StartsWith("num", StringComparison.Ordinal)) && fieldKind != PublicationFieldKind.None)
            return publicationNumber;
        return fieldKind == PublicationFieldKind.None ? string.Empty : formatted;
    }

    private static string FormatPublicationReference(SavedBulletinReference reference)
    {
        var date = AbbreviatedDate(reference.Date);
        var publicationNumber = PublicationNumberWithoutYear(reference.Number);
        if (IsAdtReference(reference))
        {
            var bar = string.IsNullOrWhiteSpace(reference.Bar) ? string.Empty : $", BAR {reference.Bar}";
            return $"Adt Nrº {publicationNumber}{bar}, {date}, da 4ª Cia PE".Trim();
        }
        return $"BI Nrº {publicationNumber}, {date}, da 4ª Cia PE".Trim();
    }

    private static string PublicationNumberWithoutYear(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var slashIndex = text.LastIndexOf('/');
        if (slashIndex > 0 && slashIndex < text.Length - 1)
        {
            var suffix = text[(slashIndex + 1)..].Trim();
            if ((suffix.Length == 2 || suffix.Length == 4) && suffix.All(char.IsDigit))
                return text[..slashIndex].Trim();
        }

        return text;
    }

    private static string AbbreviatedDate(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AllowWhiteSpaces, out var date))
            return date.ToString("dd MMM yy", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        return value;
    }

    private void ExternalBulletins_Click(object sender, RoutedEventArgs e)
    {
        var window = new ExternalBulletinsWindow(App.ExternalBulletins) { Owner = this };
        window.Show();
        window.Activate();
    }

    private async void OpenTransportAid_Click(object sender, RoutedEventArgs e) => await OpenHostActionAsync("aux_transporte");
    private async void OpenVacationPlan_Click(object sender, RoutedEventArgs e) => await OpenHostActionAsync("plano_ferias");
    private async void OpenAdjustmentAccounts_Click(object sender, RoutedEventArgs e) => await OpenHostActionAsync("ajuste_contas");

    private async Task OpenHostActionAsync(string actionId)
    {
        await SaveCurrentStateAsync();
        if (Owner is MainWindow main)
        {
            await main.ExecuteChildActionAsync(actionId);
            return;
        }
        StatusText.Text = "Abra o Boletim pela janela principal para usar este atalho.";
    }

    private void NumberToWords_Click(object sender, RoutedEventArgs e) => new NumberToWordsWindow { Owner = this }.ShowDialog();
    private void Appearance_Click(object sender, RoutedEventArgs e) => new AppearanceWindow(App.Theme) { Owner = this }.ShowDialog();
    private void Generate_Click(object sender, RoutedEventArgs e) { GeneratePreview(); WorkTabs.SelectedItem = PreviewTab; }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        GeneratePreview(); if (_lastRender is null) return; Clipboard.SetText(_lastRender.Text); StatusText.Text = "Texto copiado.";
    }

    private void CopyWord_Click(object sender, RoutedEventArgs e)
    {
        GeneratePreview(); if (_lastRender is null || PreviewDocumentViewer.Document is null) return;
        BulletinService.CopyForWord(PreviewDocumentViewer.Document, _lastRender.Text); StatusText.Text = "Boletim copiado para o Word com o nome de guerra em negrito.";
    }

    private void SaveText_Click(object sender, RoutedEventArgs e)
    {
        GeneratePreview(); if (_lastRender is null) return;
        var dialog = new SaveFileDialog { Filter = "Texto (*.txt)|*.txt", FileName = SafeName(CurrentTemplate?.Name ?? "boletim") + ".txt", InitialDirectory = App.Paths.GeneratedDocumentsDirectory };
        Directory.CreateDirectory(App.Paths.GeneratedDocumentsDirectory);
        if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, _lastRender.Text, Encoding.UTF8); StatusText.Text = "Boletim salvo em " + dialog.FileName; }
    }


    private void EditConsequencesLarge_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TextBox
        {
            Text = ConsequencesTextBox.Text ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Padding = new Thickness(12),
            MinHeight = 260
        };

        var dialog = new Window
        {
            Title = "Editar texto de fechamento — Em consequência",
            Owner = this,
            Width = 820,
            Height = 520,
            MinWidth = 680,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("AppBackgroundBrush") as Brush ?? Background,
            Foreground = TryFindResource("TextBrush") as Brush ?? Foreground,
            ShowInTaskbar = false
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "Texto de fechamento / Em consequência",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "Edite aqui o texto que será enviado ao campo de fechamento do SisBol. Quebras de linha serão preservadas.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("MutedBrush") as Brush ?? Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        Grid.SetRow(editor, 1);
        root.Children.Add(editor);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var restoreButton = new Button
        {
            Content = "Restaurar padrão",
            MinWidth = 120,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 6, 12, 6)
        };
        restoreButton.Click += (_, _) => editor.Text = CurrentDefaultConsequences();

        var cancelButton = new Button
        {
            Content = "Cancelar",
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var okButton = new Button
        {
            Content = "Aplicar texto",
            MinWidth = 120,
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            ConsequencesTextBox.Text = editor.Text;
            dialog.DialogResult = true;
        };

        buttons.Children.Add(restoreButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        editor.Focus();
        editor.CaretIndex = editor.Text.Length;
        dialog.ShowDialog();
    }


    private async void SendSisbol_Click(object sender, RoutedEventArgs e)
    {
        GeneratePreview();
        if (CurrentTemplate is null)
        {
            SigfurDialog.Show(this, "Selecione um modelo de boletim antes de enviar ao SisBol.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Envio ao SisBol cancelado: nenhum modelo selecionado.";
            return;
        }
        if (_lastRender is null)
        {
            SigfurDialog.Show(this, "Não consegui gerar a prévia do boletim. Confira os campos do modelo.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Envio ao SisBol cancelado: prévia não gerada.";
            return;
        }
        if (!App.Sisbol.IsReady)
        {
            SigfurDialog.Show(this,
                "O SisBol não está preparado. Vá na janela principal, clique em ‘Preparar SisBol’, conclua o login/captcha e valide a sessão.",
                "SisBol não preparado", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "SisBol não preparado. Prepare na janela principal antes de enviar.";
            await RefreshSisbolStatusAsync();
            return;
        }
        if (CurrentTemplate.Text.Contains("[[ITEM]]", StringComparison.OrdinalIgnoreCase) && _selected.Count == 0)
        {
            SigfurDialog.Show(this, "Adicione pelo menos um militar à ordem do boletim.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_lastRender.Text))
        {
            SigfurDialog.Show(this, "O modelo não gerou conteúdo. Confira o texto do modelo e os militares selecionados.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_lastRender.UnresolvedTokens.Count > 0)
        {
            SigfurDialog.Show(this, "Ainda existem campos não preenchidos: " + string.Join(", ", _lastRender.UnresolvedTokens), "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            WorkTabs.SelectedIndex = 0; return;
        }
        try
        {
            if (_sisbolSending) return;
            _sisbolSending = true;
            SendSisbolButton.IsEnabled = false;
            StatusText.Text = "Enviando matéria ao SisBol…";
            await App.Sisbol.SendAsync(
                _lastRender.Text,
                _selected.Select(x => x.Military).ToList(),
                CurrentTemplate.Name,
                IncludeConsequencesCheck.IsChecked == true,
                ConsequencesTextBox.Text);
            StatusText.Text = "SisBol OK: matéria enviada sem erro.";
            SigfurDialog.Show(this,
                "SisBol OK: a matéria foi enviada. Se o SISBOL não exibiu alerta de erro, pode conferir no navegador.",
                "SisBol", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning); StatusText.Text = ex.Message; }
        finally
        {
            _sisbolSending = false;
            SendSisbolButton.IsEnabled = true;
        }
    }

    private async Task RefreshSisbolStatusAsync()
    {
        if (_sisbolStatusRunning) return;
        _sisbolStatusRunning = true;
        try
        {
            var state = await App.Sisbol.GetStatusAsync();
            SisbolStatusText.Text = state.Ready
                ? $"SisBol pronto — {state.Browser}"
                : state.Alive ? "Concluir login/captcha na tela principal" : "Prepare na tela principal";
        }
        catch
        {
            var state = App.Sisbol.GetCachedStatus();
            SisbolStatusText.Text = state.Ready
                ? $"SisBol pronto — {state.Browser}"
                : state.Alive ? "Concluir login/captcha na tela principal" : "Prepare na tela principal";
        }
        finally
        {
            _sisbolStatusRunning = false;
        }
    }

    private void TemplateList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_preferences.OrderLocked) { _draggedTemplate = null; return; }
        _templateDragStart = e.GetPosition(TemplateList); _draggedTemplate = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as BulletinTemplate;
    }
    private void TemplateList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_preferences.OrderLocked) return;
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTemplate is null) return;
        var point = e.GetPosition(TemplateList); if (Math.Abs(point.X - _templateDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _templateDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(TemplateList, _draggedTemplate, DragDropEffects.Move);
    }
    private async void TemplateList_Drop(object sender, DragEventArgs e)
    {
        if (_preferences.OrderLocked) { NotifyBulletinOrderLocked(); return; }
        if (!e.Data.GetDataPresent(typeof(BulletinTemplate))) return;
        var dragged = (BulletinTemplate)e.Data.GetData(typeof(BulletinTemplate));
        var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as BulletinTemplate;
        if (target is null || target == dragged) return;
        var oldIndex = _templates.IndexOf(dragged); var newIndex = _templates.IndexOf(target); _templates.Move(oldIndex, newIndex); RenumberTemplates(); await _service.SaveTemplatesAsync(_templates); TemplateList.SelectedItem = dragged;
    }

    private void SelectedGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_preferences.OrderLocked) { _draggedSelected = null; return; }
        _selectedDragStart = e.GetPosition(SelectedGrid); _draggedSelected = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource)?.Item as BulletinSelectedMilitary;
    }
    private void SelectedGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_preferences.OrderLocked) return;
        if (e.LeftButton != MouseButtonState.Pressed || _draggedSelected is null) return;
        var point = e.GetPosition(SelectedGrid); if (Math.Abs(point.X - _selectedDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _selectedDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(SelectedGrid, _draggedSelected, DragDropEffects.Move);
    }
    private async void SelectedGrid_Drop(object sender, DragEventArgs e)
    {
        if (_preferences.OrderLocked) { NotifyBulletinOrderLocked(); return; }
        if (!e.Data.GetDataPresent(typeof(BulletinSelectedMilitary))) return;
        var dragged = (BulletinSelectedMilitary)e.Data.GetData(typeof(BulletinSelectedMilitary));
        var target = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource)?.Item as BulletinSelectedMilitary;
        if (target is null || target == dragged) return;
        _selected.Move(_selected.IndexOf(dragged), _selected.IndexOf(target)); RenumberSelected(); GeneratePreview(); await SaveCurrentStateAsync(); SelectedGrid.SelectedItem = dragged;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = GetParentObject(source);
        }
        return null;
    }

    private static DependencyObject? GetParentObject(DependencyObject source)
    {
        if (source is Visual or Visual3D)
            return VisualTreeHelper.GetParent(source);
        if (source is FrameworkContentElement frameworkContent && frameworkContent.Parent is not null)
            return frameworkContent.Parent;
        return source is ContentElement content ? ContentOperations.GetParent(content) : null;
    }

    private static string FriendlyKey(string key) => key.Replace('_', ' ').ToUpperInvariant();
    private static string AdvisoryText(string value) => (value ?? string.Empty)
        .Replace("Campo obrigatório", "Dado recomendado para conferência", StringComparison.OrdinalIgnoreCase)
        .Replace("Obrigatório", "Recomendado", StringComparison.OrdinalIgnoreCase)
        .Replace("Obrigatória", "Recomendada", StringComparison.OrdinalIgnoreCase);
    private static string SafeName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).ToArray());
    }
    private async void LockBulletinOrderCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _preferences.OrderLocked = LockBulletinOrderCheck.IsChecked == true;
        ApplyBulletinOrderLockState();
        await SavePreferencesOnlyAsync();
        StatusText.Text = _preferences.OrderLocked
            ? "Ordem travada: modelos e militares não podem ser arrastados."
            : "Ordem liberada: arraste para reorganizar.";
    }

    private void ApplyBulletinOrderLockState()
    {
        TemplateList.AllowDrop = !_preferences.OrderLocked;
        SelectedGrid.AllowDrop = !_preferences.OrderLocked;
    }

    private void NotifyBulletinOrderLocked()
    {
        StatusText.Text = "A ordem está travada. Desmarque ‘Travar ordem’ para reorganizar.";
    }

}
