using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Controls;

/// <summary>Campo monetário com prévia do formato definido no marcador do boletim.</summary>
public sealed class BulletinValueInputControl : Grid
{
    private readonly TextBox _input;
    private readonly TextBlock _preview;
    private readonly string _format;

    public BulletinValueInputControl(string value, string format)
    {
        _format = string.IsNullOrWhiteSpace(format) ? "NUMERO" : format;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _input = new TextBox { Text = value ?? string.Empty, MinWidth = 280, FontSize = 14 };
        _input.TextChanged += (_, e) => { UpdatePreview(); ValueChanged?.Invoke(this, e); };
        _input.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_input.IsKeyboardFocusWithin) return;
            e.Handled = true;
            _input.Focus();
            _input.SelectAll();
        };
        _input.GotKeyboardFocus += (_, _) => _input.SelectAll();
        Children.Add(_input);

        var border = new Border
        {
            Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1)
        };
        border.SetResourceReference(Border.BackgroundProperty, "PrimarySurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, FontWeight = FontWeights.SemiBold };
        _preview.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        border.Child = _preview;
        SetRow(border, 1);
        Children.Add(border);
        UpdatePreview();
    }

    public event RoutedEventHandler? ValueChanged;
    public string Value => _input.Text.Trim();

    private void UpdatePreview()
    {
        _preview.Text = string.IsNullOrWhiteSpace(_input.Text)
            ? "A prévia do valor aparecerá aqui."
            : BulletinService.FormatSmartValue(_input.Text, "money", _format);
    }
}
