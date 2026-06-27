using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class NumberToWordsWindow : Window
{
    private bool _ready;

    public NumberToWordsWindow(string? initialValue = null, bool currency = true)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Loaded += (_, _) =>
        {
            _ready = true;
            ModeBox.SelectedIndex = currency ? 0 : 1;
            InputBox.Text = initialValue ?? string.Empty;
            UpdateResult();
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private bool IsCurrency
        => (ModeBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "number";

    private void UpdateResult()
    {
        if (!_ready || InputBox is null || ResultBox is null || NumericPreviewText is null || ValidationText is null) return;
        var input = InputBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            ResultBox.Text = string.Empty;
            NumericPreviewText.Text = "Digite um valor para converter";
            ValidationText.Text = "PRONTO";
            return;
        }

        if (!NumberToWordsService.TryParse(input, out var value))
        {
            ResultBox.Text = "Confira o valor informado. Exemplo válido: 1.234,56.";
            NumericPreviewText.Text = "Entrada não reconhecida";
            ValidationText.Text = "REVISAR";
            return;
        }

        NumericPreviewText.Text = NumberToWordsService.FormatNumber(value, IsCurrency);
        ResultBox.Text = NumberToWordsService.Convert(value, IsCurrency);
        ValidationText.Text = "CONVERTIDO";
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateResult();
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateResult();

    private void CopyLower_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetConverted(out var converted)) return;
        Clipboard.SetText(converted.ToLowerInvariant());
    }

    private void CopyUpper_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetConverted(out var converted)) return;
        Clipboard.SetText(converted.ToUpperInvariant());
    }

    private bool TryGetConverted(out string converted)
    {
        converted = string.Empty;
        if (!NumberToWordsService.TryParse(InputBox?.Text, out var value))
        {
            ValidationText.Text = "REVISAR";
            InputBox?.Focus();
            InputBox?.SelectAll();
            return false;
        }

        converted = NumberToWordsService.Convert(value, IsCurrency);
        return !string.IsNullOrWhiteSpace(converted);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        InputBox.Focus();
    }
}
