using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Bulletin;

public sealed record SisbolIndexDateRangeSelection(DateTime StartDate, DateTime EndDate)
{
    public string PeriodText
        => $"{StartDate.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"))} a {EndDate.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"))}";
}

public sealed class SisbolIndexDateRangeDialog : Window
{
    private readonly DatePicker _startPicker = new() { SelectedDateFormat = DatePickerFormat.Short, MinWidth = 150, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DatePicker _endPicker = new() { SelectedDateFormat = DatePickerFormat.Short, MinWidth = 150, Margin = new Thickness(0, 6, 0, 0) };

    public SisbolIndexDateRangeSelection? Selection { get; private set; }

    private SisbolIndexDateRangeDialog(string title, DateTime initialStart, DateTime initialEnd)
    {
        Title = title;
        Width = 520;
        Height = 300;
        MinWidth = 480;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        ShowInTaskbar = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });
        Content = root;

        var header = new Border { Background = new SolidColorBrush(Color.FromRgb(13, 71, 161)), Padding = new Thickness(20, 0, 20, 0) };
        Grid.SetRow(header, 0);
        root.Children.Add(header);
        header.Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 18 },
                new TextBlock { Text = "Escolha o periodo exato que sera enviado ao SisBol.", Foreground = new SolidColorBrush(Color.FromRgb(220, 235, 255)), Margin = new Thickness(0, 4, 0, 0) }
            }
        };

        var body = new Border { Padding = new Thickness(22, 18, 22, 12) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition());
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        body.Child = form;

        _startPicker.SelectedDate = initialStart.Date;
        _endPicker.SelectedDate = initialEnd.Date;

        var startPanel = new StackPanel();
        startPanel.Children.Add(new TextBlock { Text = "Data inicial", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 50, 56)) });
        startPanel.Children.Add(_startPicker);
        Grid.SetColumn(startPanel, 0);
        form.Children.Add(startPanel);

        var endPanel = new StackPanel();
        endPanel.Children.Add(new TextBlock { Text = "Data final", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 50, 56)) });
        endPanel.Children.Add(_endPicker);
        endPanel.Children.Add(new TextBlock
        {
            Text = "O SIGFUR vai baixar o PDF gerado e importar o indice automaticamente.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 125, 139)),
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11
        });
        Grid.SetColumn(endPanel, 2);
        form.Children.Add(endPanel);

        var footer = new Border { Background = new SolidColorBrush(Color.FromRgb(247, 250, 252)), Padding = new Thickness(20, 0, 20, 0) };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        footer.Child = buttons;
        var cancel = new Button { Content = "Cancelar", MinWidth = 95, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "Baixar / importar", MinWidth = 145, Padding = new Thickness(14, 8, 14, 8), FontWeight = FontWeights.Bold };
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
    }

    public static SisbolIndexDateRangeSelection? Ask(Window owner, string title, DateTime initialStart, DateTime initialEnd)
    {
        var dialog = new SisbolIndexDateRangeDialog(title, initialStart, initialEnd) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    private void Confirm()
    {
        var start = _startPicker.SelectedDate?.Date;
        var end = _endPicker.SelectedDate?.Date;
        if (start is null || end is null)
        {
            MessageBox.Show(this, "Informe data inicial e data final.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var first = start.Value;
        var last = end.Value;
        if (last < first) (first, last) = (last, first);
        if (first.Year < 2000 || last.Year > DateTime.Today.Year + 1)
        {
            MessageBox.Show(this, "Informe um periodo valido.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Selection = new SisbolIndexDateRangeSelection(first, last);
        DialogResult = true;
        Close();
    }
}
