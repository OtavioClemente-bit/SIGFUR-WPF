using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views;

public partial class QuickSearchWindow : Window
{
    private readonly IReadOnlyList<MilitaryItem> _all;
    private readonly Func<MilitaryItem, Task> _open;
    public QuickSearchWindow(IEnumerable<MilitaryItem> military, Func<MilitaryItem, Task> open)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _all = military.ToList(); _open = open;
        Render();
        Loaded += (_, _) => SearchBox.Focus();
    }
    private void Render()
    {
        var q = SearchBox.Text.Trim();
        MilitaryGrid.ItemsSource = _all.Where(x => string.IsNullOrWhiteSpace(q) || $"{x.Rank} {x.Name} {x.WarName} {x.Cpf}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(300).ToList();
        if (MilitaryGrid.Items.Count > 0) MilitaryGrid.SelectedIndex = 0;
    }
    private async Task OpenSelectedAsync()
    {
        if (MilitaryGrid.SelectedItem is not MilitaryItem item) return;
        try { await _open(item); Close(); }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Carteira", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Render();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); } }
    private async void MilitaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();
    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();
}
