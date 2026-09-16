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
    private readonly string? _requiredKind;
    private ICollectionView? _view;

    public SavedBulletinPickerWindow(string? requiredKind = null, bool attachmentMode = false)
    {
        InitializeComponent();
        if (attachmentMode)
        {
            Title = "Selecionar BI ou Adt Furr para anexar";
            HelpText.Text = "Escolha um Boletim Interno ou Aditamento do Furriel. O PDF já salvo na biblioteca será usado diretamente como anexo, sem precisar exportá-lo antes.";
            UseButton.Content = "Usar como anexo";
        }
        _requiredKind = string.IsNullOrWhiteSpace(requiredKind) ? null : requiredKind.Trim();
        if (_requiredKind is not null)
        {
            var item = KindBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Content?.ToString(), _requiredKind, StringComparison.OrdinalIgnoreCase));
            if (item is not null) KindBox.SelectedItem = item;
            KindBox.IsEnabled = false;
            Title = $"Selecionar {_requiredKind} salvo";
        }
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
                    Kind = item.BulletinType.Contains("Adt", StringComparison.OrdinalIgnoreCase) || item.BulletinType.Contains("Aditamento", StringComparison.OrdinalIgnoreCase)
                        ? "Aditamento do Furriel" : "Boletim Interno",
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
        var selectedKind = _requiredKind ?? (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
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
        if (string.IsNullOrWhiteSpace(selected.PublicationNumber) || selected.SortDate == DateTime.MinValue)
        {
            CountText.Text = "Esta publicação não tem número ou data válidos. Corrija o cadastro na biblioteca antes de usá-la.";
            return;
        }
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
    public string PublicationNumber
    {
        get
        {
            var withoutYear = System.Text.RegularExpressions.Regex.Replace(
                (Number ?? string.Empty).Trim(),
                @"\b(?<num>\d{1,5})\s*/\s*(?:20)?\d{2}\b",
                match => match.Groups["num"].Value,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return System.Text.RegularExpressions.Regex.Replace(
                    withoutYear,
                    @"^\s*(?:(?:BOLETIM\s+INTERNO|BI|ADITAMENTO\s+DO\s+FURRIEL|ADT(?:\s+FURR)?)\s*)?(?:N(?:R|[º°])?\.?\s*)?",
                    string.Empty,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Trim();
        }
    }
    public string PublicationBar => System.Text.RegularExpressions.Regex.Replace(
        (Bar ?? string.Empty).Trim(),
        @"^\s*BAR\s*(?:N(?:R|[º°])?\.?\s*)?",
        string.Empty,
        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    public string ReferenceText => Kind.Equals("Aditamento do Furriel", StringComparison.OrdinalIgnoreCase)
        ? $"Adt Furr Nr {PublicationNumber}{(string.IsNullOrWhiteSpace(PublicationBar) ? string.Empty : $" BAR {PublicationBar}")}, de {AbbreviatedDate}, {OrganizationIdentity.BulletinIssuer}".Trim()
        : $"BI Nr {PublicationNumber}, de {AbbreviatedDate}, {OrganizationIdentity.BulletinIssuer}".Trim();

    private string AbbreviatedDate
    {
        get
        {
            var culture = CultureInfo.GetCultureInfo("pt-BR");
            return DateTime.TryParse(Date, culture, DateTimeStyles.AllowWhiteSpaces, out var date)
                ? date.ToString("dd MMM yy", culture).Replace(".", string.Empty).ToUpper(culture)
                : Date;
        }
    }
}
