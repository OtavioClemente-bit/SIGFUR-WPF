using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class SippesRubricPickerWindow : Window
{
    public ObservableCollection<SippesRubricRecord> Items { get; } = [];
    public SippesRubricRecord? SelectedRubric { get; private set; }

    public SippesRubricPickerWindow(string initialSearch = "")
    {
        InitializeComponent();
        App.UiState.Attach(this);
        DataContext = this;
        SearchBox.Text = initialSearch ?? string.Empty;
        Loaded += (_, _) =>
        {
            RefreshItems();
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    private string CurrentFilter
        => FilterBox.SelectedItem is ComboBoxItem item ? Convert.ToString(item.Content) ?? "Todos" : "Todos";

    private void RefreshItems()
    {
        Items.Clear();
        foreach (var row in SippesRubricCatalog.Search(SearchBox.Text, CurrentFilter)) Items.Add(row);
        ResultCountText.Text = $"{Items.Count:N0} resultado(s) exibido(s) de {SippesRubricCatalog.All.Count:N0} rubricas.";
        if (Items.Count > 0)
        {
            RubricsGrid.SelectedIndex = 0;
            RubricsGrid.ScrollIntoView(Items[0]);
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshItems();
    }

    private void Select_Click(object sender, RoutedEventArgs e) => AcceptSelection();
    private void RubricsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (RubricsGrid.SelectedItem is not SippesRubricRecord selected)
        {
            SigfurDialog.Show(this, "Selecione uma rubrica.", "Rubrica SIPPES", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedRubric = selected;
        DialogResult = true;
    }
}
