using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ObservableCollection<MilitaryRecord> _batch = [];
    private readonly ObservableCollection<PhpmFieldItem> _fields = [];
    private readonly ObservableCollection<PhpmGenerationRecord> _history = [];
    private PhpmTemplateCatalog _catalog = new();
    private List<MilitaryRecord> _military = [];
    private UiProfile _profile = new();
    private bool _loading;

    public PhpmWindow(PhpmTemplateService service, MilitaryRepository repository)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        _repository = repository;
        TemplateList.ItemsSource = _visibleTemplates;
        MilitaryGrid.ItemsSource = _visibleMilitary;
        BatchGrid.ItemsSource = _batch;
        FieldsGrid.ItemsSource = _fields;
        HistoryGrid.ItemsSource = _history;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private PhpmTemplateDefinition? SelectedTemplate => TemplateList.SelectedItem as PhpmTemplateDefinition;
    private MilitaryRecord? SelectedMilitary => MilitaryGrid.SelectedItem as MilitaryRecord;

    private async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            _catalog = await _service.LoadCatalogAsync();
            _military = await _repository.GetAllAsync();
            _profile = await App.Settings.LoadProfileAsync();
            LibreOfficeStatusText.Text = _service.IsLibreOfficeAvailable ? "LibreOffice disponível" : "LibreOffice não localizado";
            LibreOfficeStatusText.Foreground = FindResource(_service.IsLibreOfficeAvailable ? "SuccessBrush" : "WarningBrush") as System.Windows.Media.Brush;
            ApplyTemplateFilter();
            ApplyMilitaryFilter();
            await RefreshHistoryAsync();
            TemplateMetric.Text = _catalog.Templates.Count.ToString("N0");
            if (_visibleTemplates.Count > 0) TemplateList.SelectedIndex = 0;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyTemplateFilter()
    {
        var query = Normalize(TemplateSearchBox.Text);
        _visibleTemplates.Clear();
        foreach (var item in _catalog.Templates)
        {
            var haystack = Normalize(item.Title + " " + item.Description + " " + item.TypeText);
            if (string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)))
                _visibleTemplates.Add(item);
        }
    }

    private void ApplyMilitaryFilter()
    {
        var query = Normalize(MilitarySearchBox.Text);
        _visibleMilitary.Clear();
        foreach (var item in _military.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name))
        {
            var haystack = Normalize($"{item.Rank} {item.Name} {item.WarName} {item.Cpf} {item.PrecCp} {item.MilitaryId}");
            if (string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)))
                _visibleMilitary.Add(item);
        }
    }

    private async Task LoadTemplateAsync(PhpmTemplateDefinition template)
    {
        TemplateTitleText.Text = template.Title;
        TemplateDescriptionText.Text = template.Description;
        if (File.Exists(template.TemplatePath) && template.Placeholders.Count == 0)
        {
            template.Placeholders = await _service.ExtractPlaceholdersAsync(template.TemplatePath);
            await _service.SaveCatalogAsync(_catalog);
        }
        TemplateStatusText.Text = $"{template.TypeText} • {template.StatusText} • {template.Placeholders.Count:N0} marcador(es) encontrado(s)";
        BuildFields(template);
        SuggestOutputName();
    }

    private void BuildFields(PhpmTemplateDefinition template)
    {
        var existing = _fields.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        _fields.Clear();
        var saved = _catalog.SavedValues.GetValueOrDefault(template.Id) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> keys = template.IsBuiltIn
            ? template.Placeholders.Concat(_service.GetSuggestedFields(template.Id))
            : template.Placeholders.Count > 0 ? template.Placeholders : _service.GetSuggestedFields(template.Id);

        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(FieldOrder).ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase))
        {
            var value = existing.GetValueOrDefault(key) ?? saved.GetValueOrDefault(key) ?? string.Empty;
            _fields.Add(new PhpmFieldItem
            {
                Key = key,
                Label = FriendlyLabel(key),
                Value = value,
                Source = string.IsNullOrWhiteSpace(value) ? "Manual" : "Salvo"
            });
        }
        AutofillFields(overwrite: false);
    }

    private void AutofillFields(bool overwrite)
    {
        if (SelectedMilitary is not MilitaryRecord military) return;
        var automatic = _service.BuildAutomaticFields(military, _profile);
        foreach (var field in _fields)
        {
            if (!automatic.TryGetValue(field.Key, out var value)) continue;
            if (overwrite || string.IsNullOrWhiteSpace(field.Value))
            {
                field.Value = value;
                field.Source = "Cadastro";
            }
        }
        StatusText.Text = $"Cadastro de {military.ShortRank} {military.WarName} aplicado. Campos alterados manualmente ficam preservados na geração.";
    }

    private void SuggestOutputName()
    {
        if (SelectedTemplate is not { } template || SelectedMilitary is not { } military) return;
        OutputNameBox.Text = $"{template.Title}_{military.ShortRank}_{military.WarName}_{DateTime.Today:yyyyMMdd}";
    }

    private async Task RefreshHistoryAsync()
    {
        var history = await _service.LoadHistoryAsync();
        _history.Clear();
        foreach (var item in history.Take(150)) _history.Add(item);
        GeneratedMetric.Text = history.Count(x => x.Success).ToString("N0");
    }

    private async Task SaveCatalogAsync()
    {
        await _service.SaveCatalogAsync(_catalog);
        TemplateMetric.Text = _catalog.Templates.Count.ToString("N0");
    }

    private void TemplateSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyTemplateFilter();
    private void MilitarySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyMilitaryFilter();

    private async void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTemplate is not { } template) return;
        await LoadTemplateAsync(template);
    }

    private void MilitaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedMilitary is null) return;
        AutofillFields(overwrite: true);
        SuggestOutputName();
    }

    private void MilitaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedToBatch();
    private void AddSelectedToBatch_Click(object sender, RoutedEventArgs e) => AddSelectedToBatch();

    private void AddSelectedToBatch()
    {
        if (SelectedMilitary is not { } military) return;
        if (_batch.All(x => x.Id != military.Id)) _batch.Add(military);
        UpdateBatchMetric();
    }

    private void AddFilteredToBatch_Click(object sender, RoutedEventArgs e)
    {
        foreach (var military in _visibleMilitary)
            if (_batch.All(x => x.Id != military.Id)) _batch.Add(military);
        UpdateBatchMetric();
    }

    private void RemoveBatch_Click(object sender, RoutedEventArgs e)
    {
        foreach (var military in BatchGrid.SelectedItems.Cast<MilitaryRecord>().ToList()) _batch.Remove(military);
        UpdateBatchMetric();
    }

    private void ClearBatch_Click(object sender, RoutedEventArgs e)
    {
        _batch.Clear();
        UpdateBatchMetric();
    }

    private void CopyBatchName_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not MilitaryRecord military) return;
        Clipboard.SetText($"{military.ShortRank} {NameHighlightHelper.PlainDisplay(military.Name, military.WarName)}\nPrec-CP {military.PrecCp} CPF {military.FormattedCpf}".Trim());
    }

    private void UpdateBatchMetric() => BatchMetric.Text = _batch.Count.ToString("N0");
    private void Autofill_Click(object sender, RoutedEventArgs e) => AutofillFields(overwrite: true);

    private void FieldsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is PhpmFieldItem field && e.Column.Header?.ToString() == "Valor") field.Source = "Manual";
    }

    private async void SaveTemplateValues_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template) return;
        _catalog.SavedValues[template.Id] = _fields.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        await SaveCatalogAsync();
        StatusText.Text = "Valores padrão deste template foram salvos.";
    }

    private void CopyFieldValue_Click(object sender, RoutedEventArgs e)
    {
        if (FieldsGrid.SelectedItem is PhpmFieldItem field) Clipboard.SetText(field.Value ?? string.Empty);
    }

    private void ClearFieldValue_Click(object sender, RoutedEventArgs e)
    {
        if (FieldsGrid.SelectedItem is not PhpmFieldItem field) return;
        field.Value = string.Empty;
        field.Source = "Manual";
    }

    private async void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Templates PHPM|*.docx;*.odt;*.doc|Word|*.docx;*.doc|OpenDocument|*.odt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var item = await _service.ImportCustomTemplateAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), "Template personalizado importado pelo operador.");
            _catalog.Templates.Add(item);
            await SaveCatalogAsync();
            ApplyTemplateFilter();
            TemplateList.SelectedItem = item;
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "PHPM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AttachTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template) return;
        var dialog = new OpenFileDialog { Filter = "Templates PHPM|*.docx;*.odt;*.doc|Word|*.docx;*.doc|OpenDocument|*.odt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _service.AttachTemplateFileAsync(template, dialog.FileName);
            await SaveCatalogAsync();
            await LoadTemplateAsync(template);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "PHPM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RescanTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template || !File.Exists(template.TemplatePath)) return;
        template.Placeholders = await _service.ExtractPlaceholdersAsync(template.TemplatePath);
        await SaveCatalogAsync();
        BuildFields(template);
        TemplateStatusText.Text = $"{template.TypeText} • {template.Placeholders.Count:N0} marcador(es) reconhecido(s)";
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
            SigfurDialog.Show(this, "Os modelos principais não são removidos. Você pode substituir o arquivo vinculado.", "PHPM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SigfurDialog.Show(this, $"Remover o template “{template.Title}” do catálogo?", "PHPM", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _catalog.Templates.Remove(template);
        await SaveCatalogAsync();
        ApplyTemplateFilter();
    }

    private async void GenerateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template || SelectedMilitary is not { } military)
        {
            SigfurDialog.Show(this, "Selecione um template e um militar.", "PHPM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SaveCadastroBox.IsChecked == true)
        {
            var confirmation = SigfurDialog.Show(this,
                "Salvar no cadastro do militar os campos cadastrais editados nesta tela antes da geração?\n\nSomente nome, P/G, CPF, PREC-CP, identidade, datas, endereço, contato e dados bancários reconhecidos serão atualizados.",
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
            SigfurDialog.Show(this, "Selecione o template e adicione militares ao lote.", "PHPM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsEnabled = false;
        var success = 0;
        var outputPaths = new List<string>();
        try
        {
            for (var index = 0; index < _batch.Count; index++)
            {
                var military = _batch[index];
                StatusText.Text = $"Gerando {index + 1:N0}/{_batch.Count:N0}: {military.ShortRank} {military.WarName}…";
                var result = await GenerateForMilitaryAsync(template, military, $"{template.Title}_{military.ShortRank}_{military.WarName}_{DateTime.Today:yyyyMMdd}");
                if (result.Success)
                {
                    success++;
                    outputPaths.Add(result.OutputPath);
                }
            }
            await RefreshHistoryAsync();
            if (AddToPrintQueueBox.IsChecked == true && outputPaths.Count > 0) OpenPrintQueue(outputPaths);
            SigfurDialog.Show(this, $"Lote concluído: {success:N0} de {_batch.Count:N0} documento(s) gerado(s).", "PHPM", MessageBoxButton.OK,
                success == _batch.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task GenerateOneAsync(PhpmTemplateDefinition template, MilitaryRecord military, string outputName)
    {
        IsEnabled = false;
        try
        {
            var result = await GenerateForMilitaryAsync(template, military, outputName);
            await RefreshHistoryAsync();
            StatusText.Text = result.Message;
            if (!result.Success)
            {
                SigfurDialog.Show(this, result.Message, "PHPM", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (AddToPrintQueueBox.IsChecked == true) OpenPrintQueue([result.OutputPath]);
            if (OpenAfterGenerateBox.IsChecked == true && File.Exists(result.OutputPath)) ShellService.OpenPath(result.OutputPath);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task<PhpmGenerationRecord> GenerateForMilitaryAsync(PhpmTemplateDefinition template, MilitaryRecord military, string outputName)
    {
        var values = _fields.ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var manualKeys = _fields.Where(x => x.Source.Equals("Manual", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var automatic = _service.BuildAutomaticFields(military, _profile);
        foreach (var pair in automatic)
            if (!manualKeys.Contains(pair.Key)) values[pair.Key] = pair.Value;

        return await _service.GenerateAsync(new PhpmGenerationRequest
        {
            Template = template,
            Military = military,
            Fields = values,
            OutputName = outputName,
            OutputFormat = SelectedOutputFormat(),
            KeepIntermediateDocument = KeepIntermediateBox.IsChecked == true
        });
    }

    private async Task SaveRecognizedFieldsToMilitaryAsync(MilitaryRecord military)
    {
        var values = _fields.ToDictionary(x => x.Key, x => x.Value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        static string Find(IReadOnlyDictionary<string, string> source, params string[] keys)
            => keys.Select(key => source.GetValueOrDefault(key) ?? string.Empty).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        static void Apply(string value, Action<string> setter)
        {
            if (!string.IsNullOrWhiteSpace(value)) setter(value.Trim());
        }

        Apply(Find(values, "POSTO_GRAD"), value => military.Rank = value);
        Apply(Find(values, "NOME", "NOME_COMPLETO"), value => military.Name = value);
        Apply(Find(values, "NOME_GUERRA"), value => military.WarName = value);
        Apply(Find(values, "CPF", "CPF_NUMEROS"), value => military.Cpf = new string(value.Where(char.IsDigit).ToArray()));
        Apply(Find(values, "PREC_CP", "PREC-CP"), value => military.PrecCp = value);
        Apply(Find(values, "IDENTIDADE", "IDT"), value => military.MilitaryId = value);
        Apply(Find(values, "DATA_NASCIMENTO"), value => military.BirthDate = value);
        Apply(Find(values, "DATA_PRACA"), value => military.EnlistmentDate = value);
        Apply(Find(values, "ENDERECO"), value => military.Address = value);
        Apply(Find(values, "CEP"), value => military.ZipCode = value);
        Apply(Find(values, "TELEFONE"), value => military.Phone = value);
        Apply(Find(values, "EMAIL"), value => military.Email = value);
        Apply(Find(values, "BANCO"), value => military.Bank = value);
        Apply(Find(values, "AGENCIA"), value => military.Agency = value);
        Apply(Find(values, "CONTA"), value => military.Account = value);

        await _repository.SaveAsync(military);
        MilitaryGrid.Items.Refresh();
        StatusText.Text = "Campos cadastrais reconhecidos foram atualizados no Listar Militares.";
    }

    private string SelectedOutputFormat()
        => (OutputFormatBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "Original";

    private void OpenPrintQueue_Click(object sender, RoutedEventArgs e) => OpenPrintQueue([]);
    private void AddHistoryToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is PhpmGenerationRecord record && record.Success && File.Exists(record.OutputPath)) OpenPrintQueue([record.OutputPath]);
    }

    private void OpenPrintQueue(IEnumerable<string> files)
    {
        var window = new PrintQueueWindow(files) { Owner = this };
        window.Show();
    }

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenHistory();
    private void OpenHistory_Click(object sender, RoutedEventArgs e) => OpenHistory();
    private void OpenHistory()
    {
        if (HistoryGrid.SelectedItem is PhpmGenerationRecord record && record.Success && File.Exists(record.OutputPath)) ShellService.OpenPath(record.OutputPath);
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is PhpmGenerationRecord record) ShellService.OpenPath(Path.GetDirectoryName(record.OutputPath) ?? App.Paths.PhpmOutputDirectory);
    }

    private void CopyHistoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is PhpmGenerationRecord record) Clipboard.SetText(record.OutputPath ?? string.Empty);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            MilitarySearchBox.Focus();
            MilitarySearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) Close();
    }

    private static int FieldOrder(string key)
    {
        var order = new[]
        {
            "POSTO_GRAD", "POSTO_GRAD_ABREV", "NOME", "NOME_GUERRA", "CPF", "PREC_CP", "IDENTIDADE",
            "DATA_NASCIMENTO", "DATA_PRACA", "ENDERECO", "CEP", "TELEFONE", "EMAIL", "BANCO", "AGENCIA", "CONTA",
            "OM", "OPERADOR", "POSTO_OPERADOR", "FUNCAO_OPERADOR", "DATA", "DATA_EXTENSO"
        };
        var index = Array.FindIndex(order, x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 999 : index;
    }

    private static string FriendlyLabel(string key)
        => CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase((key ?? string.Empty).Replace('_', ' ').Replace('-', ' ').ToLowerInvariant());

    private static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
    }
}
