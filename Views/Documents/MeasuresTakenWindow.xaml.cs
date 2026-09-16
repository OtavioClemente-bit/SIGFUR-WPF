using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Licensed;
using SIGFUR.Wpf.Views.Military;
using SIGFUR.Wpf.Views.Finance;

namespace SIGFUR.Wpf.Views.Documents;

public partial class MeasuresTakenWindow : Window
{
    private readonly MeasuresTakenService _service;
    private readonly ObservableCollection<MeasuresMilitaryItem> _paystubAvailable = [];
    private readonly ObservableCollection<MeasuresMilitaryItem> _paymentAvailable = [];
    private readonly ObservableCollection<MeasuresSelectedItem> _paystubSelected = [];
    private readonly ObservableCollection<MeasuresSelectedItem> _paymentSelected = [];
    private readonly ObservableCollection<GratificationSpedAttachment> _measuresSpedAttachments = [];
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
        MeasuresSpedAttachmentList.ItemsSource = _measuresSpedAttachments;
        Loaded += OnLoaded;
        Closing += async (_, _) => await SaveSettingsQuietlyAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _service.LoadSettingsAsync();
            SourceBox.SelectedIndex = MeasuresTakenService.NormalizeSource(_settings.Source) switch { MeasuresTakenService.SourceLicensedTransferred => 1, MeasuresTakenService.SourceAll => 2, _ => 0 };
            SearchBox.Text = _settings.Search;
            PaystubSearchBox.Text = _settings.Search;
            PaymentSearchBox.Text = _settings.Search;
            OriginBox.Text = _settings.OriginText;
            OrganizationBox.Text = _settings.Organization;
            CommanderBox.Text = _settings.CommanderName;
            CommanderRankBox.Text = _settings.CommanderRank;
            SignatureRoleBox.Text = _settings.SignatureRole;
            ApplySpedSettingsToControls();
            PaystubDefaultMeasureBox.Text = string.Empty;
            PaymentDefaultMeasureBox.Text = string.Empty;
            PaystubIndividualMeasureBox.Clear();
            PaymentIndividualMeasureBox.Clear();
            MainTabs.SelectedIndex = Math.Clamp(_settings.LastActiveTab, 0, 3);
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

    private string SelectedSource => MeasuresTakenService.NormalizeSource((SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString());
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
        UpdateMarkedStatus();
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
        var items = GetMarkedOrSelectedItems(grid, collection);
        foreach (var item in items) collection.Remove(item);
        grid.SelectedItems.Clear();
        Renumber(collection);
        ApplyFilter();
        RefreshPreview();
        UpdateMarkedStatus(items.Count == 0
            ? "Selecione ou marque um ou mais militares para remover."
            : $"{items.Count} militar(es) removido(s) da aba.");
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
        if (sender is not DataGrid grid) return;
        UpdateMarkedStatus();
        LoadMeasureTextFromSelectionForEdit(grid);
    }

    private void LoadMeasureTextFromSelectionForEdit(DataGrid grid)
    {
        var selected = grid.SelectedItems.Cast<MeasuresSelectedItem>().ToList();
        if (selected.Count == 0 && grid.SelectedItem is MeasuresSelectedItem current) selected.Add(current);

        if (selected.Count == 1)
        {
            LoadMeasureTextForEdit(selected[0], grid);
            return;
        }

        if (selected.Count <= 1) return;

        var filledMeasures = selected
            .Select(x => NormalizeMeasureDraft(x.IndividualMeasure))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        if (filledMeasures.Count == 1)
        {
            SetMeasureBoxText(grid, filledMeasures[0]);
            StatusText.Text = $"{selected.Count} militar(es) selecionado(s) com a mesma medida. Texto carregado para edição.";
        }
        else
        {
            StatusText.Text = $"{selected.Count} militar(es) selecionado(s). Como as medidas são diferentes, o texto atual foi mantido.";
        }
    }

