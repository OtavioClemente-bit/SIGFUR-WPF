using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Finance;

/// <summary>
/// Consulta nativa da tabela do Anexo VI da Lei nº 15.167/2025.
/// O WebBrowser antigo usava o motor do Internet Explorer e era bloqueado pelo
/// portal oficial. A tabela agora é exibida localmente e a fonte continua
/// disponível no navegador padrão.
/// </summary>
internal sealed class OfficialSalaryReferenceWindow : Window
{
    private const string OfficialUrl = "https://www.planalto.gov.br/ccivil_03/_ato2023-2026/2025/lei/L15167.htm";
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly IReadOnlyList<OfficialSalaryRow> _allRows = BuildRows();
    private readonly ObservableCollection<OfficialSalaryRow> _visibleRows = [];
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _searchBox = new() { MinWidth = 310 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public OfficialSalaryReferenceWindow()
    {
        Title = "Consulta oficial de soldos — Lei nº 15.167/2025";
        Width = 1120;
        Height = 780;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        App.UiState.Attach(this);
        Content = BuildUi();
        ReloadRows();
    }

    private UIElement BuildUi()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border { Padding = new Thickness(17), Margin = new Thickness(0, 0, 0, 12) };
        ApplyStyle(header, "CardStyle");
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        var title = new TextBlock { Text = "Tabela oficial de soldos", FontSize = 22, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        text.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "Anexo VI da Lei nº 15.167, de 17 de julho de 2025.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 24, 0)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(subtitle);
        headerGrid.Children.Add(text);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var copyTable = new Button { Content = "Copiar tabela", Margin = new Thickness(0, 0, 8, 0) };
        ApplyStyle(copyTable, "SecondaryButtonStyle");
        copyTable.Click += (_, _) => CopyTable();
        var external = new Button { Content = "Abrir a lei no navegador" };
        ApplyStyle(external, "PrimaryButtonStyle");
        external.Click += (_, _) => OpenExternal();
        actions.Children.Add(copyTable);
        actions.Children.Add(external);
        Grid.SetColumn(actions, 1);
        headerGrid.Children.Add(actions);
        header.Child = headerGrid;
        root.Children.Add(header);

        var filterCard = new Border { Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12) };
        ApplyStyle(filterCard, "CardStyle");
        var filterGrid = new Grid();
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var searchLabel = new TextBlock { Text = "Pesquisar posto/graduação", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
        searchPanel.Children.Add(searchLabel);
        _searchBox.ToolTip = "Ex.: 3º Sargento, Capitão, Soldado";
        _searchBox.TextChanged += (_, _) => ReloadRows();
        searchPanel.Children.Add(_searchBox);
        filterGrid.Children.Add(searchPanel);
        var badge = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 5, 11, 5), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "SuccessSoftBrush");
        var badgeText = new TextBlock { Text = "COLUNA VIGENTE: 1º JAN 2026", FontWeight = FontWeights.Bold, FontSize = 10 };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        badge.Child = badgeText;
        Grid.SetColumn(badge, 1);
        filterGrid.Children.Add(badge);
        filterCard.Child = filterGrid;
        Grid.SetRow(filterCard, 1);
        root.Children.Add(filterCard);

