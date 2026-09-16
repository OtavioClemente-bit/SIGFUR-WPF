using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class SpedProcessLearningWindow : Window
{
    private readonly SpedMappingService _service = new(App.Paths);
    private SpedProcessAutomationSettings _settings = new();
    private bool _busy;
    private bool _allowClose;

    public SpedProcessLearningWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service.StatusChanged += Service_StatusChanged;
        _service.RecorderMessage += Service_RecorderMessage;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _service.LoadProcessSettingsAsync();
        LoginBox.Text = _settings.Login;
        SavePasswordCheck.IsChecked = _settings.SaveLoginPassword;
        PasswordInput.Password = _service.GetSavedProcessLoginPassword(_settings);
        StatusText.Text = "Abra o SPED 3.0 da 4ª RM e conclua o login antes de iniciar a gravação.";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            ReadCredentials();
            await _service.OpenRegionalLoginAsync(_settings, PasswordInput.Password);
            StartButton.IsEnabled = true;
            StatusText.Text = "SPED aberto. Confirme no navegador que o login foi concluído.";
        }, "Não foi possível abrir o SPED");
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await _service.StartFullProcessLearningAsync(regional: true);
            StartButton.IsEnabled = false;
            ProcessButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
            UpdateStatus(_service.Status);
        }, "Não foi possível iniciar o aprendizado");
    }

    private async void Process_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await _service.AddLearningPhaseMarkerAsync("FASE 2 — DIEx SALVO, ASSINADO ELETRONICAMENTE E NÃO ENCAMINHADO; CRIAÇÃO E MONTAGEM MANUAL DO PROCESSO");
            ProcessButton.IsEnabled = false;
            StatusText.Text = "DIEx assinado sem encaminhamento marcado como concluído. Continue no navegador até finalizar todo o processo.";
        }, "Não foi possível marcar a fase do processo");
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar aprendizado completo do SPED",
            Filter = "Arquivo ZIP|*.zip",
            FileName = $"SPED_DIEx_e_Processo_{DateTime.Now:yyyyMMdd_HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunBusyAsync(async () =>
        {
            await _service.AddLearningPhaseMarkerAsync("FIM — FLUXO MANUAL CONCLUÍDO");
            var exported = await _service.FinishAndExportAsync(dialog.FileName);
            ExportButton.IsEnabled = false;
            ProcessButton.IsEnabled = false;
            RecordingInfoText.Text = $"Gravação concluída com {_service.Status.EventCount} ações. ZIP: {exported}";
            StatusText.Text = "ZIP criado. Confira as capturas e envie o arquivo para transformar o caminho em automação.";
            ShellService.OpenPath(Path.GetDirectoryName(exported)!);
        }, "Não foi possível gerar o ZIP");
    }

    private void ReadCredentials()
    {
        _settings.Login = LoginBox.Text.Trim();
        _settings.SaveLoginPassword = SavePasswordCheck.IsChecked == true;
    }

    private async Task RunBusyAsync(Func<Task> action, string title)
    {
        if (_busy) return;
        _busy = true;
        OpenButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            await App.Log.WriteAsync(title, ex);
            SigfurDialog.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            OpenButton.IsEnabled = true;
        }
    }

    private void Service_StatusChanged(object? sender, SpedMappingStatus status)
        => Dispatcher.BeginInvoke(new Action(() => UpdateStatus(status)));

    private void Service_RecorderMessage(object? sender, string message)
        => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));

    private void UpdateStatus(SpedMappingStatus status)
    {
        RecordingInfoText.Text = status.IsRecording
            ? $"GRAVANDO — {status.EventCount} ação(ões) registrada(s)."
            : status.EventCount > 0 ? $"{status.EventCount} ação(ões) registradas." : "Nenhuma gravação iniciada.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_service.Status.IsRecording) return;
        var result = SigfurDialog.Show(this,
            "A gravação ainda está ativa. Se fechar agora, o navegador será encerrado e o ZIP não será criado. Deseja fechar mesmo assim?",
            "Modo de Aprendizado do SPED", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
