using System.Windows;
using System.Windows.Controls;

namespace SIGFUR.Wpf.Controls;

public partial class SigfurLoadingOverlay : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SigfurLoadingOverlay),
        new PropertyMetadata("SIGFUR trabalhando em segundo plano"));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(SigfurLoadingOverlay),
        new PropertyMetadata("Aguarde enquanto o SIGFUR conclui a operação."));

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(string), typeof(SigfurLoadingOverlay),
        new PropertyMetadata("Você pode continuar usando outras janelas do SIGFUR enquanto o processamento termina."));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(SigfurLoadingOverlay),
        new PropertyMetadata(450d));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string Footer
    {
        get => (string)GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public SigfurLoadingOverlay()
    {
        InitializeComponent();
    }
}
