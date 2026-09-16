using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class SisbolLoginWindow : Window
{
    private readonly SisbolAutomationService _service;
    public SisbolSettings Settings { get; }
    private bool _browserOpened;
    private bool _busy;
    private bool _allowClose;
    private bool _autoChecking;
    private readonly DispatcherTimer _loginMonitor;

    public SisbolLoginWindow(SisbolAutomationService service, SisbolSettings settings)
    {
        InitializeComponent();
        _service = service;
        Settings = settings ?? new SisbolSettings();
        LoginBox.Text = Settings.Login;
        PasswordBox.Password = _service.ReadSavedPassword(Settings);
        SavePasswordCheck.IsChecked = Settings.SavePassword;
        HideAfterLoginCheck.IsChecked = true;
        Settings.HideAfterLogin = true;
        IncludeAutomaticallyCheck.IsChecked = Settings.IncludeAutomatically;
        BrowserBox.SelectedItem = BrowserBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), Settings.Browser, StringComparison.OrdinalIgnoreCase))
            ?? BrowserBox.Items[0];
        App.UiState.Attach(this);
        _loginMonitor = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _loginMonitor.Tick += LoginMonitor_Tick;
        Closing += (_, e) =>
        {
            if (_busy && !_allowClose) e.Cancel = true;
        };
        Closed += (_, _) => _loginMonitor.Stop();
        SetBusy(false);
    }

    private void ReadSettingsFromForm()
    {
        Settings.Browser = (BrowserBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "edge";
        Settings.Login = LoginBox.Text.Trim();
        Settings.SavePassword = SavePasswordCheck.IsChecked == true;
        // Esta janela sempre conclui com o navegador efetivamente oculto.
        Settings.HideAfterLogin = true;
        Settings.IncludeAutomatically = true;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ReadSettingsFromForm();
        SetBusy(true, "Abrindo o navegador e preenchendo a conta…");
        try
        {
            var message = await _service.OpenLoginAsync(Settings, PasswordBox.Password);
            _browserOpened = true;
            ConfirmButton.IsEnabled = true;
            BrowserBox.IsEnabled = false;
            StepBadge.Text = "2. Digitar captcha";
            SetStatus(message, success: false);
            _loginMonitor.Start();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "SisBol", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(ex.Message, success: false, error: true);
        }
        finally { SetBusy(false); }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _autoChecking || !_browserOpened) return;
        ReadSettingsFromForm();
        SetBusy(true, "Validando o login e preparando a tela de matéria…");
        try
        {
            var result = await _service.ConfirmLoginAsync(Settings, PasswordBox.Password);
            SetStatus(result.Message, result.Success, error: !result.Success);
            if (!result.Success)
            {
                _service.ShowCurrentBrowser();
                return;
            }

            CompleteLogin(result.Message);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, success: false, error: true);
            SigfurDialog.Show(this, ex.Message, "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void LoginMonitor_Tick(object? sender, EventArgs e)
    {
        if (_busy || _autoChecking || !_browserOpened || _allowClose) return;
        _autoChecking = true;
        try
        {
            var result = await _service.TryCompleteLoginAfterEnterAsync(Settings);
            if (result.Success) CompleteLogin(result.Message);
        }
        catch
        {
            // A verificação automática é silenciosa; o botão manual continua
            // disponível para mostrar uma mensagem detalhada ao usuário.
        }
        finally { _autoChecking = false; }
    }

    private void CompleteLogin(string message)
    {
        _loginMonitor.Stop();
        StepBadge.Text = "✓ SisBol pronto e oculto";
        SetStatus(message, success: true);
        _allowClose = true;
        SetBusy(false);
        DialogResult = true;
    }

    private void ShowBrowser_Click(object sender, RoutedEventArgs e) => _service.ShowCurrentBrowser();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _loginMonitor.Stop();
        if (_browserOpened) _service.ShowCurrentBrowser();
        DialogResult = false;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !busy && !_browserOpened;
        ConfirmButton.IsEnabled = !busy && _browserOpened;
        CancelButton.IsEnabled = !busy;
        LoginBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        SavePasswordCheck.IsEnabled = !busy;
        HideAfterLoginCheck.IsEnabled = false;
        IncludeAutomaticallyCheck.IsEnabled = false;
        if (!string.IsNullOrWhiteSpace(message)) SetStatus(message, success: false);
    }

    private void SetStatus(string message, bool success, bool error = false)
    {
        StatusText.Text = message;
        var brushKey = error ? "DangerSoftBrush" : success ? "SuccessSoftBrush" : "PrimarySoftBrush";
        var textKey = error ? "DangerBrush" : success ? "SuccessBrush" : "PrimaryDarkBrush";
        StatusBorder.Background = TryFindResource(brushKey) as Brush ?? Brushes.Transparent;
        StatusText.Foreground = TryFindResource(textKey) as Brush ?? (Brush)FindResource("TextBrush");
    }
}
