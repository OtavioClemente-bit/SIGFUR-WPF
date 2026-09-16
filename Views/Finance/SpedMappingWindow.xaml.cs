using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class SpedMappingWindow : Window
{
    private readonly SpedMappingService _service;
    private readonly SpedDiexDraft _draft;
    private readonly string _reviewDirectory;
    private readonly SpedMappingSettings? _presets;
    private SpedMappingSettings _settings = new();
    private bool _busy;
    private bool _allowClose;

    public SpedAutomationResult? AutomationResult { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    public SpedMappingWindow(SpedMappingService service, SpedDiexDraft draft, string reviewDirectory, SpedMappingSettings? presets = null)
    {
        InitializeComponent();
        _service = service;
        _draft = draft;
        _reviewDirectory = reviewDirectory;
        _presets = presets;
        _service.StatusChanged += Service_StatusChanged;
        _service.RecorderMessage += Service_RecorderMessage;
        App.UiState.Attach(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _service.LoadSettingsAsync();
            if (_presets is not null)
            {
                _settings.SenderSearch = _presets.SenderSearch;
                _settings.ExternalRecipientSearch = _presets.ExternalRecipientSearch;
                _settings.ClassificationSearch = _presets.ClassificationSearch;
                _settings.DocumentPurpose = _presets.DocumentPurpose;
                _settings.Subject = _presets.Subject;
                DiexDefaultsCard.Visibility = Visibility.Collapsed;
            }
            LoginBox.Text = _settings.Login;
            SavePasswordCheck.IsChecked = _settings.SavePassword;
            PasswordInput.Password = _service.GetSavedPassword(_settings);
            RefreshChoiceSources();
            StatusText.Text = "Informe as credenciais e abra o SPED.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível restaurar as preferências do SPED.";
            await App.Log.WriteAsync("Falha ao carregar as preferências do mapeamento SPED.", ex);
        }
    }

    private async void OpenLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        LastError = string.Empty;
        AutomationResult = null;
        await RunBusyAsync(async () =>
        {
            ReadSettingsFromControls();
            _draft.Subject = _settings.Subject;
            StatusText.Text = "Executando o SPED em segundo plano...";
            AutomationResult = await _service.RunHiddenAndSaveAsync(_settings, PasswordInput.Password, _draft, _reviewDirectory);
            RefreshChoiceSources();
            StatusText.Text = AutomationResult.Message;
            DialogResult = true;
        }, "Não foi possível automatizar o SPED");
    }

    private async void StartMapping_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunBusyAsync(async () =>
        {
            ReadSettingsFromControls();
            await _service.SaveSettingsAsync(_settings, PasswordInput.Password);
            RefreshChoiceSources();
            StatusText.Text = "Abrindo o DIEx e preenchendo os padrões salvos...";
            await _service.OpenDiexAndStartMappingAsync(_settings, _draft);
            FinishButton.IsEnabled = true;
            StartMappingButton.IsEnabled = false;
            UpdateStatus(_service.Status);
        }, "Não foi possível iniciar o mapeamento");
    }

    private async void SaveChoices_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunBusyAsync(async () =>
        {
            ReadSettingsFromControls();
            await _service.SaveSettingsAsync(_settings, PasswordInput.Password);
            RefreshChoiceSources();
            StatusText.Text = "Remetente, destinatário, classificação, finalidade e assunto salvos como padrão.";
        }, "Não foi possível salvar as escolhas");
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        SenderBox.Text = "PHABLLO";
        RecipientBox.Text = "E1";
        ClassificationBox.Text = "085.612 - GRATIFICAÇÕES";
        PurposeBox.Text = "Geral";
        SubjectBox.Text = "Gratificação de Representação (2%)";
        StatusText.Text = "Padrões da Grat Rep restaurados. Clique em Salvar escolhas para mantê-los.";
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new SaveFileDialog
        {
            Title = "Exportar mapeamento seguro do SPED",
            Filter = "Arquivo ZIP|*.zip",
            FileName = $"SPED_mapeamento_{DateTime.Now:yyyyMMdd_HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunBusyAsync(async () =>
        {
            StatusText.Text = "Finalizando a gravação e preparando o pacote...";
            var exported = await _service.FinishAndExportAsync(dialog.FileName);
            FinishButton.IsEnabled = false;
            MappingInfoText.Text = $"Mapeamento concluído com {_service.Status.EventCount} ação(ões).\nArquivo: {exported}";
            RecordingBadgeText.Text = "Concluído";
            RecordingBadge.Background = (System.Windows.Media.Brush)FindResource("SuccessSoftBrush");
            StatusText.Text = "Log exportado. Você já pode enviar o ZIP para análise.";
            ShellService.OpenPath(Path.GetDirectoryName(exported)!);
        }, "Não foi possível exportar o mapeamento");
    }

    private async Task RunBusyAsync(Func<Task> action, string errorTitle)
    {
        _busy = true;
        OpenLoginButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await App.Log.WriteAsync(errorTitle, ex);
            SigfurDialog.Show(this, ex.Message, errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            OpenLoginButton.IsEnabled = true;
            var status = _service.Status;
            if (!status.IsRecording) StartMappingButton.IsEnabled = status.BrowserOpen;
        }
    }

    private void Service_StatusChanged(object? sender, SpedMappingStatus status)
        => Dispatcher.BeginInvoke(new Action(() => UpdateStatus(status)));

    private void Service_RecorderMessage(object? sender, string message)
        => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));

    private void UpdateStatus(SpedMappingStatus status)
    {
        if (status.IsRecording)
        {
            RecordingBadgeText.Text = "GRAVANDO";
            RecordingBadge.Background = (System.Windows.Media.Brush)FindResource("DangerSoftBrush");
            MappingInfoText.Text = $"Gravação ativa — {status.EventCount} ação(ões) registrada(s). Faça o DIEx normalmente no navegador e, ao terminar, volte aqui para exportar.";
            FinishButton.IsEnabled = true;
        }
        else if (status.EventCount > 0)
        {
            RecordingBadgeText.Text = "Pausado";
            MappingInfoText.Text = $"{status.EventCount} ação(ões) registrada(s).";
        }
    }

    private void ReadSettingsFromControls()
    {
        _settings.Login = LoginBox.Text.Trim();
        _settings.SavePassword = SavePasswordCheck.IsChecked == true;
        if (_presets is not null)
        {
            _settings.SenderSearch = _presets.SenderSearch;
            _settings.ExternalRecipientSearch = _presets.ExternalRecipientSearch;
            _settings.ClassificationSearch = _presets.ClassificationSearch;
            _settings.DocumentPurpose = _presets.DocumentPurpose;
            _settings.Subject = _presets.Subject;
            return;
        }
        _settings.SenderSearch = SenderBox.Text.Trim();
        _settings.ExternalRecipientSearch = RecipientBox.Text.Trim();
        _settings.ClassificationSearch = ClassificationBox.Text.Trim();
        _settings.DocumentPurpose = PurposeBox.Text.Trim();
        _settings.Subject = SubjectBox.Text.Trim();
    }

    private void RefreshChoiceSources()
    {
        _settings.SenderHistory ??= [];
        _settings.ExternalRecipientHistory ??= [];
        _settings.ClassificationHistory ??= [];
        _settings.DocumentPurposeHistory ??= [];
        SenderBox.Text = _settings.SenderSearch;
        RecipientBox.Text = _settings.ExternalRecipientSearch;
        ClassificationBox.Text = _settings.ClassificationSearch;
        PurposeBox.Text = _settings.DocumentPurpose;
        SubjectBox.Text = _settings.Subject;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_service.Status.IsRecording) return;
        var result = SigfurDialog.Show(this,
            "O mapeamento ainda está sendo gravado. Fechar agora encerrará o navegador e o log ainda não será exportado. Deseja fechar mesmo assim?",
            "Mapeamento do SPED", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) e.Cancel = true;
        else _allowClose = true;
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _service.StatusChanged -= Service_StatusChanged;
        _service.RecorderMessage -= Service_RecorderMessage;
        await _service.DisposeAsync();
    }
}
