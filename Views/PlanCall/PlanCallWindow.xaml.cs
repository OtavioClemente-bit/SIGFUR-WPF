using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.PlanCall;

public partial class PlanCallWindow : Window
{
    private readonly PlanCallService _service;
    private readonly ObservableCollection<PlanCallRecord> _visible = [];
    private List<PlanCallRecord> _all = [];
    private PlanCallSettings _settings = new();
    private PlanCallRecord? _editing;
    private bool _initializing = true;

    public PlanCallWindow(PlanCallService service)
    {
        _service = service;
        InitializeComponent();
        App.UiState.Attach(this);
        RecordsGrid.ItemsSource = _visible;
        RankBox.ItemsSource = MilitaryRankService.AllRanks.Where(x => !x.Contains("Marechal", StringComparison.OrdinalIgnoreCase));
        Loaded += OnLoaded;
        Closing += async (_, _) => await SaveSettingsAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _service.LoadSettingsAsync();
            SearchBox.Text = _settings.Search;
            SortBox.SelectedIndex = _settings.SortMode switch { "Nome" => 1, "Região" => 2, "Endereço divergente" => 3, _ => 0 };
            GroupRegionBox.IsChecked = _settings.GroupByRegion;
            DifferencesOnlyBox.IsChecked = _settings.ShowOnlyDifferences;
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _initializing = false; }
    }

    private async Task ReloadAsync(int? selectedPlanId = null)
    {
        SetBusy(true, "Carregando efetivo do Listar e dados próprios do Plano...");
        try
        {
            _all = await _service.LoadAsync();
            ApplyFilter();
            if (selectedPlanId is int id)
            {
                var selected = _visible.FirstOrDefault(x => x.Id == id);
                if (selected is not null) { RecordsGrid.SelectedItem = selected; RecordsGrid.ScrollIntoView(selected); }
            }
            StatusText.Text = $"{_all.Count} pessoa(s) do efetivo. {_all.Count(x => x.Id > 0)} com dados no Plano • {_all.Count(x => x.DifferenceStatus == "DIVERGENTE")} endereço(s) divergente(s) • {_all.Count(x => x.DifferenceStatus == "SEM ENDEREÇO NO PLANO")} sem endereço importado.";
        }
        finally { SetBusy(false); }
    }

    private void ApplyFilter()
    {
        var words = Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<PlanCallRecord> rows = _all.Where(x => words.All(w => Normalize(x.SearchText).Contains(w)));
        if (DifferencesOnlyBox.IsChecked == true) rows = rows.Where(x => x.DifferenceStatus != "CONFERE");
        var mode = (SortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Hierarquia";
        rows = mode switch
        {
            "Nome" => rows.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            "Região" => rows.OrderBy(x => x.Region).ThenBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name),
            "Endereço divergente" => rows.OrderBy(x => x.DifferenceStatus == "DIVERGENTE" ? 0 : 1).ThenBy(x => x.Name),
            _ => rows.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        _visible.Clear();
        foreach (var row in rows) _visible.Add(row);
        CountText.Text = $"{_visible.Count} exibido(s)";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_initializing) ApplyFilter(); }
    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_initializing) ApplyFilter(); }
    private void DifferencesOnlyBox_Changed(object sender, RoutedEventArgs e) { if (!_initializing) ApplyFilter(); }

    private void RecordsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is PlanCallRecord item) LoadEditor(item);
    }
    private void EditSelected_Click(object sender, MouseButtonEventArgs e) { if (RecordsGrid.SelectedItem is PlanCallRecord item) { LoadEditor(item); PhoneBox.Focus(); } }

    private void LoadEditor(PlanCallRecord item)
    {
        _editing = item;
        SelectedNameText.Text = $"{item.ShortRank} {item.Name}".Trim();
        SelectedIdentifiersText.Text = $"CPF {item.FormattedCpf}  •  PREC-CP {item.PrecCp}  •  {item.MatchStatus}";
        RankBox.Text = item.Rank; NameBox.Text = item.Name; WarNameBox.Text = item.WarName; CpfBox.Text = item.Cpf; PrecBox.Text = item.PrecCp;
        PhoneBox.Text = item.Phone; AlternatePhoneBox.Text = item.AlternatePhone;
        StreetBox.Text = item.Street; NumberBox.Text = item.Number; ComplementBox.Text = item.Complement; DistrictBox.Text = item.District; CityStateBox.Text = item.CityState; ZipCodeBox.Text = PlanCallService.FormatZipCode(item.ZipCode);
        BasePhoneText.Text = item.HasMilitaryMatch ? $"Telefone no Listar: {(string.IsNullOrWhiteSpace(item.BasePhone) ? "não informado" : item.BasePhone)}" : "Não localizado no Listar Militares";
        BaseAddressText.Text = item.HasMilitaryMatch ? $"Endereço no Listar: {(string.IsNullOrWhiteSpace(item.BaseAddress) ? "não informado" : item.BaseAddress)}\nResultado: {item.DifferenceStatus}" : "A comparação só aparece quando o nome, CPF ou PREC-CP encontra correspondência.";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        RecordsGrid.SelectedItem = null;
        _editing = new PlanCallRecord();
        SelectedNameText.Text = "Novo registro do Plano"; SelectedIdentifiersText.Text = "Apenas o nome é obrigatório para salvar.";
        foreach (var box in new[] { NameBox, WarNameBox, CpfBox, PrecBox, PhoneBox, AlternatePhoneBox, StreetBox, NumberBox, ComplementBox, DistrictBox, CityStateBox, ZipCodeBox }) box.Clear();
        RankBox.Text = string.Empty; BasePhoneText.Text = "Ainda sem comparação"; BaseAddressText.Text = "Salve e recarregue para conferir com o Listar Militares."; NameBox.Focus();
    }

    private async void SaveSelected_Click(object sender, RoutedEventArgs e)
    {
        var item = _editing ?? new PlanCallRecord();
        ApplyEditorToItem(item);
        try
        {
            SetBusy(true, "Salvando no banco próprio do Plano...");
            await _service.SaveAsync(item, false);
            await ReloadAsync(item.Id);
            StatusText.Text = "Registro salvo somente no banco do Plano de Chamada.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void ApplyEditorToItem(PlanCallRecord item)
    {
        item.Rank = RankBox.Text; item.Name = NameBox.Text; item.WarName = WarNameBox.Text; item.Cpf = CpfBox.Text; item.PrecCp = PrecBox.Text;
        item.Phone = PhoneBox.Text; item.AlternatePhone = AlternatePhoneBox.Text;
        var normalized = PlanCallService.CanonicalizeAddress(StreetBox.Text, NumberBox.Text, ComplementBox.Text, DistrictBox.Text, CityStateBox.Text);
        item.Street = normalized.Street; item.Number = normalized.Number; item.Complement = normalized.Complement; item.District = normalized.District; item.CityState = normalized.CityState; item.ZipCode = ZipCodeBox.Text;
    }

    private async void CopyPhoneToBase_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) { SelectWarning(); return; }
        ApplyEditorToItem(_editing);
        if (!_editing.HasMilitaryMatch) { SigfurDialog.Show(this, "Este registro não foi localizado no Listar Militares.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!string.IsNullOrWhiteSpace(_editing.BasePhone)) { SigfurDialog.Show(this, "O Listar Militares já possui telefone. O Plano não substitui um telefone existente.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (string.IsNullOrWhiteSpace(_editing.Phone)) { SigfurDialog.Show(this, "Informe o telefone do Plano.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            // Um militar que ainda aparece somente pelo Listar recebe primeiro seu registro
            // no banco próprio do Plano e só depois o telefone é levado, se estiver faltando.
            await _service.SaveAsync(_editing, false);
            if (await _service.CopyMissingPhoneToMilitaryAsync(_editing))
            {
                await ReloadAsync(_editing.Id);
                StatusText.Text = "Telefone ausente foi incluído no Listar Militares. O endereço não foi alterado.";
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void CopyAllPhones_Click(object sender, RoutedEventArgs e)
    {
        var candidates = _all.Count(x => x.CanCopyPhoneToMilitary);
        if (candidates == 0) { SigfurDialog.Show(this, "Não há telefones do Plano para completar no Listar.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (SigfurDialog.Show(this, $"Completar {candidates} telefone(s) que estão vazios no Listar Militares?\n\nTelefones já preenchidos não serão substituídos e endereços não serão alterados.", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { SetBusy(true, "Completando telefones ausentes..."); var count = await _service.CopyAllMissingPhonesToMilitaryAsync(_all); await ReloadAsync(); StatusText.Text = $"{count} telefone(s) incluído(s) no Listar Militares."; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void ClearOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _editing.Id <= 0) { SelectWarning(); return; }
        if (SigfurDialog.Show(this, $"Excluir {_editing.Name} somente do banco do Plano de Chamada?\n\nO Listar Militares não será alterado.", "Excluir do Plano", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _service.ClearOverrideAsync(_editing.Id); _editing = null; await ReloadAsync(); New_Click(sender, e); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void LookupZip_Click(object sender, RoutedEventArgs e)
    {
        try { SetBusy(true, "Consultando CEP..."); var result = await _service.LookupZipCodeAsync(ZipCodeBox.Text); if (result is null) throw new InvalidOperationException("CEP não encontrado."); ApplyViaCep(result); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void LookupAddress_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Buscando CEP pelo logradouro...");
            var results = await _service.LookupAddressAsync(StreetBox.Text, CityStateBox.Text);
            if (results.Count == 0) throw new InvalidOperationException("Nenhum endereço encontrado.");
            var selected = results.Count == 1 ? results[0] : SelectAddress(results);
            if (selected is not null) ApplyViaCep(selected);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private ViaCepAddress? SelectAddress(IReadOnlyList<ViaCepAddress> results)
    {
        var window = new Window { Title = "Escolher endereço", Owner = this, Width = 820, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Icon = Icon };
        var root = new Grid { Margin = new Thickness(16) }; root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var list = new ListBox { ItemsSource = results, DisplayMemberPath = "Display" }; root.Children.Add(list);
        var ok = MakeButton("Usar selecionado", "PrimaryButtonStyle", new Thickness(8, 12, 0, 0)); ok.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; }; Grid.SetRow(ok, 1); ok.HorizontalAlignment = HorizontalAlignment.Right; root.Children.Add(ok); window.Content = root; list.SelectedIndex = 0;
        return window.ShowDialog() == true ? list.SelectedItem as ViaCepAddress : null;
    }

    private void ApplyViaCep(ViaCepAddress address)
    {
        StreetBox.Text = address.Street; DistrictBox.Text = address.District; CityStateBox.Text = address.CityState; ZipCodeBox.Text = PlanCallService.FormatZipCode(address.ZipCode);
        StatusText.Text = "Endereço consultado. Confira o número e salve no Plano.";
    }

    private void NormalizeAddress_Click(object sender, RoutedEventArgs e)
    {
        var normalized = PlanCallService.CanonicalizeAddress(StreetBox.Text, NumberBox.Text, ComplementBox.Text, DistrictBox.Text, CityStateBox.Text);
        StreetBox.Text = normalized.Street; NumberBox.Text = normalized.Number; ComplementBox.Text = normalized.Complement; DistrictBox.Text = normalized.District; CityStateBox.Text = normalized.CityState; ZipCodeBox.Text = PlanCallService.FormatZipCode(ZipCodeBox.Text);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Importar Plano de Chamada", Filter = "Planilhas|*.xlsx;*.ods;*.csv|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true, "Lendo a planilha...");
            var imported = await _service.ReadImportAsync(dialog.FileName);
            var matches = _service.MatchImports(imported, _all);
            SetBusy(false);
            if (matches.Count == 0) { SigfurDialog.Show(this, "Nenhuma linha com nome foi encontrada.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (!ShowImportReview(matches)) return;
            SetBusy(true, "Gravando no banco próprio do Plano...");
            var count = await _service.ApplyImportAsync(matches, dialog.FileName);
            await ReloadAsync(); StatusText.Text = $"{count} registro(s) importado(s). A planilha criou/atualizou apenas o banco do Plano.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private bool ShowImportReview(List<PlanCallImportMatch> matches)
    {
        var window = new Window { Title = "Conferir importação — banco do Plano", Owner = this, Width = 1240, Height = 680, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Icon = Icon };
        var root = new Grid { Margin = new Thickness(16) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Marque as linhas que devem criar ou atualizar registros no banco próprio do Plano de Chamada.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var grid = new DataGrid { ItemsSource = matches, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Aplicar", Binding = new System.Windows.Data.Binding("Apply") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 65 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Linha", Binding = new System.Windows.Data.Binding("Imported.SourceRow"), Width = 55, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Pessoa", Binding = new System.Windows.Data.Binding("MilitaryText"), Width = 270, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "No banco do Plano", Binding = new System.Windows.Data.Binding("MatchKind"), Width = 120, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Alteração", Binding = new System.Windows.Data.Binding("ChangeKind"), Width = 125, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Endereço atual", Binding = new System.Windows.Data.Binding("CurrentAddress"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "Endereço importado", Binding = new System.Windows.Data.Binding("ImportedAddress"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = MakeButton("Cancelar", "GhostButtonStyle", new Thickness(0, 0, 8, 0)); var apply = MakeButton("Aplicar marcados", "PrimaryButtonStyle", new Thickness()); cancel.Click += (_, _) => window.DialogResult = false; apply.Click += (_, _) => { grid.CommitEdit(); if (matches.Any(x => x.Apply)) window.DialogResult = true; }; bar.Children.Add(cancel); bar.Children.Add(apply); Grid.SetRow(bar, 2); root.Children.Add(bar); window.Content = root;
        return window.ShowDialog() == true;
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        try { SetBusy(true, "Criando backup do banco do Plano..."); var id = await _service.CreateRestorePointAsync($"Backup manual — {DateTime.Now:dd/MM/yyyy HH:mm}"); StatusText.Text = $"Backup #{id} criado."; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var points = await _service.ListRestorePointsAsync();
            if (points.Count == 0) { SigfurDialog.Show(this, "Nenhum backup encontrado.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var selected = SelectRestorePoint(points); if (selected is null) return;
            if (SigfurDialog.Show(this, $"Restaurar o backup #{selected.Id}?", "Plano de Chamada", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true, "Restaurando banco do Plano..."); var count = await _service.RestoreAsync(selected.Id); await ReloadAsync(); StatusText.Text = $"Backup restaurado: {count} registro(s).";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private PlanCallRestorePoint? SelectRestorePoint(IReadOnlyList<PlanCallRestorePoint> points)
    {
        var window = new Window { Title = "Restaurar Plano de Chamada", Owner = this, Width = 780, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Icon = Icon };
        var root = new Grid { Margin = new Thickness(16) }; root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var list = new ListBox { ItemsSource = points, DisplayMemberPath = "Display" }; root.Children.Add(list);
        var select = MakeButton("Selecionar", "PrimaryButtonStyle", new Thickness(0, 12, 0, 0)); select.HorizontalAlignment = HorizontalAlignment.Right; select.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; }; Grid.SetRow(select, 1); root.Children.Add(select); window.Content = root; list.SelectedIndex = 0;
        return window.ShowDialog() == true ? list.SelectedItem as PlanCallRestorePoint : null;
    }

    private async void ExportExcel_Click(object sender, RoutedEventArgs e) => await ExportAsync("Excel|*.xlsx", ".xlsx", (p, r) => _service.ExportExcelAsync(p, r, GroupRegionBox.IsChecked == true));
    private async void ExportOdt_Click(object sender, RoutedEventArgs e) => await ExportAsync("OpenDocument|*.odt", ".odt", (p, r) => _service.ExportOdtAsync(p, r, GroupRegionBox.IsChecked == true));
    private async void ExportPdf_Click(object sender, RoutedEventArgs e) => await ExportAsync("PDF|*.pdf", ".pdf", (p, r) => _service.ExportPdfAsync(p, r, GroupRegionBox.IsChecked == true));
    private async Task ExportAsync(string filter, string extension, Func<string, IReadOnlyList<PlanCallRecord>, Task> exporter)
    {
        if (_visible.Count == 0) { SigfurDialog.Show(this, "Não há registros no filtro atual.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, FileName = $"Plano_de_Chamada_{DateTime.Today:yyyyMMdd}{extension}", InitialDirectory = Directory.Exists(_settings.OutputDirectory) ? _settings.OutputDirectory : App.Paths.PlanCallOutputDirectory };
        if (dialog.ShowDialog(this) != true) return;
        try { SetBusy(true, "Gerando arquivo..."); await exporter(dialog.FileName, _visible.ToList()); _settings.OutputDirectory = Path.GetDirectoryName(dialog.FileName) ?? _settings.OutputDirectory; ShellService.OpenPath(dialog.FileName); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void RecordExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        EditorGapColumn.Width = new GridLength(6);
        EditorColumn.Width = new GridLength(46);
        StatusText.Text = "Registro do Plano recolhido. Clique no cabeçalho lateral para reabrir.";
    }

    private void RecordExpander_Expanded(object sender, RoutedEventArgs e)
    {
        EditorGapColumn.Width = new GridLength(12);
        EditorColumn.Width = new GridLength(1.05, GridUnitType.Star);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) { try { await ReloadAsync(_editing?.Id); } catch (Exception ex) { ShowError(ex); } }
    private async void OpenWallet_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || !_editing.HasMilitaryMatch) { SigfurDialog.Show(this, "Este registro não foi localizado no Listar Militares.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var military = await App.MilitaryRepository.GetByIdAsync(_editing.MilitaryId);
        if (military is not null)
        {
            var wallet = new MilitaryWalletWindow(App.MilitaryRepository, App.Paystubs, military) { Owner = this };
            wallet.Show();
            wallet.Activate();
        }
    }

    private async Task SaveSettingsAsync()
    {
        try { _settings.Search = SearchBox.Text; _settings.SortMode = (SortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Hierarquia"; _settings.GroupByRegion = GroupRegionBox.IsChecked == true; _settings.ShowOnlyDifferences = DifferencesOnlyBox.IsChecked == true; await _service.SaveSettingsAsync(_settings); } catch { }
    }

    private Button MakeButton(string text, string style, Thickness margin) { var button = new Button { Content = text, Margin = margin }; if (FindResource(style) is Style found) button.Style = found; return button; }
    private void SelectWarning() => SigfurDialog.Show(this, "Selecione ou crie um registro do Plano.", "Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Information);
    private void SetBusy(bool busy, string? status = null) { BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status; }
    private void ShowError(Exception ex) { _ = App.Log.WriteAsync("Falha no Plano de Chamada nativo.", ex); SigfurDialog.Show(this, ex.Message, "SIGFUR — Plano de Chamada", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static string Normalize(string? value) { var form = (value ?? string.Empty).Normalize(NormalizationForm.FormD); var noAccents = new string(form.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()); return System.Text.RegularExpressions.Regex.Replace(noAccents.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim(); }
}