        _grid.ItemsSource = _visibleRows;
        _grid.Columns.Add(TextColumn("Posto, graduação ou situação", nameof(OfficialSalaryRow.Title), new DataGridLength(2.3, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(TextColumn("Até 31/03/2025", nameof(OfficialSalaryRow.UntilMarch2025Text), new DataGridLength(1, DataGridLengthUnitType.Star), TextAlignment.Right));
        _grid.Columns.Add(TextColumn("A partir de 01/04/2025", nameof(OfficialSalaryRow.FromApril2025Text), new DataGridLength(1, DataGridLengthUnitType.Star), TextAlignment.Right));
        _grid.Columns.Add(TextColumn("A partir de 01/01/2026", nameof(OfficialSalaryRow.FromJanuary2026Text), new DataGridLength(1, DataGridLengthUnitType.Star), TextAlignment.Right, true));
        _grid.MouseDoubleClick += (_, _) => CopySelected();

        var gridCard = new Border { Padding = new Thickness(1) };
        ApplyStyle(gridCard, "CardStyle");
        gridCard.Child = _grid;
        Grid.SetRow(gridCard, 2);
        root.Children.Add(gridCard);

        var footer = new Grid { Margin = new Thickness(2, 10, 2, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var copySelected = new Button { Content = "Copiar selecionado", Margin = new Thickness(0, 0, 8, 0) };
        ApplyStyle(copySelected, "SecondaryButtonStyle");
        copySelected.Click += (_, _) => CopySelected();
        var close = new Button { Content = "Fechar", IsCancel = true };
        ApplyStyle(close, "PrimaryButtonStyle");
        buttons.Children.Add(copySelected);
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private static DataGridTextColumn TextColumn(string header, string path, DataGridLength width, TextAlignment alignment = TextAlignment.Left, bool emphasize = false)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, alignment));
        if (emphasize) style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width, ElementStyle = style };
    }

    private void ReloadRows()
    {
        var search = Normalize(_searchBox.Text);
        _visibleRows.Clear();
        foreach (var row in _allRows.Where(x => string.IsNullOrWhiteSpace(search) || Normalize(x.Title).Contains(search)))
            _visibleRows.Add(row);
        _status.Text = $"{_visibleRows.Count} linha(s) exibida(s). Fonte: Lei nº 15.167/2025, Anexo VI. Dê duplo clique para copiar uma linha.";
    }

    private void CopySelected()
    {
        if (_grid.SelectedItem is not OfficialSalaryRow row)
        {
            SigfurDialog.Show(this, "Selecione uma linha da tabela.", "Consulta de soldos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Clipboard.SetText($"{row.Title}\t{row.UntilMarch2025Text}\t{row.FromApril2025Text}\t{row.FromJanuary2026Text}");
        _status.Text = $"Linha copiada: {row.Title}.";
    }

    private void CopyTable()
    {
        var builder = new StringBuilder("Posto/graduação\tAté 31/03/2025\tA partir de 01/04/2025\tA partir de 01/01/2026\n");
        foreach (var row in _visibleRows)
            builder.AppendLine($"{row.Title}\t{row.UntilMarch2025Text}\t{row.FromApril2025Text}\t{row.FromJanuary2026Text}");
        Clipboard.SetText(builder.ToString().TrimEnd());
        _status.Text = "Tabela visível copiada para a área de transferência.";
    }

    private void OpenExternal()
    {
        try { Process.Start(new ProcessStartInfo(OfficialUrl) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, "Não foi possível abrir o navegador.\n\n" + ex.Message,
                "Consulta oficial de soldos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static IReadOnlyList<OfficialSalaryRow> BuildRows() =>
    [
        new("Almirante de Esquadra / General de Exército / Tenente-Brigadeiro", 13471, 14077, 14711),
        new("Vice-Almirante / General de Divisão / Major-Brigadeiro", 12912, 13493, 14100),
        new("Contra-Almirante / General de Brigada / Brigadeiro", 12490, 13052, 13639),
        new("Capitão de Mar e Guerra / Coronel", 11451, 11966, 12505),
        new("Capitão de Fragata / Tenente-Coronel", 11250, 11756, 12285),
        new("Capitão de Corveta / Major", 11088, 11587, 12108),
        new("Capitão-Tenente / Capitão", 9135, 9546, 9976),
        new("Primeiro-Tenente", 8245, 8616, 9004),
        new("Segundo-Tenente", 7490, 7827, 8179),
        new("Guarda-Marinha / Aspirante a Oficial", 7315, 7644, 7988),
        new("Aspirante e Cadete (último ano), Aluno do IME (último ano) e Aluno do ITA (último ano)", 1630, 1703, 1780),
        new("Aspirante e Cadete (demais anos), Aluno do IME e ITA (demais anos), Aluno do CFO da Aeronáutica e de órgão de formação de Oficiais da Reserva", 1334, 1394, 1457),
        new("Aluno do Colégio Naval, Aluno da EsPCEx (último ano) e Aluno da Escola de Formação de Sargentos", 1199, 1253, 1309),
        new("Aluno do Colégio Naval, Aluno da EsPCEx (demais anos) e Grumete", 1185, 1238, 1294),
        new("Aprendiz-Marinheiro / Aprendiz-Fuzileiro Naval", 1105, 1155, 1207),
        new("Suboficial / Subtenente", 6169, 6447, 6737),
        new("Primeiro-Sargento", 5483, 5730, 5988),
        new("Segundo-Sargento", 4770, 4985, 5209),
        new("Terceiro-Sargento", 3825, 3997, 4177),
        new("Cabo (engajado) / Taifeiro-Mor", 2627, 2745, 2869),
        new("Cabo (não engajado)", 1078, 1127, 1177),
        new("Taifeiro de Primeira Classe", 2325, 2430, 2539),
        new("Taifeiro de Segunda Classe", 2210, 2309, 2413),
        new("Marinheiro, Soldado Fuzileiro Naval e Soldado de Primeira Classe (especializado, cursado e engajado), Soldado-Clarim ou Corneteiro de Primeira Classe e Soldado Paraquedista (engajado)", 1926, 2013, 2103),
        new("Marinheiro, Soldado Fuzileiro Naval, Soldado de Primeira Classe (não especializado), Soldado-Clarim ou Corneteiro de Segunda Classe, Soldado do Exército e Soldado de Segunda Classe (engajado)", 1765, 1844, 1927),
        new("Marinheiro-Recruta, Recruta, Soldado, Soldado-Recruta, Soldado de Segunda Classe (não engajado) e Soldado-Clarim ou Corneteiro de Terceira Classe", 1078, 1127, 1177)
    ];

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant).ToArray());
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        if (Application.Current.TryFindResource(key) is Style style) element.Style = style;
    }

    private sealed record OfficialSalaryRow(string Title, decimal UntilMarch2025, decimal FromApril2025, decimal FromJanuary2026)
    {
        public string UntilMarch2025Text => UntilMarch2025.ToString("C0", PtBr);
        public string FromApril2025Text => FromApril2025.ToString("C0", PtBr);
        public string FromJanuary2026Text => FromJanuary2026.ToString("C0", PtBr);
    }
}
