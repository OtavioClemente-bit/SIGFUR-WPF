using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Tools;

namespace SIGFUR.Wpf.Views.Military;

public partial class DocumentGenerationWindow : Window
{
    private const string GlobalProfilesKey = "GLOBAL";
    private const string GlobalCacheKey = "_GLOBAL";

    private readonly IReadOnlyList<MilitaryRecord> _military;
    private readonly MilitaryDocumentGenerationService _service;
    private readonly PostalOmAddressService _postalService;
    private readonly Dictionary<string, FrameworkElement> _fields = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _templateSelections = new(StringComparer.OrdinalIgnoreCase);
    private DocumentProfileStore _profileStore = new();
    private GeneratedDocumentType _selectedType;
    private string _selectedTemplatePath = string.Empty;
    private readonly System.Windows.Threading.DispatcherTimer _cacheSaveTimer;
    private readonly System.Windows.Threading.DispatcherTimer _postalSearchTimer;
    private bool _formReady;
    private bool _loadingPostalSuggestions;
    private bool _busy;
    private bool _storesLoaded;

    public DocumentGenerationWindow(
        IReadOnlyList<MilitaryRecord> military,
        GeneratedDocumentType initialType = GeneratedDocumentType.TransportAid)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _military = military.DistinctBy(x => x.Id).ToList();
        _service = new MilitaryDocumentGenerationService(App.Paths, App.Settings, App.Log, App.MilitaryRepository);
        _postalService = new PostalOmAddressService(App.Paths, App.Log);
        SelectionSummary.Text = _military.Count == 1
            ? $"1 militar: {_military[0].ShortRank} {_military[0].Name}"
            : $"{_military.Count} militares selecionados";
        OutputFolderBox.Text = Path.Combine(App.Paths.GeneratedDocumentsDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        TemplateFolderText.Text = $"Pasta do Listar Militares: {App.Paths.DocumentTemplatesDirectory}";
        _cacheSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _cacheSaveTimer.Tick += async (_, _) =>
        {
            _cacheSaveTimer.Stop();
            await SaveCurrentFormCacheAsync();
        };
        _postalSearchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _postalSearchTimer.Tick += async (_, _) =>
        {
            _postalSearchTimer.Stop();
            if (_selectedType != GeneratedDocumentType.PostalLabel) return;
            await LoadPostalItemsAsync(PostalSearchBox.Text, openDropDown: true);
        };
        Closing += Window_Closing;
        Loaded += async (_, _) => await SelectTypeAsync(initialType);
    }

    private async Task EnsureStoresLoadedAsync()
    {
        if (_storesLoaded) return;
        await EnsureBundledTemplatesAvailableAsync();
        _cache = await App.Json.LoadAsync<Dictionary<string, Dictionary<string, string>>>(App.Paths.DocumentFormCacheFile)
                 ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        _profileStore = await App.Json.LoadAsync<DocumentProfileStore>(App.Paths.DocumentProfilesFile) ?? new DocumentProfileStore();
        _profileStore.Profiles ??= new Dictionary<string, List<DocumentFormProfile>>(StringComparer.OrdinalIgnoreCase);
        _templateSelections = await App.Json.LoadAsync<Dictionary<string, string>>(App.Paths.DocumentTemplateSelectionFile)
                              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_cache.TryGetValue(GlobalCacheKey, out var global))
        {
            if (global.TryGetValue("OUTPUT_FOLDER", out var savedOutput)
                && !string.IsNullOrWhiteSpace(savedOutput))
                OutputFolderBox.Text = savedOutput;
            UseTransportAddressCheck.IsChecked = ReadSavedBoolean(global, "TRANSPORT_USE_ADDRESS", true);
            UseTransportBusesCheck.IsChecked = ReadSavedBoolean(global, "TRANSPORT_USE_BUSES", true);
        }
        else
        {
            UseTransportAddressCheck.IsChecked = true;
            UseTransportBusesCheck.IsChecked = true;
        }

