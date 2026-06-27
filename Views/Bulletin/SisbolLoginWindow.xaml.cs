using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private string? _lastDiagnosticArtifact;

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
        DiagnosticHintText.Text = $"Assunto de teste para buscar: {_service.DiagnosticSubjectExample}. Corpo de teste: {_service.DiagnosticBodyExample}";
        BrowserBox.SelectedItem = BrowserBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), Settings.Browser, StringComparison.OrdinalIgnoreCase))
            ?? BrowserBox.Items[0];
        App.UiState.Attach(this);
        Closing += (_, e) =>
        {
            if (_busy && !_allowClose) e.Cancel = true;
        };
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
            StepBadge.Text = "2. Validar sessão";
            SetStatus(message, success: false);
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
        if (_busy || !_browserOpened) return;
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

            StepBadge.Text = "✓ SisBol pronto";
            _allowClose = true;
            SetBusy(false);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, success: false, error: true);
            SigfurDialog.Show(this, ex.Message, "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private void ShowBrowser_Click(object sender, RoutedEventArgs e) => _service.ShowCurrentBrowser();

    private async void DiagnosticStart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Preparando o gravador de fluxo do SisBol…");
        try
        {
            var folder = await _service.StartSisbolDiagnosticAsync();
            _lastDiagnosticArtifact = folder;
            DiagnosticStatusText.Text = "Diagnóstico iniciado. No navegador, faça o caminho real: buscar assunto, selecionar a linha, colar corpo e, se estiver autorizado, clicar em Incluir. Depois volte e gere o ZIP.";
            SetStatus("Modo diagnóstico iniciado. O navegador ficou visível e a tela de matéria foi aberta.", success: true);
        }
        catch (Exception ex)
        {
            DiagnosticStatusText.Text = ex.Message;
            SetStatus(ex.Message, success: false, error: true);
            SigfurDialog.Show(this, ex.Message, "Diagnóstico SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void DiagnosticSubject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Colando assunto de teste no campo ativo do SisBol…");
        try
        {
            var result = await _service.PasteDiagnosticSubjectAsync();
            DiagnosticStatusText.Text = result + " Agora clique em Buscar/selecionar no SisBol, manualmente.";
            SetStatus(result, success: true);
        }
        catch (Exception ex)
        {
            DiagnosticStatusText.Text = ex.Message;
            SetStatus(ex.Message, success: false, error: true);
        }
        finally { SetBusy(false); }
    }

    private async void DiagnosticBody_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Colando corpo de teste no editor ativo do SisBol…");
        try
        {
            var result = await _service.PasteDiagnosticBodyAsync();
            DiagnosticStatusText.Text = result + " Confira visualmente antes de qualquer inclusão real.";
            SetStatus(result, success: true);
        }
        catch (Exception ex)
        {
            DiagnosticStatusText.Text = ex.Message;
            SetStatus(ex.Message, success: false, error: true);
        }
        finally { SetBusy(false); }
    }

    private async void DiagnosticCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Registrando checkpoint do diagnóstico…");
        try
        {
            var result = await _service.AddDiagnosticCheckpointAsync("Checkpoint manual criado pelo usuário no SIGFUR.");
            DiagnosticStatusText.Text = "Checkpoint registrado: " + result;
            SetStatus("Checkpoint do diagnóstico registrado.", success: true);
        }
        catch (Exception ex)
        {
            DiagnosticStatusText.Text = ex.Message;
            SetStatus(ex.Message, success: false, error: true);
        }
        finally { SetBusy(false); }
    }

    private async void DiagnosticFinish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Coletando HTML, frames, print e eventos do SisBol…");
        try
        {
            var zip = await _service.FinishSisbolDiagnosticAsync();
            _lastDiagnosticArtifact = zip;
            DiagnosticStatusText.Text = "Pacote gerado: " + zip;
            SetStatus("Pacote ZIP do diagnóstico gerado. Envie esse ZIP para corrigirmos a automação.", success: true);
            ShellService.RevealInExplorer(zip);
        }
        catch (Exception ex)
        {
            DiagnosticStatusText.Text = ex.Message;
            SetStatus(ex.Message, success: false, error: true);
            SigfurDialog.Show(this, ex.Message, "Diagnóstico SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private void DiagnosticOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _lastDiagnosticArtifact ?? _service.LastDiagnosticZipFile ?? _service.LastDiagnosticDirectory;
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            SigfurDialog.Show(this, "Nenhum pacote de diagnóstico foi gerado ainda.", "Diagnóstico SisBol", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShellService.RevealInExplorer(path);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_browserOpened) _service.ShowCurrentBrowser();
        DialogResult = false;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !busy;
        ConfirmButton.IsEnabled = !busy && _browserOpened;
        CancelButton.IsEnabled = !busy;
        LoginBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        SavePasswordCheck.IsEnabled = !busy;
        HideAfterLoginCheck.IsEnabled = false;
        IncludeAutomaticallyCheck.IsEnabled = false;
        DiagnosticStartButton.IsEnabled = !busy && _browserOpened;
        DiagnosticSubjectButton.IsEnabled = !busy && _service.LastDiagnosticDirectory is not null;
        DiagnosticBodyButton.IsEnabled = !busy && _service.LastDiagnosticDirectory is not null;
        DiagnosticCheckpointButton.IsEnabled = !busy && _service.LastDiagnosticDirectory is not null;
        DiagnosticFinishButton.IsEnabled = !busy && _service.LastDiagnosticDirectory is not null;
        DiagnosticOpenFolderButton.IsEnabled = !busy && (_lastDiagnosticArtifact is not null || _service.LastDiagnosticDirectory is not null || _service.LastDiagnosticZipFile is not null);
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
