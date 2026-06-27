using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views;

public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<ActionDefinition> _all;
    private readonly Func<string, Task> _execute;
    public CommandPaletteWindow(IReadOnlyList<ActionDefinition> actions, Func<string, Task> execute)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _all = actions;
        _execute = execute;
        Render();
        Loaded += (_, _) => SearchBox.Focus();
    }
    private void Render()
    {
        var q = SearchBox.Text.Trim();
        ActionList.ItemsSource = _all.Where(a => string.IsNullOrWhiteSpace(q) || $"{a.Title} {a.Description} {a.Id}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (ActionList.Items.Count > 0) ActionList.SelectedIndex = 0;
    }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Render();
    private async Task ExecuteSelectedAsync()
    {
        if (ActionList.SelectedItem is not ActionDefinition action) return;
        Close();
        await _execute(action.Id);
    }
    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync();
    private async void ActionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await ExecuteSelectedAsync();
    private async void ActionList_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await ExecuteSelectedAsync(); } }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { ActionList.Focus(); if (ActionList.Items.Count > 0) ActionList.SelectedIndex = Math.Max(0, ActionList.SelectedIndex); e.Handled = true; }
        else if (e.Key == Key.Enter) { e.Handled = true; await ExecuteSelectedAsync(); }
        else if (e.Key == Key.Escape) Close();
    }
}
