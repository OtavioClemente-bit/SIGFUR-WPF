using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class AppearanceWindow : Window
{
    private readonly ThemeService _themeService;
    private readonly SettingsService _settings;
    private readonly List<ThemeChoice> _choices;
    private readonly string _initialThemeId;
    private readonly double _initialScale;
    private bool _ready;

    public AppearanceWindow(ThemeService themeService, SettingsService settings, double currentScale)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _themeService = themeService;
        _settings = settings;
        _initialThemeId = themeService.CurrentThemeId;
        _initialScale = GlobalUiScaleService.Sanitize(currentScale <= 0 ? GlobalUiScaleService.CurrentScale : currentScale);

        _choices = themeService.AvailableThemes.Select(x => new ThemeChoice(x, x.Id == themeService.CurrentThemeId)).ToList();
        ThemeList.ItemsSource = _choices;
        ThemeList.SelectedItem = _choices.FirstOrDefault(x => x.Id == themeService.CurrentThemeId) ?? _choices.FirstOrDefault();

        ScaleSlider.Value = Math.Round(_initialScale * 100d);
        UpdateScaleTexts(_initialScale);
        _ready = true;
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ThemeList.SelectedItem is ThemeChoice selected)
            _themeService.Apply(selected.Id);
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var scale = GlobalUiScaleService.Sanitize(ScaleSlider.Value / 100d);
        UpdateScaleTexts(scale);
        GlobalUiScaleService.Apply(scale);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)) return;
        ScaleSlider.Value = Math.Round(GlobalUiScaleService.Sanitize(scale) * 100d);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaultTheme = _choices.FirstOrDefault(x => x.Id == "institutional-blue") ?? _choices.FirstOrDefault();
        if (defaultTheme is not null) ThemeList.SelectedItem = defaultTheme;
        ScaleSlider.Value = 100d;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeList.SelectedItem is not ThemeChoice selected) return;
        var scale = GlobalUiScaleService.Sanitize(ScaleSlider.Value / 100d);

        await _themeService.ApplyAndSaveAsync(selected.Id);
        await _settings.SaveFontScaleAsync(scale);
        GlobalUiScaleService.Apply(scale);

        foreach (var item in _choices) item.IsCurrent = item.Id == selected.Id;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
        {
            _themeService.Apply(_initialThemeId);
            GlobalUiScaleService.Apply(_initialScale);
        }
        base.OnClosed(e);
    }

    private void UpdateScaleTexts(double scale)
    {
        var text = scale.ToString("P0", CultureInfo.GetCultureInfo("pt-BR"));
        HeaderScaleText.Text = text;
        ScaleValueText.Text = text;
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
