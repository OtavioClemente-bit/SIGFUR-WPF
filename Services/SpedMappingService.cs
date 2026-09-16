using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Abre o SPED em navegador visível e registra as ações executadas manualmente
/// pelos elementos reais da página. Nenhuma coordenada do mouse é utilizada.
/// </summary>
public sealed partial class SpedMappingService : IAsyncDisposable
{
    public const string LoginUrl = "http://sped3.4ciape.eb.mil.br/#/login";
    public const string DiexUrl = "http://sped3.4ciape.eb.mil.br/#/diex";
    public const string RegionalLoginUrl = "http://sped3.4rm.eb.mil.br/#/login";
    public const string RegionalDiexUrl = "http://sped3.4rm.eb.mil.br/#/diex";
    public const string RegionalProcessCreationUrl = "http://sped3.4rm.eb.mil.br/#/criacao-processo";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonOptions) { WriteIndented = false };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _driverGate = new(1, 1);
    private IWebDriver? _driver;
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingTask;
    private string _sessionDirectory = string.Empty;
    private string _eventsFile = string.Empty;
    private string _friendlyLogFile = string.Empty;
    private string _sessionPurpose = "Mapeamento manual do fluxo de criação de DIEx no SPED 3.0";
    private string _sessionLoginUrl = LoginUrl;
    private string _sessionDiexUrl = DiexUrl;
    private int _eventCount;
    private bool _disposed;

    public SpedMappingService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.SpedDirectory);
        Directory.CreateDirectory(_paths.SpedMappingDirectory);
    }

    public event EventHandler<SpedMappingStatus>? StatusChanged;
    public event EventHandler<string>? RecorderMessage;

    public SpedMappingStatus Status => new()
    {
        BrowserOpen = _driver is not null,
        IsRecording = _recordingCts is { IsCancellationRequested: false },
        EventCount = Volatile.Read(ref _eventCount),
        SessionDirectory = _sessionDirectory
    };

    public async Task<SpedMappingSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_paths.SpedSettingsFile)) return new SpedMappingSettings();
            await using var stream = File.OpenRead(_paths.SpedSettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<SpedMappingSettings>(stream, JsonOptions, cancellationToken)
                ?? new SpedMappingSettings();
            NormalizeSavedChoices(settings);
            return settings;
        }
        catch
        {
            return new SpedMappingSettings();
        }
    }

    public string GetSavedPassword(SpedMappingSettings settings)
        => settings.SavePassword ? WindowsSecretProtector.Unprotect(settings.ProtectedPassword) : string.Empty;

    public async Task SaveSettingsAsync(SpedMappingSettings settings, string password, CancellationToken cancellationToken = default)
    {
        settings.Login = settings.Login?.Trim() ?? string.Empty;
        NormalizeSavedChoices(settings);
        settings.ProtectedPassword = !settings.SavePassword
            ? string.Empty
            : string.IsNullOrWhiteSpace(password)
                ? settings.ProtectedPassword
                : WindowsSecretProtector.Protect(password);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SpedSettingsFile)!);
        await using var stream = File.Create(_paths.SpedSettingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    public async Task<SpedProcessAutomationSettings> LoadProcessSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_paths.SpedProcessSettingsFile)) return new SpedProcessAutomationSettings();
            await using var stream = File.OpenRead(_paths.SpedProcessSettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<SpedProcessAutomationSettings>(stream, JsonOptions, cancellationToken)
                           ?? new SpedProcessAutomationSettings();
            settings.DispatchSignerSearch = string.IsNullOrWhiteSpace(settings.DispatchSignerSearch)
                ? "Ordenador de Despesas"
                : settings.DispatchSignerSearch.Trim();
            // "Ordenador de Despesas" era o destinatário padrão antes de existir a etapa de despacho.
            // Agora ele é o assinante do despacho, e o encaminhamento segue para o comandante escolhido.
            if (string.IsNullOrWhiteSpace(settings.ProcessForwardRecipientSearch)
                || settings.ProcessForwardRecipientSearch.Equals("Ordenador de Despesas", StringComparison.OrdinalIgnoreCase))
                settings.ProcessForwardRecipientSearch = "Comandante";
            return settings;
        }
        catch { return new SpedProcessAutomationSettings(); }
    }

    public string GetSavedProcessLoginPassword(SpedProcessAutomationSettings settings)
        => settings.SaveLoginPassword ? WindowsSecretProtector.Unprotect(settings.ProtectedLoginPassword) : string.Empty;

    public string GetSavedSignaturePassword(SpedProcessAutomationSettings settings)
        => settings.SaveSignaturePassword ? WindowsSecretProtector.Unprotect(settings.ProtectedSignaturePassword) : string.Empty;

    public async Task SaveProcessSettingsAsync(
        SpedProcessAutomationSettings settings,
        string loginPassword,
        string signaturePassword,
        CancellationToken cancellationToken = default)
    {
        settings.Login = settings.Login?.Trim() ?? string.Empty;
        settings.RecipientSearch = settings.RecipientSearch?.Trim() ?? string.Empty;
        settings.ProcessInterested = string.IsNullOrWhiteSpace(settings.ProcessInterested)
            ? OrganizationIdentity.Treasury
            : settings.ProcessInterested.Trim();
        settings.DispatchSignerSearch = string.IsNullOrWhiteSpace(settings.DispatchSignerSearch)
            ? "Ordenador de Despesas"
            : settings.DispatchSignerSearch.Trim();
        settings.ProcessForwardRecipientSearch = string.IsNullOrWhiteSpace(settings.ProcessForwardRecipientSearch)
            ? "Comandante"
            : settings.ProcessForwardRecipientSearch.Trim();
        settings.ProtectedLoginPassword = !settings.SaveLoginPassword
            ? string.Empty
            : string.IsNullOrWhiteSpace(loginPassword)
                ? settings.ProtectedLoginPassword
                : WindowsSecretProtector.Protect(loginPassword);
        settings.ProtectedSignaturePassword = !settings.SaveSignaturePassword
            ? string.Empty
            : string.IsNullOrWhiteSpace(signaturePassword)
                ? settings.ProtectedSignaturePassword
                : WindowsSecretProtector.Protect(signaturePassword);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SpedProcessSettingsFile)!);
        await using var stream = File.Create(_paths.SpedProcessSettingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    public async Task OpenRegionalLoginAsync(SpedProcessAutomationSettings settings, string password, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await SaveProcessSettingsAsync(settings, password, string.Empty, cancellationToken);
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            _sessionLoginUrl = RegionalLoginUrl;
            _sessionDiexUrl = RegionalDiexUrl;
            await StopRecordingCoreAsync();
            CloseDriverCore();
            _driver = CreateDriver();
            _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
            _driver.Navigate().GoToUrl(RegionalLoginUrl);
            TryMaximize(_driver);
            FillLoginFields(_driver, settings.Login, password);
        }
        finally { _driverGate.Release(); }
        RaiseStatus();
    }

    public async Task<SpedProcessAutomationResult> RunRegionalDiexAndSignAsync(
        SpedProcessAutomationSettings settings,
        string loginPassword,
        string signaturePassword,
        SpedDiexDraft draft,
        string storageDirectory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(loginPassword))
            throw new InvalidOperationException("Informe o login e a senha de acesso ao SPED 3.0.");
        if (string.IsNullOrWhiteSpace(settings.RecipientSearch))
            throw new InvalidOperationException("Informe para quem o DIEx será enviado.");

        await SaveProcessSettingsAsync(settings, loginPassword, signaturePassword, cancellationToken);
        var keepOpenForManual = false;
        var documentNumber = string.Empty;
        var documentUrl = string.Empty;
        var documentPdfPath = string.Empty;
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            await StopRecordingCoreAsync();
            CloseDriverCore();
            RaiseMessage("Entrando no SPED 3.0 da 4ª RM...");
            _driver = CreateDriver(headless: true);
            _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
            _driver.Navigate().GoToUrl(RegionalLoginUrl);
            TryMaximize(_driver);
            FillLoginFields(_driver, settings.Login, loginPassword);
            WaitForCondition(() => !(_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(60), cancellationToken);

            _driver.Navigate().GoToUrl(RegionalDiexUrl);
            WaitForDocument(_driver, TimeSpan.FromSeconds(45), cancellationToken);
            if ((_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O SPED recusou o login ou a sessão não foi iniciada.");

            RaiseMessage("Preenchendo destinatário, assunto, classificação, anexos e corpo do DIEx...");
            var warnings = new List<string>();
            // Neste módulo o remetente é sempre o próprio usuário autenticado.
            TryMappedStep("destinatário", warnings, () =>
                SelectPickListValueAny(_driver, ["Destinatários", "Destinatários Externos"], settings.RecipientSearch, cancellationToken));
            TryMappedStep("assunto", warnings, () => FillSubject(_driver, draft.Subject, cancellationToken));
            TryMappedStep("classificação documental", warnings, () =>
                SelectPrimeDropdownValue(_driver, "Pesquisa Rápida", "002.01 - NORMATIZAÇÃO. REGULAMENTAÇÃO", cancellationToken));
            TryMappedStep("anexos", warnings, () => UploadAttachments(_driver, draft.AttachmentPaths, cancellationToken));
            TryMappedStep("corpo do texto", warnings, () => FillRichTextEditor(_driver, draft.BodyHtml, cancellationToken));
            if (warnings.Count > 0)
                throw new InvalidOperationException("Não foi possível preencher automaticamente: " + string.Join(", ", warnings) + ".");

            ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
            var saveButton = WaitForElement(_driver,
                d => FindBestTextElement(d.FindElements(By.CssSelector("button")), "Salvar"),
                TimeSpan.FromSeconds(20), cancellationToken)
                ?? throw new InvalidOperationException("Botão Salvar do DIEx não encontrado.");
            var urlBeforeSave = _driver.Url ?? string.Empty;
            RaiseMessage("DIEx preenchido. Salvando sem encaminhar...");
            Click(_driver, saveButton);
            WaitForCondition(() =>
            {
                var currentUrl = _driver.Url ?? string.Empty;
                var signVisible = FindBestTextElement(_driver.FindElements(By.CssSelector("button")), "Assinar/Protocolar") is not null;
                return signVisible || !currentUrl.Equals(urlBeforeSave, StringComparison.OrdinalIgnoreCase);
            }, TimeSpan.FromSeconds(40), cancellationToken);

            RaiseMessage("DIEx salvo. Identificando o número gerado pelo SPED...");
            documentNumber = WaitForDiexNumber(_driver, TimeSpan.FromSeconds(35), cancellationToken);
            documentUrl = _driver.Url ?? string.Empty;
            RaiseMessage($"DIEx nº {documentNumber} salvo. Gerando o PDF de conferência em segundo plano...");
            documentPdfPath = SaveSpedPagePdf(_driver, storageDirectory, $"DIEx_SPED_{documentNumber}.pdf");

            try
            {
                RaiseMessage("Abrindo Assinar/Protocolar e configurando a assinatura eletrônica...");
                SignAndProtocol(_driver, signaturePassword, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebDriverException)
            {
                var result = new SpedProcessAutomationResult
                {
                    Success = false,
                    Signed = false,
                    DocumentNumber = documentNumber,
                    DocumentUrl = documentUrl,
                    Message = $"O DIEx nº {documentNumber} foi salvo, mas a assinatura eletrônica não foi concluída em segundo plano. Confira a senha e tente novamente. Detalhe: " + ex.Message,
                    Url = _driver.Url ?? string.Empty
                };
                RaiseMessage(result.Message);
                return result;
            }

            try
            {
                RaiseMessage($"DIEx nº {documentNumber} assinado. Atualizando o PDF de conferência...");
                _driver.Navigate().GoToUrl(documentUrl);
                WaitForDocument(_driver, TimeSpan.FromSeconds(30), cancellationToken);
                WaitForCondition(() => SafePageText(_driver).Contains(documentNumber, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(25), cancellationToken);
                documentPdfPath = SaveSpedPagePdf(_driver, storageDirectory, $"DIEx_SPED_{documentNumber}.pdf");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebDriverException or IOException)
            {
                RaiseMessage($"DIEx nº {documentNumber} assinado. O SIGFUR manteve o PDF de conferência salvo antes da assinatura porque o SPED não reabriu a página assinada: {ex.Message}");
            }

            var signedResult = new SpedProcessAutomationResult
            {
                Success = true,
                Signed = true,
                DocumentNumber = documentNumber,
                DocumentUrl = documentUrl,
                DocumentPdfPath = documentPdfPath,
                Message = $"DIEx nº {documentNumber} salvo, assinado e guardado em PDF para conferência antes do processo.",
                Url = _driver.Url ?? string.Empty
            };
            RaiseMessage(signedResult.Message);
            return signedResult;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebDriverException)
        {
            if (_driver is null) throw;
            var result = new SpedProcessAutomationResult
            {
                Success = false,
                Signed = false,
                DocumentNumber = documentNumber,
                DocumentUrl = documentUrl,
                Message = "A automação em segundo plano encontrou uma diferença no portal e foi interrompida. Nenhum processo será criado até o DIEx ser concluído e salvo em PDF. Detalhe: " + ex.Message,
                Url = SafeDriverUrl(_driver)
            };
            RaiseMessage(result.Message);
            return result;
        }
        finally
        {
            if (!keepOpenForManual) CloseDriverCore();
            _driverGate.Release();
            RaiseStatus();
        }
    }

    public async Task<SpedProcessAutomationResult> RunRegionalProcessAsync(
        SpedProcessAutomationSettings settings,
        string loginPassword,
        string subject,
        string interested,
        string documentNumber,
        string dispatchSubject,
        string dispatchBodyHtml,
        string dispatchSignerSearch,
        string forwardRecipientSearch,
        string forwardingReason,
        string storageDirectory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(loginPassword))
            throw new InvalidOperationException("Informe o login e a senha de acesso ao SPED 3.0.");
        if (string.IsNullOrWhiteSpace(documentNumber))
            throw new InvalidOperationException("Informe o número do DIEx que será incluído no processo.");
        if (string.IsNullOrWhiteSpace(interested))
            throw new InvalidOperationException("Informe o interessado que aparecerá na capa do processo.");
        if (string.IsNullOrWhiteSpace(dispatchSubject) || string.IsNullOrWhiteSpace(dispatchBodyHtml))
            throw new InvalidOperationException("O assunto e o corpo do despacho não foram preparados.");
        if (string.IsNullOrWhiteSpace(dispatchSignerSearch))
            throw new InvalidOperationException("Informe quem assinará o despacho.");
        if (string.IsNullOrWhiteSpace(forwardRecipientSearch))
            throw new InvalidOperationException("Informe para quem o processo será encaminhado.");

        settings.ProcessInterested = interested.Trim();
        settings.DispatchSignerSearch = dispatchSignerSearch.Trim();
        settings.ProcessForwardRecipientSearch = forwardRecipientSearch.Trim();
        await SaveProcessSettingsAsync(settings, loginPassword, string.Empty, cancellationToken);
        var keepOpenForManual = false;
        var processCreated = false;
        var autuated = false;
        var processPdfPath = string.Empty;
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            await StopRecordingCoreAsync();
            CloseDriverCore();
            RaiseMessage("Entrando no SPED 3.0 da 4ª RM para criar o processo...");
            _driver = CreateDriver(headless: true);
            _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
            _driver.Navigate().GoToUrl(RegionalLoginUrl);
            TryMaximize(_driver);
            FillLoginFields(_driver, settings.Login, loginPassword);
            WaitForCondition(() => !(_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(60), cancellationToken);

            RaiseMessage($"Criando o processo e pesquisando o DIEx nº {documentNumber.Trim()}...");
            var createdProcess = CreateAndAutuateProcess(
                _driver, subject, interested, documentNumber.Trim(), storageDirectory, cancellationToken);
            processPdfPath = createdProcess.PdfPath;
            processCreated = true;
            autuated = true;
            RaiseMessage($"Processo autuado. Redigindo o despacho para assinatura de {dispatchSignerSearch.Trim()}...");
            CreateDispatchDraft(
                _driver, subject, dispatchSubject, dispatchBodyHtml, dispatchSignerSearch.Trim(), cancellationToken);
            _driver.Navigate().GoToUrl(createdProcess.FinalUrl);
            WaitForDocument(_driver, TimeSpan.FromSeconds(30), cancellationToken);
            RaiseMessage($"Processo autuado. Encaminhando para {forwardRecipientSearch.Trim()}...");
            var finalUrl = ForwardAutuatedProcess(
                _driver, subject, forwardRecipientSearch.Trim(), forwardingReason, cancellationToken);
            try
            {
                RaiseMessage("Encaminhamento confirmado. Atualizando o PDF final do processo...");
                _driver.Navigate().GoToUrl(createdProcess.ManagementUrl);
                WaitForDocument(_driver, TimeSpan.FromSeconds(30), cancellationToken);
                WaitForCondition(() => NormalizeMatch(SafePageText(_driver)).Contains(NormalizeMatch(subject), StringComparison.Ordinal),
                    TimeSpan.FromSeconds(25), cancellationToken);
                processPdfPath = SaveSpedPagePdf(
                    _driver, storageDirectory, $"Processo_SPED_DIEx_{documentNumber.Trim()}.pdf");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebDriverException or IOException)
            {
                RaiseMessage("Processo encaminhado. Foi mantido o PDF salvo durante a montagem porque a página final não pôde ser reaberta: " + ex.Message);
            }
            var result = new SpedProcessAutomationResult
            {
                Success = true,
                Signed = true,
                ProcessCreated = true,
                Autuated = true,
                Forwarded = true,
                DocumentNumber = documentNumber.Trim(),
                ProcessPdfPath = processPdfPath,
                Message = $"Processo criado e autuado, despacho preparado e encaminhamento confirmado com o DIEx nº {documentNumber.Trim()}. Fluxo finalizado.",
                Url = string.IsNullOrWhiteSpace(finalUrl) ? createdProcess.FinalUrl : finalUrl
            };
            RaiseMessage(result.Message);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebDriverException)
        {
            if (_driver is null) throw;
            var autuarVisible = !autuated && FindBestTextElement(_driver.FindElements(By.CssSelector("button")), "Autuar") is not null;
            var result = new SpedProcessAutomationResult
            {
                Success = false,
                Signed = true,
                ProcessCreated = processCreated || (_driver.Url ?? string.Empty).Contains("/manter-processo/gerenciar/", StringComparison.OrdinalIgnoreCase),
                Autuated = autuated,
                NeedsManualAutuation = autuarVisible,
                NeedsManualDispatch = autuated,
                DocumentNumber = documentNumber.Trim(),
                ProcessPdfPath = processPdfPath,
                Message = autuated
                    ? "O processo foi criado e autuado, mas o despacho ou o encaminhamento em segundo plano não foi concluído. Confira o processo diretamente no SPED antes de tentar novamente. Detalhe: " + ex.Message
                    : "A automação do processo em segundo plano encontrou uma diferença no portal e foi interrompida. Detalhe: " + ex.Message,
                Url = SafeDriverUrl(_driver)
            };
            RaiseMessage(result.Message);
            return result;
        }
        finally
        {
            if (!keepOpenForManual) CloseDriverCore();
            _driverGate.Release();
            RaiseStatus();
        }
    }

    public async Task OpenLoginAsync(SpedMappingSettings settings, string password, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await SaveSettingsAsync(settings, password, cancellationToken);

        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            await StopRecordingCoreAsync();
            CloseDriverCore();
            _driver = CreateDriver();
            _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
            _driver.Navigate().GoToUrl(LoginUrl);
            TryMaximize(_driver);
            FillLoginFields(_driver, settings.Login, password);
        }
        finally
        {
            _driverGate.Release();
        }

        RaiseStatus();
        RaiseMessage(string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password)
            ? "SPED aberto. Informe o login no navegador e conclua a entrada."
            : "Login e senha preenchidos; o SIGFUR enviou Enter para iniciar a sessão.");
    }

    public async Task<SpedAutomationResult> RunHiddenAndSaveAsync(
        SpedMappingSettings settings,
        string password,
        SpedDiexDraft draft,
        string reviewDirectory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Informe o login e a senha do SPED.");
        // Login e senha são comuns aos módulos; as escolhas do DIEx pertencem ao
        // processo que chamou a automação e não devem sobrescrever outro módulo.
        var credentialSettings = await LoadSettingsAsync(cancellationToken);
        credentialSettings.Login = settings.Login;
        credentialSettings.SavePassword = settings.SavePassword;
        await SaveSettingsAsync(credentialSettings, password, cancellationToken);
        Directory.CreateDirectory(reviewDirectory);

        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            await StopRecordingCoreAsync();
            CloseDriverCore();
            RaiseMessage("Entrando no SPED em segundo plano...");
            _driver = CreateDriver(headless: true);
            _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
            _driver.Navigate().GoToUrl(LoginUrl);
            FillLoginFields(_driver, settings.Login, password);

            WaitForCondition(
                () => !(_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(60), cancellationToken);

            RaiseMessage("Login concluído. Preparando o DIEx em segundo plano...");
            _driver.Navigate().GoToUrl(DiexUrl);
            WaitForDocument(_driver, TimeSpan.FromSeconds(45), cancellationToken);
            if ((_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O SPED recusou o login ou a sessão não foi iniciada.");

            var warnings = FillMappedDefaults(_driver, settings, draft, cancellationToken);
            if (warnings.Count > 0)
                throw new InvalidOperationException("O SIGFUR não salvou porque não conseguiu preencher: " + string.Join(", ", warnings) + ".");

            ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
            Thread.Sleep(350);

            var saveButton = WaitForElement(_driver,
                d => FindDisplayed(d.FindElements(By.CssSelector("button[name='btnSalvar'], button.btnSalvar"))),
                TimeSpan.FromSeconds(20), cancellationToken)
                ?? throw new InvalidOperationException("Botão Salvar do DIEx não encontrado.");
            RaiseMessage("Todos os campos foram preenchidos. Salvando o DIEx...");
            var urlBeforeSave = _driver.Url ?? string.Empty;
            Click(_driver, saveButton);

            var confirmed = false;
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var currentUrl = _driver.Url ?? string.Empty;
                    var successMessage = _driver.FindElements(By.CssSelector(
                        ".ui-toast-message-success, .ui-growl-message-success, .ui-messages-success, .alert-success"))
                        .Any(IsDisplayed);
                    var saveGone = !_driver.FindElements(By.CssSelector("button[name='btnSalvar'], button.btnSalvar")).Any(IsDisplayed);
                    if (!currentUrl.Equals(urlBeforeSave, StringComparison.OrdinalIgnoreCase) || successMessage || saveGone)
                    {
                        confirmed = true;
                        break;
                    }
                }
                catch (WebDriverException) { confirmed = true; break; }
                Thread.Sleep(250);
            }
            if (!confirmed)
                throw new InvalidOperationException("O comando Salvar foi enviado, mas o SPED não confirmou a conclusão. O processo foi mantido como rascunho para conferência.");

            Thread.Sleep(1200);
            var expectedAttachmentNames = draft.AttachmentPaths.Select(path => Path.GetFileName(path)).ToList();
            var confirmedAttachmentNames = expectedAttachmentNames.Where(name => PageContainsAttachment(_driver, name)).ToList();
            if (confirmedAttachmentNames.Count > 0 && confirmedAttachmentNames.Count != expectedAttachmentNames.Count)
            {
                var missingAfterSave = expectedAttachmentNames.Except(confirmedAttachmentNames, StringComparer.OrdinalIgnoreCase);
                throw new InvalidOperationException("O SPED concluiu o salvamento, mas não exibiu estes anexos no documento final: " + string.Join(", ", missingAfterSave) + ".");
            }

            var result = new SpedAutomationResult
            {
                Success = true,
                Message = "DIEx salvo no SPED e processo atualizado no SIGFUR.",
                Url = _driver.Url ?? string.Empty,
                ScreenshotPaths = []
            };
            RaiseMessage(result.Message);
            return result;
        }
        finally
        {
            CloseDriverCore();
            _driverGate.Release();
            RaiseStatus();
        }
    }

    public async Task OpenDiexAndStartMappingAsync(SpedMappingSettings settings, SpedDiexDraft draft, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        List<string> warnings = [];
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            if (_driver is null) throw new InvalidOperationException("Abra o login do SPED antes de iniciar o mapeamento.");
            EnsureBrowserAvailable(_driver);
            _driver.Navigate().GoToUrl(DiexUrl);
            WaitForDocument(_driver, TimeSpan.FromSeconds(45), cancellationToken);
            if ((_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O SPED ainda está na tela de login. Conclua a entrada no navegador antes de iniciar o mapeamento.");

            await StopRecordingCoreAsync();
            CreateSessionDirectory();
            InjectRecorder(_driver);
            TrySaveInitialScreenshot(_driver);
            _recordingCts = new CancellationTokenSource();
            _recordingTask = RecorderLoopAsync(_recordingCts.Token);
            warnings = FillMappedDefaults(_driver, settings, draft, cancellationToken);
        }
        finally
        {
            _driverGate.Release();
        }

        RaiseStatus();
        RaiseMessage(warnings.Count == 0
            ? "DIEx preenchido. Confira remetente, destinatário, assunto, classificação, finalidade, anexos e corpo; o botão Salvar permanece sob seu controle."
            : "O mapeamento está ativo. Confira manualmente: " + string.Join(", ", warnings) + ".");
    }

    public async Task StartFullProcessLearningAsync(bool regional = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            if (_driver is null) throw new InvalidOperationException("Abra o SPED e conclua o login antes de iniciar o aprendizado.");
            EnsureBrowserAvailable(_driver);
            if ((_driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O SPED ainda está na tela de login. Conclua a entrada no navegador antes de gravar.");

            await StopRecordingCoreAsync();
            _sessionLoginUrl = regional ? RegionalLoginUrl : LoginUrl;
            _sessionDiexUrl = regional ? RegionalDiexUrl : DiexUrl;
            _driver.Navigate().GoToUrl(_sessionDiexUrl);
            WaitForDocument(_driver, TimeSpan.FromSeconds(45), cancellationToken);
            CreateSessionDirectory("Aprendizado completo: criação do DIEx, inclusão dos anexos e montagem do processo no SPED 3.0");
            InjectRecorder(_driver);
            TrySaveInitialScreenshot(_driver);
            _recordingCts = new CancellationTokenSource();
            _recordingTask = RecorderLoopAsync(_recordingCts.Token);
            AppendPhaseMarkerCore("FASE 1 — CRIAÇÃO MANUAL DO DIEX E INCLUSÃO DE TODOS OS ANEXOS", _driver);
        }
        finally
        {
            _driverGate.Release();
        }

        RaiseStatus();
        RaiseMessage("Modo de aprendizado ativo. Faça o DIEx e inclua todos os anexos com calma; cliques, campos utilizados e telas serão registrados.");
    }

    public async Task AddLearningPhaseMarkerAsync(string phase, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(phase)) return;
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            if (_driver is null || _recordingCts is null)
                throw new InvalidOperationException("Inicie o modo de aprendizado antes de marcar uma nova fase.");
            await DrainEventsCoreAsync(captureScreenshots: true, cancellationToken);
            AppendPhaseMarkerCore(phase.Trim(), _driver);
        }
        finally
        {
            _driverGate.Release();
        }
        RaiseStatus();
        RaiseMessage($"Etapa registrada: {phase.Trim()}. Continue normalmente no navegador.");
    }

    public async Task<string> FinishAndExportAsync(string destinationZip, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(_sessionDirectory) || !Directory.Exists(_sessionDirectory))
            throw new InvalidOperationException("Ainda não existe um mapeamento para exportar.");

        await StopRecordingAsync(cancellationToken);
        var manifest = new
        {
            generatedAt = DateTimeOffset.Now,
            application = "SIGFUR",
            purpose = _sessionPurpose,
            loginUrl = _sessionLoginUrl,
            diexUrl = _sessionDiexUrl,
            eventCount = _eventCount,
            security = new[]
            {
                "A gravação só começa depois do login.",
                "Senhas e valores digitados em campos de texto não são armazenados.",
                "As capturas de tela podem conter dados visíveis do DIEx e devem ser conferidas antes do compartilhamento."
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(_sessionDirectory, "sessao.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);

        var fullDestination = Path.GetFullPath(destinationZip);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        if (File.Exists(fullDestination)) File.Delete(fullDestination);
        ZipFile.CreateFromDirectory(_sessionDirectory, fullDestination, CompressionLevel.Optimal, includeBaseDirectory: false);
        RaiseMessage($"Log seguro exportado com {_eventCount} ação(ões).");
        return fullDestination;
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        await _driverGate.WaitAsync(cancellationToken);
        try
        {
            await DrainEventsCoreAsync(captureScreenshots: true, cancellationToken);
            if (_recordingCts is not null && !_recordingCts.IsCancellationRequested)
                _recordingCts.Cancel();
            loop = _recordingTask;
        }
        finally
        {
            _driverGate.Release();
        }

        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
        RaiseStatus();
    }

    private async Task RecorderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(700, cancellationToken);
                await _driverGate.WaitAsync(cancellationToken);
                try { await DrainEventsCoreAsync(captureScreenshots: true, cancellationToken); }
                finally { _driverGate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebDriverException ex)
        {
            RaiseMessage("O navegador do SPED foi fechado ou deixou de responder: " + ex.Message);
        }
        catch (Exception ex)
        {
            RaiseMessage("A gravação foi interrompida: " + ex.Message);
        }
        finally
        {
            RaiseStatus();
        }
    }

    private Task DrainEventsCoreAsync(bool captureScreenshots, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_driver is not IJavaScriptExecutor script || string.IsNullOrWhiteSpace(_sessionDirectory))
            return Task.CompletedTask;

        InjectRecorder(_driver);
        var json = script.ExecuteScript(
            "return JSON.stringify((window.__sigfurSpedEvents || []).splice(0, (window.__sigfurSpedEvents || []).length));")?.ToString();
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return Task.CompletedTask;

        var events = JsonSerializer.Deserialize<List<SpedBrowserEvent>>(json, JsonOptions) ?? [];
        foreach (var item in events)
        {
            item.Sequence = Interlocked.Increment(ref _eventCount);
            item.RecordedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            SanitizeEvent(item);
            if (captureScreenshots && item.EventType is "click" or "change" or "edit")
                item.Screenshot = TrySaveScreenshot(_driver, item.Sequence);

            File.AppendAllText(_eventsFile, JsonSerializer.Serialize(item, EventJsonOptions) + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(_friendlyLogFile, BuildFriendlyLine(item) + Environment.NewLine, Encoding.UTF8);
        }
        RaiseStatus();
        return Task.CompletedTask;
    }

    private void CreateSessionDirectory(string? purpose = null)
    {
        _sessionPurpose = string.IsNullOrWhiteSpace(purpose)
            ? "Mapeamento manual do fluxo de criação de DIEx no SPED 3.0"
            : purpose.Trim();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _sessionDirectory = Path.Combine(_paths.SpedMappingDirectory, $"sped_mapeamento_{stamp}");
        Directory.CreateDirectory(_sessionDirectory);
        Directory.CreateDirectory(Path.Combine(_sessionDirectory, "capturas"));
        _eventsFile = Path.Combine(_sessionDirectory, "passos.jsonl");
        _friendlyLogFile = Path.Combine(_sessionDirectory, "passos_legiveis.txt");
        _eventCount = 0;
        File.WriteAllText(Path.Combine(_sessionDirectory, "LEIA-ME.txt"),
            "MAPEAMENTO DO SPED 3.0 — SIGFUR\r\n\r\n" +
            $"Finalidade: {_sessionPurpose}.\r\n" +
            "Este pacote registra cliques, alterações e elementos HTML usados durante a execução manual no SPED.\r\n" +
            "A senha e os valores digitados em campos de texto não são gravados.\r\n" +
            "A gravação começa somente após o login.\r\n" +
            "Confira as imagens da pasta capturas antes de compartilhar o pacote, pois elas reproduzem o que estava visível na tela.\r\n",
            Encoding.UTF8);
    }

    private void AppendPhaseMarkerCore(string phase, IWebDriver driver)
    {
        var sequence = Interlocked.Increment(ref _eventCount);
        var item = new SpedBrowserEvent
        {
            Sequence = sequence,
            RecordedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            BrowserTime = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            EventType = "phase",
            Url = driver.Url ?? string.Empty,
            Tag = "SIGFUR",
            Text = phase,
            CssSelector = "[marcador-de-fase]",
            Screenshot = TrySaveScreenshot(driver, sequence)
        };
        SanitizeEvent(item);
        File.AppendAllText(_eventsFile, JsonSerializer.Serialize(item, EventJsonOptions) + Environment.NewLine, Encoding.UTF8);
        File.AppendAllText(_friendlyLogFile, BuildFriendlyLine(item) + Environment.NewLine, Encoding.UTF8);
    }

    private static IWebDriver CreateDriver(bool headless = false)
    {
        var service = EdgeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;

        var options = new EdgeOptions
        {
            PageLoadStrategy = PageLoadStrategy.Eager,
            AcceptInsecureCertificates = true
        };
        options.AddArgument("--start-maximized");
        if (headless)
        {
            options.AddArgument("--headless=new");
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--disable-gpu");
        }
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--no-first-run");
        options.AddArgument("--disable-features=msEdgeFirstRunExperience");
        return new EdgeDriver(service, options, TimeSpan.FromSeconds(90));
    }

    private static string SaveReviewScreenshot(IWebDriver driver, string directory, string fileName)
    {
        try
        {
            var path = Path.Combine(directory, fileName);
            if (driver is not ITakesScreenshot screenshot) return string.Empty;
            screenshot.GetScreenshot().SaveAsFile(path);
            return path;
        }
        catch { return string.Empty; }
    }

    private static string SaveSpedPagePdf(IWebDriver driver, string directory, string fileName)
    {
        if (driver is not ChromiumDriver chromium)
            throw new InvalidOperationException("A geração do PDF do SPED exige o Microsoft Edge.");
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("A pasta local do processo não foi informada.");

        Directory.CreateDirectory(directory);
        var safeFileName = string.Concat(fileName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (!safeFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) safeFileName += ".pdf";
        var path = Path.Combine(directory, safeFileName);
        var result = chromium.ExecuteCdpCommand("Page.printToPDF", new Dictionary<string, object?>
        {
            ["printBackground"] = true,
            ["landscape"] = false,
            ["preferCSSPageSize"] = true,
            ["scale"] = 0.9,
            ["marginTop"] = 0.25,
            ["marginBottom"] = 0.25,
            ["marginLeft"] = 0.25,
            ["marginRight"] = 0.25
        });
        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var dataElement)
            || string.IsNullOrWhiteSpace(dataElement.GetString()))
            throw new InvalidOperationException("O SPED abriu a página, mas o navegador não retornou o PDF.");
        File.WriteAllBytes(path, Convert.FromBase64String(dataElement.GetString()!));
        return path;
    }

    private static void FillLoginFields(IWebDriver driver, string login, string password)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var inputs = driver.FindElements(By.CssSelector("input"));
            var passwordInput = inputs.FirstOrDefault(x => IsDisplayed(x) &&
                string.Equals(x.GetAttribute("type"), "password", StringComparison.OrdinalIgnoreCase));
            var loginInput = inputs.FirstOrDefault(x => IsDisplayed(x) && !ReferenceEquals(x, passwordInput) &&
                !string.Equals(x.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(x.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase));
            if (loginInput is not null || passwordInput is not null)
            {
                TryFill(loginInput, login);
                TryFill(passwordInput, password);
                if (loginInput is not null && passwordInput is not null &&
                    !string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password))
                {
                    Thread.Sleep(250);
                    try { passwordInput.SendKeys(Keys.Enter); } catch { }
                }
                return;
            }
            Thread.Sleep(250);
        }
    }

    private static void TryFill(IWebElement? element, string value)
    {
        if (element is null || string.IsNullOrWhiteSpace(value)) return;
        try
        {
            element.Click();
            element.Clear();
            element.SendKeys(value);
        }
        catch { }
    }

    private static bool IsDisplayed(IWebElement element)
    {
        try { return element.Displayed && element.Enabled; }
        catch { return false; }
    }

    private List<string> FillMappedDefaults(IWebDriver driver, SpedMappingSettings settings, SpedDiexDraft draft, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        TryMappedStep("remetente", warnings, () =>
            SelectPickListValue(driver, "Remetente", settings.SenderSearch, replaceExisting: true, cancellationToken: cancellationToken));
        TryMappedStep("destinatário externo", warnings, () =>
            SelectPickListValue(driver, "Destinatários Externos", settings.ExternalRecipientSearch, replaceExisting: false, cancellationToken: cancellationToken));
        TryMappedStep("assunto", warnings, () => FillSubject(driver, draft.Subject, cancellationToken));
        TryMappedStep("classificação documental", warnings, () =>
            SelectPrimeDropdownValue(driver, "Pesquisa Rápida", settings.ClassificationSearch, cancellationToken));
        if (draft.FillDocumentPurpose)
            TryMappedStep("finalidade", warnings, () =>
                SelectPrimeDropdownValue(driver, "Selecione a finalidade do documento", settings.DocumentPurpose, cancellationToken));
        TryMappedStep("anexos", warnings, () => UploadAttachments(driver, draft.AttachmentPaths, cancellationToken));
        TryMappedStep("corpo do texto", warnings, () => FillRichTextEditor(driver, draft.BodyHtml, cancellationToken));

        return warnings;
    }

    private static void TryMappedStep(string label, ICollection<string> warnings, Action action)
    {
        try { action(); }
        catch { warnings.Add(label); }
    }

    private static void SelectPickListValue(IWebDriver driver, string buttonText, string search, bool replaceExisting, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search)) return;
        var openButton = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button")), buttonText),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException($"Botão {buttonText} não encontrado.");
        Click(driver, openButton);

        var dialog = WaitForElement(driver, FindActiveDialog, TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException($"Janela de {buttonText} não abriu.");
        var sourceWrapper = WaitForElement(driver,
            _ => FindDisplayed(dialog.FindElements(By.CssSelector(".ui-picklist-source-wrapper"))),
            TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException("Lista de opções não encontrada.");

        if (replaceExisting)
            ClearPickListTarget(driver, dialog, cancellationToken);
        var filter = FindDisplayed(sourceWrapper.FindElements(By.CssSelector("input.ui-picklist-filter")))
            ?? throw new InvalidOperationException("Pesquisa da lista não encontrada.");
        SetText(filter, search);

        var item = WaitForElement(driver,
            _ => FindBestTextElement(sourceWrapper.FindElements(By.CssSelector("li.ui-picklist-item")), search),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException($"Opção {search} não encontrada.");
        Click(driver, item);

        var buttonsCell = FindDisplayed(dialog.FindElements(By.CssSelector(".ui-picklist-buttons-cell")))
            ?? throw new InvalidOperationException("Controles da lista não encontrados.");
        var moveRight = FindPickListArrow(buttonsCell, "pi-angle-right")
            ?? throw new InvalidOperationException("Seta para adicionar à seleção não encontrada.");
        Click(driver, moveRight);

        var conclude = WaitForElement(driver,
            _ => FindBestTextElement(dialog.FindElements(By.CssSelector("button")), "Concluir"),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Botão Concluir não encontrado.");
        Click(driver, conclude);
        WaitForCondition(() => !IsDisplayed(dialog), TimeSpan.FromSeconds(15), cancellationToken);
    }

    private static void SelectPickListValueAny(
        IWebDriver driver,
        IReadOnlyList<string> buttonTexts,
        string search,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var buttonText in buttonTexts)
        {
            try
            {
                SelectPickListValue(driver, buttonText, search, replaceExisting: false, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException("Botão de Destinatários não encontrado no SPED.", last);
    }

    private static void SignAndProtocol(IWebDriver driver, string signaturePassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signaturePassword))
            throw new InvalidOperationException("A senha de assinatura eletrônica não foi informada.");

        var signButton = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button, a")), "Assinar/Protocolar"),
            TimeSpan.FromSeconds(25), cancellationToken)
            ?? throw new InvalidOperationException("Botão Assinar/Protocolar não encontrado.");
        Click(driver, signButton);

        var dialog = WaitForElement(driver, FindActiveDialog, TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Janela Tipo de Assinatura não abriu.");
        var electronic = WaitForElement(driver,
            _ => FindBestTextElement(dialog.FindElements(By.CssSelector("label, div, span, p")), "Assinatura Eletrônica"),
            TimeSpan.FromSeconds(12), cancellationToken)
            ?? throw new InvalidOperationException("Opção Assinatura Eletrônica não encontrada.");
        Click(driver, electronic);
        SetCheckboxByNearbyText(driver, dialog, "Não encaminhar automaticamente", true);

        var confirmSign = WaitForElement(driver,
            _ => FindBestTextElement(dialog.FindElements(By.CssSelector("button")), "Assinar"),
            TimeSpan.FromSeconds(12), cancellationToken)
            ?? throw new InvalidOperationException("Botão Assinar não encontrado.");
        Click(driver, confirmSign);

        var passwordInput = WaitForElement(driver,
            d => FindDisplayed(d.FindElements(By.CssSelector("input[type='password'], input[placeholder*='Senha']"))),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Campo Senha Eletrônica não encontrado.");
        SetText(passwordInput, signaturePassword);
        var passwordDialog = FindActiveDialog(driver);
        var confirmButton = WaitForElement(driver,
            d => FindBestTextElement((passwordDialog ?? d.FindElement(By.TagName("body"))).FindElements(By.CssSelector("button")), "Confirmar"),
            TimeSpan.FromSeconds(12), cancellationToken)
            ?? throw new InvalidOperationException("Botão Confirmar da senha eletrônica não encontrado.");
        Click(driver, confirmButton);
        WaitForCondition(() =>
        {
            try { return !passwordInput.Displayed; }
            catch (StaleElementReferenceException) { return true; }
        }, TimeSpan.FromSeconds(35), cancellationToken);
    }

    private static string WaitForDiexNumber(IWebDriver driver, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string? number = null;
        WaitForCondition(() =>
        {
            var body = SafePageText(driver);
            // O "SPED 3.0" do cabeçalho não pode ser confundido com o número do documento.
            // Na minuta salva o portal apresenta a linha "DIEx nº: 12345".
            var match = Regex.Match(body, @"\bDIEx\s+n[º°o.]?\s*:?\s*(\d{2,8})\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            number = match.Groups[1].Value;
            return true;
        }, timeout, cancellationToken);
        return number ?? throw new InvalidOperationException("O número do DIEx assinado não foi localizado.");
    }

    private sealed record CreatedProcessPage(string FinalUrl, string PdfPath, string ManagementUrl);

    private static CreatedProcessPage CreateAndAutuateProcess(
        IWebDriver driver,
        string subject,
        string interested,
        string documentNumber,
        string storageDirectory,
        CancellationToken cancellationToken)
    {
        driver.Navigate().GoToUrl(RegionalProcessCreationUrl);
        WaitForDocument(driver, TimeSpan.FromSeconds(45), cancellationToken);
        if ((driver.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A sessão do SPED terminou antes da criação do processo.");

        SelectPrimeDropdownValue(driver, "Pesquisa Rápida", "002.01 - NORMATIZAÇÃO. REGULAMENTAÇÃO", cancellationToken);

        var subjectField = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("textarea.ui-inputtext, textarea"))
                .FirstOrDefault(element => IsDisplayed(element)
                    && string.IsNullOrWhiteSpace(element.GetAttribute("readonly"))
                    && string.IsNullOrWhiteSpace(element.GetAttribute("disabled"))),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Campo Assunto do processo não encontrado.");
        SetText(subjectField, subject);
        DispatchInputEvents(driver, subjectField);

        var interestedField = WaitForElement(driver,
            d => FindDisplayed(d.FindElements(By.CssSelector("input[placeholder='Interessados'], input[placeholder*='Interessado']"))),
            TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException("Campo Interessados do processo não encontrado.");
        SetText(interestedField, interested);
        DispatchInputEvents(driver, interestedField);

        var originButton = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button")), "Buscar Documento de Origem no SPED"),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Botão Buscar Documento de Origem no SPED não encontrado.");
        Click(driver, originButton);

        var searchDialog = WaitForElement(driver, FindActiveDialog, TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Janela Buscar Documento no SPED não abriu.");
        var numberField = WaitForElement(driver,
            _ => FindDisplayed(searchDialog.FindElements(By.CssSelector("input[name='numeroDocumento']"))),
            TimeSpan.FromSeconds(12), cancellationToken)
            ?? throw new InvalidOperationException("Campo Nº do Documento não encontrado.");
        SetText(numberField, documentNumber);
        DispatchInputEvents(driver, numberField);

        var searchButton = WaitForElement(driver,
            _ => FindBestTextElement(searchDialog.FindElements(By.CssSelector("button")), "Pesquisar"),
            TimeSpan.FromSeconds(12), cancellationToken)
            ?? throw new InvalidOperationException("Botão Pesquisar documento não encontrado.");
        Click(driver, searchButton);

        var resultRow = WaitForElement(driver,
            _ => searchDialog.FindElements(By.CssSelector("tbody tr"))
                .FirstOrDefault(row => IsDisplayed(row) && NormalizeMatch(SafeText(row)).Contains(NormalizeMatch(documentNumber), StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30), cancellationToken)
            ?? throw new InvalidOperationException($"O DIEx nº {documentNumber} não apareceu na pesquisa do SPED.");
        var radio = FindDisplayed(resultRow.FindElements(By.CssSelector("div.ui-radiobutton-box, input[type='radio']")))
            ?? throw new InvalidOperationException("Seletor do DIEx pesquisado não encontrado.");
        Click(driver, radio);

        var includeButton = WaitForElement(driver,
            _ => FindBestTextElement(searchDialog.FindElements(By.CssSelector("button")), "Incluir"),
            TimeSpan.FromSeconds(12), cancellationToken)
            ?? throw new InvalidOperationException("Botão Incluir documento não encontrado.");
        Click(driver, includeButton);
        WaitForCondition(() => !IsDisplayed(searchDialog), TimeSpan.FromSeconds(20), cancellationToken);

        var saveButton = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button")), "Salvar"),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Botão Salvar processo não encontrado.");
        Click(driver, saveButton);
        var confirmation = WaitForElement(driver, FindActiveDialog, TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException("Confirmação de criação do processo não abriu.");
        var yesButton = WaitForElement(driver,
            _ => FindBestTextElement(confirmation.FindElements(By.CssSelector("button")), "Sim"),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Botão Sim da criação do processo não encontrado.");
        Click(driver, yesButton);

        WaitForCondition(() => (driver.Url ?? string.Empty).Contains("/manter-processo/gerenciar/", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(45), cancellationToken);
        var autuar = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button")), "Autuar"),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("O processo foi salvo, mas a tela de Autuar não foi confirmada.");
        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({block:'center'});", autuar);

        var managementUrl = driver.Url ?? string.Empty;
        var processPdfPath = SaveSpedPagePdf(driver, storageDirectory, $"Processo_SPED_DIEx_{documentNumber}.pdf");

        Click(driver, autuar);
        var autuationConfirmation = WaitForElement(driver, FindActiveDialog, TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException("A confirmação da autuação não abriu.");
        var confirmAutuation = WaitForElement(driver,
            _ => FindBestTextElement(autuationConfirmation.FindElements(By.CssSelector("button")), "Sim"),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Botão Sim da autuação não encontrado.");
        Click(driver, confirmAutuation);
        WaitForCondition(() => (driver.Url ?? string.Empty).Contains("/meus-processos", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(45), cancellationToken);
        return new CreatedProcessPage(driver.Url ?? string.Empty, processPdfPath, managementUrl);
    }

    private static void CreateDispatchDraft(
        IWebDriver driver,
        string processSubject,
        string dispatchSubject,
        string dispatchBodyHtml,
        string signerSearch,
        CancellationToken cancellationToken)
    {
        WaitForCondition(() => (driver.Url ?? string.Empty).Contains("/meus-processos", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(30), cancellationToken);

        var processRow = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("tbody tr"))
                .Where(IsDisplayed)
                .FirstOrDefault(row => NormalizeMatch(SafeText(row)).Contains(NormalizeMatch(processSubject), StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30), cancellationToken)
            ?? throw new InvalidOperationException("O processo recém-criado não foi localizado para redigir o despacho.");
        var subjectCell = FindBestTextElement(processRow.FindElements(By.CssSelector("div.conteudo-coluna-ellipsis, td, span")), processSubject)
            ?? processRow;
        Click(driver, subjectCell);
        WaitForCondition(() => (driver.Url ?? string.Empty).Contains("/manter-processo/gerenciar/", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(30), cancellationToken);

        if (HasDispatchDraft(driver)) return;

        var dispatchButton = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button, a")), "Redigir Despacho"),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Botão Redigir Despacho não encontrado no processo autuado.");
        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({block:'center'});", dispatchButton);

        var originalHandle = driver.CurrentWindowHandle;
        var handlesBefore = driver.WindowHandles.ToHashSet(StringComparer.Ordinal);
        var managementUrl = driver.Url ?? string.Empty;

        // Um único clique. O SPED abre o editor em outra janela/aba e pode deixá-la atrás da atual.
        Click(driver, dispatchButton);
        WaitForCondition(() =>
        {
            var newHandle = driver.WindowHandles.FirstOrDefault(handle => !handlesBefore.Contains(handle));
            if (!string.IsNullOrWhiteSpace(newHandle))
            {
                driver.SwitchTo().Window(newHandle);
                return true;
            }

            if (!string.Equals(driver.Url, managementUrl, StringComparison.OrdinalIgnoreCase)) return true;
            return FindDispatchSubjectField(driver) is not null;
        }, TimeSpan.FromSeconds(30), cancellationToken);
        WaitForDocument(driver, TimeSpan.FromSeconds(30), cancellationToken);

        FillDispatchSubject(driver, dispatchSubject, cancellationToken);
        FillRichTextEditor(driver, dispatchBodyHtml, cancellationToken);
        SelectDispatchSigner(driver, signerSearch, cancellationToken);

        var saveButton = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("button")), "Salvar"),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Botão Salvar do despacho não encontrado.");
        Click(driver, saveButton);

        var confirmation = WaitForElement(driver, FindActiveDialog, TimeSpan.FromSeconds(5), cancellationToken);
        if (confirmation is not null)
        {
            var confirm = FindBestTextElement(confirmation.FindElements(By.CssSelector("button")), "Sim")
                          ?? FindBestTextElement(confirmation.FindElements(By.CssSelector("button")), "Confirmar");
            if (confirm is not null) Click(driver, confirm);
        }

        WaitForCondition(() =>
        {
            try
            {
                return NormalizeMatch(SafePageText(driver)).Contains("DOCUMENTOS EM ELABORACAO", StringComparison.Ordinal)
                       || FindDispatchSubjectField(driver) is null;
            }
            catch (NoSuchWindowException) { return true; }
        }, TimeSpan.FromSeconds(25), cancellationToken, throwOnTimeout: false);

        try
        {
            var openHandles = driver.WindowHandles;
            if (openHandles.Contains(originalHandle, StringComparer.Ordinal))
            {
                string currentHandle;
                try { currentHandle = driver.CurrentWindowHandle; }
                catch (NoSuchWindowException) { currentHandle = string.Empty; }
                if (!string.IsNullOrWhiteSpace(currentHandle)
                    && !string.Equals(currentHandle, originalHandle, StringComparison.Ordinal)
                    && openHandles.Contains(currentHandle, StringComparer.Ordinal))
                {
                    try { driver.Close(); }
                    catch (WebDriverException) { }
                }
                driver.SwitchTo().Window(originalHandle);
            }
        }
        catch (WebDriverException) { }
    }

    private static bool HasDispatchDraft(IWebDriver driver)
    {
        try
        {
            var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const norm = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/\s+/g, ' ').trim().toUpperCase();
                const headings = Array.from(document.querySelectorAll('legend, h1, h2, h3, h4, .ui-fieldset-legend, .ui-panel-title'));
                const heading = headings.find(e => norm(e.innerText) === 'DOCUMENTOS EM ELABORACAO');
                if (!heading) return false;
                const root = heading.closest('fieldset, .ui-fieldset, .ui-panel, section') || heading.parentElement;
                if (!root) return false;
                return Array.from(root.querySelectorAll('tbody tr')).some(row => {
                    const cells = Array.from(row.querySelectorAll('td')).map(cell => norm(cell.innerText));
                    return cells.some(text => text === 'DESPACHO');
                });
                """);
            return result is bool found && found;
        }
        catch (WebDriverException) { return false; }
    }

    private static IWebElement? FindDispatchSubjectField(IWebDriver driver)
        => driver.FindElements(By.CssSelector(
                "#txAssunto, textarea[name*='assunto' i], input[name*='assunto' i], textarea[placeholder*='Assunto' i], input[placeholder*='Assunto' i]"))
            .FirstOrDefault(element => IsDisplayed(element)
                && string.IsNullOrWhiteSpace(element.GetAttribute("readonly"))
                && string.IsNullOrWhiteSpace(element.GetAttribute("disabled")));

    private static void FillDispatchSubject(
        IWebDriver driver,
        string subject,
        CancellationToken cancellationToken)
    {
        var field = WaitForElement(driver, FindDispatchSubjectField, TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Campo Assunto do despacho não encontrado.");
        SetText(field, subject);
        DispatchInputEvents(driver, field);
    }

    private static void SelectDispatchSigner(
        IWebDriver driver,
        string signerSearch,
        CancellationToken cancellationToken)
    {
        Exception? pickListError = null;
        try
        {
            SelectPickListValueAny(driver, ["Assinantes", "Assinante"], signerSearch, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            pickListError = ex;
        }

        var label = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("label.ui-dropdown-label"))
                .Where(IsDisplayed)
                .FirstOrDefault(element =>
                {
                    try
                    {
                        var container = element.FindElement(By.XPath("ancestor::*[self::div or self::span][1]"));
                        return NormalizeMatch(SafeText(container)).Contains("ASSINANTE", StringComparison.Ordinal);
                    }
                    catch (NoSuchElementException) { return false; }
                }),
            TimeSpan.FromSeconds(5), cancellationToken);
        if (label is null)
            throw new InvalidOperationException("Campo Assinantes do despacho não encontrado.", pickListError);

        Click(driver, label);
        var filter = WaitForElement(driver,
            d => FindDisplayed(d.FindElements(By.CssSelector("input.ui-dropdown-filter"))),
            TimeSpan.FromSeconds(5), cancellationToken);
        if (filter is not null) SetText(filter, signerSearch);
        var option = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("li.ui-dropdown-item")), signerSearch),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException($"Assinante '{signerSearch}' não encontrado no despacho.");
        Click(driver, option);
    }

    private static string ForwardAutuatedProcess(
        IWebDriver driver,
        string subject,
        string recipientSearch,
        string reasonSource,
        CancellationToken cancellationToken)
    {
        WaitForCondition(() => (driver.Url ?? string.Empty).Contains("/meus-processos", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(30), cancellationToken);

        var processRow = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("tbody tr"))
                .Where(IsDisplayed)
                .FirstOrDefault(row => NormalizeMatch(SafeText(row)).Contains(NormalizeMatch(subject), StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30), cancellationToken)
            ?? throw new InvalidOperationException("O processo recém-criado não foi localizado em Meus Processos pelo assunto.");
        var subjectCell = FindBestTextElement(processRow.FindElements(By.CssSelector("div.conteudo-coluna-ellipsis, td, span")), subject)
            ?? processRow;
        Click(driver, subjectCell);

        var forwardButton = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("button"))
                .FirstOrDefault(button => IsDisplayed(button)
                    && button.FindElements(By.CssSelector(".ui-icon-send")).Count > 0),
            TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException("Botão Encaminhar do processo não encontrado.");
        Click(driver, forwardButton);

        var forwardDialog = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("div.ui-dialog"))
                .LastOrDefault(dialog => IsDisplayed(dialog)
                    && NormalizeMatch(SafeText(dialog)).Contains("ENCAMINHAR", StringComparison.Ordinal)
                    && !NormalizeMatch(SafeText(dialog)).Contains("CONFIRMAR ENCAMINHAMENTO", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Janela Encaminhar não abriu.");

        var recipientDropdown = WaitForElement(driver,
            _ => FindDisplayed(forwardDialog.FindElements(By.CssSelector("label.ui-dropdown-label"))),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Campo Destinatário do encaminhamento não encontrado.");
        Click(driver, recipientDropdown);
        var recipientFilter = WaitForElement(driver,
            _ => FindDisplayed(forwardDialog.FindElements(By.CssSelector("input.ui-dropdown-filter"))),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Pesquisa de destinatário do encaminhamento não encontrada.");
        SetText(recipientFilter, recipientSearch);
        var recipientOption = WaitForElement(driver,
            _ => FindBestTextElement(forwardDialog.FindElements(By.CssSelector("li.ui-dropdown-item")), recipientSearch),
            TimeSpan.FromSeconds(25), cancellationToken)
            ?? throw new InvalidOperationException($"Destinatário '{recipientSearch}' não encontrado para encaminhamento.");
        Click(driver, recipientOption);

        var reasonField = WaitForElement(driver,
            _ => FindDisplayed(forwardDialog.FindElements(By.CssSelector("input[name='motivo']"))),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Campo Motivo do encaminhamento não encontrado.");
        SetText(reasonField, BuildShortForwardingReason(reasonSource, subject));
        DispatchInputEvents(driver, reasonField);

        var saveButton = WaitForElement(driver,
            _ => FindBestTextElement(forwardDialog.FindElements(By.CssSelector("button")), "Salvar"),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Botão Salvar do encaminhamento não encontrado.");
        Click(driver, saveButton);

        var confirmation = WaitForElement(driver,
            d => d.FindElements(By.CssSelector("div.ui-dialog"))
                .LastOrDefault(dialog => IsDisplayed(dialog)
                    && NormalizeMatch(SafeText(dialog)).Contains("CONFIRMAR ENCAMINHAMENTO", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("A confirmação final do encaminhamento não apareceu.");
        var confirmButton = WaitForElement(driver,
            _ => FindBestTextElement(confirmation.FindElements(By.CssSelector("button")), "Confirmar"),
            TimeSpan.FromSeconds(10), cancellationToken)
            ?? throw new InvalidOperationException("Botão Confirmar do encaminhamento não encontrado.");
        Click(driver, confirmButton);
        WaitForCondition(() => !IsDisplayed(confirmation), TimeSpan.FromSeconds(35), cancellationToken);
        return driver.Url ?? string.Empty;
    }

    private static string BuildShortForwardingReason(string reasonSource, string fallbackSubject)
    {
        var reason = Regex.Replace(string.IsNullOrWhiteSpace(reasonSource) ? fallbackSubject : reasonSource,
            "\\s+", " ").Trim();
        reason = Regex.Replace(reason, @"\s*\([^)]*\)\s*$", string.Empty).Trim(' ', '-', '—', '/', '.');
        if (reason.Length <= 80) return reason;
        var shortened = reason[..80].TrimEnd();
        var lastSpace = shortened.LastIndexOf(' ');
        return (lastSpace >= 40 ? shortened[..lastSpace] : shortened).TrimEnd(' ', '-', '—', '/', '.');
    }

    private static string SafePageText(IWebDriver driver)
    {
        try { return driver.FindElement(By.TagName("body")).Text ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void SetCheckboxByNearbyText(IWebDriver driver, IWebElement scope, string text, bool desired)
    {
        var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
            const root = arguments[0], wanted = arguments[1], desired = arguments[2];
            const norm = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toUpperCase();
            const target = Array.from(root.querySelectorAll('label, span, div, p')).find(e => norm(e.innerText).includes(norm(wanted)));
            if (!target) return false;
            let input = target.querySelector('input[type=checkbox]');
            if (!input && target.parentElement) input = target.parentElement.querySelector('input[type=checkbox]');
            if (!input) {
              const id = target.getAttribute('for');
              if (id) input = document.getElementById(id);
            }
            if (!input) return false;
            if (input.checked !== desired) input.click();
            input.dispatchEvent(new Event('change', {bubbles:true}));
            return input.checked === desired;
            """, scope, text, desired);
        if (result is not bool success || !success)
            throw new InvalidOperationException("Opção Não encaminhar automaticamente não encontrada.");
    }

    private static void ClearPickListTarget(IWebDriver driver, IWebElement dialog, CancellationToken cancellationToken)
    {
        var target = FindDisplayed(dialog.FindElements(By.CssSelector(".ui-picklist-target-wrapper")));
        if (target is null) return;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var selectedItems = target.FindElements(By.CssSelector("li.ui-picklist-item")).Where(IsDisplayed).ToList();
            var item = selectedItems.FirstOrDefault();
            if (item is null) return;
            var previousCount = selectedItems.Count;
            Click(driver, item);
            var buttonsCell = FindDisplayed(dialog.FindElements(By.CssSelector(".ui-picklist-buttons-cell")));
            var moveLeft = buttonsCell is null ? null : FindPickListArrow(buttonsCell, "pi-angle-left");
            if (moveLeft is null) throw new InvalidOperationException("Seta para retirar o remetente atual não encontrada.");
            Click(driver, moveLeft);
            WaitForCondition(
                () => target.FindElements(By.CssSelector("li.ui-picklist-item")).Count(IsDisplayed) < previousCount,
                TimeSpan.FromSeconds(5), cancellationToken,
                throwOnTimeout: false);
        }
    }

    private static IWebElement? FindPickListArrow(IWebElement buttonsCell, string iconClass)
        => buttonsCell.FindElements(By.CssSelector("button"))
            .Where(IsDisplayed)
            .FirstOrDefault(button =>
            {
                try
                {
                    if ((button.GetAttribute("icon") ?? string.Empty).Contains(iconClass, StringComparison.OrdinalIgnoreCase))
                        return true;
                    return button.FindElements(By.CssSelector("span." + iconClass)).Any();
                }
                catch (StaleElementReferenceException) { return false; }
            });

    private static void FillSubject(IWebDriver driver, string subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("O assunto do DIEx está vazio.");
        var field = WaitForElement(driver,
            d => FindDisplayed(d.FindElements(By.CssSelector("#txAssunto"))),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException("Campo Assunto não encontrado.");
        SetText(field, subject);
        DispatchInputEvents(driver, field);
    }

    private static void FillRichTextEditor(IWebDriver driver, string html, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(html)) throw new InvalidOperationException("O corpo do DIEx está vazio.");
        var script = (IJavaScriptExecutor)driver;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var updated = script.ExecuteScript("""
                    var html = arguments[0];
                    if (!window.CKEDITOR || !window.CKEDITOR.instances) return false;
                    for (var name in window.CKEDITOR.instances) {
                        if (!Object.prototype.hasOwnProperty.call(window.CKEDITOR.instances, name)) continue;
                        var editor = window.CKEDITOR.instances[name];
                        if (!editor || editor.status !== 'ready') continue;
                        editor.setData(html);
                        editor.fire('change');
                        if (editor.updateElement) editor.updateElement();
                        return true;
                    }
                    return false;
                    """, html);
                if (updated is bool success && success) return;
            }
            catch (WebDriverException) { }
            Thread.Sleep(200);
        }

        foreach (var frame in driver.FindElements(By.CssSelector("iframe.cke_wysiwyg_frame, .cke_contents iframe, iframe")).Where(IsDisplayed))
        {
            try
            {
                driver.SwitchTo().Frame(frame);
                var editorBody = FindDisplayed(driver.FindElements(By.CssSelector("body.cke_editable, body[contenteditable='true'], [contenteditable='true']")));
                if (editorBody is null) continue;
                script.ExecuteScript("""
                    arguments[0].innerHTML = arguments[1];
                    arguments[0].dispatchEvent(new Event('input', { bubbles: true }));
                    arguments[0].dispatchEvent(new Event('change', { bubbles: true }));
                    arguments[0].dispatchEvent(new Event('blur', { bubbles: true }));
                    """, editorBody, html);
                return;
            }
            catch (WebDriverException) { }
            finally { driver.SwitchTo().DefaultContent(); }
        }

        var inlineEditor = FindDisplayed(driver.FindElements(By.CssSelector(".cke_editable[contenteditable='true'], [contenteditable='true']")))
            ?? throw new InvalidOperationException("Editor do corpo do DIEx não encontrado.");
        script.ExecuteScript("""
            arguments[0].innerHTML = arguments[1];
            arguments[0].dispatchEvent(new Event('input', { bubbles: true }));
            arguments[0].dispatchEvent(new Event('change', { bubbles: true }));
            arguments[0].dispatchEvent(new Event('blur', { bubbles: true }));
            """, inlineEditor, html);
    }

    private static void UploadAttachments(IWebDriver driver, IReadOnlyList<string> attachmentPaths, CancellationToken cancellationToken)
    {
        if (attachmentPaths.Count == 0) return;
        var files = attachmentPaths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count != attachmentPaths.Count)
            throw new InvalidOperationException("Um ou mais anexos salvos não foram encontrados na pasta da Grat Rep.");

        var total = files.Sum(path => new FileInfo(path).Length);
        if (total > GratificationService.SpedAttachmentLimitBytes)
            throw new InvalidOperationException($"Os anexos somam {total / 1_000_000d:0.00} MB e ultrapassam o limite total de 10 MB do SPED.");

        foreach (var file in files)
        {
            var input = WaitForElement(driver,
                d => d.FindElements(By.CssSelector("p-fileupload input[type='file'][multiple], input[type='file'][multiple]")).LastOrDefault(),
                TimeSpan.FromSeconds(20), cancellationToken)
                ?? throw new InvalidOperationException("Campo Escolher Arquivos não encontrado.");
            SendFileToInput(driver, input, file);
            var fileName = Path.GetFileName(file);
            WaitForCondition(
                () => PageContainsAttachment(driver, fileName),
                TimeSpan.FromSeconds(8), cancellationToken);
            Thread.Sleep(700);
        }

        Thread.Sleep(1800);
        var missing = files.Select(path => Path.GetFileName(path)).Where(name => !PageContainsAttachment(driver, name)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("O SPED não confirmou estes anexos: " + string.Join(", ", missing) + ".");
    }

    private static void SendFileToInput(IWebDriver driver, IWebElement input, string file)
    {
        try { input.SendKeys(file); }
        catch (ElementNotInteractableException)
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                arguments[0].style.display = 'block';
                arguments[0].style.visibility = 'visible';
                arguments[0].style.opacity = '1';
                arguments[0].style.position = 'fixed';
                arguments[0].style.left = '10px';
                arguments[0].style.top = '10px';
                arguments[0].style.width = '300px';
                arguments[0].style.height = '40px';
                arguments[0].style.zIndex = '2147483647';
                """, input);
            input.SendKeys(file);
        }
    }

    private static bool PageContainsAttachment(IWebDriver driver, string fileName)
    {
        try
        {
            var found = ((IJavaScriptExecutor)driver).ExecuteScript("""
                var wanted = (arguments[0] || '').toLocaleLowerCase();
                var body = (document.body && document.body.innerText || '').toLocaleLowerCase();
                if (body.indexOf(wanted) >= 0) return true;
                return Array.from(document.querySelectorAll('textarea, input:not([type=file])'))
                    .some(function (element) { return (element.value || '').toLocaleLowerCase().indexOf(wanted) >= 0; });
                """, fileName);
            return found is bool success && success;
        }
        catch (WebDriverException) { return false; }
    }

    private static void DispatchInputEvents(IWebDriver driver, IWebElement element)
    {
        ((IJavaScriptExecutor)driver).ExecuteScript("""
            arguments[0].dispatchEvent(new Event('input', { bubbles: true }));
            arguments[0].dispatchEvent(new Event('change', { bubbles: true }));
            arguments[0].dispatchEvent(new Event('blur', { bubbles: true }));
            """, element);
    }

    private static void SelectPrimeDropdownValue(IWebDriver driver, string currentLabel, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var label = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("label.ui-dropdown-label")), currentLabel),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException($"Campo {currentLabel} não encontrado.");
        if (TextMatchScore(label.Text, value) == 0) return;
        Click(driver, label);

        var filter = WaitForElement(driver,
            d => FindDisplayed(d.FindElements(By.CssSelector("input.ui-dropdown-filter"))),
            TimeSpan.FromSeconds(4), cancellationToken);
        if (filter is not null) SetText(filter, value);

        var option = WaitForElement(driver,
            d => FindBestTextElement(d.FindElements(By.CssSelector("li.ui-dropdown-item")), value),
            TimeSpan.FromSeconds(20), cancellationToken)
            ?? throw new InvalidOperationException($"Opção {value} não encontrada.");
        Click(driver, option);
        Thread.Sleep(450);
    }

    private static IWebElement? FindActiveDialog(IWebDriver driver)
        => driver.FindElements(By.CssSelector("div.ui-dialog")).LastOrDefault(IsDisplayed);

    private static IWebElement? FindDisplayed(IEnumerable<IWebElement> elements)
        => elements.FirstOrDefault(IsDisplayed);

    private static IWebElement? FindBestTextElement(IEnumerable<IWebElement> elements, string query)
        => elements
            .Where(IsDisplayed)
            .Select(x => new { Element = x, Score = TextMatchScore(x.Text, query) })
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score)
            .ThenBy(x => SafeText(x.Element).Length)
            .Select(x => x.Element)
            .FirstOrDefault();

    private static int TextMatchScore(string? text, string? query)
    {
        var candidate = NormalizeMatch(text);
        var wanted = NormalizeMatch(query);
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(wanted)) return int.MaxValue;
        if (candidate.Equals(wanted, StringComparison.Ordinal)) return 0;
        var tokens = Regex.Split(candidate, "[^A-Z0-9]+", RegexOptions.CultureInvariant);
        if (!wanted.Contains(' ') && tokens.Contains(wanted, StringComparer.Ordinal)) return 1;
        if (candidate.EndsWith(" " + wanted, StringComparison.Ordinal)) return 2;
        if (candidate.Contains(wanted, StringComparison.Ordinal)) return 3;
        var wantedTokens = Regex.Split(wanted, "[^A-Z0-9]+", RegexOptions.CultureInvariant).Where(x => x.Length > 1).ToList();
        return wantedTokens.Count > 0 && wantedTokens.All(x => tokens.Contains(x, StringComparer.Ordinal)) ? 4 : int.MaxValue;
    }

    private static string NormalizeMatch(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(character));
        return Regex.Replace(builder.ToString(), "\\s+", " ").Trim();
    }

    private static string SafeText(IWebElement element)
    {
        try { return element.Text ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeDriverUrl(IWebDriver driver)
    {
        try { return driver.Url ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static IWebElement? WaitForElement(
        IWebDriver driver,
        Func<IWebDriver, IWebElement?> finder,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var element = finder(driver);
                if (element is not null) return element;
            }
            catch (StaleElementReferenceException) { }
            catch (NoSuchElementException) { }
            Thread.Sleep(160);
        }
        return null;
    }

    private static void WaitForCondition(ActionProbe condition, TimeSpan timeout, CancellationToken cancellationToken, bool throwOnTimeout = true)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (condition()) return; }
            catch (StaleElementReferenceException) { return; }
            Thread.Sleep(140);
        }
        if (throwOnTimeout) throw new TimeoutException("O SPED demorou para concluir a etapa.");
    }

    private delegate bool ActionProbe();

    private static void SetText(IWebElement element, string value)
    {
        element.Click();
        element.SendKeys(Keys.Control + "a");
        element.SendKeys(value);
        Thread.Sleep(350);
    }

    private static void Click(IWebDriver driver, IWebElement element)
    {
        try { element.Click(); }
        catch { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element); }
        Thread.Sleep(220);
    }

    private static void NormalizeSavedChoices(SpedMappingSettings settings)
    {
        settings.SenderHistory ??= [];
        settings.ExternalRecipientHistory ??= [];
        settings.ClassificationHistory ??= [];
        settings.DocumentPurposeHistory ??= [];
        settings.SenderSearch = settings.SenderSearch?.Trim() ?? string.Empty;
        settings.ExternalRecipientSearch = settings.ExternalRecipientSearch?.Trim() ?? string.Empty;
        settings.ClassificationSearch = settings.ClassificationSearch?.Trim() ?? string.Empty;
        settings.DocumentPurpose = settings.DocumentPurpose?.Trim() ?? string.Empty;
        settings.Subject = settings.Subject?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(settings.SenderSearch) || settings.SenderSearch.Equals("Geral", StringComparison.OrdinalIgnoreCase))
            settings.SenderSearch = "PHABLLO";
        if (string.IsNullOrWhiteSpace(settings.ExternalRecipientSearch) || settings.ExternalRecipientSearch.Equals("Geral", StringComparison.OrdinalIgnoreCase))
            settings.ExternalRecipientSearch = "E1";
        if (string.IsNullOrWhiteSpace(settings.ClassificationSearch) || settings.ClassificationSearch.Equals("Geral", StringComparison.OrdinalIgnoreCase))
            settings.ClassificationSearch = "085.612 - GRATIFICAÇÕES";
        if (string.IsNullOrWhiteSpace(settings.DocumentPurpose)) settings.DocumentPurpose = "Geral";
        if (string.IsNullOrWhiteSpace(settings.Subject)) settings.Subject = "Gratificação de Representação (2%)";
        AddChoice(settings.SenderHistory, settings.SenderSearch);
        AddChoice(settings.ExternalRecipientHistory, settings.ExternalRecipientSearch);
        AddChoice(settings.ClassificationHistory, settings.ClassificationSearch);
        AddChoice(settings.DocumentPurposeHistory, settings.DocumentPurpose);
    }

    private static void AddChoice(List<string> history, string value)
    {
        history.RemoveAll(x => string.IsNullOrWhiteSpace(x) || x.Equals(value, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrWhiteSpace(value)) history.Insert(0, value);
        if (history.Count > 12) history.RemoveRange(12, history.Count - 12);
    }

    private static void WaitForDocument(IWebDriver driver, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState")?.ToString();
                if (state is "interactive" or "complete") return;
            }
            catch { }
            Thread.Sleep(200);
        }
    }

    private static void EnsureBrowserAvailable(IWebDriver driver)
    {
        try { _ = driver.WindowHandles; }
        catch (Exception ex) { throw new InvalidOperationException("O navegador do SPED não está mais aberto.", ex); }
    }

    private static void TryMaximize(IWebDriver driver)
    {
        try { driver.Manage().Window.Maximize(); }
        catch { }
    }

    private static void InjectRecorder(IWebDriver driver)
    {
        if (driver is not IJavaScriptExecutor script) return;
        script.ExecuteScript(RecorderScript);
    }

    private string TrySaveScreenshot(IWebDriver driver, int sequence)
    {
        try
        {
            if (driver is not ITakesScreenshot takesScreenshot) return string.Empty;
            var relative = Path.Combine("capturas", $"passo_{sequence:0000}.png");
            takesScreenshot.GetScreenshot().SaveAsFile(Path.Combine(_sessionDirectory, relative));
            return relative.Replace('\\', '/');
        }
        catch { return string.Empty; }
    }

    private void TrySaveInitialScreenshot(IWebDriver driver)
    {
        try
        {
            if (driver is not ITakesScreenshot takesScreenshot) return;
            takesScreenshot.GetScreenshot().SaveAsFile(Path.Combine(_sessionDirectory, "capturas", "passo_0000_inicio.png"));
        }
        catch { }
    }

    private static string BuildFriendlyLine(SpedBrowserEvent item)
    {
        var label = FirstNotEmpty(item.AriaLabel, item.Placeholder, item.Text, item.Name, item.Id, item.Tag);
        var value = string.IsNullOrWhiteSpace(item.ValueSummary) ? string.Empty : $" | valor: {item.ValueSummary}";
        var screenshot = string.IsNullOrWhiteSpace(item.Screenshot) ? string.Empty : $" | captura: {item.Screenshot}";
        return $"{item.Sequence:0000} | {item.EventType.ToUpperInvariant()} | {label} | CSS: {item.CssSelector}{value}{screenshot} | {item.Url}";
    }

    private static string FirstNotEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "elemento sem rótulo";

    private static void SanitizeEvent(SpedBrowserEvent item)
    {
        item.Url = SanitizeText(item.Url, 1000);
        item.Tag = SanitizeText(item.Tag, 60);
        item.Id = SanitizeText(item.Id, 200);
        item.Name = SanitizeText(item.Name, 200);
        item.Role = SanitizeText(item.Role, 100);
        item.InputType = SanitizeText(item.InputType, 80);
        item.AriaLabel = SanitizeText(item.AriaLabel, 400);
        item.Placeholder = SanitizeText(item.Placeholder, 400);
        item.Text = SanitizeText(item.Text, 500);
        item.ValueSummary = SanitizeText(item.ValueSummary, 300);
        item.CssSelector = SanitizeText(item.CssSelector, 1200);
        item.XPath = SanitizeText(item.XPath, 1200);
        item.OuterHtml = SanitizeText(item.OuterHtml, 2500);
    }

    private static string SanitizeText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        clean = CpfRegex().Replace(clean, "[CPF PROTEGIDO]");
        clean = LongNumberRegex().Replace(clean, "[NÚMERO PROTEGIDO]");
        return clean.Length <= maxLength ? clean : clean[..maxLength] + "…";
    }

    private Task StopRecordingCoreAsync()
    {
        if (_recordingCts is not null && !_recordingCts.IsCancellationRequested)
            _recordingCts.Cancel();
        _recordingTask = null;
        _recordingCts?.Dispose();
        _recordingCts = null;
        return Task.CompletedTask;
    }

    private void CloseDriverCore()
    {
        if (_driver is null) return;
        try { _driver.Quit(); } catch { }
        try { _driver.Dispose(); } catch { }
        _driver = null;
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status);
    private void RaiseMessage(string message) => RecorderMessage?.Invoke(this, message);
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpedMappingService));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _driverGate.WaitAsync();
        try
        {
            await StopRecordingCoreAsync();
            CloseDriverCore();
        }
        finally
        {
            _driverGate.Release();
            _driverGate.Dispose();
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{3}\.?\d{3}\.?\d{3}-?\d{2}(?!\d)")]
    private static partial Regex CpfRegex();

    [GeneratedRegex(@"(?<!\d)\d{9,}(?!\d)")]
    private static partial Regex LongNumberRegex();

    private const string RecorderScript = """
        (() => {
            if (window.__sigfurSpedRecorderInstalled) return;
            window.__sigfurSpedRecorderInstalled = true;
            window.__sigfurSpedEvents = window.__sigfurSpedEvents || [];

            const clean = (value, max = 500) => {
                const text = String(value || '').replace(/\s+/g, ' ').trim();
                return text.length <= max ? text : text.slice(0, max) + '…';
            };
            const sensitive = element => {
                const source = [element.type, element.name, element.id, element.placeholder,
                    element.getAttribute && element.getAttribute('aria-label')]
                    .filter(Boolean).join(' ').toLowerCase();
                return source.includes('password') || source.includes('senha');
            };
            const editable = element => {
                if (!element || !element.closest) return false;
                return !!element.closest('input, textarea, [contenteditable="true"], [role="textbox"]');
            };
            const css = element => {
                if (!element || element.nodeType !== 1) return '';
                if (element.id) return '#' + CSS.escape(element.id);
                for (const attribute of ['data-testid', 'name', 'aria-label', 'placeholder']) {
                    const value = element.getAttribute && element.getAttribute(attribute);
                    if (value) {
                        const candidate = `${element.tagName.toLowerCase()}[${attribute}="${CSS.escape(value)}"]`;
                        try { if (document.querySelectorAll(candidate).length === 1) return candidate; } catch { }
                    }
                }
                const parts = [];
                let current = element;
                while (current && current.nodeType === 1 && parts.length < 7) {
                    let part = current.tagName.toLowerCase();
                    const classes = Array.from(current.classList || [])
                        .filter(x => !/^(ng-|mat-|cdk-|active|focus|hover)/i.test(x)).slice(0, 2);
                    if (classes.length) part += '.' + classes.map(x => CSS.escape(x)).join('.');
                    const siblings = current.parentElement
                        ? Array.from(current.parentElement.children).filter(x => x.tagName === current.tagName)
                        : [];
                    if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
                    parts.unshift(part);
                    const candidate = parts.join(' > ');
                    try { if (document.querySelectorAll(candidate).length === 1) return candidate; } catch { }
                    current = current.parentElement;
                }
                return parts.join(' > ');
            };
            const xpath = element => {
                if (!element || element.nodeType !== 1) return '';
                if (element.id) return `//*[@id="${String(element.id).replace(/"/g, '')}"]`;
                const parts = [];
                let current = element;
                while (current && current.nodeType === 1 && parts.length < 8) {
                    const tag = current.tagName.toLowerCase();
                    const siblings = current.parentElement
                        ? Array.from(current.parentElement.children).filter(x => x.tagName === current.tagName)
                        : [];
                    parts.unshift(siblings.length > 1 ? `${tag}[${siblings.indexOf(current) + 1}]` : tag);
                    current = current.parentElement;
                }
                return '/' + parts.join('/');
            };
            const valueSummary = element => {
                if (!element) return '';
                if (sensitive(element)) return '[PROTEGIDO]';
                if (element.type === 'checkbox' || element.type === 'radio') return element.checked ? 'marcado' : 'desmarcado';
                if (element.tagName === 'SELECT') {
                    const selected = element.options && element.options[element.selectedIndex];
                    return selected ? clean(selected.textContent, 200) : '';
                }
                if ('value' in element || element.isContentEditable)
                    return `[${String(element.value || element.textContent || '').length} caracteres]`;
                return '';
            };
            const safeHtml = element => {
                try {
                    const clone = element.cloneNode(false);
                    if (clone.removeAttribute) {
                        clone.removeAttribute('value');
                        clone.removeAttribute('src');
                    }
                    return clean(clone.outerHTML, 2000);
                } catch { return ''; }
            };
            const record = (eventType, rawTarget) => {
                let element = rawTarget && rawTarget.nodeType === 3 ? rawTarget.parentElement : rawTarget;
                if (!element || element.nodeType !== 1) return;
                const editor = element.closest && element.closest('input, textarea, [contenteditable="true"], [role="textbox"]');
                const target = editor || element;
                const label = target.getAttribute && (target.getAttribute('aria-label') || target.getAttribute('title'));
                let text = editable(target) ? '' : clean(target.innerText || target.textContent, 500);
                if (!text && target.labels && target.labels.length) text = clean(target.labels[0].innerText, 300);
                window.__sigfurSpedEvents.push({
                    browserTime: new Date().toISOString(),
                    eventType,
                    url: location.origin + location.pathname + location.hash,
                    tag: (target.tagName || '').toLowerCase(),
                    id: target.id || '',
                    name: target.getAttribute && target.getAttribute('name') || '',
                    role: target.getAttribute && target.getAttribute('role') || '',
                    inputType: target.getAttribute && target.getAttribute('type') || '',
                    ariaLabel: label || '',
                    placeholder: target.getAttribute && target.getAttribute('placeholder') || '',
                    text,
                    valueSummary: valueSummary(target),
                    cssSelector: css(target),
                    xpath: xpath(target),
                    outerHtml: safeHtml(target)
                });
                if (window.__sigfurSpedEvents.length > 1000) window.__sigfurSpedEvents.shift();
            };

            document.addEventListener('click', event => record('click', event.target), true);
            document.addEventListener('change', event => record('change', event.target), true);
            document.addEventListener('blur', event => {
                if (editable(event.target)) record('edit', event.target);
            }, true);
        })();
        """;
}