        if (MigrateProfilesToGlobal())
            await App.Json.SaveAsync(App.Paths.DocumentProfilesFile, _profileStore);
        _storesLoaded = true;
    }

    private static async Task EnsureBundledTemplatesAvailableAsync()
    {
        try
        {
            Directory.CreateDirectory(App.Paths.DocumentTemplatesDirectory);
            var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "Documentos", "Listar");
            if (!Directory.Exists(bundled)) return;
            foreach (var source in Directory.EnumerateFiles(bundled, "*.*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(App.Paths.DocumentTemplatesDirectory, Path.GetFileName(source));
                if (File.Exists(target)) continue;
                await using var input = File.OpenRead(source);
                await using var output = File.Create(target);
                await input.CopyToAsync(output);
            }
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao preparar pasta de modelos de documentos.", ex);
        }
    }

    private static bool ReadSavedBoolean(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        if (bool.TryParse(raw, out var parsed)) return parsed;
        var normalized = raw.Trim().ToLower(CultureInfo.GetCultureInfo("pt-BR"));
        if (normalized is "1" or "sim" or "s" or "yes" or "true") return true;
        if (normalized is "0" or "não" or "nao" or "n" or "no" or "false") return false;
        return fallback;
    }

    private bool MigrateProfilesToGlobal()
    {
        if (!_profileStore.Profiles.TryGetValue(GlobalProfilesKey, out var global))
        {
            global = [];
            _profileStore.Profiles[GlobalProfilesKey] = global;
        }

        var changed = false;
        foreach (var pair in _profileStore.Profiles.Where(x => !string.Equals(x.Key, GlobalProfilesKey, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            foreach (var profile in pair.Value ?? [])
            {
                if (global.Any(x => string.Equals(x.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase))) continue;
                global.Add(new DocumentFormProfile
                {
                    Name = profile.Name,
                    Values = new Dictionary<string, string>(profile.Values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    UpdatedAt = profile.UpdatedAt
                });
                changed = true;
            }
        }
        return changed;
    }

    private async void DocumentType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && Enum.TryParse<GeneratedDocumentType>(raw, out var type))
            await SelectTypeAsync(type);
    }

    private async Task SelectTypeAsync(GeneratedDocumentType type)
    {
        try
        {
            await EnsureStoresLoadedAsync();
            if (_formReady) await SaveCurrentFormCacheAsync();
            _formReady = false;
            _cacheSaveTimer.Stop();
            _selectedType = type;
            DocumentTitleText.Text = MilitaryDocumentGenerationService.DisplayName(type);
            DocumentDescriptionText.Text = Description(type);
            DynamicFormPanel.Children.Clear();
            _fields.Clear();

            foreach (var field in MilitaryDocumentGenerationService.FieldsFor(type))
            {
                var label = new TextBlock
                {
                    Text = field.Label,
                    Margin = new Thickness(0, 0, 0, 4),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
                };
                var initialValue = CachedFieldDefault(type, field.Key, FieldDefault(field.Key, field.Default));
                var control = CreateFieldControl(field.Key, initialValue);
                _fields[field.Key] = control;
                DynamicFormPanel.Children.Add(label);
                DynamicFormPanel.Children.Add(control);
            }

            TransportToolsPanel.Visibility = type == GeneratedDocumentType.TransportAid ? Visibility.Visible : Visibility.Collapsed;
            PostalToolsPanel.Visibility = type == GeneratedDocumentType.PostalLabel ? Visibility.Visible : Visibility.Collapsed;
            if (type == GeneratedDocumentType.TransportAid)
                await LoadTransportDefaultsAsync(type);
            _formReady = true;
            UpdateButtonSelection();
            RefreshProfiles();
            await RefreshTemplateStatusAsync();
            if (type == GeneratedDocumentType.PostalLabel)
            {
                await LoadPostalItemsAsync(openDropDown: false);
                RefreshDerivedPostalAddress();
            }
            if (type != GeneratedDocumentType.TransportAid)
                StatusText.Text = "Campos restaurados. Alterações são salvas automaticamente.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha preparando a geração de documentos.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Gerar documentação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static readonly string[] MonthOptions =
    [
        "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO",
        "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"
    ];

    private FrameworkElement CreateFieldControl(string key, string initialValue)
    {
        var safeName = "Field_" + new string(key.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (key.Equals("MES", StringComparison.OrdinalIgnoreCase) || key.Equals("MÊS", StringComparison.OrdinalIgnoreCase))
        {
            var combo = new ComboBox
            {
                Name = safeName,
                ItemsSource = MonthOptions,
                IsEditable = false,
                Margin = new Thickness(0, 0, 0, 10),
                MinHeight = 34
            };
            var normalized = NormalizeMonth(initialValue);
            combo.SelectedItem = MonthOptions.FirstOrDefault(x => string.Equals(x, normalized, StringComparison.CurrentCultureIgnoreCase))
                                 ?? MonthOptions[Math.Clamp(DateTime.Today.Month - 1, 0, MonthOptions.Length - 1)];
            combo.SelectionChanged += (_, _) => DynamicFieldChanged();
            return combo;
        }

        if (IsDateField(key))
        {
            var picker = new DatePicker
            {
                Name = safeName,
                SelectedDateFormat = DatePickerFormat.Short,
                Language = System.Windows.Markup.XmlLanguage.GetLanguage("pt-BR"),
                Margin = new Thickness(0, 0, 0, 10),
                MinHeight = 34
            };
            if (TryParseFieldDate(initialValue, out var date)) picker.SelectedDate = date;
            else picker.Text = initialValue ?? string.Empty;
            picker.SelectedDateChanged += (_, _) => DynamicFieldChanged();
            picker.LostKeyboardFocus += (_, _) => DynamicFieldChanged();
            return picker;
        }

        var multiline = key is "OBSERVACOES" or "DECLARACAO_REQUER"
            || key.Contains("DESCRICAO", StringComparison.OrdinalIgnoreCase)
            || key.Contains("RESUMO", StringComparison.OrdinalIgnoreCase)
            || key.Contains("DOC_MATERIALIZOU", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BOLETIM", StringComparison.OrdinalIgnoreCase)
            || key.Equals("REFERENTE_A", StringComparison.OrdinalIgnoreCase);
        var box = new TextBox
        {
            Name = safeName,
            Text = initialValue ?? string.Empty,
            Margin = new Thickness(0, 0, 0, 10),
            MinHeight = multiline ? 66 : 34,
            AcceptsReturn = multiline,
            TextWrapping = TextWrapping.Wrap
        };
        box.TextChanged += (_, _) => DynamicFieldChanged();
        return box;
    }

    private void DynamicFieldChanged()
    {
        if (!_formReady) return;
        if (_selectedType == GeneratedDocumentType.PostalLabel) RefreshDerivedPostalAddress();
        _cacheSaveTimer.Stop();
        _cacheSaveTimer.Start();
    }

    private static bool IsDateField(string key)
        => key.Contains("DATA", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFieldDate(string? value, out DateTime date)
    {
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy" };
        return DateTime.TryParseExact((value ?? string.Empty).Trim(), formats, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date)
               || DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date);
    }

    private static string NormalizeMonth(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (int.TryParse(new string(raw.Where(char.IsDigit).ToArray()), out var number) && number is >= 1 and <= 12)
            return MonthOptions[number - 1];
        return raw.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
    }

    private static string ReadFieldValue(FrameworkElement control) => control switch
    {
        TextBox box => box.Text?.Trim() ?? string.Empty,
        ComboBox combo => (combo.SelectedItem?.ToString() ?? combo.Text ?? string.Empty).Trim(),
        DatePicker picker when picker.SelectedDate is DateTime date => date.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")),
        DatePicker picker => picker.Text?.Trim() ?? string.Empty,
        _ => string.Empty
    };

    private static void WriteFieldValue(FrameworkElement control, string value)
    {
        switch (control)
        {
            case TextBox box:
                box.Text = value;
                break;
            case ComboBox combo:
                var normalized = NormalizeMonth(value);
                combo.SelectedItem = MonthOptions.FirstOrDefault(x => string.Equals(x, normalized, StringComparison.CurrentCultureIgnoreCase));
                if (combo.SelectedItem is null) combo.Text = value;
                break;
            case DatePicker picker:
                if (TryParseFieldDate(value, out var date)) picker.SelectedDate = date;
                else { picker.SelectedDate = null; picker.Text = value; }
                break;
        }
    }

    private string CachedFieldDefault(GeneratedDocumentType type, string key, string fallback)
    {
        // Valores específicos do militar têm prioridade. Em seguida usa o último
        // preenchimento daquele tipo de documento, como na versão Python.
        if (_military.Count == 1
            && _cache.TryGetValue(MilitaryCacheKey(type, _military[0].Id), out var personal)
            && personal.TryGetValue(key, out var personalValue))
            return key == "TEMPO_SERVICO" && string.IsNullOrWhiteSpace(personalValue) ? fallback : personalValue;
        if (key == "TEMPO_SERVICO") return fallback;
        if (_cache.TryGetValue(type.ToString(), out var values)
            && values.TryGetValue(key, out var cached))
            return cached;
        return fallback;
    }

    private static string MilitaryCacheKey(GeneratedDocumentType type, int militaryId)
        => $"{type}:MILITAR:{militaryId}";

    private string FieldDefault(string key, string fallback)
    {
        if (_military.Count != 1) return fallback;
        var military = _military[0];
        return key switch
        {
            "TEMPO_SERVICO" => military.ServiceTimeText,
            _ => fallback
        };
    }

    private async Task LoadTransportDefaultsAsync(GeneratedDocumentType type)
    {
        if (type != GeneratedDocumentType.TransportAid) return;
        if (_military.Count != 1)
        {
            TransportOptionsStatusText.Text =
                "Geração em lote: endereço, ônibus, tarifas e valores serão consultados individualmente para cada militar no momento da geração.";
            StatusText.Text = "As opções acima serão aplicadas individualmente a cada militar selecionado.";
            return;
        }

        try
        {
            ReloadTransportButton.IsEnabled = false;
            var military = _military[0];
            var defaults = await _service.GetTransportDefaultsAsync(
                military,
                CollectFields(),
                UseTransportAddressCheck.IsChecked == true,
                UseTransportBusesCheck.IsChecked == true);

            foreach (var pair in defaults)
                SetField(pair.Key, pair.Value);

            var rankSource = string.IsNullOrWhiteSpace(military.Rank) ? military.ShortRank : military.Rank;
            var fullRank = MilitaryRankService.Canonicalize(rankSource).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
            var addressText = UseTransportAddressCheck.IsChecked == true ? "endereço do banco" : "endereço deixado para preenchimento manual";
            var busText = UseTransportBusesCheck.IsChecked == true
                ? "ônibus, tarifas e valores do banco"
                : "ônibus, tarifas e totais deixados para preenchimento manual";
            TransportOptionsStatusText.Text = $"Posto/graduação: {fullRank}. Usando {addressText}; {busText}.";
            StatusText.Text = "Dados do Auxílio-Transporte atualizados conforme as opções selecionadas.";
        }
        catch (Exception ex)
        {
            TransportOptionsStatusText.Text = "Não foi possível carregar todos os dados do banco. Os campos manuais foram mantidos.";
            await App.Log.WriteAsync("Falha ao preencher automaticamente a geração de Auxílio-Transporte.", ex);
        }
        finally
        {
            ReloadTransportButton.IsEnabled = true;
        }
    }

    private async void TransportOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_storesLoaded || !_formReady || _selectedType != GeneratedDocumentType.TransportAid) return;
        await LoadTransportDefaultsAsync(_selectedType);
        _cacheSaveTimer.Stop();
        _cacheSaveTimer.Start();
    }

    private async void ReloadTransport_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedType != GeneratedDocumentType.TransportAid) return;
        await LoadTransportDefaultsAsync(_selectedType);
        await SaveCurrentFormCacheAsync();
    }

    private void SetDatabaseDefault(GeneratedDocumentType type, string key, string? value)
    {
        if (!_fields.ContainsKey(key) || HasCachedField(type, key)) return;
        SetField(key, value);
    }

    private bool HasCachedField(GeneratedDocumentType type, string key)
    {
        if (_military.Count == 1
            && _cache.TryGetValue(MilitaryCacheKey(type, _military[0].Id), out var personal)
            && personal.ContainsKey(key)) return true;
        return _cache.TryGetValue(type.ToString(), out var common) && common.ContainsKey(key);
    }

    private static IReadOnlyList<string> SplitIntoLines(string value, int maxLength, int maxLines)
    {
        var words = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0) current.Append(word);
            else if (current.Length + 1 + word.Length <= maxLength) current.Append(' ').Append(word);
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
                if (lines.Count == maxLines - 1) break;
            }
        }
        if (current.Length > 0 && lines.Count < maxLines) lines.Add(current.ToString());
        while (lines.Count < maxLines) lines.Add(string.Empty);
        return lines.Take(maxLines).ToList();
    }

    private void UpdateButtonSelection()
    {
        foreach (var button in new[] { TransportButton, PecuniaryButton, PostalButton, PaymentCopyButton, ChristmasButton })
        {
            var active = string.Equals(button.Tag?.ToString(), _selectedType.ToString(), StringComparison.Ordinal);
            button.Style = (Style)FindResource(active ? "PrimaryButtonStyle" : "DocumentTypeButtonStyle");
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 0, 9);
        }
    }

    private List<DocumentFormProfile> CurrentProfiles()
    {
        if (!_profileStore.Profiles.TryGetValue(GlobalProfilesKey, out var profiles))
        {
            profiles = [];
            _profileStore.Profiles[GlobalProfilesKey] = profiles;
        }
        return profiles;
    }

    private void RefreshProfiles(string? selectedId = null)
    {
        var profiles = CurrentProfiles().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _profileStore.Profiles[GlobalProfilesKey] = profiles;
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = profiles;
        ProfileCombo.SelectedItem = profiles.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, string> CollectFields()
        => _fields.ToDictionary(x => x.Key, x => ReadFieldValue(x.Value), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> CollectProfileFields()
        => _fields.Where(x => IsReusableProfileField(x.Key))
            .ToDictionary(x => x.Key, x => ReadFieldValue(x.Value), StringComparer.OrdinalIgnoreCase);

    private static bool IsReusableProfileField(string key)
        => key.StartsWith("COMANDANTE_", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("ASSINANTE_", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("ORDENADOR", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("REMETENTE_", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("ORIGEM_", StringComparison.OrdinalIgnoreCase)
           || key.Equals("CMT_PUBLICACAO", StringComparison.OrdinalIgnoreCase)
           || key.Equals("UNIDADE_SERVINDO", StringComparison.OrdinalIgnoreCase)
           || key.Equals("OM_NOME", StringComparison.OrdinalIgnoreCase)
           || key.Equals("CMT_OM", StringComparison.OrdinalIgnoreCase)
           || key.Equals("CMD_NOME_POSTO", StringComparison.OrdinalIgnoreCase);

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not DocumentFormProfile profile)
        {
            SigfurDialog.Show(this, "Escolha um perfil salvo.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var (key, value) in profile.Values)
            SetField(key, value);
        StatusText.Text = $"Perfil “{profile.Name}” aplicado.";
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow(
            "Salvar nomes e assinaturas",
            "Dê um nome para este conjunto, por exemplo: Comandante atual, Ordenador atual ou Assinatura Furriel.") { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
        var profileValues = CollectProfileFields();
        if (profileValues.Count == 0 || profileValues.All(x => string.IsNullOrWhiteSpace(x.Value)))
        {
            SigfurDialog.Show(this, "Preencha ao menos um nome, posto, função ou dado de assinatura antes de salvar o perfil.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var profiles = CurrentProfiles();
        var existing = profiles.FirstOrDefault(x => string.Equals(x.Name, prompt.Value, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
        {
            if (SigfurDialog.Show(this, $"Atualizar o perfil “{existing.Name}” com os campos atuais?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            existing.Values = new Dictionary<string, string>(profileValues, StringComparer.OrdinalIgnoreCase);
            existing.UpdatedAt = DateTime.Now;
            await App.Json.SaveAsync(App.Paths.DocumentProfilesFile, _profileStore);
            RefreshProfiles(existing.Id);
            StatusText.Text = $"Perfil “{existing.Name}” atualizado para todos os documentos.";
            return;
        }
        var profile = new DocumentFormProfile
        {
            Name = prompt.Value.Trim(),
            Values = new Dictionary<string, string>(profileValues, StringComparer.OrdinalIgnoreCase),
            UpdatedAt = DateTime.Now
        };
        profiles.Add(profile);
        await App.Json.SaveAsync(App.Paths.DocumentProfilesFile, _profileStore);
        RefreshProfiles(profile.Id);
        StatusText.Text = $"Perfil “{profile.Name}” salvo e disponível em todos os documentos.";
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not DocumentFormProfile profile) return;
        if (SigfurDialog.Show(this, $"Excluir o perfil “{profile.Name}”?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CurrentProfiles().RemoveAll(x => string.Equals(x.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        await App.Json.SaveAsync(App.Paths.DocumentProfilesFile, _profileStore);
        RefreshProfiles();
        StatusText.Text = "Perfil excluído.";
    }

    private async Task RefreshTemplateStatusAsync()
    {
        var key = _selectedType.ToString();
        _selectedTemplatePath = string.Empty;
        if (_templateSelections.TryGetValue(key, out var manual) && File.Exists(manual))
        {
            _selectedTemplatePath = manual;
            SelectedTemplateText.Text = Path.GetFileName(manual);
            TemplateStatusText.Text = $"Modelo escolhido: {Path.GetFileName(manual)}";
            return;
        }
        if (!string.IsNullOrWhiteSpace(manual) && !File.Exists(manual)) _templateSelections.Remove(key);
        var automatic = await _service.ResolveTemplateAsync(_selectedType);
        _selectedTemplatePath = automatic ?? string.Empty;
        SelectedTemplateText.Text = string.IsNullOrWhiteSpace(automatic) ? "Automático — nenhum modelo encontrado" : $"Automático: {Path.GetFileName(automatic)}";
        TemplateStatusText.Text = string.IsNullOrWhiteSpace(automatic)
            ? "Modelo não localizado — será gerado RTF"
            : $"Modelo: {Path.GetFileName(automatic)}";
    }

    private async void ChooseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Escolher modelo — {MilitaryDocumentGenerationService.DisplayName(_selectedType)}",
            Filter = "Documento do Word/LibreOffice (*.docx;*.odt)|*.docx;*.odt|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        _templateSelections[_selectedType.ToString()] = dialog.FileName;
        await App.Json.SaveAsync(App.Paths.DocumentTemplateSelectionFile, _templateSelections);
        await RefreshTemplateStatusAsync();
        StatusText.Text = "Modelo escolhido e salvo para este tipo de documento.";
    }

    private async void UseAutomaticTemplate_Click(object sender, RoutedEventArgs e)
    {
        _templateSelections.Remove(_selectedType.ToString());
        await App.Json.SaveAsync(App.Paths.DocumentTemplateSelectionFile, _templateSelections);
        await RefreshTemplateStatusAsync();
        StatusText.Text = "Localização automática de modelo ativada.";
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta para os documentos gerados",
            InitialDirectory = Directory.Exists(OutputFolderBox.Text) ? OutputFolderBox.Text : App.Paths.GeneratedDocumentsDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) OutputFolderBox.Text = dialog.FolderName;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = OutputFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) path = App.Paths.GeneratedDocumentsDirectory;
        Directory.CreateDirectory(path);
        ShellService.OpenPath(path);
    }

    private void OpenTemplates_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.Paths.DocumentTemplatesDirectory);
        ShellService.OpenPath(App.Paths.DocumentTemplatesDirectory);
    }

    private async Task LoadPostalItemsAsync(string? term = null, bool openDropDown = false)
    {
        if (_loadingPostalSuggestions) return;
        _loadingPostalSuggestions = true;
        try
        {
            var items = await _postalService.SearchAsync(term, 120);
            PostalOmCombo.ItemsSource = items;
            PostalStatusText.Text = items.Count == 0
                ? "Nenhuma OM encontrada. Preencha o endereço e salve para cadastrar."
                : $"{items.Count} OM(s) encontrada(s). Digite para filtrar, escolha e clique em Aplicar.";
            if (items.Count == 1 && !string.IsNullOrWhiteSpace(term)) PostalOmCombo.SelectedItem = items[0];
            if (openDropDown && items.Count > 0) PostalOmCombo.IsDropDownOpen = true;
        }
        finally { _loadingPostalSuggestions = false; }
    }

    private async void SearchPostal_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadPostalItemsAsync(PostalSearchBox.Text, openDropDown: true); }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Base de OMs", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void PostalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_formReady || _selectedType != GeneratedDocumentType.PostalLabel) return;
        _postalSearchTimer.Stop();
        _postalSearchTimer.Start();
    }

    private async void PostalOmCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (_loadingPostalSuggestions || _selectedType != GeneratedDocumentType.PostalLabel) return;
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.Tab or Key.Escape) return;
        try { await LoadPostalItemsAsync(PostalOmCombo.Text, openDropDown: true); }
        catch (Exception ex) { PostalStatusText.Text = ex.Message; }
    }

    private void PostalSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            PostalOmCombo.Focus();
            PostalOmCombo.IsDropDownOpen = true;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        SearchPostal_Click(sender, e);
        e.Handled = true;
    }

    private void PostalOmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PostalOmCombo.SelectedItem is PostalOmAddress item)
        {
            PostalStatusText.Text = item.ImportedOnline ? "Endereço importado da base oficial." : "Endereço salvo localmente.";
            PostalSearchBox.Text = item.OmName;
        }
    }

    private async void ApplyPostalOm_Click(object sender, RoutedEventArgs e)
    {
        var item = PostalOmCombo.SelectedItem as PostalOmAddress;
        var typed = !string.IsNullOrWhiteSpace(PostalOmCombo.Text) ? PostalOmCombo.Text : PostalSearchBox.Text;
        if (item is null && !string.IsNullOrWhiteSpace(typed))
            item = await _postalService.FindByNameAsync(typed);
        if (item is null)
        {
            SigfurDialog.Show(this, "Escolha uma OM na lista. Caso não exista, preencha os campos e use + Salvar / atualizar OM.", "Base de OMs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ApplyPostalItem(item);
        StatusText.Text = $"Endereço de “{item.OmName}” aplicado ao documento.";
    }

    private void ApplyPostalItem(PostalOmAddress item)
    {
        SetField("OM_DESTINO", item.OmName);
        SetField("DESTINATARIO", string.IsNullOrWhiteSpace(item.OmName) ? "Sr Comandante" : $"Sr Comandante do {item.OmName}");
        SetField("LOGRADOURO_DESTINO", item.Street);
        SetField("NUMERO_DESTINO", item.Number);
        SetField("COMPLEMENTO_DESTINO", item.Complement);
        SetField("BAIRRO_DESTINO", item.Neighborhood);
        SetField("CIDADE_DESTINO", item.City);
        SetField("UF_DESTINO", item.State);
        SetField("CEP_DESTINO", PostalOmAddressService.FormatZipCode(item.ZipCode));
        SetField("ENDERECO_DESTINO", PostalOmAddressService.BuildDestinationAddress(item));
    }

    private async void SavePostalOm_Click(object sender, RoutedEventArgs e)
    {
        var item = BuildPostalItemFromForm();
        if (string.IsNullOrWhiteSpace(item.OmName))
        {
            SigfurDialog.Show(this, "Preencha o campo OM de destino antes de salvar.", "Base de OMs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await _postalService.UpsertAsync(item);
            PostalSearchBox.Text = item.OmName;
            await LoadPostalItemsAsync(item.OmName, openDropDown: false);
            PostalOmCombo.SelectedItem = (PostalOmCombo.ItemsSource as IEnumerable<PostalOmAddress>)?.FirstOrDefault();
            PostalStatusText.Text = "OM salva/atualizada na base local.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Base de OMs", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void SyncPostal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncPostalButton.IsEnabled = false;
            var progress = new Progress<string>(text => PostalStatusText.Text = text);
            var count = await _postalService.SyncFromOfficialAsync(progress);
            await LoadPostalItemsAsync(PostalSearchBox.Text, openDropDown: false);
            SigfurDialog.Show(this, $"Base oficial sincronizada.\n\nOMs importadas/atualizadas: {count}", "Correios — Base de OMs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Correios — Base de OMs", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SyncPostalButton.IsEnabled = true; }
    }

    private PostalOmAddress BuildPostalItemFromForm() => new()
    {
        OmName = GetField("OM_DESTINO"),
        Street = GetField("LOGRADOURO_DESTINO"),
        Number = GetField("NUMERO_DESTINO"),
        Complement = GetField("COMPLEMENTO_DESTINO"),
        Neighborhood = GetField("BAIRRO_DESTINO"),
        City = GetField("CIDADE_DESTINO"),
        State = GetField("UF_DESTINO"),
        ZipCode = PostalOmAddressService.FormatZipCode(GetField("CEP_DESTINO")),
        SourceName = "Cadastro manual do SIGFUR",
        ImportedOnline = false
    };

    private void RefreshDerivedPostalAddress()
    {
        if (!_formReady || _selectedType != GeneratedDocumentType.PostalLabel) return;
        var composed = PostalOmAddressService.BuildDestinationAddress(BuildPostalItemFromForm());
        var current = GetField("ENDERECO_DESTINO");
        if (!string.Equals(current, composed, StringComparison.Ordinal))
            SetField("ENDERECO_DESTINO", composed);
    }

    private string GetField(string key) => _fields.TryGetValue(key, out var control) ? ReadFieldValue(control) : string.Empty;
    private void SetField(string key, string? value)
    {
        if (!_fields.TryGetValue(key, out var control)) return;
        WriteFieldValue(control, value ?? string.Empty);
    }

    private async Task SaveCurrentFormCacheAsync()
    {
        if (!_storesLoaded) return;
        try
        {
            if (_formReady && _fields.Count > 0)
            {
                var current = new Dictionary<string, string>(CollectFields(), StringComparer.OrdinalIgnoreCase);
                _cache[_selectedType.ToString()] = current;
                if (_military.Count == 1)
                    _cache[MilitaryCacheKey(_selectedType, _military[0].Id)] = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            }
            if (!_cache.TryGetValue(GlobalCacheKey, out var global))
            {
                global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _cache[GlobalCacheKey] = global;
            }
            global["OUTPUT_FOLDER"] = OutputFolderBox.Text?.Trim() ?? string.Empty;
            global["TRANSPORT_USE_ADDRESS"] = (UseTransportAddressCheck.IsChecked == true).ToString();
            global["TRANSPORT_USE_BUSES"] = (UseTransportBusesCheck.IsChecked == true).ToString();
            await App.Json.SaveAsync(App.Paths.DocumentFormCacheFile, _cache);
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao salvar automaticamente os campos da geração de documentos.", ex);
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cacheSaveTimer.Stop();
        await SaveCurrentFormCacheAsync();
    }

    private async void SaveForm_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentFormCacheAsync();
        StatusText.Text = _military.Count == 1
            ? $"Preenchimento salvo para {_military[0].ShortRank} {_military[0].WarName}."
            : "Preenchimento comum salvo para este tipo de documento.";
        SigfurDialog.Show(this,
            "Os campos atuais foram salvos sem gerar o documento. Na próxima abertura eles serão restaurados automaticamente.",
            "SIGFUR — Preenchimento salvo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_military.Count == 0)
        {
            SigfurDialog.Show(this, "Nenhum militar foi selecionado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _busy = true;
            GenerateButton.IsEnabled = false;
            if (_selectedType == GeneratedDocumentType.PostalLabel) RefreshDerivedPostalAddress();
            var fields = CollectFields();
            await SaveCurrentFormCacheAsync();
            var request = new DocumentGenerationRequest
            {
                Type = _selectedType,
                Military = _military,
                Fields = fields,
                OutputDirectory = OutputFolderBox.Text.Trim(),
                OpenAfterGenerate = OpenAfterCheck.IsChecked == true,
                UseTransportAddressFromDatabase = UseTransportAddressCheck.IsChecked == true,
                UseTransportBusesFromDatabase = UseTransportBusesCheck.IsChecked == true,
                TemplatePath = _selectedTemplatePath,
                PrintAfterGenerate = PrintAfterCheck.IsChecked == true
            };
            var progress = new Progress<(int Current, int Total, string Name)>(value =>
                StatusText.Text = $"Gerando {value.Current}/{value.Total}: {value.Name}");
            var result = await _service.GenerateAsync(request, progress);
            StatusText.Text = $"Concluído: {result.Files.Count} gerado(s), {result.Failures.Count} falha(s).";
            if (request.PrintAfterGenerate && result.Files.Count > 0)
            {
                var printQueue = new PrintQueueWindow(result.Files) { Owner = this };
                printQueue.ShowDialog();
            }
            SigfurDialog.Show(this,
                $"Documentos gerados: {result.Files.Count}\nFalhas: {result.Failures.Count}\n\nPasta:\n{request.OutputDirectory}" +
                (result.Failures.Count > 0 ? "\n\nConsulte FALHAS_GERACAO.txt na pasta." : string.Empty),
                MilitaryDocumentGenerationService.DisplayName(_selectedType), MessageBoxButton.OK,
                result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Gerar documentação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            GenerateButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Description(GeneratedDocumentType type) => type switch
    {
        GeneratedDocumentType.TransportAid => "Gera o documento de Auxílio-Transporte usando cadastro, endereço e ônibus salvos. Só aparecem os campos realmente necessários ao modelo.",
        GeneratedDocumentType.PecuniaryCompensation => "Gera o requerimento de compensação pecuniária com dados do cadastro, banco, tempo de serviço e publicação do licenciamento.",
        GeneratedDocumentType.PostalLabel => "Gera DIEx/etiqueta dos Correios. Os endereços das OMs podem ser pesquisados, salvos e sincronizados com a base oficial.",
        GeneratedDocumentType.AuthenticPaymentCopy => "Gera a cópia autêntica de pagamento com BAR, valor, documento financeiro e assinatura.",
        GeneratedDocumentType.AdvanceChristmasBonus => "Gera no modelo enviado de antecipação da primeira parcela do adicional natalino, usando dados salvos e pedindo apenas férias, BI, OD e declaração.",
        _ => string.Empty
    };
}
