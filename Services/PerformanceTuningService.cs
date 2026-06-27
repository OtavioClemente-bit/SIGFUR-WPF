using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Ajustes globais leves para manter o SIGFUR responsivo em computadores mais
/// antigos, sessões remotas e máquinas com driver de vídeo limitado.
///
/// Importante: as propriedades de virtualização são declaradas nos estilos XAML.
/// Elas não podem ser alteradas depois que o ItemsControl já foi medido pelo WPF.
/// Por isso este serviço nunca percorre DataGrids/ListViews em tempo de execução.
/// </summary>
public static class PerformanceTuningService
{
    private static bool _installed;

    public static bool CompatibilityMode { get; private set; }
    public static bool ReducedEffects { get; private set; }
    public static bool SlowStorageDetected { get; private set; }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        var tier = RenderCapability.Tier >> 16;
        var forced = IsEnabled("SIGFUR_PERFORMANCE_MODE");
        var fullEffects = IsEnabled("SIGFUR_FULL_EFFECTS");
        var forceSoftware = IsEnabled("SIGFUR_SOFTWARE_RENDERING");
        var remote = IsRemoteSession();
        SlowStorageDetected = IsSlowStorageLocation(AppContext.BaseDirectory);

        CompatibilityMode = forced || remote || tier < 2 || SlowStorageDetected;
        ReducedEffects = !fullEffects;

        try
        {
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline),
                new FrameworkPropertyMetadata(CompatibilityMode ? 24 : 45));
        }
        catch { }

        if (forceSoftware || remote || tier == 0)
        {
            try { RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; }
            catch { }
        }

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;

        // Apenas propriedades seguras e herdáveis. Não altera ItemsPanel,
        // VirtualizationMode, ScrollUnit ou CanContentScroll após o Measure.
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(window, TextHintingMode.Fixed);

        if (ReducedEffects && window.Effect is not null)
            window.Effect = null;
    }

    private static bool IsEnabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsSlowStorageLocation(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Network or DriveType.Removable;
        }
        catch { return false; }
    }

    private static bool IsRemoteSession()
    {
        try { return GetSystemMetrics(0x1000) != 0; }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
