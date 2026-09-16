using System.Windows;
using System.Windows.Controls;

namespace SIGFUR.Wpf.Views.Military;

public partial class ColumnChooserWindow : Window
{
    public sealed record ColumnEntry(string Header, DataGridColumn Column, bool Required, bool DefaultVisible);
    private readonly IReadOnlyList<ColumnEntry> _entries;
    private readonly Dictionary<ColumnEntry, CheckBox> _checks = [];

    public ColumnChooserWindow(IReadOnlyList<ColumnEntry> entries)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _entries = entries;
        foreach (var entry in _entries)
        {
            var check = new CheckBox
            {
                Content = entry.Required ? entry.Header + " (essencial)" : entry.Header,
                IsChecked = entry.Column.Visibility == Visibility.Visible,
                IsEnabled = !entry.Required,
                Margin = new Thickness(0, 4, 0, 8),
                FontWeight = entry.Required ? FontWeights.SemiBold : FontWeights.Normal
            };
            _checks[entry] = check;
            OptionsPanel.Children.Add(check);
        }
    }

    private void EssentialOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries)
            _checks[entry].IsChecked = entry.Required;
    }

    private void RestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries)
            _checks[entry].IsChecked = entry.Required || entry.DefaultVisible;
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries)
            _checks[entry].IsChecked = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries)
            entry.Column.Visibility = entry.Required || _checks[entry].IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
