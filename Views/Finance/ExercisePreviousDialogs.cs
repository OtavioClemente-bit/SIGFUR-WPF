using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

internal sealed class ExercisePreviousIpcaWindow : Window
{
    private readonly ExercisePreviousRepository _repository;
    private readonly ExercisePreviousExcelService _excel;
    private readonly ObservableCollection<ExercisePreviousIpcaRow> _rows = [];
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _competence = new() { Width = 110 };
    private readonly TextBox _percentage = new() { Width = 110 };
    private readonly TextBox _factor = new() { Width = 140 };
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };

    public ExercisePreviousIpcaWindow(ExercisePreviousRepository repository, ExercisePreviousExcelService excel)
    {
        _repository = repository;
        _excel = excel;
        Title = "IPCA-E — Exercício Anterior";
        Width = 820; Height = 650; MinWidth = 700; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "Índices IPCA-E", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Importe os fatores da aba “Cálculo do Acumulado” do XLSM ou corrija uma competência manualmente.", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var actions = new WrapPanel();
        AddButton(actions, "Importar planilha IPCA-E...", ImportAsync, true);
        AddButton(actions, "Recarregar", ReloadAsync);
        AddButton(actions, "Excluir selecionado", DeleteAsync);
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var form = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        form.Children.Add(Label("Competência (AAAA-MM)", _competence));
        form.Children.Add(Label("Percentual", _percentage));
        form.Children.Add(Label("Fator", _factor));
        AddButton(form, "Adicionar / atualizar", UpsertAsync, true);
        footer.Children.Add(form);
        footer.Children.Add(_status);
        var close = new Button { Content = "Fechar", Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close(); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        _grid.ItemsSource = _rows;
        _grid.Columns.Add(new DataGridTextColumn { Header = "Competência", Binding = new Binding(nameof(ExercisePreviousIpcaRow.Competence)), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Percentual", Binding = new Binding(nameof(ExercisePreviousIpcaRow.Percentage)) { StringFormat = "N8" }, Width = 180 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Fator", Binding = new Binding(nameof(ExercisePreviousIpcaRow.Factor)) { StringFormat = "N12" }, Width = 200 });
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not ExercisePreviousIpcaRow row) return;
            _competence.Text = row.Competence;
            _percentage.Text = row.Percentage?.ToString("G17", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;
            _factor.Text = row.Factor.ToString("G17", CultureInfo.GetCultureInfo("pt-BR"));
        };
        root.Children.Add(_grid);
        return root;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var list = await _repository.ListIpcaAsync();
            _rows.Clear(); foreach (var row in list) _rows.Add(row);
            _status.Text = list.Count == 0 ? "Nenhum índice importado." : $"{list.Count} competência(s). Última: {list[^1].Competence}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione a planilha IPCA-E",
            Filter = "Planilhas Excel|*.xlsm;*.xlsx|Excel com macros (*.xlsm)|*.xlsm|Excel (*.xlsx)|*.xlsx|Todos os arquivos|*.*"
        };
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) dialog.InitialDirectory = downloads;
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IsEnabled = false; _status.Text = "Lendo a planilha IPCA-E pelo Microsoft Excel...";
            var count = await _excel.ImportIpcaFromWorkbookAsync(dialog.FileName);
            await ReloadAsync();
            _status.Text = $"{count} competência(s) importadas/atualizadas de {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsEnabled = true; }
    }

    private async Task UpsertAsync()
    {
        try
        {
            var competence = _competence.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(competence, @"^\d{4}-(0[1-9]|1[0-2])$"))
                throw new InvalidOperationException("Informe a competência no formato AAAA-MM.");
            if (!TryNumber(_factor.Text, out var factor) || factor <= 0)
                throw new InvalidOperationException("Informe um fator numérico maior que zero.");
            double? percentage = null;
            if (!string.IsNullOrWhiteSpace(_percentage.Text))
            {
                if (!TryNumber(_percentage.Text, out var parsed)) throw new InvalidOperationException("Percentual inválido.");
                percentage = parsed;
            }
            await _repository.UpsertIpcaAsync(competence, percentage, factor);
            await ReloadAsync();
            _status.Text = $"Competência {competence} salva.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task DeleteAsync()
    {
        if (_grid.SelectedItem is not ExercisePreviousIpcaRow row) return;
        if (SigfurDialog.Show(this, $"Excluir o fator IPCA-E de {row.Competence}?", "Excluir IPCA-E", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _repository.DeleteIpcaAsync(row.Competence);
        await ReloadAsync();
    }

    private void ShowError(Exception ex)
    {
        _status.Text = "Erro: " + ex.Message;
        SigfurDialog.Show(this, ex.Message, "IPCA-E", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool TryNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out value)
           || double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    private static FrameworkElement Label(string text, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 5) };
        panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray });
        control.MinHeight = 30; panel.Children.Add(control); return panel;
    }
    private static void AddButton(Panel panel, string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(3), MinHeight = 32 };
        if (primary) { button.Background = new SolidColorBrush(Color.FromRgb(30, 91, 158)); button.Foreground = Brushes.White; }
        button.Click += async (_, _) => await action(); panel.Children.Add(button);
    }
}


