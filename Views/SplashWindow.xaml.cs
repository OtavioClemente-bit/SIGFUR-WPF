using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SIGFUR.Wpf.Views;

public partial class SplashWindow : Window
{
    private readonly ObservableCollection<SplashStep> _steps =
    [
        new("Identidade visual", "Tema, escala e perfil institucional", 12),
        new("Banco oficial", "Integridade, efetivo e vínculos administrativos", 32),
        new("Proteção", "Snapshot consistente antes da abertura", 54),
        new("Áreas de trabalho", "Janelas independentes e múltiplos monitores", 72),
        new("Controles", "Pagamentos, escalas, alertas e auditorias", 90),
        new("Painel", "SIGFUR 6.0 pronto para operação", 100)
    ];

    private readonly DispatcherTimer _pulseTimer;
    private double _targetProgress;

    public SplashWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = version;
        StepsControl.ItemsSource = _steps;
        Opacity = 0;
        Loaded += OnLoaded;
        _pulseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => AnimateProgress(), Dispatcher);
        _pulseTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    public void SetStage(string title, string detail, double progress, string footer)
    {
        Dispatcher.Invoke(() =>
        {
            StageText.Text = title;
            DetailText.Text = detail;
            FooterText.Text = footer;
            _targetProgress = Math.Max(_targetProgress, progress);
            UpdateSteps(progress);
        });
    }

    public async Task CloseSmoothAsync()
    {
        _targetProgress = 100;
        while (Progress.Value < 99.4) await Task.Delay(16);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(250)));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(250)));
        await Task.Delay(270);
        _pulseTimer.Stop();
        Close();
    }

    private void AnimateProgress()
    {
        var delta = _targetProgress - Progress.Value;
        Progress.Value = Math.Abs(delta) < .08 ? _targetProgress : Progress.Value + delta * .11;
        PercentText.Text = $"{Math.Round(Progress.Value):00}%";
    }

    private void UpdateSteps(double progress)
    {
        var primary = Application.Current.TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var soft = Application.Current.TryFindResource("PrimarySoftBrush") as Brush ?? Brushes.AliceBlue;
        var subtle = Application.Current.TryFindResource("SubtleTextBrush") as Brush ?? Brushes.SlateGray;
        var surface = Application.Current.TryFindResource("SurfaceAltBrush") as Brush ?? Brushes.WhiteSmoke;

        foreach (var step in _steps)
        {
            var done = progress >= step.Threshold - 1;
            step.Marker = done ? "✓" : "•";
            step.Color = done ? primary : subtle;
            step.Background = done ? soft : surface;
        }
        StepsControl.Items.Refresh();
    }
}

public sealed class SplashStep
{
    public SplashStep(string title, string subtitle, double threshold)
    {
        Title = title;
        Subtitle = subtitle;
        Threshold = threshold;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public double Threshold { get; }
    public string Marker { get; set; } = "•";
    public Brush Color { get; set; } = Brushes.SlateGray;
    public Brush Background { get; set; } = Brushes.WhiteSmoke;
}
