using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class AppearanceWindow : Window
{
    private readonly ThemeService _themeService;
    private readonly List<ThemeChoice> _choices;

    public AppearanceWindow(ThemeService themeService)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _themeService = themeService;
        _choices = themeService.AvailableThemes.Select(x => new ThemeChoice(x, x.Id == themeService.CurrentThemeId)).ToList();
        ThemeList.ItemsSource = _choices;
        ThemeList.SelectedItem = _choices.FirstOrDefault(x => x.Id == themeService.CurrentThemeId) ?? _choices.FirstOrDefault();
    }

    private void ThemeList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ThemeList.SelectedItem is ThemeChoice selected)
            _themeService.Apply(selected.Id);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeList.SelectedItem is not ThemeChoice selected) return;
        await _themeService.ApplyAndSaveAsync(selected.Id);
        foreach (var item in _choices) item.IsCurrent = item.Id == selected.Id;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            _themeService.Apply(_choices.FirstOrDefault(x => x.IsCurrent)?.Id ?? "institutional-blue");
        base.OnClosed(e);
    }
}

public sealed class ThemeChoice : INotifyPropertyChanged
{
    private bool _isCurrent;
    public ThemeChoice(ThemePalette palette, bool isCurrent)
    {
        Id = palette.Id;
        DisplayName = palette.DisplayName;
        Description = palette.Description;
        PrimaryBrush = Brush(palette.Primary);
        DarkBrush = Brush(palette.PrimaryDark);
        SoftBrush = Brush(palette.PrimarySoft);
        _isCurrent = isCurrent;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public Brush PrimaryBrush { get; }
    public Brush DarkBrush { get; }
    public Brush SoftBrush { get; }
    public bool IsCurrent { get => _isCurrent; set { if (_isCurrent == value) return; _isCurrent = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
