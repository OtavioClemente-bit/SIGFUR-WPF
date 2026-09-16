using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class SisbolSubmissionHistoryWindow : Window
{
    private static readonly CompareInfo SearchComparer = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
    private readonly ObservableCollection<SisbolSubmissionHistoryEntry> _items = [];
    private ICollectionView? _view;

    public SisbolSubmissionHistoryWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        HistoryGrid.ItemsSource = _items;
        Loaded += async (_, _) => await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            StatusText.Text = "Carregando histórico…";
            var rows = await App.Sisbol.History.LoadAsync();
            _items.Clear();
            foreach (var row in rows) _items.Add(row);
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = MatchesFilter;

            var categories = new[] { "Todos os tipos" }
                .Concat(_items.Select(x => x.Category).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x))
                .ToList();
            var currentCategory = CategoryBox.SelectedItem?.ToString();
            CategoryBox.ItemsSource = categories;
            CategoryBox.SelectedItem = currentCategory is not null && categories.Contains(currentCategory) ? currentCategory : categories[0];
            RefreshFilter();
            if (_items.Count > 0) HistoryGrid.SelectedIndex = 0;
            StatusText.Text = _items.Count == 0
                ? "Ainda não há lançamentos registrados. Os próximos envios confirmados serão salvos automaticamente."
                : "Selecione um lançamento para consultar ou copiar.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível carregar o histórico.";
            await App.Log.WriteAsync("Falha ao abrir o histórico de envios ao SISBOL.", ex);
            SigfurDialog.Show(this, ex.Message, "Histórico do SISBOL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool MatchesFilter(object item)
    {
        if (item is not SisbolSubmissionHistoryEntry entry) return false;
        var category = CategoryBox.SelectedItem?.ToString() ?? "Todos os tipos";
        if (!category.Equals("Todos os tipos", StringComparison.OrdinalIgnoreCase)
            && !entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) return false;

        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;
        var searchable = string.Join(' ', entry.SentAtText, entry.Category, entry.GeneralSubject, entry.Subject,
            entry.MilitaryText, string.Join(' ', entry.Military.Select(x => x.WarName)), entry.OpeningText, entry.ClosingText);
        return SearchComparer.IndexOf(searchable, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    private void Filter_Changed(object sender, EventArgs e) => RefreshFilter();

    private void RefreshFilter()
    {
        _view?.Refresh();
        var visible = _view?.Cast<object>().Count() ?? _items.Count;
        CountText.Text = visible == 1 ? "1 lançamento" : $"{visible} lançamentos";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SisbolSubmissionHistoryEntry entry)
        {
            DetailTitleText.Text = "Selecione um lançamento";
            DetailMetaText.Text = string.Empty;
            DetailMilitaryText.Text = string.Empty;
            OpeningTextBox.Text = string.Empty;
            ClosingTextBox.Text = string.Empty;
            return;
        }
        DetailTitleText.Text = string.IsNullOrWhiteSpace(entry.Subject) ? "Matéria sem título informado" : entry.Subject;
        DetailMetaText.Text = $"{entry.SentAtText}  •  {entry.Category}  •  Assunto geral: {entry.GeneralSubject}";
        DetailMilitaryText.Text = "Militar(es): " + entry.MilitaryText;
        OpeningTextBox.Text = entry.OpeningText;
        ClosingTextBox.Text = entry.ClosingText;
    }

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is SisbolSubmissionHistoryEntry) CopyAll();
    }

    private void CopySubject_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } entry) return;
        CopyToClipboard(entry.Subject, "Assunto copiado.");
    }

    private void CopyOpening_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } entry) return;
        CopyToClipboard(entry.OpeningText, "Texto de abertura copiado.");
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();

    private void CopyAll()
    {
        if (Selected() is not { } entry) return;
        CopyToClipboard(entry.CopyAllText, "Abertura e fechamento copiados.");
    }

    private SisbolSubmissionHistoryEntry? Selected()
    {
        if (HistoryGrid.SelectedItem is SisbolSubmissionHistoryEntry entry) return entry;
        StatusText.Text = "Selecione um lançamento primeiro.";
        return null;
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Este lançamento não possui texto nesse campo.";
            return;
        }
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            StatusText.Text = "O Windows não permitiu acessar a área de transferência.";
            _ = App.Log.WriteAsync("Falha ao copiar item do histórico SISBOL.", ex);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
