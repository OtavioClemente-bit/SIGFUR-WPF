using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views.Finance;

public sealed class PaymentConferenceReviewWindow : Window
{
    private readonly ListBox _people = new() { DisplayMemberPath = nameof(PaymentConferenceResultRow.ReviewLabel) };
    private readonly ConferencePdfPreview _bulletin = new("Boletim — publicação");
    private readonly ConferencePdfPreview _paystub = new("Contracheque — folha selecionada");
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
    private readonly Button _verify = new() { Content = "Marcar como verificado", Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
    public PaymentConferenceReviewWindow(Func<PaymentConferenceResultRow, bool, Task>? saveVerification = null)
    {
        Title = "Conferência visual — boletim e contracheque"; Width = 1540; Height = 900;
        MinWidth = 1050; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(233, 239, 247));
        var root = new DockPanel { Background = Background };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var (label, step) in new[] { ("◀ Militar anterior", -1), ("Próximo militar ▶", 1) })
        {
            var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
            button.Click += (_, _) => { if (_people.Items.Count > 0) { _people.SelectedIndex = Math.Clamp(_people.SelectedIndex + step, 0, _people.Items.Count - 1); _people.ScrollIntoView(_people.SelectedItem); } };
            bar.Children.Add(button);
        }
        bar.Children.Add(_verify);
        _verify.SetBinding(ContentControl.ContentProperty, new Binding("SelectedItem.VerificationActionText") { Source = _people, TargetNullValue = "Marcar como verificado" });
        _verify.Click += async (_, _) =>
        {
            if (_people.SelectedItem is not PaymentConferenceResultRow row || saveVerification is null) return;
            _verify.IsEnabled = false;
            try { await saveVerification(row, !row.IsVerified); }
            catch (Exception ex) { MessageBox.Show(this, "Não foi possível salvar a marcação: " + ex.Message); }
            finally { _verify.IsEnabled = true; }
        };
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_detail, Dock.Bottom); root.Children.Add(_detail);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 170 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
        ScrollViewer.SetHorizontalScrollBarVisibility(_people, ScrollBarVisibility.Disabled);
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(213, 232, 255))));
        selectedTrigger.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); style.Triggers.Add(selectedTrigger);
        var verifiedTrigger = new DataTrigger { Binding = new Binding(nameof(PaymentConferenceResultRow.IsVerified)), Value = true };
        verifiedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(209, 245, 218))));
        verifiedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(20, 100, 45))));
        style.Triggers.Add(verifiedTrigger);
        _people.ItemContainerStyle = style;
        var template = new DataTemplate(); var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaymentConferenceResultRow.ReviewLabel)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); template.VisualTree = text;
        _people.DisplayMemberPath = ""; _people.ItemTemplate = template;
        grid.Children.Add(_people);
        foreach (var column in new[] { 1, 3 }) { var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, column); grid.Children.Add(splitter); }
        Grid.SetColumn(_bulletin, 2); grid.Children.Add(_bulletin); Grid.SetColumn(_paystub, 4); grid.Children.Add(_paystub);
        root.Children.Add(grid); Content = root;
        Loaded += (_, _) => { if (_people.SelectedItem is not null) _people.ScrollIntoView(_people.SelectedItem); };
        _people.SelectionChanged += async (_, _) =>
        {
            if (_people.SelectedItem is not PaymentConferenceResultRow row) return;
            _detail.Text = $"{row.Military} · {row.Status}\n{row.RubricsFound}\n{row.Notes}";
            await Task.WhenAll(_bulletin.ShowAsync(row.BulletinPath, row.BulletinPage, row.HighlightName), _paystub.ShowAsync(row.PaystubPath));
        };
    }
    public void ShowRows(IReadOnlyList<PaymentConferenceResultRow> rows, PaymentConferenceResultRow? selected)
    {
        _people.ItemsSource = rows;
        _people.SelectedItem = selected ?? rows.FirstOrDefault();
        if (_people.SelectedItem is not null) _people.ScrollIntoView(_people.SelectedItem);
    }
    public async Task ShowBulletinAsync(string path)
    { _people.ItemsSource = null; _detail.Text = "Execute a conferência para navegar pelos militares e seus contracheques."; await Task.WhenAll(_bulletin.ShowAsync(path), _paystub.ShowAsync("")); }
    public async Task ShowPaystubAsync(string path)
    { _people.ItemsSource = null; _detail.Text = "Contracheque da competência selecionada. Selecione um resultado para comparar com a publicação."; await Task.WhenAll(_bulletin.ShowAsync(""), _paystub.ShowAsync(path)); }
}
