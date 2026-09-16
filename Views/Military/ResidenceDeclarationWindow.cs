using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using System.Globalization;
using System.Windows.Markup;

namespace SIGFUR.Wpf.Views.Military;

public sealed class ResidenceDeclarationWindow : Window
{
    private readonly MilitaryRecord _military;
    private readonly TextBox _city = new() { Padding = new Thickness(7), MaxLength = 100, TextWrapping = TextWrapping.Wrap };
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, SelectedDateFormat = DatePickerFormat.Short, Language = XmlLanguage.GetLanguage("pt-BR") };
    private readonly TextBlock _saveStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TransportResidenceService? _preferences;
    private readonly FlowDocumentPageViewer _preview = new() { Zoom = 85, MinZoom = 50, MaxZoom = 150 };

    public ResidenceDeclarationWindow(MilitaryRecord military, TransportResidenceService? preferences = null)
    {
        _military = military;
        _preferences = preferences;
        Title = "Declaração de residência · Auxílio-transporte"; Width = 1140; Height = 840;
        MinWidth = 920; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        Foreground = Brushes.Black;
        var root = new Grid { Margin = new Thickness(18), Background = Background };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(315) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var form = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        form.Children.Add(new TextBlock { Text = "Residência em nome de terceiro", FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "O militar e o endereço vêm do cadastro. Os dados do responsável e a assinatura serão preenchidos à mão.", Margin = new Thickness(0, 10, 0, 15), TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "MILITAR SELECIONADO", FontSize = 11, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = $"{military.ShortRank} {military.Name}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 10) });
        form.Children.Add(new TextBlock { Text = "ENDEREÇO DO CADASTRO", FontSize = 11, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(military.Address) ? "Endereço não informado no cadastro" : military.Address, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 18) });
        form.Children.Add(new TextBlock { Text = "Cidade / UF", Margin = new Thickness(0, 7, 0, 3) });
        try { _city.Text = preferences?.LoadDeclarationCity() ?? ""; }
        catch (Exception ex) { _saveStatus.Text = "Não foi possível carregar a cidade salva: " + ex.Message; }
        form.Children.Add(_city);
        form.Children.Add(_saveStatus);
        form.Children.Add(new TextBlock { Text = "Data da declaração", Margin = new Thickness(0, 14, 0, 3) });
        form.Children.Add(_date);
        _city.TextChanged += (_, _) => { SaveCity(); Refresh(); };
        _date.SelectedDateChanged += (_, _) => Refresh();
        _date.DateValidationError += (_, e) => { e.ThrowException = false; _date.SelectedDate = null; _saveStatus.Text = "Escolha uma data válida no calendário."; };
        var print = new Button { Content = "Imprimir declaração", Padding = new Thickness(12), Margin = new Thickness(0, 18, 0, 0) };
        print.Click += (_, _) => {
            if (_date.SelectedDate is null) { _saveStatus.Text = "Escolha a data da declaração no calendário."; return; }
            SaveCity();
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() == true) dialog.PrintDocument(((IDocumentPaginatorSource)BuildDocument()).DocumentPaginator, "Declaração de residência");
        };
        form.Children.Add(print);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(_preview, 1); root.Children.Add(_preview); Content = root; Refresh();
    }

    private void SaveCity()
    {
        if (_preferences is null) return;
        try { _preferences.SaveDeclarationCity(_city.Text); _saveStatus.Text = "Cidade / UF salva para as próximas declarações."; }
        catch (Exception ex) { _saveStatus.Text = "Não foi possível salvar a cidade / UF: " + ex.Message; }
    }
    private void Refresh() => _preview.Document = BuildDocument();
    public FlowDocument BuildDocument()
    {
        const string blank = "________________________________________________";
        string Value(string key) => key switch
        {
            "address" => string.IsNullOrWhiteSpace(_military.Address) ? blank : _military.Address.Trim(),
            "city" => string.IsNullOrWhiteSpace(_city.Text) ? blank : _city.Text.Trim(),
            "date" => _date.SelectedDate?.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) ?? blank,
            _ => blank
        };
        var doc = new FlowDocument { PageWidth = 793.7, PageHeight = 1122.5, PagePadding = new Thickness(64, 55, 64, 55), ColumnWidth = 1000, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, LineHeight = 21, Background = Brushes.White, Foreground = Brushes.Black };
        void P(string text, double size = 13, bool bold = false, double after = 10) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, after) });
        P("EXÉRCITO BRASILEIRO  /  4ª COMPANHIA DE POLÍCIA DO EXÉRCITO", 11, true, 24);
        P("DECLARAÇÃO DE RESIDÊNCIA", 25, true, 6);
        P("Comprovante em nome de terceiro · Auxílio-transporte", 12, false, 26);
        P("01   RESPONSÁVEL PELO IMÓVEL", 12, true);
        P($"Nome: {Value("owner")}\nCPF: {Value("cpf")}\nIdentidade / órgão expedidor: {Value("identity")}\nVínculo com o imóvel: {Value("relation")}", after: 20);
        P("02   MILITAR RESIDENTE", 12, true);
        P($"Nome: {_military.Name}\nPosto/graduação: {_military.ShortRank}    CPF: {_military.FormattedCpf}", after: 20);
        P("03   ENDEREÇO DECLARADO", 12, true);
        P(Value("address"), after: 20);
        P($"Declaro, para fins de comprovação de residência, que o militar identificado acima reside comigo no endereço informado, desde {Value("since")}. Declaro que as informações prestadas correspondem à verdade e assumo a responsabilidade por esta declaração.", after: 26);
        P($"Cidade / UF: {Value("city")}\nData: {Value("date")}", after: 38);
        P("__________________________________________________________", after: 3);
        P("Assinatura do responsável pelo imóvel", 12, true, 5);
        P("O militar residente não substitui a assinatura do declarante.", 11, false, 28);
        P("Documento destinado à instrução do processo de auxílio-transporte.\nAnexar o comprovante disponível em nome do declarante, quando houver.", 10);
        return doc;
    }
}
