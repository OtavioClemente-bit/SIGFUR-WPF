using System.Collections.ObjectModel;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class HotkeysWindow : Window
{
    private readonly IReadOnlyList<ActionDefinition> _actions;
    private readonly ObservableCollection<HotkeyRow> _rows = [];
    public HotkeysWindow(IReadOnlyList<ActionDefinition> actions, Dictionary<string, string> current)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _actions = actions;
        foreach (var action in actions.Where(a => current.ContainsKey(a.Id)))
            _rows.Add(new HotkeyRow { Id = action.Id, Title = action.Title, HotKey = current.GetValueOrDefault(action.Id, string.Empty) });
        foreach (var key in new[] { "font_up", "font_down", "font_reset" })
            _rows.Add(new HotkeyRow { Id = key, Title = key switch { "font_up" => "Aumentar interface", "font_down" => "Diminuir interface", _ => "Restaurar interface" }, HotKey = current.GetValueOrDefault(key, string.Empty) });
        Grid.ItemsSource = _rows;
        Hotkeys = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
    }
    public Dictionary<string, string> Hotkeys { get; private set; }
    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = SettingsService.DefaultHotkeys();
        foreach (var row in _rows) row.HotKey = defaults.GetValueOrDefault(row.Id, string.Empty);
        Grid.Items.Refresh();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var duplicate = _rows.Where(r => !string.IsNullOrWhiteSpace(r.HotKey)).GroupBy(r => r.HotKey.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            SigfurDialog.Show(this, $"O atalho {duplicate.Key} está repetido.", "Atalhos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var row in _rows) Hotkeys[row.Id] = row.HotKey.Trim();
        DialogResult = true;
    }
}

public sealed class HotkeyRow
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string HotKey { get; set; } = string.Empty;
}
