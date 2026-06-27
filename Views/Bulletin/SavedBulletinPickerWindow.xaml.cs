using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class SavedBulletinPickerWindow : Window
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly ObservableCollection<SavedBulletinReference> _items = [];
    private ICollectionView? _view;

    public SavedBulletinPickerWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Loaded += OnLoaded;
    }

    public SavedBulletinReference? SelectedReference { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var intelligentTask = App.IntelligentBulletins.LoadAsync();
            var furrielService = new FurrielBulletinService(App.Paths, App.Settings, App.MilitaryRepository, App.Log);
            var furrielTask = furrielService.LoadIndexAsync();
            await Task.WhenAll(intelligentTask, furrielTask);

            foreach (var item in intelligentTask.Result.Items)
            {
                _items.Add(new SavedBulletinReference
                {
                    Kind = "Boletim Interno",
                    Number = Clean(item.BulletinNumber),
                    Date = Clean(item.BulletinDate),
                    Title = FirstNotBlank(item.OriginalFileName, item.FileName, "Boletim Interno"),
                    Path = item.PdfPath,
                    SortDate = ParseDate(item.DateIso, item.BulletinDate)
                });
            }

            foreach (var item in furrielTask.Result.Files)
            {
                _items.Add(new SavedBulletinReference
                {
                    Kind = "Aditamento do Furriel",
                    Number = Clean(item.Bulletin),
                    Bar = Clean(item.Bar),
                    Date = Clean(item.Date),
                    Title = FirstNotBlank(item.Title, item.OriginalName, "Aditamento do Furriel"),
                    Path = item.StoredPath,
                    SortDate = ParseDate(null, item.Date)
                });
            }

            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = FilterItem;
            _view.SortDescriptions.Add(new SortDescription(nameof(SavedBulletinReference.SortDate), ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(nameof(SavedBulletinReference.Number), ListSortDirection.Descending));
            ItemsGrid.ItemsSource = _view;
            RefreshCount();
            SearchBox.Focus();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao carregar BI/ADT salvos para o Boletim.", ex);
            CountText.Text = "Não foi possível carregar as publicações salvas.";
        }
    }

    private bool FilterItem(object item)
    {
        if (item is not SavedBulletinReference reference) return false;
        var selectedKind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
        if (!selectedKind.Equals("Todos", StringComparison.OrdinalIgnoreCase) &&
            !reference.Kind.Equals(selectedKind, StringComparison.OrdinalIgnoreCase)) return false;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(query) || reference.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();
    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();

    private void RefreshFilter()
    {
        _view?.Refresh();
        RefreshCount();
    }

    private void RefreshCount()
    {
        if (CountText is null) return;
        CountText.Text = $"{_view?.Cast<object>().Count() ?? 0} publicação(ões) disponível(is)";
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();
    private void Use_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (ItemsGrid.SelectedItem is not SavedBulletinReference selected) return;
        SelectedReference = selected;
        DialogResult = true;
    }

    private static DateTime ParseDate(string? iso, string? display)
    {
        if (DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        return DateTime.TryParse(display, PtBr, DateTimeStyles.AllowWhiteSpaces, out parsed) ? parsed : DateTime.MinValue;
    }

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Trim() is "—" or "-" ? string.Empty : value.Trim();

    private static string FirstNotBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

public sealed class SavedBulletinReference
{
    public string Kind { get; init; } = string.Empty;
    public string Number { get; init; } = string.Empty;
    public string Bar { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public DateTime SortDate { get; init; }
    public string SearchText => string.Join(' ', Kind, Number, Bar, Date, Title, Path);
    public string ReferenceText => Kind.Equals("Aditamento do Furriel", StringComparison.OrdinalIgnoreCase)
        ? $"Aditamento do Furriel nº {NumberWithoutYear}{(string.IsNullOrWhiteSpace(Bar) ? string.Empty : $" BAR {Bar}")}, de {Date}".Trim()
        : $"Boletim Interno nº {NumberWithoutYear}, de {Date}".Trim();

    private string NumberWithoutYear => System.Text.RegularExpressions.Regex.Replace(
        (Number ?? string.Empty).Trim(),
        @"\b(?<num>\d{1,5})\s*/\s*(?:20)?\d{2}\b",
        match => match.Groups["num"].Value,
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
