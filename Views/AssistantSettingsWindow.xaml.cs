using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class AssistantSettingsWindow : Window
{
    private readonly AssistantStorageService _storage;
    private readonly AssistantCredentialService _credentials;
    private readonly OpenAiAssistantService _api;
    private AssistantSettings _settings = new();
    private CancellationTokenSource? _testCts;
    private CancellationTokenSource? _diagnosticCts;

    public AssistantSettingsWindow(AssistantStorageService storage, AssistantCredentialService credentials, OpenAiAssistantService api)
    {
        InitializeComponent();
        _storage = storage;
        _credentials = credentials;
        _api = api;
        App.UiState.Attach(this);
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _testCts?.Cancel(); _diagnosticCts?.Cancel(); };
    }

    private async Task LoadAsync()
    {
        _settings = await _storage.LoadSettingsAsync();
        SelectComboByTag(ProviderBox, _settings.Provider);
        ApplyProviderUi(false);
        ModelBox.Text = _settings.Model;
        SelectComboByTag(ReasoningBox, _settings.ReasoningEffort);
        MaxTokensBox.Text = _settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        BudgetBox.Text = _settings.MonthlyBudgetBrl.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"));
        DollarRateBox.Text = _settings.DollarRate.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"));
        HistoryLimitBox.Text = _settings.MaxHistoryMessages.ToString(CultureInfo.InvariantCulture);
        LocalDataBox.IsChecked = _settings.EnableLocalDataTools;
        AttachmentsBox.IsChecked = _settings.EnableAttachments;
        RedactBox.IsChecked = _settings.RedactSensitiveData;
        SaveHistoryBox.IsChecked = _settings.SaveHistoryLocally;
        ConfirmActionsBox.IsChecked = _settings.ConfirmOperationalActions;
        HardBudgetBox.IsChecked = _settings.HardBudgetLimit;
        ApiBaseUrlBox.Text = _settings.ApiBaseUrl;
        CustomInstructionsBox.Text = _settings.OperatorInstructions;
        WebResearchBox.IsChecked = _settings.EnableOfficialWebResearch;
        DeadlineNotificationsBox.IsChecked = _settings.EnableDeadlineNotifications;
        DeadlineDaysBox.Text = _settings.DeadlineLookaheadDays.ToString(CultureInfo.InvariantCulture);
        ContextLimitBox.Text = _settings.MaxContextCharacters.ToString(CultureInfo.InvariantCulture);
        ToolRoundsBox.Text = _settings.MaxToolRounds.ToString(CultureInfo.InvariantCulture);
        UpdateKeyStatus();

    }

    private void UpdateKeyStatus()
    {
        var provider = SelectedProvider();
        var configured = _credentials.HasApiKey(provider);
        ApiStatusText.Text = configured ? $"● Chave da {provider} configurada" : $"○ Chave da {provider} não configurada";
        ApiStatusText.Foreground = (System.Windows.Media.Brush)FindResource(configured ? "SuccessBrush" : "WarningBrush");
    }

    private AssistantSettings ReadForm()
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var provider = SelectedProvider();
        var model = ModelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException($"Informe o modelo da {provider}.");
        if (!int.TryParse(MaxTokensBox.Text, out var maxTokens)) throw new InvalidOperationException("O limite de tokens deve ser um número inteiro.");
        if (!int.TryParse(HistoryLimitBox.Text, out var historyLimit)) throw new InvalidOperationException("O limite do histórico deve ser um número inteiro.");
        if (!decimal.TryParse(BudgetBox.Text, NumberStyles.Number, culture, out var budget)) throw new InvalidOperationException("Informe um limite mensal válido.");
        if (!decimal.TryParse(DollarRateBox.Text, NumberStyles.Number, culture, out var dollarRate)) throw new InvalidOperationException("Informe uma cotação válida.");
        if (!int.TryParse(DeadlineDaysBox.Text, out var deadlineDays) || deadlineDays is < 1 or > 30)
            throw new InvalidOperationException("A antecedência dos alertas deve ser de 1 a 30 dias.");
        if (!int.TryParse(ContextLimitBox.Text, out var contextLimit) || contextLimit is < 20000 or > 120000)
            throw new InvalidOperationException("O limite de contexto deve ser de 20.000 a 120.000 caracteres.");
        if (!int.TryParse(ToolRoundsBox.Text, out var toolRounds) || toolRounds is < 2 or > 12)
            throw new InvalidOperationException("As etapas de consulta devem ser de 2 a 12.");
        var baseUrl = ApiBaseUrlBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(provider == "DeepSeek" ? "api.deepseek.com" : "api.openai.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Por segurança, use o endereço oficial da {provider}.");

        return new AssistantSettings
        {
            SchemaVersion = 4,
            Provider = provider,
            Model = model,
            ApiBaseUrl = baseUrl,
            UseTls12Compatibility = _settings.UseTls12Compatibility,
            ReasoningEffort = (ReasoningBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none",
            MaxOutputTokens = Math.Clamp(maxTokens, 256, 16_000),
            MaxHistoryMessages = Math.Clamp(historyLimit, 2, 40),
            MaxAttachmentCharacters = _settings.MaxAttachmentCharacters,
            EnableLocalDataTools = LocalDataBox.IsChecked == true,
            EnableAttachments = AttachmentsBox.IsChecked == true,
            RedactSensitiveData = RedactBox.IsChecked == true,
            SaveHistoryLocally = SaveHistoryBox.IsChecked == true,
            ConfirmOperationalActions = ConfirmActionsBox.IsChecked == true,
            HardBudgetLimit = HardBudgetBox.IsChecked == true,
            MonthlyBudgetBrl = Math.Clamp(budget, 0, 10_000),
            DollarRate = Math.Clamp(dollarRate, 1, 20),
            OperatorInstructions = CustomInstructionsBox.Text.Trim(),
            DiexRitex = _settings.DiexRitex,
            EnableOfficialWebResearch = WebResearchBox.IsChecked == true,
            EnableDeadlineNotifications = DeadlineNotificationsBox.IsChecked == true,
            DeadlineLookaheadDays = deadlineDays,
            MaxContextCharacters = contextLimit,
            MaxToolRounds = toolRounds
        };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveButton.IsEnabled = false;
            var settings = ReadForm();
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _credentials.SaveApiKey(ApiKeyBox.Password, settings.Provider);
            await _storage.SaveSettingsAsync(settings);
            _settings = settings;
            UpdateKeyStatus();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Assistente SIGFUR — Configuração", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SaveButton.IsEnabled = true; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            TestStatusText.Text = "Testando a chave pela rede do Windows (proxy automatico)...";
            var settings = ReadForm();
            await _api.TestAsync(settings, ApiKeyBox.Password, _testCts.Token);
            _settings = await _storage.LoadSettingsAsync();
            TestStatusText.Text = _settings.UseTls12Compatibility
                ? "Conexao concluida. A chave foi reconhecida usando o modo compativel TLS 1.2, que ficou salvo para este PC."
                : "Conexao concluida. A chave foi reconhecida usando o TLS automatico do Windows.";
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            TestStatusText.Text = ex is OperationCanceledException or TaskCanceledException
                ? $"A intranet nao respondeu em 150 segundos. A chave pode ser salva normalmente; o acesso a {(SelectedProvider() == "DeepSeek" ? "api.deepseek.com" : "api.openai.com")} precisa estar liberado no proxy/firewall."
                : ex.Message;
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    private async void Diagnostic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _diagnosticCts?.Cancel();
            _diagnosticCts?.Dispose();
            _diagnosticCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            DiagnosticButton.IsEnabled = false;
            TestButton.IsEnabled = false;
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryDarkBrush");

            var progress = new Progress<string>(message => TestStatusText.Text = message);
            var result = await OpenAiNetworkDiagnostic.RunAsync(App.Paths, progress, _diagnosticCts.Token);
            TestStatusText.Text = result.Summary + " Abrindo o relatório...";
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");

            try
            {
                Process.Start(new ProcessStartInfo(result.ReportPath) { UseShellExecute = true });
            }
            catch
            {
                Clipboard.SetText(result.ReportPath);
                TestStatusText.Text = result.Summary + " Caminho do relatório copiado.";
            }
        }
        catch (OperationCanceledException)
        {
            TestStatusText.Text = "Diagnóstico cancelado ou limite total de 3 minutos atingido.";
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"Falha ao gerar diagnóstico: {ex.GetType().Name}, HResult=0x{ex.HResult:X8}: {ex.Message}";
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            await App.Log.WriteAsync("Falha ao gerar diagnóstico detalhado da OpenAI.", ex);
        }
        finally
        {
            DiagnosticButton.IsEnabled = SelectedProvider() == "OpenAI";
            TestButton.IsEnabled = true;
            _diagnosticCts?.Dispose();
            _diagnosticCts = null;
        }
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
                throw new InvalidOperationException("Cole a chave da API antes de salvar.");

            var enteredKey = ApiKeyBox.Password.Trim();
            var provider = SelectedProvider();
            _credentials.SaveApiKey(enteredKey, provider);
            var savedKey = _credentials.ReadApiKey(provider);
            if (!string.Equals(savedKey, enteredKey, StringComparison.Ordinal))
                throw new InvalidOperationException("O Windows informou que salvou a chave, mas o SIGFUR nao conseguiu le-la novamente neste usuario.");
            ApiKeyBox.Clear();
            UpdateKeyStatus();
            TestStatusText.Text = "Chave salva e conferida neste usuario do Windows. O teste de internet e opcional.";
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Assistente SIGFUR — Chave", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider();
        if (SigfurDialog.Show(this, $"Remover a chave da {provider} salva neste computador?", "Assistente SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _credentials.DeleteApiKey(provider);
            ApiKeyBox.Clear();
            UpdateKeyStatus();
            TestStatusText.Text = "Chave removida do Gerenciador de Credenciais do Windows.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Assistente SIGFUR", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private string SelectedProvider()
        => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "DeepSeek" ? "DeepSeek" : "OpenAI";

    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyProviderUi(true);
    }

    private void ApplyProviderUi(bool resetModel)
    {
        var provider = SelectedProvider();
        var models = provider == "DeepSeek"
            ? new[] { "deepseek-v4-flash", "deepseek-v4-pro" }
            : new[] { "gpt-5.6-luna", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.4", "gpt-5.5" };
        ModelBox.Items.Clear();
        foreach (var model in models) ModelBox.Items.Add(new ComboBoxItem { Content = model });
        if (resetModel || string.IsNullOrWhiteSpace(ModelBox.Text)) ModelBox.Text = models[0];
        ApiBaseUrlBox.Text = provider == "DeepSeek" ? "https://api.deepseek.com" : "https://api.openai.com/v1";
        DiagnosticButton.IsEnabled = provider == "OpenAI";
        DiagnosticButton.ToolTip = provider == "OpenAI" ? null : "O diagnóstico detalhado atual verifica a rota da OpenAI; use Testar conexão para a DeepSeek.";
        ApiKeyBox.Clear();
        UpdateKeyStatus();
        TestStatusText.Text = provider == "DeepSeek"
            ? "Use uma chave criada na plataforma DeepSeek. A cobrança depende do saldo disponível na conta."
            : string.Empty;
    }

    private static void SelectComboByTag(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
}
