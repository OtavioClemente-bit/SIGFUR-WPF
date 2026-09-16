using System.Net;
using System.Text;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class SpedProcessAutomationWindow : Window
{
    private readonly SpedMappingService _service = new(App.Paths);
    private readonly SpedProcessAutomationMode _mode;
    private readonly SpedDiexDraft? _draft;
    private readonly string _initialRecipient;
    private readonly string _initialInterested;
    private readonly string _documentNumber;
    private readonly string _forwardingReasonSource;
    private readonly string _storageDirectory;
    private SpedProcessAutomationSettings _settings = new();
    private bool _busy;

    public SpedProcessAutomationWindow(
        SpedProcessAutomationMode mode,
        SpedDiexDraft? draft,
        string initialRecipient,
        string initialInterested,
        string documentNumber = "",
        string forwardingReasonSource = "",
        string storageDirectory = "")
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _mode = mode;
        _draft = draft;
        _initialRecipient = initialRecipient;
        _initialInterested = initialInterested;
        _documentNumber = documentNumber;
        _forwardingReasonSource = forwardingReasonSource;
        _storageDirectory = storageDirectory;
        _service.RecorderMessage += Service_RecorderMessage;
    }

    public SpedProcessAutomationResult? AutomationResult { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _service.LoadProcessSettingsAsync();
        LoginBox.Text = _settings.Login;
        LoginPasswordBox.Password = _service.GetSavedProcessLoginPassword(_settings);
        SignaturePasswordBox.Password = _service.GetSavedSignaturePassword(_settings);
        SaveLoginPasswordCheck.IsChecked = _settings.SaveLoginPassword;
        SaveSignaturePasswordCheck.IsChecked = _settings.SaveSignaturePassword;
        RecipientBox.Text = string.IsNullOrWhiteSpace(_initialRecipient) ? _settings.RecipientSearch : _initialRecipient;
        ProcessInterestedBox.Text = string.IsNullOrWhiteSpace(_initialInterested) ? _settings.ProcessInterested : _initialInterested;
        DispatchSignerBox.Text = _settings.DispatchSignerSearch;
        ForwardRecipientBox.Text = _settings.ProcessForwardRecipientSearch;
        ConfigureMode();
    }

    private void ConfigureMode()
    {
        var createDiex = _mode == SpedProcessAutomationMode.CreateDiex;
        Title = createDiex ? "SIGFUR — Criar DIEx no SPED 3.0" : "SIGFUR — Criar e encaminhar processo no SPED 3.0";
        HeaderTitleText.Text = createDiex ? "CRIAR DIEx" : "CRIAR E ENCAMINHAR PROCESSO";
        AccessTitleText.Text = createDiex ? "ACESSO E DESTINATÁRIO" : "ACESSO E DADOS DO PROCESSO";
        RecipientPanel.Visibility = createDiex ? Visibility.Visible : Visibility.Collapsed;
        ForwardRecipientPanel.Visibility = createDiex ? Visibility.Collapsed : Visibility.Visible;
        SignatureCard.Visibility = createDiex ? Visibility.Visible : Visibility.Collapsed;
        DocumentNumberCard.Visibility = createDiex ? Visibility.Collapsed : Visibility.Visible;
        DocumentNumberBox.Text = _documentNumber;
        RunButton.Content = createDiex ? "Criar e assinar DIEx" : "Criar e encaminhar processo";
        RunButton.MinWidth = 205;
        SequenceText.Text = createDiex
            ? "1. Preencher destinatário, assunto, classificação 002.01, anexos e corpo.\n2. Salvar o DIEx e capturar o número gerado.\n3. Assinar eletronicamente, marcando Não encaminhar automaticamente.\n4. Salvar a página do DIEx em PDF no processo local para conferência."
            : "1. Criar o processo com classificação 002.01 e incluir o DIEx pelo número guardado.\n2. Salvar a página completa do processo em PDF.\n3. Confirmar e autuar o processo.\n4. Abrir Redigir Despacho uma única vez, preencher assunto, corpo e assinante e salvar a minuta.\n5. Voltar ao processo e abrir Encaminhar.\n6. Escolher o destinatário do encaminhamento, preencher um motivo curto, salvar e confirmar.";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _settings.Login = LoginBox.Text.Trim();
        _settings.RecipientSearch = RecipientBox.Text.Trim();
        _settings.ProcessInterested = ProcessInterestedBox.Text.Trim();
        _settings.DispatchSignerSearch = DispatchSignerBox.Text.Trim();
        _settings.ProcessForwardRecipientSearch = ForwardRecipientBox.Text.Trim();
        _settings.SaveLoginPassword = SaveLoginPasswordCheck.IsChecked == true;
        _settings.SaveSignaturePassword = SaveSignaturePasswordCheck.IsChecked == true;
        if (_mode == SpedProcessAutomationMode.CreateDiex && string.IsNullOrWhiteSpace(_settings.RecipientSearch))
        {
            SigfurDialog.Show(this, "Informe para quem o DIEx será enviado.", "Destinatário", MessageBoxButton.OK, MessageBoxImage.Information);
            RecipientBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.ProcessInterested))
        {
            SigfurDialog.Show(this, "Informe o interessado que aparecerá na capa do processo.", "Interessado", MessageBoxButton.OK, MessageBoxImage.Information);
            ProcessInterestedBox.Focus();
            return;
        }
        if (_mode == SpedProcessAutomationMode.CreateProcess && string.IsNullOrWhiteSpace(_settings.ProcessForwardRecipientSearch))
        {
            SigfurDialog.Show(this, "Informe para quem o processo será encaminhado.", "Destinatário do processo", MessageBoxButton.OK, MessageBoxImage.Information);
            ForwardRecipientBox.Focus();
            return;
        }
        if (_mode == SpedProcessAutomationMode.CreateProcess && string.IsNullOrWhiteSpace(_settings.DispatchSignerSearch))
        {
            SigfurDialog.Show(this, "Informe quem assinará o despacho.", "Assinante do despacho", MessageBoxButton.OK, MessageBoxImage.Information);
            DispatchSignerBox.Focus();
            return;
        }
        if (_mode == SpedProcessAutomationMode.CreateDiex && _draft is null)
            throw new InvalidOperationException("Os dados do DIEx não foram preparados.");

        _busy = true;
        RunButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        StatusText.Text = "Abrindo o SPED 3.0 da 4ª RM...";
        try
        {
            AutomationResult = _mode == SpedProcessAutomationMode.CreateDiex
                ? await _service.RunRegionalDiexAndSignAsync(
                    _settings, LoginPasswordBox.Password, SignaturePasswordBox.Password, _draft!, _storageDirectory)
                : await _service.RunRegionalProcessAsync(
                    _settings, LoginPasswordBox.Password, _draft?.Subject ?? string.Empty,
                    ProcessInterestedBox.Text.Trim(), _documentNumber,
                    _draft?.DispatchSubject ?? string.Empty, _draft?.DispatchBodyHtml ?? string.Empty,
                    DispatchSignerBox.Text.Trim(), ForwardRecipientBox.Text.Trim(),
                    _forwardingReasonSource, _storageDirectory);
            StatusText.Text = AutomationResult.Message;
            if (AutomationResult.Forwarded)
            {
                SigfurDialog.Show(this,
                    AutomationResult.Message,
                    "Processo finalizado", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            else if (!AutomationResult.Success)
            {
                SigfurDialog.Show(this,
                    AutomationResult.Message,
                    "Automação interrompida", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (AutomationResult.Success && AutomationResult.Signed)
            {
                SigfurDialog.Show(this, AutomationResult.Message, "SPED 3.0", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Não foi possível concluir a automação no SPED 3.0 da 4ª RM.", ex);
            StatusText.Text = ex.Message;
            SigfurDialog.Show(this, ex.Message, "Automação do SPED 3.0", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            RunButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void Service_RecorderMessage(object? sender, string message)
        => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _service.RecorderMessage -= Service_RecorderMessage;
        await _service.DisposeAsync();
    }
}