    private void LoadMeasureTextForEdit(MeasuresSelectedItem item, DataGrid grid)
    {
        var box = MeasureBoxForGrid(grid);
        var measure = NormalizeMeasureDraft(item.IndividualMeasure);

        if (!string.IsNullOrWhiteSpace(measure))
        {
            SetMeasureBoxText(grid, measure);
            var markedCount = MarkedCountForGrid(grid);
            StatusText.Text = markedCount > 0 && !item.IsMeasureMarked
                ? $"Medida de {DisplayName(item)} carregada. Atenção: há {markedCount} militar(es) marcado(s); Aplicar vai alterar os marcados. Aperte ESC para limpar e alterar só este."
                : $"Medida de {DisplayName(item)} carregada. Altere o texto e clique em Aplicar aos marcados/selecionados.";
            return;
        }

        if (string.IsNullOrWhiteSpace(box.Text))
        {
            SetMeasureBoxText(grid, string.Empty);
            StatusText.Text = $"{DisplayName(item)} ainda está sem medida. Digite a medida e aplique.";
        }
        else
        {
            StatusText.Text = $"{DisplayName(item)} ainda está sem medida. Mantive o texto que você já estava digitando para não apagar sem querer.";
        }
    }

    private TextBox MeasureBoxForGrid(DataGrid grid)
        => ReferenceEquals(grid, PaystubSelectedGrid) ? PaystubIndividualMeasureBox : PaymentIndividualMeasureBox;

    private void SetMeasureBoxText(DataGrid grid, string text)
    {
        var box = MeasureBoxForGrid(grid);
        _loadingIndividual = true;
        box.Text = text;
        box.CaretIndex = box.Text.Length;
        _loadingIndividual = false;
    }

    private static string DisplayName(MeasuresSelectedItem item)
    {
        var rank = item.Rank?.Trim() ?? string.Empty;
        var warName = item.WarName?.Trim();
        var name = !string.IsNullOrWhiteSpace(warName) ? warName : item.Name?.Trim();
        return string.IsNullOrWhiteSpace(rank) ? (name ?? "militar") : $"{rank} {name}".Trim();
    }

