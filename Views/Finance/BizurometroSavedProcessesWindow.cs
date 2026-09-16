using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SIGFUR.Wpf.Controls;

namespace SIGFUR.Wpf.Views.Finance;

internal sealed class BizurometroSavedProcessesWindow : Window
{
    private readonly IReadOnlyList<BizurometroSpedWindow.SavedProcess> _processes;
    private readonly ICollectionView _view;
    private readonly TextBox _search = new();
    private readonly TextBlock _count = new();
    private readonly DataGrid _grid = new();

    public BizurometroSpedWindow.SavedProcess? SelectedProcess { get; private set; }

    public BizurometroSavedProcessesWindow(
        IReadOnlyList<BizurometroSpedWindow.SavedProcess> processes,
        string? currentProcessId)
    {
        _processes = processes;
        _view = CollectionViewSource.GetDefaultView(processes);
        _view.Filter = MatchesSearch;

        Title = "Processos salvos — SPED Processos";
        Width = 1180;
        Height = 700;
        MinWidth = 880;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        Content = BuildContent();

        _search.TextChanged += (_, _) => RefreshFilter();
        _search.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) Accept();
            else if (args.Key == Key.Escape) Close();
        };
        _grid.MouseDoubleClick += (_, _) => Accept();
        _grid.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { Accept(); args.Handled = true; }
        };
        _grid.SelectedItem = processes.FirstOrDefault(item => item.Id == currentProcessId) ?? processes.FirstOrDefault();
        Loaded += (_, _) => { RefreshFilter(); _search.Focus(); };
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "PROCESSOS SALVOS",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        root.Children.Add(heading);

        var searchPanel = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search.MinHeight = 38;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = "Pesquise por nome completo, nome de guerra, CPF, título, finalidade ou documento";
        _search.SetResourceReference(Control.StyleProperty, "TextBoxStyle");
        searchPanel.Children.Add(_search);
        _count.Margin = new Thickness(14, 10, 2, 0);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(_count, 1);
        searchPanel.Children.Add(_count);
        Grid.SetRow(searchPanel, 1);
        root.Children.Add(searchPanel);

        _grid.ItemsSource = _view;
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.RowHeaderWidth = 0;
        _grid.MinRowHeight = 48;
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Atualizado",
            Binding = new Binding(nameof(BizurometroSpedWindow.SavedProcess.UpdatedAtText)),
            Width = 135
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Título",
            Binding = new Binding(nameof(BizurometroSpedWindow.SavedProcess.Title)),
            Width = new DataGridLength(1.15, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Finalidade",
            Binding = new Binding("State.Flow"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var militaryFactory = new FrameworkElementFactory(typeof(HighlightedNameTextBlock));
        militaryFactory.SetBinding(HighlightedNameTextBlock.FullNameProperty,
            new Binding(nameof(BizurometroSpedWindow.SavedProcess.PersonFullName)));
        militaryFactory.SetBinding(HighlightedNameTextBlock.WarNameProperty,
            new Binding(nameof(BizurometroSpedWindow.SavedProcess.PersonWarName)));
        militaryFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        militaryFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 5));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Pessoa / militar",
            CellTemplate = new DataTemplate { VisualTree = militaryFactory },
            Width = new DataGridLength(1.35, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Documento ou relatório",
            Binding = new Binding("State.OriginDocumentDisplay"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(4) };
        cancel.SetResourceReference(Control.StyleProperty, "SecondaryButtonStyle");
        cancel.Click += (_, _) => Close();
        var open = new Button { Content = "Abrir processo", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(4), IsDefault = true };
        open.SetResourceReference(Control.StyleProperty, "PrimaryButtonStyle");
        open.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        return root;
    }

    private bool MatchesSearch(object item)
    {
        if (item is not BizurometroSpedWindow.SavedProcess process) return false;
        var query = Normalize(_search.Text);
        return string.IsNullOrWhiteSpace(query) || process.SearchText.Contains(query, StringComparison.Ordinal);
    }

    private void RefreshFilter()
    {
        _view.Refresh();
        _count.Text = $"{_view.Cast<object>().Count()} de {_processes.Count} processo(s)";
        if (_grid.SelectedItem is null) _grid.SelectedItem = _view.Cast<object>().FirstOrDefault();
    }

    private void Accept()
    {
        if (_grid.SelectedItem is not BizurometroSpedWindow.SavedProcess selected) return;
        SelectedProcess = selected;
        DialogResult = true;
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var normalized = new string(decomposed
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
