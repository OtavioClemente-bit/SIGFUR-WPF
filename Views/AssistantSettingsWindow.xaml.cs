using System.Windows;
using System.Windows.Controls;
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

    public AssistantSettingsWindow(AssistantStorageService storage, AssistantCredentialService credentials, OpenAiAssistantService api)
    {
        InitializeComponent();
        _storage = storage;
        _credentials = credentials;
        _api = api;
        App.UiState.Attach(this);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await _storage.LoadSettingsAsync();
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
        UpdateKeyStatus();
    }

    private void UpdateKeyStatus()
    {
        var configured = _credentials.HasApiKey();
        ApiStatusText.Text = configured ? "● Chave configurada" : "○ Chave não configurada";
        ApiStatusText.Foreground = (System.Windows.Media.Brush)FindResource(configured ? "SuccessBrush" : "WarningBrush");
    }

    private AssistantSettings ReadForm()
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var model = ModelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Informe o modelo da OpenAI.");
        if (!int.TryParse(MaxTokensBox.Text, out var maxTokens)) throw new InvalidOperationException("O limite de tokens deve ser um número inteiro.");
        if (!int.TryParse(HistoryLimitBox.Text, out var historyLimit)) throw new InvalidOperationException("O limite do histórico deve ser um número inteiro.");
        if (!decimal.TryParse(BudgetBox.Text, NumberStyles.Number, culture, out var budget)) throw new InvalidOperationException("Informe um limite mensal válido.");
        if (!decimal.TryParse(DollarRateBox.Text, NumberStyles.Number, culture, out var dollarRate)) throw new InvalidOperationException("Informe uma cotação válida.");
        var baseUrl = ApiBaseUrlBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Por segurança, use o endereço oficial https://api.openai.com/v1.");

        return new AssistantSettings
        {
            Model = model,
            ApiBaseUrl = baseUrl,
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
            OperatorInstructions = CustomInstructionsBox.Text.Trim()
        };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveButton.IsEnabled = false;
            var settings = ReadForm();
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _credentials.SaveApiKey(ApiKeyBox.Password);
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
            _testCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            TestStatusText.Text = "Testando conexão e modelo...";
            var settings = ReadForm();
            await _api.TestAsync(settings, ApiKeyBox.Password, _testCts.Token);
            TestStatusText.Text = "Conexão concluída com sucesso. A chave e o modelo responderam corretamente.";
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            TestStatusText.Text = ex.Message;
            TestStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Remover a chave da OpenAI salva neste computador?", "Assistente SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _credentials.DeleteApiKey();
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

    private static void SelectComboByTag(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
}
