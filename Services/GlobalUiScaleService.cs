using System.Windows;
using System.Windows.Media;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Controla a escala visual global do SIGFUR.
///
/// A versão anterior diminuía apenas o RootGrid da janela principal. Com isso,
/// qualquer módulo aberto depois continuava com fonte/tamanho próprio, causando
/// diferença visual entre a tela principal, cadastro, férias, auditorias etc.
///
/// Este serviço aplica a mesma escala ao conteúdo de todas as janelas anexadas ao
/// UiState, inclusive as que forem abertas depois da alteração.
/// </summary>
public static class GlobalUiScaleService
{
    public const double MinimumScale = 0.65;
    public const double MaximumScale = 1.45;
    public const double DefaultScale = 1.00;

    private static readonly HashSet<Window> AttachedWindows = [];

    public static event EventHandler? ScaleChanged;
    public static double CurrentScale { get; private set; } = DefaultScale;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (AttachedWindows.Add(window))
        {
            window.Loaded += (_, _) => ApplyToWindow(window);
            window.ContentRendered += (_, _) => ApplyToWindow(window);
            window.Closed += (_, _) => AttachedWindows.Remove(window);
        }

        ApplyToWindow(window);
    }

    public static void Apply(double scale, bool raiseEvent = true)
    {
        scale = Sanitize(scale);
        CurrentScale = scale;

        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            resources["GlobalUiScale"] = scale;
            resources["GlobalFontSize"] = 12d * scale;
            resources["GlobalSmallFontSize"] = 10d * scale;
            resources["GlobalLargeFontSize"] = 16d * scale;
        }

        var application = Application.Current;
        if (application is not null)
        {
            foreach (Window window in application.Windows)
                ApplyToWindow(window);
        }

        if (raiseEvent) ScaleChanged?.Invoke(null, EventArgs.Empty);
    }

    public static double Sanitize(double scale)
        => Math.Clamp(double.IsFinite(scale) ? scale : DefaultScale, MinimumScale, MaximumScale);

    private static void ApplyToWindow(Window window)
    {
        if (window.Content is not FrameworkElement content) return;

        var scale = Sanitize(CurrentScale);
        if (Math.Abs(scale - DefaultScale) < 0.001)
        {
            content.LayoutTransform = Transform.Identity;
        }
        else
        {
            content.LayoutTransform = new ScaleTransform(scale, scale);
        }

        // FontSize é herdável. Ele ajuda controles que não estejam visualmente dentro
        // do conteúdo principal da janela, enquanto o LayoutTransform cobre fontes
        // declaradas explicitamente no XAML.
        window.FontSize = 12d;
        window.InvalidateMeasure();
        window.InvalidateVisual();
    }
}