internal sealed class ExercisePreviousEntryEditorWindow : Window
{
    private readonly ComboBox _codeBox = new() { MinWidth = 540, MinHeight = 38, IsTextSearchEnabled = true, FontSize = 13 };
    private readonly ComboBox _yearBox = new() { Width = 155, MinHeight = 38, IsEditable = true, IsTextSearchEnabled = true, FontSize = 13 };
    private readonly ComboBox _monthBox = new() { Width = 215, MinHeight = 38, IsTextSearchEnabled = true, FontSize = 13 };
    private readonly TextBox _receivedBox = new() { Width = 215, MinHeight = 40, FontSize = 14, Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _dueBox = new() { Width = 215, MinHeight = 40, FontSize = 14, Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _codeDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _netText = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _correctedHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly IReadOnlyList<CodeOption> _codes;

    public ExercisePreviousEntry Entry { get; private set; }

    public ExercisePreviousEntryEditorWindow(
        IEnumerable<ExercisePreviousCode> codes,
        ExercisePreviousEntry? source = null,
        string title = "Novo lançamento")
    {
        _codes = codes.OrderBy(x => x.Order)
            .Select(x => new CodeOption(x.Order, x.Description, x.Type))
            .ToList();
        if (_codes.Count == 0)
            _codes = Enumerable.Range(1, 17).Select(x => new CodeOption(x, string.Empty, "-")).ToList();

        Entry = new ExercisePreviousEntry
        {
            Id = source?.Id ?? 0,
            ProcessId = source?.ProcessId ?? 0,
            Year = source?.Year ?? DateTime.Today.Year,
            Month = source?.Month ?? DateTime.Today.Month,
            CodeOrder = source?.CodeOrder ?? _codes[0].Order,
            Received = source?.Received ?? 0m,
            Due = source?.Due ?? 0m,
            Factor = source?.Factor ?? 1m
        };

        Title = title;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        Width = 820;
        Height = 670;
        MinWidth = 740;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        App.UiState.Attach(this);
        Content = BuildUi();
        LoadEntry();
    }

    private UIElement BuildUi()
    {
        var root = new Grid { Margin = new Thickness(26, 24, 26, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(2, 0, 2, 20) };
        var title = new TextBlock { Text = "Lançamento financeiro", FontSize = 24, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        header.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "Escolha a competência e o código pelo nome. Os valores serão levados para as abas Recebido e Devido do XLSM.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        header.Children.Add(subtitle);
        root.Children.Add(header);

        var formCard = new Border { Padding = new Thickness(22) };
        ApplyStyle(formCard, "CardStyle");
        Grid.SetRow(formCard, 1);
        root.Children.Add(formCard);

        var form = new StackPanel { Margin = new Thickness(1, 1, 1, 2) };
        formCard.Child = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _codeBox.ItemsSource = _codes;
        _codeBox.DisplayMemberPath = nameof(CodeOption.Display);
        _codeBox.SelectionChanged += (_, _) => RefreshCodeDetails();
        form.Children.Add(Labeled("Código / rubrica", _codeBox));

        var codeCard = new Border { Padding = new Thickness(14), Margin = new Thickness(0, 9, 0, 20), CornerRadius = new CornerRadius(10) };
        codeCard.SetResourceReference(Border.BackgroundProperty, "PrimarySurfaceBrush");
        codeCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        codeCard.BorderThickness = new Thickness(1);
        codeCard.Child = _codeDetails;
        form.Children.Add(codeCard);

        var competence = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        competence.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        competence.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        competence.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        var yearField = Labeled("Ano", _yearBox);
        var monthField = Labeled("Mês", _monthBox);
        Grid.SetColumn(monthField, 2);
        competence.Children.Add(yearField);
        competence.Children.Add(monthField);
        form.Children.Add(competence);

        var values = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var receivedField = Labeled("Valor recebido", _receivedBox, "Informe 0,00 quando não houve pagamento na competência.");
        var dueField = Labeled("Valor devido", _dueBox, "Informe o valor correto que deveria ter sido pago.");
        Grid.SetColumn(dueField, 2);
        values.Children.Add(receivedField);
        values.Children.Add(dueField);
        form.Children.Add(values);

        _receivedBox.TextChanged += (_, _) => RefreshNet();
        _dueBox.TextChanged += (_, _) => RefreshNet();

        var resultCard = new Border { Padding = new Thickness(18), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(12) };
        resultCard.SetResourceReference(Border.BackgroundProperty, "SuccessSoftBrush");
        resultCard.SetResourceReference(Border.BorderBrushProperty, "SuccessBrush");
        resultCard.BorderThickness = new Thickness(1);
        var resultGrid = new Grid();
        resultGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resultText = new StackPanel();
        var resultTitle = new TextBlock { Text = "Líquido da competência", FontWeight = FontWeights.SemiBold };
        resultTitle.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        resultText.Children.Add(resultTitle);
        _correctedHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        resultText.Children.Add(_correctedHint);
        resultGrid.Children.Add(resultText);
        _netText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        _netText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_netText, 1);
        resultGrid.Children.Add(_netText);
        resultCard.Child = resultGrid;
        form.Children.Add(resultCard);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "Cancelar", Margin = new Thickness(0, 0, 8, 0) };
        ApplyStyle(cancel, "GhostButtonStyle");
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = Title.StartsWith("Editar", StringComparison.OrdinalIgnoreCase) ? "Salvar alterações" : "Incluir lançamento" };
        ApplyStyle(save, "PrimaryButtonStyle");
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        return root;
    }

    private void LoadEntry()
    {
        _yearBox.ItemsSource = Enumerable.Range(2006, Math.Max(1, DateTime.Today.Year + 2 - 2006)).Reverse().ToArray();
        _yearBox.Text = Entry.Year.ToString(CultureInfo.InvariantCulture);
        _monthBox.ItemsSource = Enumerable.Range(1, 12).Select(x => new MonthOption(x)).ToArray();
        _monthBox.DisplayMemberPath = nameof(MonthOption.Display);
        _monthBox.SelectedItem = _monthBox.Items.OfType<MonthOption>().FirstOrDefault(x => x.Number == Entry.Month);
        _codeBox.SelectedItem = _codes.FirstOrDefault(x => x.Order == Entry.CodeOrder) ?? _codes[0];
        _receivedBox.Text = Entry.Received.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
        _dueBox.Text = Entry.Due.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
        RefreshCodeDetails();
        RefreshNet();
    }

    private void RefreshCodeDetails()
    {
        if (_codeBox.SelectedItem is not CodeOption code)
        {
            _codeDetails.Text = "Selecione um código.";
            return;
        }
        var description = string.IsNullOrWhiteSpace(code.Description) ? "Sem descrição cadastrada" : code.Description;
        _codeDetails.Text = $"Código {code.Order:00} • {code.Type}\n{description}";
    }

    private void RefreshNet()
    {
        var received = TryMoney(_receivedBox.Text, out var rec) ? rec : 0m;
        var due = TryMoney(_dueBox.Text, out var dev) ? dev : 0m;
        var net = due - received;
        _netText.Text = net.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
        _correctedHint.Text = "O fator IPCA-E será aplicado automaticamente conforme a competência ao salvar e recalcular.";
    }

    private void Save()
    {
        if (_codeBox.SelectedItem is not CodeOption code)
        {
            ShowValidation("Escolha o código do lançamento.");
            return;
        }
        if (!int.TryParse(_yearBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) || year is < 2006 or > 2100)
        {
            ShowValidation("Informe um ano válido entre 2006 e 2100.");
            return;
        }
        if (_monthBox.SelectedItem is not MonthOption month)
        {
            ShowValidation("Escolha o mês da competência.");
            return;
        }
        if (!TryMoney(_receivedBox.Text, out var received) || received < 0m)
        {
            ShowValidation("Informe um valor recebido válido, igual ou maior que zero.");
            return;
        }
        if (!TryMoney(_dueBox.Text, out var due) || due < 0m)
        {
            ShowValidation("Informe um valor devido válido, igual ou maior que zero.");
            return;
        }

        Entry.CodeOrder = code.Order;
        Entry.Year = year;
        Entry.Month = month.Number;
        Entry.Received = received;
        Entry.Due = due;
        DialogResult = true;
    }

    private void ShowValidation(string message)
        => SigfurDialog.Show(this, message, "Lançamento EA", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool TryMoney(string? text, out decimal value)
    {
        var cleaned = (text ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value)
               || decimal.TryParse(cleaned.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static FrameworkElement Labeled(string title, Control control, string? hint = null)
    {
        var panel = new StackPanel();
        var label = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(label);
        panel.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var hintText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 11 };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(hintText);
        }
        return panel;
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        if (Application.Current.TryFindResource(key) is Style style) element.Style = style;
    }

    private sealed class CodeOption
    {
        public CodeOption(int order, string? description, string? type)
        {
            Order = order;
            Description = description?.Trim() ?? string.Empty;
            Type = string.IsNullOrWhiteSpace(type) ? "-" : type.Trim();
        }
        public int Order { get; }
        public string Description { get; }
        public string Type { get; }
        public string Display => $"{Order:00} — {(string.IsNullOrWhiteSpace(Description) ? "Sem descrição" : Description)}  [{Type}]";
    }

    private sealed class MonthOption
    {
        private static readonly string[] Names =
        [
            "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho",
            "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro"
        ];
        public MonthOption(int number) => Number = number;
        public int Number { get; }
        public string Display => $"{Number:00} — {Names[Number - 1]}";
    }
}

internal sealed class ExercisePreviousProtocolWindow : Window
{
    private readonly ExercisePreviousProcess _process;
    private readonly ExercisePreviousProtocolService _service;
    private readonly Dictionary<string, (DateTime LastWrite, long Size)> _snapshot;
    private readonly DateTime _openedUtc = DateTime.UtcNow;
    private readonly TextBox _protocol = new();
    private readonly TextBox _date = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _pdf = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.DarkGreen, TextWrapping = TextWrapping.Wrap };
    private readonly Button _waitButton = new();

    public ExercisePreviousProtocolWindow(ExercisePreviousProcess process, ExercisePreviousProtocolService service)
    {
        _process = process; _service = service; _snapshot = service.SnapshotPdfs();
        Title = "Registrar protocolo CPEx"; Width = 760; Height = 520; MinWidth = 650; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _protocol.Text = process.CpexProtocol; _date.Text = string.IsNullOrWhiteSpace(process.CpexProtocolledAt) ? DateTime.Today.ToString("yyyy-MM-dd") : process.CpexProtocolledAt;
        _notes.Text = process.CpexNotes; _pdf.Text = process.CpexPrintPage;
        Content = BuildUi();
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(new TextBlock { Text = "Registrar protocolo gerado no site do CPEx", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"{_process.Rank} {_process.FullName} — CPF {_process.Cpf} — PREC-CP {_process.PrecCp}", Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(4) }; cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Salvar e marcar OK", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(4), Background = new SolidColorBrush(Color.FromRgb(30, 91, 158)), Foreground = Brushes.White };
        save.Click += (_, _) => Save(); buttons.Children.Add(cancel); buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var form = new StackPanel();
        form.Children.Add(Labeled("Protocolo CPEx", _protocol));
        form.Children.Add(Labeled("Data do protocolo (AAAA-MM-DD ou DD/MM/AAAA)", _date));
        form.Children.Add(Labeled("Observação", _notes));
        form.Children.Add(Labeled("PDF da página/protocolo", _pdf));
        var pdfButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        AddButton(pdfButtons, "Escolher PDF...", ChoosePdfAsync);
        _waitButton.Content = "Aguardar próximo PDF baixado"; _waitButton.Padding = new Thickness(10, 5, 10, 5); _waitButton.Margin = new Thickness(3); _waitButton.Click += async (_, _) => await WaitPdfAsync(); pdfButtons.Children.Add(_waitButton);
        AddButton(pdfButtons, "Capturar PDF mais recente", CaptureLatestAsync);
        AddButton(pdfButtons, "Ler protocolo do PDF", ReadProtocolAsync);
        AddButton(pdfButtons, "Abrir PDF", OpenPdfAsync);
        form.Children.Add(pdfButtons); form.Children.Add(_status);
        form.Children.Add(new TextBlock { Text = "Nada será enviado automaticamente. O botão apenas registra o protocolo e arquiva uma cópia do PDF no AppData do SIGFUR.", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private Task ChoosePdfAsync()
    {
        var dialog = new OpenFileDialog { Title = "Escolha o PDF baixado da CPEx", Filter = "PDF (*.pdf)|*.pdf|Todos os arquivos (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) ProcessPdf(dialog.FileName);
        return Task.CompletedTask;
    }

    private async Task WaitPdfAsync()
    {
        try
        {
            _waitButton.IsEnabled = false; _status.Text = "Aguardando o próximo PDF baixado pelo navegador (até 3 minutos)...";
            var found = await _service.WaitForNextPdfAsync(_snapshot, TimeSpan.FromMinutes(3), _openedUtc.AddSeconds(-5));
            if (found is null) { _status.Text = "Nenhum PDF novo foi encontrado em até 3 minutos."; return; }
            ProcessPdf(found);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _waitButton.IsEnabled = true; }
    }

    private async Task CaptureLatestAsync()
    {
        try
        {
            var found = await _service.FindLatestReadyPdfAsync(_openedUtc.AddSeconds(-5));
            if (found is null) throw new InvalidOperationException("Nenhum PDF recente e pronto foi encontrado nas pastas de download.");
            ProcessPdf(found);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task ReadProtocolAsync()
    {
        try
        {
            if (!File.Exists(_pdf.Text)) throw new FileNotFoundException("Escolha ou capture um PDF primeiro.", _pdf.Text);
            var protocol = _service.ExtractProtocolFromPdf(_pdf.Text);
            if (string.IsNullOrWhiteSpace(protocol)) throw new InvalidOperationException("Não encontrei o número do protocolo no texto interno do PDF. Digite-o manualmente.");
            _protocol.Text = protocol; _status.Text = "Protocolo lido do PDF: " + protocol;
        }
        catch (Exception ex) { ShowError(ex); }
        return Task.CompletedTask;
    }

    private Task OpenPdfAsync()
    {
        try
        {
            if (!File.Exists(_pdf.Text)) throw new FileNotFoundException("PDF não encontrado.", _pdf.Text);
            Process.Start(new ProcessStartInfo(_pdf.Text) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
        return Task.CompletedTask;
    }

    private void ProcessPdf(string source)
    {
        var extracted = _service.ExtractProtocolFromPdf(source);
        if (!string.IsNullOrWhiteSpace(extracted)) _protocol.Text = extracted;
        var fullSource = Path.GetFullPath(source);
        var protocolRoot = Path.GetFullPath(_service.ProtocolDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var archived = fullSource.StartsWith(protocolRoot, StringComparison.OrdinalIgnoreCase)
            ? fullSource : _service.ArchivePdf(fullSource, _process, _protocol.Text);
        _pdf.Text = archived;
        _status.Text = !string.IsNullOrWhiteSpace(extracted)
            ? $"PDF arquivado: {Path.GetFileName(archived)} — protocolo lido: {extracted}"
            : $"PDF arquivado: {Path.GetFileName(archived)}";
    }

    private void Save()
    {
        var protocol = _protocol.Text.Trim();
        if (string.IsNullOrWhiteSpace(protocol) && File.Exists(_pdf.Text)) protocol = _service.ExtractProtocolFromPdf(_pdf.Text);
        if (string.IsNullOrWhiteSpace(protocol))
        {
            SigfurDialog.Show(this, "Informe o protocolo gerado no site ou escolha um PDF que contenha o número.", "Protocolo CPEx", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_pdf.Text) && SigfurDialog.Show(this, "Nenhum PDF foi vinculado. Salvar mesmo assim?", "PDF CPEx", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _process.CpexProtocol = protocol;
        _process.CpexPrintPage = _pdf.Text.Trim();
        _process.CpexProtocolledAt = _date.Text.Trim();
        _process.CpexStatus = "OK";
        _process.CpexNotes = _notes.Text.Trim();
        DialogResult = true;
    }

    private void ShowError(Exception ex)
    {
        _status.Text = "Erro: " + ex.Message;
        SigfurDialog.Show(this, ex.Message, "Protocolo CPEx", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private static FrameworkElement Labeled(string title, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) });
        box.MinHeight = 31; box.Padding = new Thickness(7, 4, 7, 4); panel.Children.Add(box); return panel;
    }
    private static void AddButton(Panel panel, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(3) };
        button.Click += async (_, _) => await action(); panel.Children.Add(button);
    }
}

internal sealed class ExercisePreviousBulletinWindow : Window
{
    private readonly ExercisePreviousProcess _process;
    private readonly ExercisePreviousProtocolService _service;
    private readonly TextBox _text;

    public ExercisePreviousBulletinWindow(ExercisePreviousProcess process, ExercisePreviousProtocolService service, string text)
    {
        _process = process; _service = service;
        Title = "Boletim EA CPEx — Prévia"; Width = 980; Height = 680; MinWidth = 760; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        var title = new TextBlock { Text = "Prévia do boletim — Exercícios Anteriores", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        _text = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Padding = new Thickness(10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var copy = Button("Copiar texto"); copy.Click += (_, _) =>
        {
            var render = new BulletinRenderResult
            {
                Text = _text.Text,
                BoldRanges = BulletinTextFormatter.FindWarNameRanges(
                    _text.Text,
                    new[] { (_process.FullName, _process.WarName) }),
                UnresolvedTokens = []
            };
            var document = new BulletinService(App.Paths, App.Json, App.Log).BuildDocument(render);
            BulletinService.CopyForWord(document, render.Text);
            SigfurDialog.Show(this, "Texto copiado com o nome de guerra em negrito.", "Boletim EA", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        var save = Button("Salvar .txt"); save.Click += (_, _) => { var path = _service.SaveBulletinText(_process, _text.Text); SigfurDialog.Show(this, "Arquivo salvo:\n" + path, "Boletim EA", MessageBoxButton.OK, MessageBoxImage.Information); };
        var close = Button("Fechar"); close.Click += (_, _) => Close(); buttons.Children.Add(copy); buttons.Children.Add(save); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(_text); Content = root;
    }
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4) };
}
