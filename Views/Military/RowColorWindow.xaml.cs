using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Military;

public partial class RowColorWindow : Window
{
    private readonly string _automaticColor;
    private readonly int _targetCount;
    private readonly bool _mixedColors;
    private Button? _selectedButton;

    public RowColorWindow(string? currentCustomColor, string? effectiveCurrentColor = null, int targetCount = 1, bool mixedColors = false)
    {
        InitializeComponent();
        App.UiState.Attach(this);

        SelectedColor = currentCustomColor?.Trim() ?? string.Empty;
        _automaticColor = effectiveCurrentColor?.Trim() ?? "#FFFFFF";
        _targetCount = Math.Max(1, targetCount);
        _mixedColors = mixedColors;
        Title = _targetCount == 1 ? "Cor de destaque" : $"Cor de destaque — {_targetCount} militares";
        HeadingText.Text = _targetCount == 1 ? "Destaque visual do militar" : $"Destaque visual de {_targetCount} militares";

        Loaded += (_, _) =>
        {
            if (_mixedColors)
            {
                ApplyButton.IsEnabled = false;
                UpdatePreview("#FFFFFF", $"Os {_targetCount} militares selecionados possuem cores diferentes. Escolha uma cor ou remova os destaques manuais.");
                SelectMatchingButton(null);
                return;
            }

            var effective = string.IsNullOrWhiteSpace(SelectedColor) ? _automaticColor : SelectedColor;
            UpdatePreview(effective, string.IsNullOrWhiteSpace(SelectedColor)
                ? "Cor automática da hierarquia/ano — nenhum destaque manual aplicado."
                : $"Destaque manual atualmente salvo: {SelectedColor.ToUpperInvariant()}.");
            SelectMatchingButton(SelectedColor);
        };
    }

    public string SelectedColor { get; private set; }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        SelectedColor = button.Tag?.ToString() ?? string.Empty;
        _selectedButton = button;
        ApplyButton.IsEnabled = true;
        UpdateButtonSelection();
        UpdatePreview(SelectedColor, $"Nova cor escolhida: {button.Content} ({SelectedColor.ToUpperInvariant()}).");
    }

    private void SelectMatchingButton(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            _selectedButton = null;
            UpdateButtonSelection();
            return;
        }

        _selectedButton = PaletteGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Tag?.ToString(), color, StringComparison.OrdinalIgnoreCase));
        UpdateButtonSelection();
    }

    private void UpdateButtonSelection()
    {
        foreach (var child in PaletteGrid.Children.OfType<Button>())
        {
            var selected = ReferenceEquals(child, _selectedButton);
            child.Opacity = _selectedButton is null || selected ? 1.0 : 0.72;
            child.BorderThickness = selected ? new Thickness(4) : new Thickness(1.5);
            child.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
        }
    }

    private void UpdatePreview(string color, string description)
    {
        CurrentColorPreview.Background = ToBrush(color, Brushes.White);
        CurrentColorPreview.BorderBrush = ToBrush(DarkenForBorder(color), Brushes.SlateGray);
        CurrentColorNameText.Text = ColorDisplayName(color);
        SelectedColorText.Text = description;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = string.Empty;
        _selectedButton = null;
        ApplyButton.IsEnabled = true;
        UpdateButtonSelection();
        var description = _targetCount == 1
            ? "O destaque manual será removido e a linha voltará à cor automática da hierarquia/ano."
            : $"Os destaques manuais serão removidos dos {_targetCount} militares; cada linha voltará à cor automática da sua hierarquia/ano.";
        UpdatePreview(_automaticColor, description);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private static Brush ToBrush(string? value, Brush fallback)
    {
        try
        {
            var converted = new BrushConverter().ConvertFromString(value ?? string.Empty) as Brush;
            if (converted is null) return fallback;
            if (converted.CanFreeze) converted.Freeze();
            return converted;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ColorDisplayName(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return "Sem cor definida";
        return color.ToUpperInvariant() switch
        {
            "#BBDEFB" => "Azul claro",
            "#64B5F6" => "Azul forte",
            "#A5D6A7" => "Verde claro",
            "#81C784" => "Verde forte",
            "#FFF59D" => "Amarelo claro",
            "#FFD54F" => "Amarelo forte",
            "#FFCC80" => "Laranja claro",
            "#FFB74D" => "Laranja forte",
            "#EF9A9A" => "Vermelho claro",
            "#E57373" => "Vermelho forte",
            "#F48FB1" => "Rosa claro",
            "#F06292" => "Rosa forte",
            "#CE93D8" => "Roxo claro",
            "#BA68C8" => "Roxo forte",
            "#80DEEA" => "Ciano claro",
            "#4DD0E1" => "Ciano forte",
            "#CFD8DC" => "Cinza claro",
            "#90A4AE" => "Cinza forte",
            "#C5E1A5" => "Lima claro",
            "#AED581" => "Lima forte",
            _ => $"Cor atual ({color.ToUpperInvariant()})"
        };
    }

    private static string DarkenForBorder(string? color)
    {
        try
        {
            var parsed = (Color)ColorConverter.ConvertFromString(color ?? "#FFFFFF")!;
            byte Darken(byte component) => (byte)Math.Max(0, component * 0.62);
            return $"#{Darken(parsed.R):X2}{Darken(parsed.G):X2}{Darken(parsed.B):X2}";
        }
        catch
        {
            return "#475569";
        }
    }
}
