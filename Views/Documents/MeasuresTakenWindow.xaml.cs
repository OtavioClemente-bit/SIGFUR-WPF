using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Licensed;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Documents;

public partial class MeasuresTakenWindow : Window
{
    private readonly MeasuresTakenService _service;
    private readonly ObservableCollection<MeasuresMilitaryItem> _paystubAvailable = [];
    private readonly ObservableCollection<MeasuresMilitaryItem> _paymentAvailable = [];
    private readonly ObservableCollection<MeasuresSelectedItem> _paystubSelected = [];
    private readonly ObservableCollection<MeasuresSelectedItem> _paymentSelected = [];
    private List<MeasuresMilitaryItem> _allPeople = [];
    private MeasuresTakenSettings _settings = new();
    private int? _currentWorkId;
    private string _currentWorkName = string.Empty;
    private Point _dragStart;
    private DataGrid? _dragSourceGrid;
    private bool _loadingIndividual;
    private bool _initializing = true;

    public MeasuresTakenWindow(MeasuresTakenService service)
    {
        _service = service;
        InitializeComponent();
        App.UiState.Attach(this);
        PaystubAvailableGrid.ItemsSource = _paystubAvailable;
        PaymentAvailableGrid.ItemsSource = _paymentAvailable;
        PaystubSelectedGrid.ItemsSource = _paystubSelected;
        PaymentSelectedGrid.ItemsSource = _paymentSelected;
        Loaded += OnLoaded;
        Closing += async (_, _) => await SaveSettingsQuietlyAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _service.LoadSettingsAsync();
            SourceBox.SelectedIndex = _settings.Source switch { "Transferidos" => 1, "Todos" => 2, _ => 0 };
            SearchBox.Text = _settings.Search;
            PaystubSearchBox.Text = _settings.Search;
            PaymentSearchBox.Text = _settings.Search;
            OriginBox.Text = _settings.OriginText;
            OrganizationBox.Text = _settings.Organization;
            CommanderBox.Text = _settings.CommanderName;
            CommanderRankBox.Text = _settings.CommanderRank;
            SignatureRoleBox.Text = _settings.SignatureRole;
            PaystubDefaultMeasureBox.Text = string.IsNullOrWhiteSpace(_settings.PaystubDefaultMeasure) ? _settings.DefaultMeasure : _settings.PaystubDefaultMeasure;
            PaymentDefaultMeasureBox.Text = string.IsNullOrWhiteSpace(_settings.PaymentDefaultMeasure) ? _settings.DefaultMeasure : _settings.PaymentDefaultMeasure;
            MainTabs.SelectedIndex = Math.Clamp(_settings.LastActiveTab, 0, 2);
            _currentWorkId = _settings.LastWorkId;

            await ReloadPeopleAsync();
            if (_currentWorkId is int lastId)
            {
                var last = await _service.LoadWorkAsync(lastId);
                if (last is not null)
                {
                    _currentWorkName = last.Value.Name;
                    await ApplyPayloadAsync(last.Value.Payload);
                }
            }
            RefreshPreview();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _initializing = false; }
    }

    private string SelectedSource => (SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Banco principal";
    private bool IsPaystubTab => MainTabs.SelectedIndex == 0;
    private DataGrid ActiveSelectedGrid => IsPaystubTab ? PaystubSelectedGrid : PaymentSelectedGrid;
    private TextBox ActiveIndividualBox => IsPaystubTab ? PaystubIndividualMeasureBox : PaymentIndividualMeasureBox;
    private ObservableCollection<MeasuresSelectedItem> ActiveSelected => IsPaystubTab ? _paystubSelected : _paymentSelected;

    private async Task ReloadPeopleAsync()
    {
        SetBusy(true, "Carregando militares...");
        try
        {
            _allPeople = await _service.LoadPeopleAsync(SelectedSource);
            ApplyFilter();
        }
        finally { SetBusy(false, $"{_allPeople.Count} militar(es) carregados."); }
    }

    private void ApplyFilter()
    {
        ApplyFilterTo(_paystubAvailable, _paystubSelected, PaystubSortBox, PaystubSearchBox.Text);
        ApplyFilterTo(_paymentAvailable, _paymentSelected, PaymentSortBox, PaymentSearchBox.Text);
    }

    private void ApplyFilterTo(
        ObservableCollection<MeasuresMilitaryItem> destination,
        ObservableCollection<MeasuresSelectedItem> selected,
        ComboBox sortBox,
        string localQuery)
    {
        var selectedKeys = selected.Select(x => Key(x.Person)).ToHashSet(StringComparer.Ordinal);
        IEnumerable<MeasuresMilitaryItem> rows = MeasuresTakenService.FilterPeople(_allPeople, string.IsNullOrWhiteSpace(localQuery) ? SearchBox.Text : localQuery)
            .Where(x => !selectedKeys.Contains(Key(x)));
        var sort = (sortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Hierarquia";
        rows = sort switch
        {
            "Nome" => rows.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            "PREC-CP" => rows.OrderBy(x => MilitaryFormatting.Digits(x.PrecCp), StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        destination.Clear();
        foreach (var row in rows) destination.Add(row);
    }

    private static string Key(MeasuresMilitaryItem item) => $"{item.IsTransferred}:{item.Military.Id}";

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _initializing) return;
        try { await ReloadPeopleAsync(); } catch (Exception ex) { ShowError(ex); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void LocalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_initializing) ApplyFilter();
    }

    private void AvailableSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_initializing) ApplyFilter();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || e.Source != MainTabs) return;
        LoadIndividualTextFromSelection(ActiveSelectedGrid);
    }

    private void AddPaystubSelected_Click(object sender, RoutedEventArgs e) => AddPeople(
        PaystubAvailableGrid.SelectedItems.Cast<MeasuresMilitaryItem>().ToList(),
        _paystubSelected,
        MeasuresSections.PaystubExam);

    private void AddPaystubFiltered_Click(object sender, RoutedEventArgs e) => AddPeople(
        _paystubAvailable.ToList(),
        _paystubSelected,
        MeasuresSections.PaystubExam);

    private void AddPaymentSelected_Click(object sender, RoutedEventArgs e) => AddPeople(
        PaymentAvailableGrid.SelectedItems.Cast<MeasuresMilitaryItem>().ToList(),
        _paymentSelected,
        MeasuresSections.PaymentExam);

    private void AddPaymentFiltered_Click(object sender, RoutedEventArgs e) => AddPeople(
        _paymentAvailable.ToList(),
        _paymentSelected,
        MeasuresSections.PaymentExam);

    private void AddPeople(
        IEnumerable<MeasuresMilitaryItem> rows,
        ObservableCollection<MeasuresSelectedItem> destination,
        string section)
    {
        var keys = destination.Select(x => Key(x.Person)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!keys.Add(Key(row))) continue;
            destination.Add(new MeasuresSelectedItem
            {
                Person = row,
                Section = section,
                Order = destination.Count + 1
            });
        }
        RenumberAll();
        ApplyFilter();
        RefreshPreview();
    }

    private void MovePaystubUp_Click(object sender, RoutedEventArgs e) => MoveSelected(_paystubSelected, PaystubSelectedGrid, -1);
    private void MovePaystubDown_Click(object sender, RoutedEventArgs e) => MoveSelected(_paystubSelected, PaystubSelectedGrid, 1);
    private void MovePaymentUp_Click(object sender, RoutedEventArgs e) => MoveSelected(_paymentSelected, PaymentSelectedGrid, -1);
    private void MovePaymentDown_Click(object sender, RoutedEventArgs e) => MoveSelected(_paymentSelected, PaymentSelectedGrid, 1);
    private void RemovePaystub_Click(object sender, RoutedEventArgs e) => RemoveSelected(_paystubSelected, PaystubSelectedGrid);
    private void RemovePayment_Click(object sender, RoutedEventArgs e) => RemoveSelected(_paymentSelected, PaymentSelectedGrid);
    private void ClearPaystub_Click(object sender, RoutedEventArgs e) => ClearSection(_paystubSelected, "Exame de Contracheque");
    private void ClearPayment_Click(object sender, RoutedEventArgs e) => ClearSection(_paymentSelected, "Exame de Pagamento");

    private void MoveSelected(ObservableCollection<MeasuresSelectedItem> collection, DataGrid grid, int delta)
    {
        if (grid.SelectedItem is not MeasuresSelectedItem item) return;
        var index = collection.IndexOf(item);
        var target = index + delta;
        if (target < 0 || target >= collection.Count) return;
        collection.Move(index, target);
        Renumber(collection);
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        RefreshPreview();
    }

    private void RemoveSelected(ObservableCollection<MeasuresSelectedItem> collection, DataGrid grid)
    {
        foreach (var item in grid.SelectedItems.Cast<MeasuresSelectedItem>().ToList()) collection.Remove(item);
        Renumber(collection);
        ApplyFilter();
        LoadIndividualTextFromSelection(grid);
        RefreshPreview();
    }

    private void ClearSection(ObservableCollection<MeasuresSelectedItem> collection, string title)
    {
        if (collection.Count == 0) return;
        if (SigfurDialog.Show(this, $"Remover todos os militares da aba {title}?", "Medidas Tomadas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        collection.Clear();
        RenumberAll();
        ApplyFilter();
        RefreshPreview();
    }

    private void RenumberAll()
    {
        Renumber(_paystubSelected);
        Renumber(_paymentSelected);
    }

    private void Renumber(ObservableCollection<MeasuresSelectedItem> collection)
    {
        for (var i = 0; i < collection.Count; i++) collection[i].Order = i + 1;
        PaystubCountText.Text = $"{_paystubSelected.Count} militar(es)";
        PaymentCountText.Text = $"{_paymentSelected.Count} militar(es)";
    }

    private void SelectedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid) LoadIndividualTextFromSelection(grid);
    }

    private void LoadIndividualTextFromSelection(DataGrid grid)
    {
        var box = ReferenceEquals(grid, PaystubSelectedGrid) ? PaystubIndividualMeasureBox : PaymentIndividualMeasureBox;
        _loadingIndividual = true;
        box.Text = (grid.SelectedItem as MeasuresSelectedItem)?.IndividualMeasure ?? string.Empty;
        _loadingIndividual = false;
    }

    private void DefaultMeasureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing && IsLoaded) RefreshPreview();
    }

    private void IndividualMeasureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingIndividual || sender is not TextBox box) return;
        var grid = ReferenceEquals(box, PaystubIndividualMeasureBox) ? PaystubSelectedGrid : PaymentSelectedGrid;
        if (grid.SelectedItem is not MeasuresSelectedItem item) return;
        item.IndividualMeasure = box.Text;
        RefreshPreview();
    }

    private void ApplyIndividualToSelected_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveSelectedGrid;
        var items = grid.SelectedItems.Cast<MeasuresSelectedItem>().ToList();
        if (items.Count == 0 && grid.SelectedItem is MeasuresSelectedItem current) items.Add(current);
        if (items.Count == 0) return;
        var measure = ActiveIndividualBox.Text.Trim();
        foreach (var item in items) item.IndividualMeasure = measure;
        RefreshPreview();
    }

    private void ClearIndividual_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveSelectedGrid;
        var items = grid.SelectedItems.Cast<MeasuresSelectedItem>().ToList();
        if (items.Count == 0 && grid.SelectedItem is MeasuresSelectedItem current) items.Add(current);
        if (items.Count == 0) return;
        _loadingIndividual = true;
        foreach (var item in items) item.IndividualMeasure = string.Empty;
        ActiveIndividualBox.Clear();
        _loadingIndividual = false;
        RefreshPreview();
    }

    private void SelectedGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        _dragSourceGrid = grid;
        _dragStart = e.GetPosition(grid);
    }

    private void SelectedGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not DataGrid grid || e.LeftButton != MouseButtonState.Pressed || grid.SelectedItem is not MeasuresSelectedItem item) return;
        var current = e.GetPosition(grid);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragSourceGrid = grid;
        DragDrop.DoDragDrop(grid, item, DragDropEffects.Move);
    }

    private void SelectedGrid_Drop(object sender, DragEventArgs e)
    {
        if (sender is not DataGrid targetGrid || !ReferenceEquals(targetGrid, _dragSourceGrid)) return;
        if (!e.Data.GetDataPresent(typeof(MeasuresSelectedItem))) return;
        var source = (MeasuresSelectedItem)e.Data.GetData(typeof(MeasuresSelectedItem))!;
        var target = FindRowItem(e.OriginalSource as DependencyObject);
        if (target is null || ReferenceEquals(source, target)) return;
        var collection = ReferenceEquals(targetGrid, PaystubSelectedGrid) ? _paystubSelected : _paymentSelected;
        var oldIndex = collection.IndexOf(source);
        var newIndex = collection.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;
        collection.Move(oldIndex, newIndex);
        Renumber(collection);
        targetGrid.SelectedItem = source;
        RefreshPreview();
    }

    private static MeasuresSelectedItem? FindRowItem(DependencyObject? current)
    {
        while (current is not null && current is not DataGridRow)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return (current as DataGridRow)?.Item as MeasuresSelectedItem;
    }

    private MeasuresDocumentData BuildData() => new()
    {
        OriginText = OriginBox.Text.Trim(),
        Organization = OrganizationBox.Text.Trim(),
        DefaultMeasure = PaymentDefaultMeasureBox.Text.Trim(),
        PaymentDefaultMeasure = PaymentDefaultMeasureBox.Text.Trim(),
        PaystubDefaultMeasure = PaystubDefaultMeasureBox.Text.Trim(),
        CommanderName = CommanderBox.Text.Trim(),
        CommanderRank = CommanderRankBox.Text.Trim(),
        SignatureRole = SignatureRoleBox.Text.Trim(),
        Items = [.. _paystubSelected, .. _paymentSelected]
    };

    private void RefreshPreview()
    {
        if (!IsLoaded) return;
        PreviewBox.Text = MeasuresTakenService.BuildPreview(BuildData());
        PreviewStatusText.Text = $"Contracheque: {_paystubSelected.Count} militar(es) • Pagamento: {_paymentSelected.Count} militar(es).";
        RenumberAll();
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e) => RefreshPreview();

    private async void ReloadPeople_Click(object sender, RoutedEventArgs e)
    {
        try { await ReloadPeopleAsync(); StatusText.Text = "Militares atualizados."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DeleteCurrentWork_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWorkId is not int id)
        {
            SigfurDialog.Show(this, "Nenhum trabalho salvo está aberto.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SigfurDialog.Show(this, $"Excluir '{_currentWorkName}'?", "Medidas Tomadas", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _service.DeleteWorkAsync(id);
            _currentWorkId = null;
            _currentWorkName = string.Empty;
            _settings.LastWorkId = null;
            await SaveSettingsQuietlyAsync();
            StatusText.Text = "Trabalho excluído.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportPdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Relatório PDF|*.pdf", Title = "Importar Exame de Pagamento/Contracheque" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true, "Importando PDF...");
        try
        {
            var import = await _service.ImportPdfAsync(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(import.SuggestedOrigin)) OriginBox.Text = import.SuggestedOrigin;
            if (_allPeople.Count == 0) await ReloadPeopleAsync();
            var matches = MeasuresTakenService.MatchImported(import.Entries, _allPeople);
            var added = 0;
            var notFound = new List<string>();
            foreach (var pair in matches)
            {
                if (pair.Match is null)
                {
                    notFound.Add(pair.Entry.Name);
                    continue;
                }
                var isPaystub = pair.Entry.Section == MeasuresSections.PaystubExam;
                var destination = isPaystub ? _paystubSelected : _paymentSelected;
                if (destination.Any(x => Key(x.Person) == Key(pair.Match))) continue;
                destination.Add(new MeasuresSelectedItem
                {
                    Person = pair.Match,
                    Section = isPaystub ? MeasuresSections.PaystubExam : MeasuresSections.PaymentExam,
                    IndividualMeasure = pair.Entry.Changes,
                    Order = destination.Count + 1
                });
                added++;
            }
            RenumberAll();
            ApplyFilter();
            RefreshPreview();
            StatusText.Text = $"PDF importado: {added} militar(es) incluído(s).";
            if (notFound.Count > 0)
                SigfurDialog.Show(this, $"{notFound.Count} nome(s) não foram localizados no cadastro:\n\n{string.Join("\n", notFound.Take(30))}", "Importação concluída com pendências", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void NewWork_Click(object sender, RoutedEventArgs e)
    {
        if ((_paystubSelected.Count + _paymentSelected.Count) > 0 && SigfurDialog.Show(this, "Iniciar um novo trabalho e limpar as duas abas?", "Medidas Tomadas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _currentWorkId = null;
        _currentWorkName = string.Empty;
        _paystubSelected.Clear();
        _paymentSelected.Clear();
        OriginBox.Text = _settings.OriginText;
        OrganizationBox.Text = _settings.Organization;
        CommanderBox.Text = _settings.CommanderName;
        CommanderRankBox.Text = _settings.CommanderRank;
        SignatureRoleBox.Text = _settings.SignatureRole;
        PaystubDefaultMeasureBox.Text = _settings.PaystubDefaultMeasure;
        PaymentDefaultMeasureBox.Text = _settings.PaymentDefaultMeasure;
        PaystubIndividualMeasureBox.Clear();
        PaymentIndividualMeasureBox.Clear();
        ApplyFilter();
        RefreshPreview();
        StatusText.Text = "Novo trabalho iniciado.";
    }

    private async void SaveWork_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = _currentWorkName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Prompt("Nome do trabalho", "Salvar Medidas Tomadas", $"Medidas {DateTime.Now:dd-MM-yyyy HH-mm}");
                if (string.IsNullOrWhiteSpace(name)) return;
            }
            _currentWorkId = await _service.SaveWorkAsync(name, BuildPayload(), _currentWorkId);
            _currentWorkName = name;
            _settings.LastWorkId = _currentWorkId;
            await SaveSettingsQuietlyAsync();
            StatusText.Text = $"Trabalho salvo: {name}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RenameWork_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWorkId is null)
        {
            SigfurDialog.Show(this, "Salve o trabalho antes de renomear.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = Prompt("Novo nome", "Renomear trabalho", _currentWorkName);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _currentWorkId = await _service.SaveWorkAsync(name, BuildPayload(), _currentWorkId);
            _currentWorkName = name;
            StatusText.Text = $"Trabalho renomeado para {name}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void SavedWorks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var works = await _service.ListWorksAsync();
            var dialog = new Window
            {
                Title = "Trabalhos salvos — Medidas Tomadas",
                Owner = this,
                Width = 780,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Background,
                Icon = Icon
            };
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var list = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = works
            };
            list.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            list.Columns.Add(new DataGridTextColumn { Header = "Atualizado", Binding = new System.Windows.Data.Binding("UpdatedText"), Width = 170 });
            grid.Children.Add(list);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var delete = StyledButton("Excluir", "DangerButtonStyle", new Thickness(0, 0, 8, 0));
            var cancel = StyledButton("Fechar", "GhostButtonStyle", new Thickness(0, 0, 8, 0));
            var open = StyledButton("Abrir", "PrimaryButtonStyle", new Thickness());
            delete.Click += async (_, _) =>
            {
                if (list.SelectedItem is not MeasuresSavedWorkSummary item) return;
                if (SigfurDialog.Show(dialog, $"Excluir '{item.Name}'?", "Medidas Tomadas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await _service.DeleteWorkAsync(item.Id);
                if (_currentWorkId == item.Id) { _currentWorkId = null; _currentWorkName = string.Empty; }
                list.ItemsSource = await _service.ListWorksAsync();
            };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            open.Click += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
            list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
            bar.Children.Add(delete); bar.Children.Add(cancel); bar.Children.Add(open);
            Grid.SetRow(bar, 1); grid.Children.Add(bar);
            dialog.Content = grid;

            if (dialog.ShowDialog() != true || list.SelectedItem is not MeasuresSavedWorkSummary selected) return;
            var loaded = await _service.LoadWorkAsync(selected.Id);
            if (loaded is null) return;
            _currentWorkId = selected.Id;
            _currentWorkName = loaded.Value.Name;
            await ApplyPayloadAsync(loaded.Value.Payload);
            StatusText.Text = $"Trabalho aberto: {loaded.Value.Name}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Button StyledButton(string content, string styleKey, Thickness margin)
    {
        var button = new Button { Content = content, Margin = margin };
        if (FindResource(styleKey) is Style style) button.Style = style;
        return button;
    }

    private MeasuresSavedWorkPayload BuildPayload() => new()
    {
        OriginText = OriginBox.Text,
        Organization = OrganizationBox.Text,
        DefaultMeasure = PaymentDefaultMeasureBox.Text,
        PaymentDefaultMeasure = PaymentDefaultMeasureBox.Text,
        PaystubDefaultMeasure = PaystubDefaultMeasureBox.Text,
        CommanderName = CommanderBox.Text,
        CommanderRank = CommanderRankBox.Text,
        SignatureRole = SignatureRoleBox.Text,
        People = [.. _paystubSelected.Select(ToSavedPerson), .. _paymentSelected.Select(ToSavedPerson)]
    };

    private static MeasuresSavedPerson ToSavedPerson(MeasuresSelectedItem item) => new()
    {
        MilitaryId = item.Person.Military.Id,
        IsTransferred = item.Person.IsTransferred,
        Section = item.Section,
        IndividualMeasure = item.IndividualMeasure,
        Order = item.Order,
        Name = item.Name,
        WarName = item.WarName,
        Rank = item.Person.Military.Rank,
        PrecCp = item.PrecCp,
        Cpf = item.Person.Military.Cpf
    };

    private async Task ApplyPayloadAsync(MeasuresSavedWorkPayload payload)
    {
        OriginBox.Text = payload.OriginText;
        OrganizationBox.Text = payload.Organization;
        PaymentDefaultMeasureBox.Text = string.IsNullOrWhiteSpace(payload.PaymentDefaultMeasure) ? payload.DefaultMeasure : payload.PaymentDefaultMeasure;
        PaystubDefaultMeasureBox.Text = string.IsNullOrWhiteSpace(payload.PaystubDefaultMeasure) ? payload.DefaultMeasure : payload.PaystubDefaultMeasure;
        CommanderBox.Text = payload.CommanderName;
        CommanderRankBox.Text = payload.CommanderRank;
        SignatureRoleBox.Text = payload.SignatureRole;
        if (_allPeople.Count == 0) await ReloadPeopleAsync();
        _paystubSelected.Clear();
        _paymentSelected.Clear();

        foreach (var saved in payload.People.OrderBy(x => x.Section).ThenBy(x => x.Order))
        {
            var match = _allPeople.FirstOrDefault(x => x.IsTransferred == saved.IsTransferred && x.Military.Id == saved.MilitaryId)
                ?? new MeasuresMilitaryItem
                {
                    IsTransferred = saved.IsTransferred,
                    Source = saved.IsTransferred ? "Transferidos" : "Banco principal",
                    Military = new MilitaryRecord
                    {
                        Id = saved.MilitaryId,
                        Rank = saved.Rank,
                        Name = saved.Name,
                        WarName = saved.WarName,
                        PrecCp = saved.PrecCp,
                        Cpf = saved.Cpf
                    }
                };
            var section = saved.Section == MeasuresSections.PaystubExam ? MeasuresSections.PaystubExam : MeasuresSections.PaymentExam;
            var item = new MeasuresSelectedItem
            {
                Person = match,
                Section = section,
                IndividualMeasure = saved.IndividualMeasure,
                Order = saved.Order
            };
            (section == MeasuresSections.PaystubExam ? _paystubSelected : _paymentSelected).Add(item);
        }
        ReorderBySavedOrder(_paystubSelected);
        ReorderBySavedOrder(_paymentSelected);
        RenumberAll();
        ApplyFilter();
        RefreshPreview();
    }

    private static void ReorderBySavedOrder(ObservableCollection<MeasuresSelectedItem> items)
    {
        var ordered = items.OrderBy(x => x.Order).ToList();
        items.Clear();
        foreach (var item in ordered) items.Add(item);
    }

    private async void ExportPaymentExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_paymentSelected.Count == 0)
        {
            SigfurDialog.Show(this, "Inclua militares na aba Exame de Pagamento antes de gerar a relação nominal.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = SaveDialog("Planilha Excel|*.xlsx", ".xlsx", "Relacao_Nominal");
        if (dialog.ShowDialog(this) != true) return;
        await RunExportAsync(data => _service.ExportPaymentRelationXlsxAsync(dialog.FileName, data), dialog.FileName, openAfter: true);
    }

    private async void ExportDocx_Click(object sender, RoutedEventArgs e) => await ExportAsync("Documento Word|*.docx", ".docx", _service.ExportDocxAsync);
    private async void ExportOdt_Click(object sender, RoutedEventArgs e) => await ExportAsync("Documento ODT|*.odt", ".odt", _service.ExportOdtAsync);

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = SaveDialog("Documento PDF|*.pdf", ".pdf");
        if (dialog.ShowDialog(this) != true) return;
        await RunExportAsync(async data => { _ = await _service.ExportPdfAsync(dialog.FileName, data); }, dialog.FileName, openAfter: true);
    }

    private async Task ExportAsync(string filter, string extension, Func<string, MeasuresDocumentData, CancellationToken, Task> exporter)
    {
        var dialog = SaveDialog(filter, extension);
        if (dialog.ShowDialog(this) != true) return;
        await RunExportAsync(data => exporter(dialog.FileName, data, CancellationToken.None), dialog.FileName, openAfter: true);
    }

    private SaveFileDialog SaveDialog(string filter, string extension, string prefix = "Medidas_Tomadas") => new()
    {
        Filter = filter,
        DefaultExt = extension,
        FileName = $"{prefix}_{DateTime.Today:yyyyMMdd}{extension}",
        InitialDirectory = Directory.Exists(_settings.OutputDirectory) ? _settings.OutputDirectory : App.Paths.MeasuresTakenOutputDirectory
    };

    private async Task RunExportAsync(Func<MeasuresDocumentData, Task> action, string path, bool openAfter)
    {
        if ((_paystubSelected.Count + _paymentSelected.Count) == 0)
        {
            SigfurDialog.Show(this, "Selecione pelo menos um militar.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetBusy(true, "Gerando documento...");
        try
        {
            await action(BuildData());
            _settings.OutputDirectory = Path.GetDirectoryName(path) ?? _settings.OutputDirectory;
            StatusText.Text = $"Documento gerado: {Path.GetFileName(path)}";
            if (openAfter) ShellService.OpenPath(path);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private MeasuresSelectedItem? SelectedMilitaryItem()
    {
        if (ActiveSelectedGrid.SelectedItem is MeasuresSelectedItem active) return active;
        return PaystubSelectedGrid.SelectedItem as MeasuresSelectedItem ?? PaymentSelectedGrid.SelectedItem as MeasuresSelectedItem;
    }

    private async void OpenWallet_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedMilitaryItem();
        if (item is null)
        {
            SigfurDialog.Show(this, "Selecione um militar na ordem da aba.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (item.Person.IsTransferred)
        {
            var record = (await App.LicensedTransferred.GetAllAsync(true)).FirstOrDefault(x => x.Id == item.Person.Military.Id);
            if (record is not null)
            {
                var wallet = new LicensedTransferredWalletWindow(App.LicensedTransferred, App.Paystubs, record) { Owner = this };
                wallet.Show();
                wallet.Activate();
            }
        }
        else
        {
            var wallet = new MilitaryWalletWindow(App.MilitaryRepository, App.Paystubs, item.Person.Military) { Owner = this };
            wallet.Show();
            wallet.Activate();
        }
    }

    private async void OpenPaystubs_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedMilitaryItem();
        if (item is null)
        {
            SigfurDialog.Show(this, "Selecione um militar na ordem da aba.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true, "Procurando contracheques...");
        try
        {
            var files = await App.Paystubs.FindForMilitaryAsync(item.Person.Military);
            if (files.Count == 0)
            {
                SigfurDialog.Show(this, "Nenhum contracheque salvo foi localizado para este militar.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShowPaystubChooser(item, files);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void ShowPaystubChooser(MeasuresSelectedItem item, IReadOnlyList<PaystubFileRecord> files)
    {
        var dialog = new Window
        {
            Title = $"Escolher contracheque — {item.Rank} {item.Name}",
            Owner = this,
            Width = 980,
            Height = 570,
            MinWidth = 760,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Icon = Icon
        };
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = $"Foram encontrados {files.Count} contracheque(s). Selecione exatamente o arquivo que deseja abrir.",
            Margin = new Thickness(0, 0, 0, 12),
            FontWeight = FontWeights.SemiBold
        });
        var list = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            ItemsSource = files
        };
        list.Columns.Add(new DataGridTextColumn { Header = "Referência", Binding = new System.Windows.Data.Binding("Reference"), Width = 120 });
        list.Columns.Add(new DataGridTextColumn { Header = "Arquivo", Binding = new System.Windows.Data.Binding("FileName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        list.Columns.Add(new DataGridTextColumn { Header = "Alterado em", Binding = new System.Windows.Data.Binding("ModifiedAt") { StringFormat = "dd/MM/yyyy HH:mm" }, Width = 150 });
        list.Columns.Add(new DataGridTextColumn { Header = "Tamanho", Binding = new System.Windows.Data.Binding("SizeText"), Width = 95 });
        Grid.SetRow(list, 1); root.Children.Add(list);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var close = StyledButton("Fechar", "GhostButtonStyle", new Thickness(0, 0, 8, 0));
        var open = StyledButton("Abrir selecionado", "PrimaryButtonStyle", new Thickness());
        void OpenSelected()
        {
            if (list.SelectedItem is not PaystubFileRecord selected) return;
            ShellService.OpenPath(selected.Path);
            dialog.Close();
        }
        close.Click += (_, _) => dialog.Close();
        open.Click += (_, _) => OpenSelected();
        list.MouseDoubleClick += (_, _) => OpenSelected();
        bar.Children.Add(close); bar.Children.Add(open);
        Grid.SetRow(bar, 2); root.Children.Add(bar);
        dialog.Content = root;
        dialog.Loaded += (_, _) => { if (files.Count > 0) list.SelectedIndex = 0; };
        dialog.ShowDialog();
    }

    private void OpenPhoto_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedMilitaryItem();
        if (item is not null && File.Exists(item.Person.Military.PhotoPath)) ShellService.OpenPath(item.Person.Military.PhotoPath);
        else SigfurDialog.Show(this, "Foto não localizada.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SaveCommanderDefaults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsQuietlyAsync();
            var profile = await App.Settings.LoadProfileAsync();
            profile.CommanderName = CommanderBox.Text.Trim().ToUpperInvariant();
            profile.CommanderRank = CommanderRankBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(OrganizationBox.Text))
                profile.Organization = OrganizationBox.Text.Trim();
            await App.Settings.SaveProfileAsync(profile);
            CommanderBox.Text = profile.CommanderName;
            StatusText.Text = "Comandante, posto/graduação, função de assinatura e OM salvos como padrão.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        try
        {
            _settings.Source = SelectedSource;
            _settings.Search = IsPaystubTab ? PaystubSearchBox.Text : PaymentSearchBox.Text;
            _settings.OriginText = OriginBox.Text;
            _settings.Organization = OrganizationBox.Text;
            _settings.CommanderName = CommanderBox.Text;
            _settings.CommanderRank = CommanderRankBox.Text;
            _settings.SignatureRole = SignatureRoleBox.Text;
            _settings.DefaultMeasure = PaymentDefaultMeasureBox.Text;
            _settings.PaymentDefaultMeasure = PaymentDefaultMeasureBox.Text;
            _settings.PaystubDefaultMeasure = PaystubDefaultMeasureBox.Text;
            _settings.LastActiveTab = Math.Clamp(MainTabs.SelectedIndex, 0, 2);
            _settings.LastWorkId = _currentWorkId;
            await _service.SaveSettingsAsync(_settings);
        }
        catch { }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void ShowError(Exception ex)
    {
        _ = App.Log.WriteAsync("Falha em Medidas Tomadas.", ex);
        SigfurDialog.Show(this, ex.Message, "SIGFUR — Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private string? Prompt(string caption, string title, string initial)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 500,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Background,
            Icon = Icon
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = caption });
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12) };
        Grid.SetRow(box, 1); grid.Children.Add(box);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Salvar", Width = 90, IsDefault = true };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        ok.Click += (_, _) => dialog.DialogResult = true;
        bar.Children.Add(cancel); bar.Children.Add(ok);
        Grid.SetRow(bar, 2); grid.Children.Add(bar);
        dialog.Content = grid;
        dialog.Loaded += (_, _) => { box.SelectAll(); box.Focus(); };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