    private void DefaultMeasureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Mantido apenas para compatibilidade com trabalhos antigos. A tela nova não aplica medida padrão automaticamente.
    }

    private void IndividualMeasureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingIndividual || _initializing || !IsLoaded) return;
        StatusText.Text = "Texto pronto. Marque o(s) militar(es) e clique em Aplicar aos selecionados.";
    }

    private void ApplyIndividualToSelected_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveSelectedGrid;
        var items = GetMarkedOrSelectedItems(grid, ActiveSelected);
        if (items.Count == 0)
        {
            SigfurDialog.Show(this, "Marque um ou mais militares pela caixinha à esquerda antes de aplicar a medida. A marcação fica fixa até você desmarcar ou apertar ESC.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var measure = NormalizeMeasureDraft(ActiveIndividualBox.Text);
        if (string.IsNullOrWhiteSpace(measure))
        {
            SigfurDialog.Show(this, "Digite a medida tomada antes de aplicar. Para apagar medida já preenchida, use o botão Limpar medida.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var item in items) item.IndividualMeasure = measure;
        ActiveSelectedGrid.Items.Refresh();
        RefreshPreview();
        StatusText.Text = $"Medida aplicada a {items.Count} militar(es). Eles ficaram verdes; as marcações continuam até ESC/desmarcar.";
    }

    private void ClearIndividual_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveSelectedGrid;
        var items = GetMarkedOrSelectedItems(grid, ActiveSelected);
        if (items.Count == 0)
        {
            SigfurDialog.Show(this, "Marque um ou mais militares pela caixinha à esquerda para limpar a medida.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var item in items) item.IndividualMeasure = string.Empty;
        ActiveSelectedGrid.Items.Refresh();
        RefreshPreview();
        StatusText.Text = $"Medida removida de {items.Count} militar(es). O texto digitado foi mantido; ESC limpa as marcações.";
    }


    private List<MeasuresSelectedItem> GetMarkedOrSelectedItems(DataGrid grid, ObservableCollection<MeasuresSelectedItem> collection)
    {
        var marked = collection.Where(x => x.IsMeasureMarked).ToList();
        if (marked.Count > 0) return marked;

        var selected = grid.SelectedItems.Cast<MeasuresSelectedItem>().ToList();
        if (selected.Count == 0 && grid.SelectedItem is MeasuresSelectedItem current) selected.Add(current);
        return selected.Distinct().ToList();
    }

    private int ActiveMarkedCount => ActiveSelected.Count(x => x.IsMeasureMarked);

    private int MarkedCountForGrid(DataGrid grid)
        => ReferenceEquals(grid, PaystubSelectedGrid)
            ? _paystubSelected.Count(x => x.IsMeasureMarked)
            : _paymentSelected.Count(x => x.IsMeasureMarked);

    private int TotalMarkedCount => _paystubSelected.Count(x => x.IsMeasureMarked) + _paymentSelected.Count(x => x.IsMeasureMarked);

    private void UpdateMarkedStatus(string? message = null)
    {
        if (!IsLoaded || _initializing) return;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
            return;
        }

        var count = ActiveMarkedCount;
        if (count > 0)
            StatusText.Text = $"{count} militar(es) marcado(s) nesta aba. Escreva a medida e clique em Aplicar aos marcados. ESC limpa tudo.";
    }

    private void ClearMeasureMarks(bool showStatus)
    {
        var total = TotalMarkedCount;
        foreach (var item in _paystubSelected) item.IsMeasureMarked = false;
        foreach (var item in _paymentSelected) item.IsMeasureMarked = false;
        PaystubSelectedGrid.SelectedItems.Clear();
        PaymentSelectedGrid.SelectedItems.Clear();
        PaystubSelectedGrid.Items.Refresh();
        PaymentSelectedGrid.Items.Refresh();
        if (showStatus) StatusText.Text = total > 0 ? $"Marcação limpa: {total} militar(es)." : "Nenhuma marcação ativa.";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        ClearMeasureMarks(showStatus: true);
        e.Handled = true;
    }

    private static string NormalizeMeasureDraft(string value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private async void ImproveMeasureWithAssistant_Click(object sender, RoutedEventArgs e)
    {
        var source = NormalizeMeasureDraft(ActiveIndividualBox.Text);
        if (string.IsNullOrWhiteSpace(source))
        {
            SigfurDialog.Show(this, "Digite a medida tomada antes de pedir melhoria ao Assistente SIGFUR.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Assistente SIGFUR revisando a medida...");
        ActiveIndividualBox.IsEnabled = false;
        string? warning = null;
        var usedOnlineAi = true;
        try
        {
            var settings = await App.AssistantStorage.LoadSettingsAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var revised = await App.Assistant.RewriteTextAsync(
                source,
                "Reescreva como medida tomada em linguagem administrativa militar, formal, clara e objetiva. Preserve rigorosamente fatos, nomes, datas, números, siglas, referências legais, valores e sentido original. Retorne somente o texto final da medida tomada, sem título, sem explicação e sem aspas.",
                settings,
                timeout.Token);

            revised = NormalizeMeasureDraft(revised);
            var accepted = ShowAssistantMeasureReviewDialog(source, revised, "Análise online concluída. Confira a redação sugerida antes de aplicar aos militares marcados.");
            ApplyAssistantMeasureReviewResult(accepted, online: true);
        }
        catch (Exception ex)
        {
            usedOnlineAi = false;
            warning = BuildAssistantMeasureWarning(ex);
            await App.Log.WriteAsync("Falha ao melhorar medida tomada com IA online. Aplicando revisão local segura.", ex);

            var revised = BuildLocalMeasureSuggestion(source);
            var accepted = ShowAssistantMeasureReviewDialog(
                source,
                revised,
                "A IA online não respondeu agora. O SIGFUR fez uma revisão local para não travar a tela. Quando a conexão SSL/API voltar, a melhoria online continua funcionando.",
                warning);
            ApplyAssistantMeasureReviewResult(accepted, online: false);
        }
        finally
        {
            ActiveIndividualBox.IsEnabled = true;
            ActiveIndividualBox.Focus();
            SetBusy(false);
            if (!usedOnlineAi && !string.IsNullOrWhiteSpace(warning))
                StatusText.Text = "IA online indisponível; revisão local exibida. Detalhe: " + warning;
        }
    }

    private void ApplyAssistantMeasureReviewResult(string? accepted, bool online)
    {
        if (!string.IsNullOrWhiteSpace(accepted))
        {
            _loadingIndividual = true;
            ActiveIndividualBox.Text = NormalizeMeasureDraft(accepted);
            ActiveIndividualBox.CaretIndex = ActiveIndividualBox.Text.Length;
            _loadingIndividual = false;
            StatusText.Text = online
                ? "Texto melhorado pelo Assistente SIGFUR. Revise e aplique aos militares marcados."
                : "Texto revisado localmente. Revise e aplique aos militares marcados.";
        }
        else
        {
            StatusText.Text = "Texto original mantido.";
        }
    }

    private static string BuildAssistantMeasureWarning(Exception ex)
    {
        if (ex is OperationCanceledException or TaskCanceledException)
            return "tempo limite de 45 segundos atingido.";
        if (ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message))
            return ex.Message;
        var message = OpenAiAssistantService.FriendlyNetworkMessage(ex);
        return message.Length <= 320 ? message : message[..320] + "...";
    }

    private static string BuildLocalMeasureSuggestion(string source)
    {
        var text = NormalizeMeasureDraft(source);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sippes"] = "SIPPES",
            ["sisbol"] = "SISBOL",
            ["cpex"] = "CPEx",
            ["om"] = "OM",
            ["ok"] = "regularizado",
            ["arrumado"] = "regularizado",
            ["arrumada"] = "regularizada",
            ["corrigido"] = "retificado",
            ["corrigida"] = "retificada",
            ["mandado"] = "encaminhado",
            ["mandada"] = "encaminhada",
            ["feito"] = "realizado",
            ["feita"] = "realizada"
        };

        foreach (var pair in replacements)
            text = Regex.Replace(text, $"\b{Regex.Escape(pair.Key)}\b", pair.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"\s+([,.;:])", "$1");
        text = UppercaseFirst(text);
        if (!Regex.IsMatch(text, @"[.!?]$")) text += ".";

        return text;
    }

    private static string UppercaseFirst(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return char.ToUpper(text[0], CultureInfo.GetCultureInfo("pt-BR")) + (text.Length > 1 ? text[1..] : string.Empty);
    }

    private string? ShowAssistantMeasureReviewDialog(string original, string revised, string assistantNote, string? warning = null)
    {
        var dialog = new Window
        {
            Title = "Assistente SIGFUR — revisão inteligente da medida",
            Owner = this,
            Width = 1080,
            Height = 700,
            MinWidth = 860,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Icon = Icon
        };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Assistente SIGFUR analisou sua medida tomada",
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var noteText = assistantNote + Environment.NewLine + Environment.NewLine + BuildChangeSample(original, revised);
        if (!string.IsNullOrWhiteSpace(warning)) noteText += Environment.NewLine + Environment.NewLine + "Aviso técnico: " + warning;
        var sample = new TextBlock
        {
            Text = noteText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DarkSlateGray,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(sample, 1);
        root.Children.Add(sample);

        var compare = new Grid();
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        compare.RowDefinitions.Add(new RowDefinition());

        compare.Children.Add(new TextBlock { Text = "Texto original", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var rightLabel = new TextBlock { Text = "Texto sugerido / editável", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetColumn(rightLabel, 2);
        compare.Children.Add(rightLabel);

        var originalBox = new TextBox
        {
            Text = original,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(originalBox, 1);
        compare.Children.Add(originalBox);

        var revisedBox = new TextBox
        {
            Text = revised,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            SpellCheck = { IsEnabled = true }
        };
        Grid.SetRow(revisedBox, 1);
        Grid.SetColumn(revisedBox, 2);
        compare.Children.Add(revisedBox);

        Grid.SetRow(compare, 2);
        root.Children.Add(compare);

        var selectedText = (string?)null;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var keepOriginal = StyledButton("Manter original", "GhostButtonStyle", new Thickness(0, 0, 8, 0));
        var useSuggested = StyledButton("Usar esta versão", "PrimaryButtonStyle", new Thickness());
        keepOriginal.Click += (_, _) => dialog.DialogResult = false;
        useSuggested.Click += (_, _) =>
        {
            selectedText = revisedBox.Text;
            dialog.DialogResult = true;
        };
        bar.Children.Add(keepOriginal);
        bar.Children.Add(useSuggested);
        Grid.SetRow(bar, 3);
        root.Children.Add(bar);

        dialog.Content = root;
        dialog.Loaded += (_, _) => revisedBox.Focus();
        return dialog.ShowDialog() == true ? selectedText : null;
    }

    private static string BuildChangeSample(string original, string revised)
    {
        if (string.Equals(NormalizeInline(original), NormalizeInline(revised), StringComparison.Ordinal))
            return "O texto já estava claro; foram feitos apenas ajustes mínimos de padronização.";

        return "O que mudou:" + Environment.NewLine
            + "Antes: " + ShortPreview(original) + Environment.NewLine
            + "Depois: " + ShortPreview(revised);
    }

    private static string ShortPreview(string value)
    {
        var text = NormalizeInline(value);
        return text.Length <= 220 ? text : text[..220] + "...";
    }

    private static string NormalizeInline(string value)
        => string.Join(" ", (value ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private void SelectedGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        _dragSourceGrid = grid;
        _dragStart = e.GetPosition(grid);

        // A linha pode conter Run/TextElement dentro do HighlightedNameTextBlock. Por isso
        // a busca do item usa GetParentObject, sem chamar VisualTreeHelper diretamente em Run.
        if (FindRowItem(e.OriginalSource as DependencyObject) is MeasuresSelectedItem item)
        {
            grid.SelectedItem = item;
            LoadMeasureTextForEdit(item, grid);
        }
    }

    private void MeasureMarkCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MeasuresSelectedItem item) return;

        if (sender is CheckBox checkBox)
            item.IsMeasureMarked = checkBox.IsChecked == true;

        var grid = IsPaystubTab ? PaystubSelectedGrid : PaymentSelectedGrid;
        grid.SelectedItem = item;
        LoadMeasureTextForEdit(item, grid);
        UpdateMarkedStatus();
        e.Handled = true;
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
        while (current is not null)
        {
            if (current is DataGridRow row) return row.Item as MeasuresSelectedItem;
            current = GetParentObject(current);
        }

        return null;
    }

    private static DependencyObject? GetParentObject(DependencyObject current)
    {
        if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            return System.Windows.Media.VisualTreeHelper.GetParent(current)
                   ?? System.Windows.LogicalTreeHelper.GetParent(current);

        if (current is FrameworkContentElement frameworkContentElement)
            return frameworkContentElement.Parent as DependencyObject
                   ?? System.Windows.LogicalTreeHelper.GetParent(frameworkContentElement);

        if (current is FrameworkElement frameworkElement)
            return frameworkElement.Parent
                   ?? System.Windows.LogicalTreeHelper.GetParent(frameworkElement);

        return System.Windows.LogicalTreeHelper.GetParent(current);
    }

    private MeasuresDocumentData BuildData() => new()
    {
        OriginText = OriginBox.Text.Trim(),
        Organization = OrganizationBox.Text.Trim(),
        DefaultMeasure = string.Empty,
        PaymentDefaultMeasure = string.Empty,
        PaystubDefaultMeasure = string.Empty,
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
            // O assunto deve acompanhar obrigatoriamente o mês/ano lido do relatório,
            // mesmo quando o trabalho anterior possuía outro assunto salvo.
            SpedSubjectBox.Text = SuggestedSpedSubject();
            var importPeople = await _service.LoadAllPeopleAsync();
            if (MeasuresTakenService.NormalizeSource(SelectedSource) == MeasuresTakenService.SourceAll)
            {
                _allPeople = importPeople;
            }
            else if (_allPeople.Count == 0)
            {
                await ReloadPeopleAsync();
            }
            var matches = MeasuresTakenService.MatchImported(import.Entries, importPeople);
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
                    IndividualMeasure = string.Empty,
                    Order = destination.Count + 1
                });
                added++;
            }
            RenumberAll();
            ApplyFilter();
            RefreshPreview();
            if (_currentWorkId is not null) await SaveCurrentWorkAsync();
            StatusText.Text = $"PDF importado: {import.Entries.Count} ocorrência(s), {added} militar(es). Assunto definido: {SpedSubjectBox.Text}.";
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
        PaystubDefaultMeasureBox.Clear();
        PaymentDefaultMeasureBox.Clear();
        PaystubIndividualMeasureBox.Clear();
        PaymentIndividualMeasureBox.Clear();
        _measuresSpedAttachments.Clear();
        ApplySpedSettingsToControls();
        ApplyFilter();
        RefreshPreview();
        StatusText.Text = "Novo trabalho iniciado.";
    }

    private async void SaveWork_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await SaveCurrentWorkAsync()) StatusText.Text = $"Trabalho salvo: {_currentWorkName}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task<bool> SaveCurrentWorkAsync()
    {
        var name = _currentWorkName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Prompt("Nome do trabalho", "Salvar Medidas Tomadas", $"Medidas {DateTime.Now:dd-MM-yyyy HH-mm}");
            if (string.IsNullOrWhiteSpace(name)) return false;
        }
        _currentWorkId = await _service.SaveWorkAsync(name, BuildPayload(), _currentWorkId);
        _currentWorkName = name;
        _settings.LastWorkId = _currentWorkId;
        await SaveSettingsQuietlyAsync();
        return true;
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
        DefaultMeasure = string.Empty,
        PaymentDefaultMeasure = string.Empty,
        PaystubDefaultMeasure = string.Empty,
        CommanderName = CommanderBox.Text,
        CommanderRank = CommanderRankBox.Text,
        SignatureRole = SignatureRoleBox.Text,
        People = [.. _paystubSelected.Select(ToSavedPerson), .. _paymentSelected.Select(ToSavedPerson)],
        SpedSender = SpedSenderBox.Text.Trim(),
        SpedRecipient = SpedRecipientBox.Text.Trim(),
        SpedClassification = SpedClassificationBox.Text.Trim(),
        SpedFillPurpose = SpedFillPurposeCheck.IsChecked == true,
        SpedPurpose = SpedPurposeBox.Text.Trim(),
        SpedSubject = SpedSubjectBox.Text.Trim(),
        SpedContact = SpedContactBox.Text.Trim(),
        SpedRitex = SpedRitexBox.Text.Trim(),
        SpedEmail = SpedEmailBox.Text.Trim(),
        SpedAttachmentFiles = _measuresSpedAttachments.Select(x => x.FileName).ToList()
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
        PaymentDefaultMeasureBox.Clear();
        PaystubDefaultMeasureBox.Clear();
        CommanderBox.Text = payload.CommanderName;
        CommanderRankBox.Text = payload.CommanderRank;
        SignatureRoleBox.Text = payload.SignatureRole;
        SpedSenderBox.Text = string.IsNullOrWhiteSpace(payload.SpedSender) ? _settings.SpedSender : payload.SpedSender;
        SpedRecipientBox.Text = string.IsNullOrWhiteSpace(payload.SpedRecipient) ? _settings.SpedRecipient : payload.SpedRecipient;
        SpedClassificationBox.Text = payload.SpedClassification;
        SpedFillPurposeCheck.IsChecked = payload.SpedFillPurpose;
        SpedPurposeBox.Text = string.IsNullOrWhiteSpace(payload.SpedPurpose) ? "Geral" : payload.SpedPurpose;
        SpedSubjectBox.Text = string.IsNullOrWhiteSpace(payload.SpedSubject) ? SuggestedSpedSubject() : payload.SpedSubject;
        SpedContactBox.Text = payload.SpedContact;
        SpedRitexBox.Text = payload.SpedRitex;
        SpedEmailBox.Text = payload.SpedEmail;
        if (_allPeople.Count == 0) await ReloadPeopleAsync();
        _paystubSelected.Clear();
        _paymentSelected.Clear();

        foreach (var saved in payload.People.OrderBy(x => x.Section).ThenBy(x => x.Order))
        {
            var match = _allPeople.FirstOrDefault(x => x.IsTransferred == saved.IsTransferred && x.Military.Id == saved.MilitaryId)
                ?? new MeasuresMilitaryItem
                {
                    IsTransferred = saved.IsTransferred,
                    Source = saved.IsTransferred ? MeasuresTakenService.SourceLicensedTransferred : MeasuresTakenService.SourceActive,
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
        RefreshMeasuresDocuments(payload.SpedAttachmentFiles);
    }

    private static void ReorderBySavedOrder(ObservableCollection<MeasuresSelectedItem> items)
    {
        var ordered = items.OrderBy(x => x.Order).ToList();
        items.Clear();
        foreach (var item in ordered) items.Add(item);
    }

    private void ApplySpedSettingsToControls()
    {
        SpedSenderBox.Text = _settings.SpedSender;
        SpedRecipientBox.Text = _settings.SpedRecipient;
        SpedClassificationBox.Text = _settings.SpedClassification;
        SpedFillPurposeCheck.IsChecked = _settings.SpedFillPurpose;
        SpedPurposeBox.Text = string.IsNullOrWhiteSpace(_settings.SpedPurpose) ? "Geral" : _settings.SpedPurpose;
        SpedSubjectBox.Text = string.IsNullOrWhiteSpace(_settings.SpedSubject) ? SuggestedSpedSubject() : _settings.SpedSubject;
        SpedContactBox.Text = _settings.SpedContact;
        SpedRitexBox.Text = _settings.SpedRitex;
        SpedEmailBox.Text = _settings.SpedEmail;
    }

    private string SuggestedSpedSubject()
    {
        var origin = OriginBox.Text ?? string.Empty;
        var match = Regex.Match(origin, @"(?<mes>janeiro|fevereiro|março|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)[^0-9]*(?<ano>20\d{2})", RegexOptions.IgnoreCase);
        return match.Success
            ? $"resposta ao exame de pagamento - {match.Groups["mes"].Value.ToLowerInvariant()} {match.Groups["ano"].Value}"
            : "resposta ao exame de pagamento";
    }

    private string CurrentWorkDirectory
    {
        get
        {
            if (_currentWorkId is not int id) throw new InvalidOperationException("Salve o trabalho antes de adicionar ou gerar documentos.");
            var path = Path.Combine(App.Paths.MeasuresTakenProcessesDirectory, id.ToString("D6"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private void RefreshMeasuresDocuments(IEnumerable<string>? savedNames = null)
    {
        _measuresSpedAttachments.Clear();
        if (_currentWorkId is null) return;
        var directory = CurrentWorkDirectory;
        var names = (savedNames ?? Directory.EnumerateFiles(directory).Select(Path.GetFileName))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFileName(x!)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            _measuresSpedAttachments.Add(new GratificationSpedAttachment { FileName = name, FullPath = path, SizeBytes = new FileInfo(path).Length });
        }
    }

    private async void AddMeasuresDocuments_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await SaveCurrentWorkAsync()) return;
            var dialog = new OpenFileDialog { Title = "Adicionar documentos ao trabalho", Multiselect = true, Filter = "Todos os documentos|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            var existingTotal = _measuresSpedAttachments.Sum(x => x.SizeBytes);
            var incoming = dialog.FileNames.Where(File.Exists).ToList();
            var projected = existingTotal + incoming.Sum(x => new FileInfo(x).Length);
            if (projected > GratificationService.SpedAttachmentLimitBytes)
                throw new InvalidOperationException($"Os documentos selecionados ultrapassam o limite total de 10 MB do SPED ({projected / 1_000_000d:0.00} MB).");
            foreach (var source in incoming)
            {
                var destination = UniqueDestination(CurrentWorkDirectory, Path.GetFileName(source));
                File.Copy(source, destination, false);
            }
            RefreshMeasuresDocuments();
            await SaveCurrentWorkAsync();
            StatusText.Text = $"{incoming.Count} documento(s) adicionado(s) ao trabalho.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static string UniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 2; ; number++)
        {
            destination = Path.Combine(directory, $"{stem}_{number}{extension}");
            if (!File.Exists(destination)) return destination;
        }
    }

    private async void RemoveMeasuresDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GratificationSpedAttachment attachment }) return;
        try
        {
            if (File.Exists(attachment.FullPath)) File.Delete(attachment.FullPath);
            RefreshMeasuresDocuments();
            await SaveCurrentWorkAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void MeasuresSpedAttachmentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MeasuresSpedAttachmentList.SelectedItem is GratificationSpedAttachment attachment && File.Exists(attachment.FullPath))
            ShellService.OpenPath(attachment.FullPath);
    }

    private async void OpenMeasuresWorkFolder_Click(object sender, RoutedEventArgs e)
    {
        try { if (await SaveCurrentWorkAsync()) ShellService.OpenPath(CurrentWorkDirectory); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void GenerateWorkRelation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await SaveCurrentWorkAsync()) return;
            var path = Path.Combine(CurrentWorkDirectory, $"Relacao_Nominal_{DateTime.Today:yyyyMMdd}.xlsx");
            await _service.ExportPaymentRelationXlsxAsync(path, BuildData());
            RefreshMeasuresDocuments(); await SaveCurrentWorkAsync(); ShellService.OpenPath(path);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void GenerateWorkOdt_Click(object sender, RoutedEventArgs e) => await GenerateWorkMeasuresDocumentAsync("odt");
    private async void GenerateWorkPdf_Click(object sender, RoutedEventArgs e) => await GenerateWorkMeasuresDocumentAsync("pdf");

    private async Task GenerateWorkMeasuresDocumentAsync(string extension)
    {
        try
        {
            if (!await SaveCurrentWorkAsync()) return;
            var path = Path.Combine(CurrentWorkDirectory, $"Medidas_Tomadas_{DateTime.Today:yyyyMMdd}.{extension}");
            if (extension == "odt") await _service.ExportOdtAsync(path, BuildData());
            else _ = await _service.ExportPdfAsync(path, BuildData());
            RefreshMeasuresDocuments(); await SaveCurrentWorkAsync(); ShellService.OpenPath(path);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RunMeasuresSped_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await SaveCurrentWorkAsync()) return;
            if (string.IsNullOrWhiteSpace(SpedClassificationBox.Text))
            {
                SigfurDialog.Show(this, "Informe a classificação documental exatamente como aparece na pesquisa rápida do SPED.", "Medidas Tomadas", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var files = _measuresSpedAttachments.Where(x => File.Exists(x.FullPath)).Select(x => x.FullPath).ToList();
            if (files.Sum(x => new FileInfo(x).Length) > GratificationService.SpedAttachmentLimitBytes)
                throw new InvalidOperationException("Os documentos do trabalho ultrapassam o limite total de 10 MB do SPED.");
            var subject = string.IsNullOrWhiteSpace(SpedSubjectBox.Text) ? SuggestedSpedSubject() : SpedSubjectBox.Text.Trim();
            var draft = new SpedDiexDraft
            {
                Subject = subject,
                AttachmentPaths = files,
                FillDocumentPurpose = SpedFillPurposeCheck.IsChecked == true,
                BodyHtml = BuildMeasuresSpedBody()
            };
            var presets = new SpedMappingSettings
            {
                SenderSearch = SpedSenderBox.Text.Trim(),
                ExternalRecipientSearch = SpedRecipientBox.Text.Trim(),
                ClassificationSearch = SpedClassificationBox.Text.Trim(),
                DocumentPurpose = SpedPurposeBox.Text.Trim(),
                Subject = subject
            };
            var review = Path.Combine(CurrentWorkDirectory, "revisao_sped");
            Directory.CreateDirectory(review);
            var window = new SpedMappingWindow(new SpedMappingService(App.Paths), draft, review, presets) { Owner = this };
            window.ShowDialog();
            await SaveCurrentWorkAsync();
            StatusText.Text = window.AutomationResult?.Message ?? (string.IsNullOrWhiteSpace(window.LastError) ? "Automação encerrada." : window.LastError);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private string BuildMeasuresSpedBody()
    {
        static string H(string value) => WebUtility.HtmlEncode(value?.Trim() ?? string.Empty);
        var origin = H(OriginBox.Text);
        var contact = H(SpedContactBox.Text);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact)) details.Add(contact);
        if (!string.IsNullOrWhiteSpace(SpedRitexBox.Text)) details.Add($"RITEX {H(SpedRitexBox.Text)}");
        if (!string.IsNullOrWhiteSpace(SpedEmailBox.Text)) details.Add($"e-mail {H(SpedEmailBox.Text)}");
        var contactText = details.Count == 0 ? "esta Organização Militar" : string.Join(", ", details);
        return $"<p>1. Encaminho ao Senhor, conforme anexos, as medidas tomadas em atendimento à determinação contida em {origin}, do Cmdo 4ª RM.</p>" +
               "<p>2. Remeto, ainda, as medidas tomadas e a relação nominal da OM atualizada. Os documentos seguem em versão assinada e/ou editável, conforme os arquivos anexos, para facilitar os trabalhos das equipes de exame de pagamento e demais destinatários.</p>" +
               $"<p>3. Para esclarecimentos adicionais, coloco à disposição {contactText}.</p>";
    }

    private async void ExportPaymentExcel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = SaveDialog("Planilha Excel|*.xlsx", ".xlsx", "Relacao_Nominal");
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true, "Gerando a relação completa da companhia...");
        try
        {
            await _service.ExportPaymentRelationXlsxAsync(dialog.FileName, BuildData());
            _settings.OutputDirectory = Path.GetDirectoryName(dialog.FileName) ?? _settings.OutputDirectory;
            StatusText.Text = $"Relação completa da companhia gerada: {Path.GetFileName(dialog.FileName)}";
            ShellService.OpenPath(dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
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
            _settings.DefaultMeasure = string.Empty;
            _settings.PaymentDefaultMeasure = string.Empty;
            _settings.PaystubDefaultMeasure = string.Empty;
            _settings.LastActiveTab = Math.Clamp(MainTabs.SelectedIndex, 0, 3);
            _settings.LastWorkId = _currentWorkId;
            _settings.SpedSender = SpedSenderBox.Text.Trim();
            _settings.SpedRecipient = SpedRecipientBox.Text.Trim();
            _settings.SpedClassification = SpedClassificationBox.Text.Trim();
            _settings.SpedFillPurpose = SpedFillPurposeCheck.IsChecked == true;
            _settings.SpedPurpose = SpedPurposeBox.Text.Trim();
            _settings.SpedSubject = SpedSubjectBox.Text.Trim();
            _settings.SpedContact = SpedContactBox.Text.Trim();
            _settings.SpedRitex = SpedRitexBox.Text.Trim();
            _settings.SpedEmail = SpedEmailBox.Text.Trim();
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
