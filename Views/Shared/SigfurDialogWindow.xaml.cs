using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SIGFUR.Wpf.Views.Shared;

public partial class SigfurDialogWindow : Window
{
    private readonly MessageBoxResult _fallbackResult;

    public SigfurDialogWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        InitializeComponent();
        CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "SIGFUR" : caption;
        MessageText.Text = message ?? string.Empty;
        _fallbackResult = ResolveFallback(buttons, defaultResult);
        ConfigureVisual(image);
        BuildButtons(buttons, defaultResult);
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private void ConfigureVisual(MessageBoxImage image)
    {
        var (glyph, category, brushKey) = image switch
        {
            MessageBoxImage.Error => ("!", "Atenção necessária", "DangerBrush"),
            MessageBoxImage.Warning => ("!", "Verifique antes de continuar", "WarningBrush"),
            MessageBoxImage.Information => ("i", "Informação do SIGFUR", "InfoBrush"),
            MessageBoxImage.Question => ("?", "Confirmação do sistema", "PrimaryBrush"),
            _ => ("i", "Mensagem do sistema", "PrimaryBrush")
        };

        IconText.Text = glyph;
        CategoryText.Text = category;
        if (TryFindResource(brushKey) is Brush brush)
        {
            IconText.Foreground = brush;
            if (brush is SolidColorBrush solid)
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(24, solid.Color.R, solid.Color.G, solid.Color.B));
        }
    }

    private void BuildButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        var definitions = buttons switch
        {
            MessageBoxButton.OKCancel => new[]
            {
                ("Cancelar", MessageBoxResult.Cancel, "SecondaryButtonStyle"),
                ("Confirmar", MessageBoxResult.OK, "PrimaryButtonStyle")
            },
            MessageBoxButton.YesNo => new[]
            {
                ("Não", MessageBoxResult.No, "SecondaryButtonStyle"),
                ("Sim", MessageBoxResult.Yes, "PrimaryButtonStyle")
            },
            MessageBoxButton.YesNoCancel => new[]
            {
                ("Cancelar", MessageBoxResult.Cancel, "GhostButtonStyle"),
                ("Não", MessageBoxResult.No, "SecondaryButtonStyle"),
                ("Sim", MessageBoxResult.Yes, "PrimaryButtonStyle")
            },
            _ => new[] { ("OK", MessageBoxResult.OK, "PrimaryButtonStyle") }
        };

        Button? defaultButton = null;
        foreach (var (label, result, styleKey) in definitions)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource(styleKey),
                IsDefault = result == defaultResult || defaultResult == MessageBoxResult.None && result is MessageBoxResult.OK or MessageBoxResult.Yes,
                IsCancel = result == MessageBoxResult.Cancel || buttons == MessageBoxButton.YesNo && result == MessageBoxResult.No
            };
            button.Click += (_, _) => Complete(result);
            ButtonsPanel.Children.Add(button);
            if (button.IsDefault) defaultButton = button;
        }

        Loaded += (_, _) => (defaultButton ?? ButtonsPanel.Children.OfType<Button>().LastOrDefault())?.Focus();
    }

    private static MessageBoxResult ResolveFallback(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        if (defaultResult != MessageBoxResult.None) return defaultResult;
        return buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
    }

    private void Complete(MessageBoxResult result)
    {
        Result = result;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Complete(_fallbackResult);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Complete(_fallbackResult);
        }
    }
}
