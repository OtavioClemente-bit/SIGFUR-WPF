using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Tools;

namespace SIGFUR.Wpf.Views.Documents;

public partial class PhpmWindow : Window
{
    private readonly PhpmTemplateService _service;
    private readonly MilitaryRepository _repository;
    private readonly ObservableCollection<PhpmTemplateDefinition> _visibleTemplates = [];
    private readonly ObservableCollection<MilitaryRecord> _visibleMilitary = [];
    private readonly ObservableCollection<PhpmBatchItem> _batch = [];
    private readonly ObservableCollection<PhpmFieldItem> _fields = [];
    private readonly ObservableCollection<PhpmGenerationRecord> _history = [];
    private readonly Dictionary<string, Dictionary<string, string>> _manualValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhpmFormConfiguration> _formConfigurations = new(StringComparer.OrdinalIgnoreCase);
    private PhpmPublicationReferenceCatalog _publicationReferences = new();
    private PhpmTemplateCatalog _catalog = new();
    private List<MilitaryRecord> _military = [];
    private UiProfile _profile = new();
    private MilitaryRecord? _editingMilitary;
    private bool _loading;
    private bool _switchingPerson;
    private bool _loadingFormConfiguration;

    public PhpmWindow(PhpmTemplateService service, MilitaryRepository repository)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        _repository = repository;
        TemplateList.ItemsSource = _visibleTemplates;
        MilitaryGrid.ItemsSource = _visibleMilitary;
        BatchGrid.ItemsSource = _batch;
        FieldsList.ItemsSource = _fields;
        HistoryGrid.ItemsSource = _history;
        var yesNo = new[] { "Não", "Sim" };
        SpouseBox.ItemsSource = yesNo;
        CompanionBox.ItemsSource = yesNo;
        DesignatedPersonBox.ItemsSource = yesNo;
        ChildrenCountBox.ItemsSource = Enumerable.Range(0, 7).ToList();
        OtherBeneficiariesCountBox.ItemsSource = Enumerable.Range(0, 4).ToList();
        DependentsCountBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        var fieldsView = CollectionViewSource.GetDefaultView(_fields);
        fieldsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PhpmFieldItem.Group)));
        Loaded += async (_, _) => await InitializeAsync();
    }

    private PhpmTemplateDefinition? SelectedTemplate => TemplateList.SelectedItem as PhpmTemplateDefinition;

    private async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            var catalogTask = _service.LoadCatalogAsync();
            var militaryTask = _repository.GetAllAsync();
            var profileTask = App.Settings.LoadProfileAsync();
            var publicationReferencesTask = _service.LoadPublicationReferencesAsync();
            await Task.WhenAll(catalogTask, militaryTask, profileTask, publicationReferencesTask);
            _catalog = await catalogTask;
            _military = await militaryTask;
            _profile = await profileTask;
            _publicationReferences = await publicationReferencesTask;
            LibreOfficeStatusText.Text = _service.IsLibreOfficeAvailable ? "LibreOffice disponível" : "LibreOffice não localizado";
            LibreOfficeStatusText.Foreground = FindResource(_service.IsLibreOfficeAvailable ? "SuccessBrush" : "WarningBrush") as System.Windows.Media.Brush;
            ApplyTemplateFilter();
            ApplyMilitaryFilter();
            await RefreshHistoryAsync();
            TemplateMetric.Text = _catalog.Templates.Count.ToString("N0");
            if (_visibleTemplates.Count > 0)
            {
                TemplateList.SelectedIndex = 0;
                await LoadTemplateAsync(_visibleTemplates[0]);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyTemplateFilter()
    {
        var query = Normalize(TemplateSearchBox.Text);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selectedId = SelectedTemplate?.Id;
        _visibleTemplates.Clear();
        foreach (var item in _catalog.Templates)
        {
            var haystack = Normalize(item.Title + " " + item.Description + " " + item.TypeText);
            if (terms.Length == 0 || terms.All(term => haystack.Contains(term, StringComparison.Ordinal))) _visibleTemplates.Add(item);
        }
        if (!string.IsNullOrWhiteSpace(selectedId))
            TemplateList.SelectedItem = _visibleTemplates.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyMilitaryFilter()
    {
        var query = Normalize(MilitarySearchBox.Text);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _visibleMilitary.Clear();
        foreach (var item in _military.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name))
        {
            var haystack = Normalize($"{item.Rank} {item.Name} {item.WarName} {item.Cpf} {item.PrecCp} {item.MilitaryId}");
            if (terms.Length == 0 || terms.All(term => haystack.Contains(term, StringComparison.Ordinal))) _visibleMilitary.Add(item);
        }
        StatusText.Text = $"{_visibleMilitary.Count:N0} militar(es) disponível(is). Selecione várias linhas para montar o lote.";
    }

    private async Task LoadTemplateAsync(PhpmTemplateDefinition template)
    {
        SaveCurrentManualValues();
        TemplateTitleText.Text = template.Title;
        TemplateDescriptionText.Text = template.Description;
        if (File.Exists(template.TemplatePath) && template.Placeholders.Count == 0)
        {
            template.Placeholders = await _service.ExtractPlaceholdersAsync(template.TemplatePath);
            await _service.SaveCatalogAsync(_catalog);
        }
        TemplateStatusText.Text = $"{template.TypeText}  •  {template.Placeholders.Count:N0} campo(s) usado(s)";
        RebuildFields();
        SuggestOutputName();
        UpdateAllBatchStatuses();
        UpdateGenerationSummary();
    }

    private void RebuildFields()
    {
        foreach (var field in _fields) field.PropertyChanged -= Field_PropertyChanged;
        _fields.Clear();
        if (SelectedTemplate is not { } template || _editingMilitary is not { } military)
        {
            DynamicFormPanel.Visibility = Visibility.Collapsed;
            EditingMilitaryText.Text = "Selecione um militar na etapa Pessoas e lote.";
            RequiredMetric.Text = "0";
            MissingMetric.Text = "0";
            return;
        }

        var automatic = _service.BuildAutomaticFields(military, _profile);
        var defaults = _catalog.SavedValues.GetValueOrDefault(template.Id) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manual = _manualValues.GetValueOrDefault(PersonStateKey(template, military)) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configuration = GetFormConfiguration(template, military);
        ConfigureDynamicFormPanel(template, configuration);
        var allKeys = template.Placeholders.Count > 0 ? template.Placeholders : _service.GetSuggestedFields(template.Id);
        var keys = GetActiveFieldKeys(template, allKeys, configuration);
        var required = GetActiveRequiredFields(template, keys, configuration);
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(FieldGroupOrder).ThenBy(FieldOrder).ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase))
        {
            string value;
            string source;
            if (manual.TryGetValue(key, out var manualValue)) { value = manualValue; source = "Manual"; }
            else if (automatic.TryGetValue(key, out var automaticValue)) { value = automaticValue; source = "Cadastro"; }
            else if (defaults.TryGetValue(key, out var defaultValue)) { value = defaultValue; source = "Padrão"; }
            else { value = string.Empty; source = "Informar"; }

            var suggestions = FieldSuggestions(key);
            var officialReference = IsBulletinReferenceField(key) || IsAditamentReferenceField(key);
            if (officialReference && suggestions.Count > 0 && !suggestions.Contains(value, StringComparer.CurrentCultureIgnoreCase))
            {
                value = string.Empty;
                source = "Informar";
            }

            var field = new PhpmFieldItem
            {
                Key = key,
                Label = FriendlyLabel(key),
                Group = FieldGroup(key),
                Hint = FieldHint(key),
                IsRequired = required.Contains(key),
                Suggestions = suggestions,
                IsOfficialPublicationReference = officialReference
            };
            field.SetValue(value, source);
            field.PropertyChanged += Field_PropertyChanged;
            _fields.Add(field);
        }
        EditingMilitaryText.Text = $"{military.ShortRank} {NameHighlightHelper.PlainDisplay(military.Name, military.WarName)}";
        FieldsGuidanceText.Text = template.Placeholders.Count > 0
            ? BuildFieldsGuidance(template, configuration)
            : "O modelo ainda não expôs marcadores; exibindo os campos essenciais sugeridos.";
        UpdateFieldMetrics();
    }

    private PhpmFormConfiguration GetFormConfiguration(PhpmTemplateDefinition template, MilitaryRecord military)
    {
        var key = PersonStateKey(template, military);
        if (_formConfigurations.TryGetValue(key, out var configuration)) return configuration;
        configuration = new PhpmFormConfiguration
        {
            DependentsCount = template.Id.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase) ? 1 : 0
        };
        _formConfigurations[key] = configuration;
        return configuration;
    }

    private void ConfigureDynamicFormPanel(PhpmTemplateDefinition template, PhpmFormConfiguration configuration)
    {
        var beneficiaryForm = template.Id.Equals("phpm_decl_benef", StringComparison.OrdinalIgnoreCase);
        var dependentForm = template.Id.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase);
        DynamicFormPanel.Visibility = beneficiaryForm || dependentForm ? Visibility.Visible : Visibility.Collapsed;
        BeneficiaryConfigurationPanel.Visibility = beneficiaryForm ? Visibility.Visible : Visibility.Collapsed;
        DependentConfigurationPanel.Visibility = dependentForm ? Visibility.Visible : Visibility.Collapsed;
        if (!beneficiaryForm && !dependentForm) return;

        _loadingFormConfiguration = true;
        try
        {
            SpouseBox.SelectedItem = configuration.IncludeSpouse ? "Sim" : "Não";
            ChildrenCountBox.SelectedItem = Math.Clamp(configuration.ChildrenCount, 0, 6);
            CompanionBox.SelectedItem = configuration.IncludeCompanion ? "Sim" : "Não";
            OtherBeneficiariesCountBox.SelectedItem = Math.Clamp(configuration.OtherBeneficiariesCount, 0, 3);
            DesignatedPersonBox.SelectedItem = configuration.IncludeDesignatedPerson ? "Sim" : "Não";
            DependentsCountBox.SelectedItem = Math.Clamp(configuration.DependentsCount <= 0 ? 1 : configuration.DependentsCount, 1, 10);
        }
        finally { _loadingFormConfiguration = false; }
    }

    private void DynamicFormConfiguration_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFormConfiguration || _loading || SelectedTemplate is not { } template || _editingMilitary is not { } military) return;
        SaveCurrentManualValues();
        var configuration = GetFormConfiguration(template, military);
        configuration.IncludeSpouse = SpouseBox.SelectedItem?.ToString() == "Sim";
        configuration.ChildrenCount = ChildrenCountBox.SelectedItem is int children ? children : 0;
        configuration.IncludeCompanion = CompanionBox.SelectedItem?.ToString() == "Sim";
        configuration.OtherBeneficiariesCount = OtherBeneficiariesCountBox.SelectedItem is int others ? others : 0;
        configuration.IncludeDesignatedPerson = DesignatedPersonBox.SelectedItem?.ToString() == "Sim";
        configuration.DependentsCount = DependentsCountBox.SelectedItem is int dependents ? dependents : 1;
        RebuildFields();
        UpdateGenerationSummary();
        StatusText.Text = "Formulário ajustado. Somente os dados das pessoas selecionadas estão abertos para preenchimento.";
    }

    private static List<string> GetActiveFieldKeys(PhpmTemplateDefinition template, IEnumerable<string> fields, PhpmFormConfiguration configuration)
    {
        var keys = fields.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (template.Id.Equals("phpm_capa", StringComparison.OrdinalIgnoreCase))
            return keys.Where(key => key is "MILITAR_PG_ABREV" or "MILITAR_NOME").ToList();

        if (template.Id.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase))
            return keys.Where(key => !TryNumberedField(key, "DEP", out var number) || number <= configuration.DependentsCount).ToList();

        if (!template.Id.Equals("phpm_decl_benef", StringComparison.OrdinalIgnoreCase)) return keys;
        return keys.Where(key =>
        {
            if (key.StartsWith("CONJUGE_", StringComparison.OrdinalIgnoreCase)) return configuration.IncludeSpouse;
            if (TryNumberedField(key, "FILHO", out var child)) return child <= configuration.ChildrenCount;
            if (key.StartsWith("COMP_", StringComparison.OrdinalIgnoreCase)) return configuration.IncludeCompanion;
            if (TryNumberedField(key, "OUTRO", out var other)) return other <= configuration.OtherBeneficiariesCount;
            if (key.StartsWith("PESSOA_", StringComparison.OrdinalIgnoreCase)) return configuration.IncludeDesignatedPerson;
            return true;
        }).ToList();
    }

    private IReadOnlySet<string> GetActiveRequiredFields(PhpmTemplateDefinition template, IReadOnlyCollection<string> activeFields, PhpmFormConfiguration configuration)
    {
        var required = _service.GetRequiredFields(template)
            .Where(activeFields.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (template.Id.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in activeFields.Where(key => TryNumberedField(key, "DEP", out _)
                                                          && !key.EndsWith("_OBS", StringComparison.OrdinalIgnoreCase)))
                required.Add(key);
        }
        else if (template.Id.Equals("phpm_decl_benef", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in activeFields)
            {
                var suffix = key[(key.LastIndexOf('_') + 1)..].ToUpperInvariant();
                if (key.StartsWith("CONJUGE_", StringComparison.OrdinalIgnoreCase) && suffix == "NOME") required.Add(key);
                else if (TryNumberedField(key, "FILHO", out _) && suffix is "NOME" or "SEXO" or "NASC") required.Add(key);
                else if (key.StartsWith("COMP_", StringComparison.OrdinalIgnoreCase) && suffix is "NOME" or "SEXO" or "CPF" or "NASC") required.Add(key);
                else if (TryNumberedField(key, "OUTRO", out _) && suffix is "NOME" or "SEXO" or "NASC" or "PARENTESCO") required.Add(key);
                else if (key.StartsWith("PESSOA_", StringComparison.OrdinalIgnoreCase) && suffix is "NOME" or "SEXO" or "NASC") required.Add(key);
            }
        }
        return required;
    }

    private static bool TryNumberedField(string key, string prefix, out int number)
    {
        var match = Regex.Match(key, $@"^{Regex.Escape(prefix)}(\d+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return int.TryParse(match.Success ? match.Groups[1].Value : string.Empty, out number);
    }

    private static string BuildFieldsGuidance(PhpmTemplateDefinition template, PhpmFormConfiguration configuration)
    {
        if (template.Id.Equals("phpm_capa", StringComparison.OrdinalIgnoreCase))
            return "A capa usa somente o P/G e o nome do militar. O nome de guerra será destacado em negrito automaticamente no documento.";
        if (template.Id.Equals("phpm_decl_benef", StringComparison.OrdinalIgnoreCase))
            return "Informe primeiro quem fará parte da declaração. Os formulários aparecem na quantidade escolhida e os campos opcionais permanecem identificados.";
        if (template.Id.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase))
            return $"Preenchendo {configuration.DependentsCount} beneficiário(s). A tabela do documento terá exatamente essa quantidade de linhas; observação é opcional.";
        return "Somente os campos realmente usados neste documento são exibidos. Os destacados precisam ser preenchidos.";
    }

    private void Field_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhpmFieldItem.Value)) return;
        UpdateFieldMetrics();
        SaveCurrentManualValues();
        UpdateGenerationSummary();
    }

    private void SetEditingMilitary(MilitaryRecord? military, bool openFields)
    {
        if (_switchingPerson) return;
        _switchingPerson = true;
        try
        {
            SaveCurrentManualValues();
            _editingMilitary = military;
            RebuildFields();
            SuggestOutputName();
            UpdateGenerationSummary();
            if (openFields && military is not null) WorkflowTabs.SelectedIndex = 1;
        }
        finally { _switchingPerson = false; }
    }

    private void SaveCurrentManualValues()
    {
        if (SelectedTemplate is not { } template || _editingMilitary is not { } military || _fields.Count == 0) return;
        var automatic = _service.BuildAutomaticFields(military, _profile);
        var stateKey = PersonStateKey(template, military);
        var manual = _manualValues.TryGetValue(stateKey, out var existing)
            ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in _fields)
        {
            var differsFromAutomatic = !automatic.TryGetValue(field.Key, out var automaticValue)
                                       || !string.Equals(automaticValue?.Trim(), field.Value?.Trim(), StringComparison.CurrentCulture);
            if (field.Source.Equals("Manual", StringComparison.OrdinalIgnoreCase) && differsFromAutomatic)
                manual[field.Key] = field.Value ?? string.Empty;
            else
                manual.Remove(field.Key);
        }
        _manualValues[stateKey] = manual;
        var batchItem = _batch.FirstOrDefault(item => item.Id == military.Id);
        if (batchItem is not null) batchItem.PendingFields = GetMissingFields(template, military).Count;
    }

    private Dictionary<string, string> BuildValuesFor(PhpmTemplateDefinition template, MilitaryRecord military)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_catalog.SavedValues.TryGetValue(template.Id, out var defaults))
            foreach (var pair in defaults) values[pair.Key] = pair.Value ?? string.Empty;
        foreach (var pair in _service.BuildAutomaticFields(military, _profile)) values[pair.Key] = pair.Value ?? string.Empty;
        if (_manualValues.TryGetValue(PersonStateKey(template, military), out var manual))
            foreach (var pair in manual) values[pair.Key] = pair.Value ?? string.Empty;
        var configuration = GetFormConfiguration(template, military);
        var active = GetActiveFieldKeys(template, template.Placeholders.Count > 0 ? template.Placeholders : _service.GetSuggestedFields(template.Id), configuration)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in template.Placeholders.Where(key => !active.Contains(key))) values[key] = string.Empty;
        return values;
    }

    private List<string> GetMissingFields(PhpmTemplateDefinition template, MilitaryRecord military)
    {
        var values = BuildValuesFor(template, military);
        var allKeys = template.Placeholders.Count > 0
            ? template.Placeholders
            : _service.GetSuggestedFields(template.Id);
        var configuration = GetFormConfiguration(template, military);
        var keys = GetActiveFieldKeys(template, allKeys, configuration);
        var required = GetActiveRequiredFields(template, keys, configuration);
        return keys
            .Where(key => ValidateFieldValue(key, values.GetValueOrDefault(key), required.Contains(key)).Length > 0)
            .OrderBy(FieldGroupOrder).ThenBy(FieldOrder).ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void UpdateFieldMetrics()
    {
        foreach (var field in _fields)
            field.ValidationMessage = ValidateFieldValue(field.Key, field.Value, field.IsRequired);

        var required = _fields.Count(field => field.IsRequired);
        var missing = _fields.Count(field => field.HasError);
        RequiredMetric.Text = required.ToString("N0");
        MissingMetric.Text = missing.ToString("N0");
        if (_editingMilitary is not null)
        {
            var batchItem = _batch.FirstOrDefault(item => item.Id == _editingMilitary.Id);
            if (batchItem is not null) batchItem.PendingFields = missing;
        }
    }

    private void UpdateAllBatchStatuses()
    {
        if (SelectedTemplate is not { } template) return;
        foreach (var item in _batch) item.PendingFields = GetMissingFields(template, item.Military).Count;
        UpdateBatchMetric();
    }

    private void UpdateGenerationSummary()
    {
        if (SelectedTemplate is not { } template)
        {
            GenerationSummaryText.Text = "Selecione um documento para iniciar.";
            return;
        }
        var lines = new List<string> { $"Documento: {template.Title}" };
        if (_editingMilitary is not null)
        {
            var missing = GetMissingFields(template, _editingMilitary);
            lines.Add($"Militar em edição: {_editingMilitary.ShortRank} {_editingMilitary.WarName}");
            lines.Add(missing.Count == 0 ? "Situação: pronto para gerar." : $"Situação: existem {missing.Count} campo(s) pendente(s) ou inválido(s)." );
        }
        else lines.Add("Militar em edição: nenhum selecionado.");
        var ready = _batch.Count(item => item.PendingFields == 0);
        lines.Add($"Lote: {ready} pronto(s) de {_batch.Count} pessoa(s)." );
        GenerationSummaryText.Text = string.Join(Environment.NewLine, lines);
    }

    private void SuggestOutputName()
    {
        if (SelectedTemplate is not { } template || _editingMilitary is not { } military) return;
        OutputNameBox.Text = $"{template.Title}_{military.ShortRank}_{military.WarName}_{DateTime.Today:yyyyMMdd}";
    }

    private async Task RefreshHistoryAsync()
    {
        var history = await _service.LoadHistoryAsync();
        _history.Clear();
        foreach (var item in history.Take(200)) _history.Add(item);
        GeneratedMetric.Text = history.Count(x => x.Success).ToString("N0");
    }

    private async Task SaveCatalogAsync()
    {
        await _service.SaveCatalogAsync(_catalog);
        TemplateMetric.Text = _catalog.Templates.Count.ToString("N0");
    }

    private void TemplateSearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyTemplateFilter(); }
    private void MilitarySearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyMilitaryFilter(); }

    private async void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTemplate is not { } template) return;
        await LoadTemplateAsync(template);
    }

    private void MilitaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _switchingPerson || MilitaryGrid.SelectedItem is not MilitaryRecord military) return;
        SetEditingMilitary(military, openFields: false);
    }

    private void BatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _switchingPerson || BatchGrid.SelectedItem is not PhpmBatchItem item) return;
        SetEditingMilitary(item.Military, openFields: false);
    }

    private void MilitaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AddSelectedToBatch();
        if (_editingMilitary is not null) WorkflowTabs.SelectedIndex = 1;
    }

    private void AddSelectedToBatch_Click(object sender, RoutedEventArgs e) => AddSelectedToBatch();

    private void AddSelectedToBatch()
    {
        var selected = MilitaryGrid.SelectedItems.Cast<MilitaryRecord>().ToList();
        if (selected.Count == 0 && MilitaryGrid.SelectedItem is MilitaryRecord single) selected.Add(single);
        if (selected.Count == 0) { StatusText.Text = "Selecione ao menos um militar."; return; }
        SaveCurrentManualValues();
        var added = 0;
        foreach (var military in selected)
        {
            if (_batch.Any(item => item.Id == military.Id)) continue;
            var item = new PhpmBatchItem { Military = military };
            item.PendingFields = SelectedTemplate is { } template ? GetMissingFields(template, military).Count : 0;
            _batch.Add(item);
            added++;
        }
        UpdateBatchMetric();
        StatusText.Text = added == 0 ? "Os militares selecionados já estavam no lote." : $"{added:N0} militar(es) adicionado(s) ao lote.";
    }

    private void AddFilteredToBatch_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentManualValues();
        var added = 0;
        foreach (var military in _visibleMilitary)
        {
            if (_batch.Any(item => item.Id == military.Id)) continue;
            var item = new PhpmBatchItem { Military = military };
            item.PendingFields = SelectedTemplate is { } template ? GetMissingFields(template, military).Count : 0;
            _batch.Add(item);
            added++;
        }
        UpdateBatchMetric();
        StatusText.Text = $"{added:N0} militar(es) filtrado(s) adicionado(s). Clique em cada pendente para completar os dados.";
    }

    private void RemoveBatchItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: PhpmBatchItem item }) RemoveBatchItem(item);
    }

    private void RemoveBatch_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is PhpmBatchItem item) RemoveBatchItem(item);
    }

    private void RemoveBatchItem(PhpmBatchItem item)
    {
        _batch.Remove(item);
        if (_editingMilitary?.Id == item.Id)
        {
            var next = _batch.FirstOrDefault()?.Military ?? MilitaryGrid.SelectedItem as MilitaryRecord;
            SetEditingMilitary(next, openFields: false);
        }
        UpdateBatchMetric();
        StatusText.Text = $"{item.ShortRank} {item.WarName} removido do lote.";
    }

    private void ClearBatch_Click(object sender, RoutedEventArgs e)
    {
        _batch.Clear();
        UpdateBatchMetric();
        UpdateGenerationSummary();
        StatusText.Text = "Lote limpo.";
    }

    private void CopyBatchName_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not PhpmBatchItem item) return;
        var military = item.Military;
        Clipboard.SetText($"{military.ShortRank} {NameHighlightHelper.PlainDisplay(military.Name, military.WarName)}\nPrec-CP {military.PrecCp} CPF {military.FormattedCpf}".Trim());
    }

    private void OpenBatchFields_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not PhpmBatchItem item) { StatusText.Text = "Selecione uma pessoa do lote."; return; }
        SetEditingMilitary(item.Military, openFields: true);
    }

    private void UpdateBatchMetric()
    {
        BatchMetric.Text = _batch.Count.ToString("N0");
        UpdateGenerationSummary();
    }

    private void Autofill_Click(object sender, RoutedEventArgs e)
    {
        if (_editingMilitary is null || SelectedTemplate is not { } template) return;
        var stateKey = PersonStateKey(template, _editingMilitary);
        if (_manualValues.TryGetValue(stateKey, out var manual))
        {
            var automaticKeys = _service.BuildAutomaticFields(_editingMilitary, _profile).Keys.ToList();
            foreach (var key in automaticKeys) manual.Remove(key);
            if (manual.Count == 0) _manualValues.Remove(stateKey);
        }
        RebuildFields();
        StatusText.Text = "Dados cadastrais reaplicados. Os campos complementares foram preservados quando não existem no cadastro.";
    }

    private void ClearManualFields_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template || _editingMilitary is null) return;
        _manualValues.Remove(PersonStateKey(template, _editingMilitary));
        RebuildFields();
        StatusText.Text = "Campos manuais desta pessoa foram limpos; os dados do cadastro permanecem.";
    }

    private void OpenGeneration_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentManualValues();
        UpdateGenerationSummary();
        WorkflowTabs.SelectedIndex = 2;
    }

    private async void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Templates PHPM|*.docx;*.odt;*.doc|Word|*.docx;*.doc|OpenDocument|*.odt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var item = await _service.ImportCustomTemplateAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), "Modelo personalizado do PHPM.");
            _catalog.Templates.Add(item);
            await SaveCatalogAsync();
            ApplyTemplateFilter();
            TemplateList.SelectedItem = item;
            StatusText.Text = $"Modelo adicionado com {item.Placeholders.Count:N0} campo(s) reconhecido(s).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void AttachTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template) { StatusText.Text = "Selecione um documento."; return; }
        var dialog = new OpenFileDialog { Filter = "Templates PHPM|*.docx;*.odt;*.doc|Word|*.docx;*.doc|OpenDocument|*.odt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _service.AttachTemplateFileAsync(template, dialog.FileName);
            await SaveCatalogAsync();
            await LoadTemplateAsync(template);
            TemplateList.Items.Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RescanTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template || !File.Exists(template.TemplatePath)) return;
        template.Placeholders = await _service.ExtractPlaceholdersAsync(template.TemplatePath);
        await SaveCatalogAsync();
        await LoadTemplateAsync(template);
        StatusText.Text = $"Modelo reexaminado: {template.Placeholders.Count:N0} campo(s) reconhecido(s).";
    }

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } template && File.Exists(template.TemplatePath)) ShellService.OpenPath(template.TemplatePath);
    }

    private void OpenTemplatesFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.PhpmTemplatesDirectory);
    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.PhpmOutputDirectory);

    private async void RemoveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template) return;
        if (template.IsBuiltIn)
        {
            SigfurDialog.Show(this, "Os modelos oficiais não são removidos. Você pode substituir o arquivo vinculado.", "PHPM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SigfurDialog.Show(this, $"Remover o modelo “{template.Title}” do catálogo?", "PHPM", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _catalog.Templates.Remove(template);
        await SaveCatalogAsync();
        ApplyTemplateFilter();
    }

    private async void GenerateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template || _editingMilitary is not { } military)
        {
            SigfurDialog.Show(this, "Selecione um documento e um militar.", "PHPM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SaveCurrentManualValues();
        var missing = GetMissingFields(template, military);
        if (!EnsureFieldsComplete(military, missing)) return;

        if (SaveCadastroBox.IsChecked == true)
        {
            var confirmation = SigfurDialog.Show(this,
                "Atualizar no cadastro os dados cadastrais editados antes da geração?\n\nDados específicos do documento, dependentes e beneficiários não alteram o cadastro geral.",
                "Atualizar cadastro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (confirmation == MessageBoxResult.Cancel) return;
            if (confirmation == MessageBoxResult.Yes) await SaveRecognizedFieldsToMilitaryAsync(military);
        }
        await GenerateOneAsync(template, military, OutputNameBox.Text.Trim());
    }

    private async void GenerateBatch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template || _batch.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione o documento e adicione militares ao lote.", "PHPM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SaveCurrentManualValues();
        UpdateAllBatchStatuses();
        var invalid = _batch.Where(item => item.PendingFields > 0).ToList();
        if (invalid.Count > 0)
        {
            var first = invalid[0];
            BatchGrid.SelectedItem = first;
            BatchGrid.ScrollIntoView(first);
            SetEditingMilitary(first.Military, openFields: true);
            var details = string.Join(Environment.NewLine, invalid.Take(10).Select(item => $"• {item.ShortRank} {item.WarName}: {item.PendingFields} pendência(s)"));
            SigfurDialog.Show(this, $"O lote ainda possui {invalid.Count:N0} pessoa(s) com campos pendentes ou inválidos.\n\n{details}\n\nA primeira pessoa pendente foi aberta para preenchimento.", "Conferir lote PHPM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsEnabled = false;
        var success = 0;
        var outputPaths = new List<string>();
        try
        {
            for (var index = 0; index < _batch.Count; index++)
            {
                var military = _batch[index].Military;
                StatusText.Text = $"Gerando {index + 1:N0}/{_batch.Count:N0}: {military.ShortRank} {military.WarName}…";
                var result = await GenerateForMilitaryAsync(template, military, $"{template.Title}_{military.ShortRank}_{military.WarName}_{DateTime.Today:yyyyMMdd}");
                if (!result.Success) continue;
                success++;
                outputPaths.Add(result.OutputPath);
            }
            await RefreshHistoryAsync();
            if (AddToPrintQueueBox.IsChecked == true && outputPaths.Count > 0) OpenPrintQueue(outputPaths);
            SigfurDialog.Show(this, $"Lote concluído: {success:N0} de {_batch.Count:N0} documento(s) gerado(s).", "PHPM", MessageBoxButton.OK,
                success == _batch.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private bool EnsureFieldsComplete(MilitaryRecord military, IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return true;
        WorkflowTabs.SelectedIndex = 1;
        var first = _fields.FirstOrDefault(field => field.Key.Equals(missing[0], StringComparison.OrdinalIgnoreCase));
        if (first is not null) { FieldsList.SelectedItem = first; FieldsList.ScrollIntoView(first); }
        var labels = string.Join(Environment.NewLine, missing.Take(12).Select(key => "• " + FriendlyLabel(key)));
        SigfurDialog.Show(this, $"Revise os campos pendentes ou inválidos de {military.ShortRank} {military.WarName}:\n\n{labels}", "Conferência dos campos", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task GenerateOneAsync(PhpmTemplateDefinition template, MilitaryRecord military, string outputName)
    {
        IsEnabled = false;
        try
        {
            var result = await GenerateForMilitaryAsync(template, military, outputName);
            await RefreshHistoryAsync();
            StatusText.Text = result.Message;
            if (!result.Success) { SigfurDialog.Show(this, result.Message, "PHPM", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (AddToPrintQueueBox.IsChecked == true) OpenPrintQueue([result.OutputPath]);
            if (OpenAfterGenerateBox.IsChecked == true && File.Exists(result.OutputPath)) ShellService.OpenPath(result.OutputPath);
        }
        finally { IsEnabled = true; }
    }

    private async Task<PhpmGenerationRecord> GenerateForMilitaryAsync(PhpmTemplateDefinition template, MilitaryRecord military, string outputName)
        => await _service.GenerateAsync(new PhpmGenerationRequest
        {
            Template = template,
            Military = military,
            Fields = BuildValuesFor(template, military),
            OutputName = outputName,
            OutputFormat = SelectedOutputFormat(),
            KeepIntermediateDocument = KeepIntermediateBox.IsChecked == true
        });

    private async Task SaveRecognizedFieldsToMilitaryAsync(MilitaryRecord military)
    {
        if (SelectedTemplate is not { } template) return;
        var values = BuildValuesFor(template, military);
        static string Find(IReadOnlyDictionary<string, string> source, params string[] keys)
            => keys.Select(key => source.GetValueOrDefault(key) ?? string.Empty).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        static void Apply(string value, Action<string> setter) { if (!string.IsNullOrWhiteSpace(value)) setter(value.Trim()); }
        Apply(Find(values, "POSTO_GRAD"), value => military.Rank = value);
        Apply(Find(values, "NOME", "NOME_COMPLETO", "MILITAR_NOME", "DECL_NOME", "BENEF_NOME"), value => military.Name = value);
        Apply(Find(values, "NOME_GUERRA"), value => military.WarName = value);
        Apply(Find(values, "CPF", "CPF_NUMEROS", "MILITAR_CPF", "DECL_CPF"), value => military.Cpf = new string(value.Where(char.IsDigit).ToArray()));
        Apply(Find(values, "PREC_CP", "PREC-CP", "MILITAR_PREC_CP"), value => military.PrecCp = value);
        Apply(Find(values, "IDENTIDADE", "IDT", "MILITAR_IDT", "DECL_IDT"), value => military.MilitaryId = value);
        Apply(Find(values, "DATA_NASCIMENTO"), value => military.BirthDate = value);
        Apply(Find(values, "DATA_PRACA", "DECL_DATA_PRACA"), value => military.EnlistmentDate = value);
        Apply(Find(values, "ENDERECO"), value => military.Address = value);
        Apply(Find(values, "CEP"), value => military.ZipCode = value);
        Apply(Find(values, "TELEFONE"), value => military.Phone = value);
        Apply(Find(values, "EMAIL"), value => military.Email = value);
        Apply(Find(values, "BANCO"), value => military.Bank = value);
        Apply(Find(values, "AGENCIA"), value => military.Agency = value);
        Apply(Find(values, "CONTA"), value => military.Account = value);
        await _repository.SaveAsync(military);
        MilitaryGrid.Items.Refresh();
    }

    private string SelectedOutputFormat() => (OutputFormatBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "Original";
    private void OpenPrintQueue_Click(object sender, RoutedEventArgs e) => OpenPrintQueue([]);
    private void AddHistoryToQueue_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is PhpmGenerationRecord record && record.Success && File.Exists(record.OutputPath)) OpenPrintQueue([record.OutputPath]); }
    private void OpenPrintQueue(IEnumerable<string> files) => new PrintQueueWindow(files) { Owner = this }.Show();
    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenHistory();
    private void OpenHistory_Click(object sender, RoutedEventArgs e) => OpenHistory();
    private void OpenHistory() { if (HistoryGrid.SelectedItem is PhpmGenerationRecord record && record.Success && File.Exists(record.OutputPath)) ShellService.OpenPath(record.OutputPath); }
    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is PhpmGenerationRecord record) ShellService.OpenPath(Path.GetDirectoryName(record.OutputPath) ?? App.Paths.PhpmOutputDirectory); }
    private void CopyHistoryPath_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is PhpmGenerationRecord record) Clipboard.SetText(record.OutputPath ?? string.Empty); }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { WorkflowTabs.SelectedIndex = 0; MilitarySearchBox.Focus(); MilitarySearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { MilitarySearchBox.Clear(); e.Handled = true; }
    }

    private static string PersonStateKey(PhpmTemplateDefinition template, MilitaryRecord military) => $"{template.Id}:{military.Id}";

    private static string ValidateFieldValue(string key, string? value, bool required)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) return required ? "Preenchimento obrigatório" : string.Empty;

        var normalizedKey = key.ToUpperInvariant();
        var digits = MilitaryFormatting.Digits(text);
        if (normalizedKey.Contains("CPF", StringComparison.Ordinal) && digits.Length != 11)
            return "CPF deve ter 11 números";
        if (normalizedKey.Contains("CEP", StringComparison.Ordinal) && digits.Length != 8)
            return "CEP deve ter 8 números";
        if (normalizedKey.Contains("EMAIL", StringComparison.Ordinal)
            && (!text.Contains('@', StringComparison.Ordinal) || text.StartsWith('@') || text.EndsWith('@')))
            return "Informe um e-mail válido";
        if (normalizedKey.Contains("DATA_", StringComparison.Ordinal)
            && normalizedKey is not "LOCAL_DATA" and not "DATA_EXTENSO"
            && MilitaryFormatting.ParseDate(text) is null)
            return "Use uma data válida: dd/MM/aaaa";

        return string.Empty;
    }

    private static int FieldOrder(string key)
    {
        var numbered = Regex.Match(key, @"^(DEP|FILHO|OUTRO)(\d+)_(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (numbered.Success && int.TryParse(numbered.Groups[2].Value, out var number))
        {
            var suffixOrder = numbered.Groups[3].Value.ToUpperInvariant() switch
            {
                "NOME" => 0, "PARENTESCO" => 1, "SEXO" => 2, "DATA_NASC" => 3,
                "BI" => 4, "OM_PUB" => 5, "OBS" => 6, _ => 8
            };
            return 100 + number * 10 + suffixOrder;
        }
        var order = new[] { "MILITAR_PG_ABREV", "POSTO_GRAD", "MILITAR_NOME", "NOME", "NOME_GUERRA", "MILITAR_IDT", "IDENTIDADE", "MILITAR_CPF", "CPF", "MILITAR_PREC_CP", "PREC_CP", "DECL_NOME", "DECL_IDT", "DECL_CPF", "DECL_DATA_PRACA", "BENEF_NOME", "DEP_NOME", "DEP_DATA_NASC", "BI_PREESCOLAR", "FAIXA_REMUNERACAO", "COTA_PARTE", "VALOR_RECEBER", "ENDERECO", "LOCAL_DATA", "CMT_NOME_PG", "CMT_FUNCAO" };
        var index = Array.FindIndex(order, value => value.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 999 : index;
    }

    private static int FieldGroupOrder(string key) => FieldGroup(key) switch
    {
        "Militar ou declarante" => 0, "Dados do documento" => 1, "Cônjuge ou companheiro" => 2,
        "Filhos" => 3, "Dependentes" => 4, "Outros beneficiários" => 5,
        "Organização e assinatura" => 6, _ => 7
    };

    private static string FieldGroup(string key)
    {
        var value = key.ToUpperInvariant();
        if (value.StartsWith("CONJUGE") || value.StartsWith("COMP_")) return "Cônjuge ou companheiro";
        if (value.StartsWith("FILHO")) return "Filhos";
        if (value.StartsWith("DEP") || value.Contains("DEPENDENTE")) return "Dependentes";
        if (value.StartsWith("OUTRO") || value.StartsWith("PESSOA_")) return "Outros beneficiários";
        if (value.StartsWith("OM_") || value.StartsWith("CMT_") || value is "LOCAL_DATA" or "DATA" or "DATA_EXTENSO") return "Organização e assinatura";
        if (IsBulletinReferenceField(value) || IsAditamentReferenceField(value) || value.Contains("FAIXA") || value.Contains("COTA") || value.Contains("VALOR") || value.Contains("SOLDO") || value.Contains("AUX_") || value is "PNR" or "PENSAO_JUDICIAL") return "Dados do documento";
        if (value.StartsWith("MILITAR_") || value.StartsWith("DECL_") || value.StartsWith("BENEF_") || value is "NOME" or "NOME_GUERRA" or "CPF" or "PREC_CP" or "IDENTIDADE" or "POSTO_GRAD") return "Militar ou declarante";
        return "Dados complementares";
    }

    private static string FriendlyLabel(string key)
    {
        var value = key.ToUpperInvariant();
        var bulletinReference = IsBulletinReferenceField(value);
        var aditamentReference = IsAditamentReferenceField(value);
        if (bulletinReference && aditamentReference) return "Publicação oficial (BI ou ADT)";
        if (bulletinReference) return "Boletim Interno (BI)";
        if (aditamentReference) return "Aditamento Furriel (ADT)";
        var numbered = Regex.Match(value, @"^(DEP|FILHO|OUTRO)(\d+)_(.+)$");
        if (numbered.Success)
        {
            var kind = numbered.Groups[1].Value switch { "DEP" => "Dependente", "FILHO" => "Filho", _ => "Outro beneficiário" };
            return $"{kind} {numbered.Groups[2].Value} • {FriendlyWords(numbered.Groups[3].Value)}";
        }
        return FriendlyWords(value
            .Replace("MILITAR_", string.Empty, StringComparison.Ordinal)
            .Replace("DECL_", "Declarante_", StringComparison.Ordinal)
            .Replace("BENEF_", "Beneficiário_", StringComparison.Ordinal)
            .Replace("DEP_", "Dependente_", StringComparison.Ordinal)
            .Replace("COMP_", "Companheiro_", StringComparison.Ordinal));
    }

    private static string FriendlyWords(string value)
    {
        var exact = value.ToUpperInvariant();
        if (exact == "PAIS") return "Nome da Mãe/Pai";
        if (exact == "DATA_NASC") return "Data de Nascimento";
        if (exact == "DATA_OBITO") return "Data de Óbito";
        if (exact == "OM_PUB") return "OM de Publicação";
        if (exact == "GRAU_PARENTESCO") return "Grau de Parentesco";
        var words = value.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        words = Regex.Replace(words, @"\bIdt\b", "identidade", RegexOptions.IgnoreCase);
        words = Regex.Replace(words, @"\bPg\b", "P/G", RegexOptions.IgnoreCase);
        words = Regex.Replace(words, @"\bBi\b", "BI", RegexOptions.IgnoreCase);
        words = Regex.Replace(words, @"\bCpf\b", "CPF", RegexOptions.IgnoreCase);
        words = Regex.Replace(words, @"\bOm\b", "OM", RegexOptions.IgnoreCase);
        return CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(words)
            .Replace("Cpf", "CPF", StringComparison.Ordinal).Replace(" Bi", " BI", StringComparison.Ordinal)
            .Replace("Om ", "OM ", StringComparison.Ordinal).Replace("P/g", "P/G", StringComparison.Ordinal);
    }

    private static string FieldHint(string key)
    {
        var value = key.ToUpperInvariant();
        if (value.Contains("DATA_NASC") || value.Contains("DATA_PRACA") || value.Contains("DATA_OBITO")) return "dd/MM/aaaa";
        if (value.Contains("CPF")) return "000.000.000-00";
        if (IsBulletinReferenceField(value)) return "Escolha um BI salvo no Boletim Inteligente";
        if (IsAditamentReferenceField(value)) return "Escolha um ADT salvo no Aditamento Furriel";
        if (value.Contains("OM_PUB")) return "Organização Militar que publicou o dependente";
        if (value.Contains("COTA") || value.Contains("VALOR") || value.Contains("MENSALIDADE")) return "R$ 0,00";
        if (value.Contains("CEP")) return "00000-000";
        if (value.Contains("SEXO")) return "Selecione";
        if (value.Contains("PARENTESCO")) return "Selecione ou digite";
        return string.Empty;
    }

    private List<string> FieldSuggestions(string key)
    {
        var value = key.ToUpperInvariant();
        var bulletinReference = IsBulletinReferenceField(value);
        var aditamentReference = IsAditamentReferenceField(value);
        if (bulletinReference || aditamentReference)
        {
            IEnumerable<string> references = [];
            if (bulletinReference) references = references.Concat(_publicationReferences.Bulletins);
            if (aditamentReference) references = references.Concat(_publicationReferences.Aditaments);
            return references.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        if (value.Contains("SEXO")) return ["Masculino", "Feminino"];
        if (value.Contains("PARENTESCO")) return ["Filho(a)", "Cônjuge", "Companheiro(a)", "Enteado(a)", "Pai", "Mãe", "Irmão(ã)", "Outro"];
        if (value.Contains("ESTADO_CIVIL")) return ["Solteiro(a)", "Casado(a)", "União estável", "Separado(a)", "Divorciado(a)", "Viúvo(a)"];
        if (value.Contains("DETENTOR")) return ["Não", "Sim"];
        return [];
    }

    private static bool IsBulletinReferenceField(string key)
        => Regex.IsMatch(key.ToUpperInvariant(), @"(^|_)(BI|BOLETIM)(_|$)", RegexOptions.CultureInvariant);

    private static bool IsAditamentReferenceField(string key)
        => Regex.IsMatch(key.ToUpperInvariant(), @"(^|_)(ADT|ADIT|ADITAMENTO)(_|$)", RegexOptions.CultureInvariant);

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        SigfurDialog.Show(this, ex.Message, "PHPM", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
