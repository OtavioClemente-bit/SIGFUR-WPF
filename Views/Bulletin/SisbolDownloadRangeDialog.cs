using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Bulletin;

public sealed record SisbolDownloadRangeSelection(int Year, int? Month)
{
    public string ScopeText => Month.HasValue ? $"{Month.Value:00}/{Year}" : $"ano inteiro {Year}";
}

public sealed class SisbolDownloadRangeDialog : Window
{
    private static readonly string[] MonthLabels =
    [
        "Todos do ano", "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho",
        "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro"
    ];

    private readonly ComboBox _yearBox = new() { IsEditable = true, MinWidth = 130, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _monthBox = new() { MinWidth = 190, Margin = new Thickness(0, 6, 0, 0) };

    public SisbolDownloadRangeSelection? Selection { get; private set; }

    private SisbolDownloadRangeDialog(string bulletinLabel, int initialYear, int? initialMonth)
    {
        Title = "Baixar boletins do SisBol";
        Width = 500;
        Height = 315;
        MinWidth = 460;
        MinHeight = 295;
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
                new TextBlock { Text = bulletinLabel, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 18 },
                new TextBlock { Text = "Escolha o ano e baixe um mês específico ou todos os meses disponíveis.", Foreground = new SolidColorBrush(Color.FromRgb(220, 235, 255)), Margin = new Thickness(0, 4, 0, 0) }
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

        var currentYear = DateTime.Today.Year;
        var years = Enumerable.Range(Math.Max(2020, currentYear - 8), Math.Min(15, currentYear - Math.Max(2020, currentYear - 8) + 2))
            .OrderByDescending(x => x)
            .Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (!years.Contains(initialYear.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            years.Insert(0, initialYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _yearBox.ItemsSource = years;
        _yearBox.Text = initialYear.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _monthBox.ItemsSource = MonthLabels;
        _monthBox.SelectedIndex = initialMonth is >= 1 and <= 12 ? initialMonth.Value : 0;

        var yearPanel = new StackPanel();
        yearPanel.Children.Add(new TextBlock { Text = "Ano", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 50, 56)) });
        yearPanel.Children.Add(_yearBox);
        Grid.SetColumn(yearPanel, 0);
        form.Children.Add(yearPanel);

        var monthPanel = new StackPanel();
        monthPanel.Children.Add(new TextBlock { Text = "Mês", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 50, 56)) });
        monthPanel.Children.Add(_monthBox);
        monthPanel.Children.Add(new TextBlock
        {
            Text = "Use ‘Todos do ano’ para atualizar janeiro até o mês atual quando o ano for o corrente.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 125, 139)),
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11
        });
        Grid.SetColumn(monthPanel, 2);
        form.Children.Add(monthPanel);

        var footer = new Border { Background = new SolidColorBrush(Color.FromRgb(247, 250, 252)), Padding = new Thickness(20, 0, 20, 0) };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        footer.Child = buttons;
        var cancel = new Button { Content = "Cancelar", MinWidth = 95, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "Baixar / atualizar", MinWidth = 145, Padding = new Thickness(14, 8, 14, 8), FontWeight = FontWeights.Bold };
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
    }

    public static SisbolDownloadRangeSelection? Ask(Window owner, string bulletinLabel, int initialYear, int? initialMonth = null)
    {
        var dialog = new SisbolDownloadRangeDialog(bulletinLabel, initialYear, initialMonth) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    private void Confirm()
    {
        var yearText = (_yearBox.Text ?? string.Empty).Trim();
        if (!int.TryParse(yearText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var year) || year < 2000 || year > DateTime.Today.Year + 1)
        {
            MessageBox.Show(this, "Informe um ano válido.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var monthIndex = Math.Max(0, _monthBox.SelectedIndex);
        int? month = monthIndex == 0 ? null : monthIndex;
        if (year == DateTime.Today.Year && month.HasValue && month.Value > DateTime.Today.Month)
        {
            MessageBox.Show(this, "Esse mês ainda não chegou no ano atual.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Selection = new SisbolDownloadRangeSelection(year, month);
        DialogResult = true;
        Close();
    }
}
