using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Support.UI;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Views.Bulletin;

namespace SIGFUR.Wpf.Services;

public sealed class SisbolBulletinDownloadResult
{
    public int Year { get; init; }
    public int BulletinTypeCode { get; init; }
    public string BulletinTypeName { get; init; } = string.Empty;
    public int? Month { get; init; }
    public List<string> DownloadedFiles { get; } = [];
    public List<string> SkippedFiles { get; } = [];
    public List<string> Errors { get; } = [];
    public int Downloaded => DownloadedFiles.Count;
    public int Skipped => SkippedFiles.Count;
}

public sealed class SisbolAutomationService : IDisposable
{
    public const string LoginUrl = "https://10.122.8.31/band/sisbol.php";
    public const string MatterUrl = "https://10.122.8.31/band/cadmateriabi.php?codTipoBol=3";

    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _lastSubmissionFingerprint = string.Empty;
    private DateTime _lastSubmissionAtUtc;
    private IWebDriver? _driver;
    private object? _driverService;
    private string _browser = string.Empty;
    private bool _confirmed;
    private int _serviceProcessId;
    private readonly HashSet<int> _driverProcessIds = [];
    private readonly HashSet<int> _browserProcessIds = [];
    private readonly List<IntPtr> _hiddenWindows = [];
    private bool _disposed;
    private bool _cachedAlive;
    private bool _cachedReady;
    private DateTime _lastStatusProbeUtc = DateTime.MinValue;
    private string? _diagnosticDirectory;
    private string? _diagnosticZipFile;
    private string _diagnosticSessionId = string.Empty;

    private const string DiagnosticSubjectText = "ADICIONAL";
    private const string DiagnosticBodyText = "TESTE TÉCNICO SIGFUR - DIAGNÓSTICO DO FLUXO SISBOL. NÃO PUBLICAR EM BOLETIM REAL SEM AUTORIZAÇÃO.";

    public SisbolAutomationService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
    }

    public bool IsAlive => DriverAlive();

    public bool IsReady
    {
        get
        {
            if (!_confirmed || !DriverAlive() || _driver is null)
            {
                _cachedAlive = _driver is not null && DriverAlive();
                _cachedReady = false;
                return false;
            }
            try
            {
                if (LooksLikeLoginOrCaptcha(_driver))
                {
                    _confirmed = false;
                    _cachedAlive = true;
                    _cachedReady = false;
                    return false;
                }
                _cachedAlive = true;
                _cachedReady = true;
                return true;
            }
            catch
            {
                _confirmed = false;
                _cachedAlive = false;
                _cachedReady = false;
                return false;
            }
        }
    }


    public async Task<(bool Ready, bool Alive, string Browser)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // O Selenium pode demorar para responder quando o navegador está oculto,
        // a rede está lenta ou a sessão expirou. Nunca fazemos essa consulta no
        // thread da interface. Se outra operação estiver usando o navegador,
        // devolvemos imediatamente o último estado conhecido.
        if (!await _gate.WaitAsync(0, cancellationToken))
            return (_cachedReady, _cachedAlive, BrowserLabel);

        try
        {
            return await Task.Run(() =>
            {
                var alive = DriverAlive();
                var ready = false;
                if (_confirmed && alive && _driver is not null)
                {
                    try
                    {
                        ready = !LooksLikeLoginOrCaptcha(_driver);
                        if (!ready) _confirmed = false;
                    }
                    catch
                    {
                        ready = false;
                        _confirmed = false;
                    }
                }

                _cachedAlive = alive;
                _cachedReady = ready;
                _lastStatusProbeUtc = DateTime.UtcNow;
                return (ready, alive, BrowserLabel);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public (bool Ready, bool Alive, string Browser, DateTime LastProbeUtc) GetCachedStatus()
        => (_cachedReady, _cachedAlive, BrowserLabel, _lastStatusProbeUtc);

    public string BrowserLabel => NormalizeBrowser(_browser) switch
    {
        "edge" => "Microsoft Edge",
        "chrome" => "Google Chrome",
        "firefox" => "Mozilla Firefox",
        _ => string.Empty
    };

    public async Task PrepareAsync(Window owner)
    {
        if (IsReady)
        {
            HideCurrentBrowser();
            SigfurDialog.Show(owner, "A sessão do SisBol já está preparada, compartilhada e oculta.", "SisBol pronto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = await LoadSettingsAsync();
        var dialog = new SisbolLoginWindow(this, settings) { Owner = owner };
        dialog.ShowDialog();
    }

    public async Task<string> OpenLoginAsync(SisbolSettings settings, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Browser = NormalizeBrowser(settings.Browser);
        await SaveSettingsAsync(settings, password);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DriverAlive() && !NormalizeBrowser(_browser).Equals(settings.Browser, StringComparison.OrdinalIgnoreCase))
                    DisposeDriver(fast: true);

                if (!DriverAlive())
                {
                    DisposeDriver(fast: true);
                    var launch = CreateDriver(settings.Browser);
                    _driver = launch.Driver;
                    _driverService = launch.Service;
                    _serviceProcessId = launch.ServiceProcessId;
                    _driverProcessIds.Clear();
                    _driverProcessIds.UnionWith(launch.DriverProcessIds);
                    _browserProcessIds.Clear();
                    _browserProcessIds.UnionWith(launch.BrowserProcessIds);
                    _browser = settings.Browser;
                    HideDriverServiceWindows();
                    _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(25);
                }

                _confirmed = false;
                ShowBrowser(_driver!);
                _driver!.Navigate().GoToUrl(LoginUrl);
                WaitReady(_driver, 10);
                WaitForDomIdle(_driver, TimeSpan.FromSeconds(2));
                TryFillLogin(_driver, settings.Login, password);
            }, cancellationToken);

            return "Digite o captcha no navegador e pressione Enter. Depois volte ao SIGFUR e clique em ‘Já entrei — validar e ocultar’.";
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao abrir login do SisBol.", ex);
            throw new InvalidOperationException("Não consegui abrir o navegador do SisBol. " + ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Success, string Message)> ConfirmLoginAsync(SisbolSettings settings, string password, CancellationToken cancellationToken = default)
    {
        await SaveSettingsAsync(settings, password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null) return (false, "O navegador do SisBol não está mais aberto.");

            return await Task.Run(() =>
            {
                try
                {
                    WaitReady(_driver, 10);
                    WaitForDomIdle(_driver, TimeSpan.FromSeconds(2));
                    if (LooksLikeLoginOrCaptcha(_driver))
                    {
                        _confirmed = false;
                        ShowBrowser(_driver);
                        return (false, "O SisBol ainda está na tela de login/captcha. Conclua o login no navegador e tente validar novamente.");
                    }

                    _confirmed = true;
                    _cachedAlive = true;
                    _cachedReady = true;
                    settings.HideAfterLogin = true;
                    if (!HideBrowser(_driver))
                    {
                        _confirmed = false;
                        _cachedReady = false;
                        ShowBrowser(_driver);
                        return (false, "A sessão foi validada, mas o Windows não permitiu ocultar completamente a janela do navegador. Tente validar novamente.");
                    }
                    return (true, "SisBol preparado. A sessão foi validada e será reutilizada por todos os módulos.");
                }
                catch (Exception ex)
                {
                    _confirmed = false;
                    return (false, "Não consegui validar a sessão do SisBol: " + ex.Message);
                }
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }


    public async Task<SisbolBulletinDownloadResult> DownloadGeneratedBulletinsAsync(
        int bulletinTypeCode,
        int year,
        string outputDirectory,
        bool replaceExisting = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        int? month = null)
    {
        if (bulletinTypeCode is not 1 and not 3)
            throw new ArgumentOutOfRangeException(nameof(bulletinTypeCode), "Use 1 para Boletim Interno ou 3 para Aditamento do Furriel.");
        if (year < 2000 || year > DateTime.Today.Year + 1)
            throw new ArgumentOutOfRangeException(nameof(year), "Ano inválido para download de boletim.");
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "Mês inválido para download de boletim.");
        if (year == DateTime.Today.Year && month.HasValue && month.Value > DateTime.Today.Month)
            throw new InvalidOperationException("O mês escolhido ainda não chegou. Para o ano atual, baixe somente até o mês corrente.");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Informe a pasta de destino.", nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);
        var typeName = bulletinTypeCode == 1 ? "Boletim Interno" : "Aditamento do Furriel";
        var result = new SisbolBulletinDownloadResult
        {
            Year = year,
            Month = month,
            BulletinTypeCode = bulletinTypeCode,
            BulletinTypeName = typeName
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null || !_confirmed)
                throw new InvalidOperationException("Prepare o SisBol primeiro. Faça login/captcha uma vez em ‘Preparar SisBol’ e deixe a sessão pronta.");

            return await Task.Run(async () =>
            {
                var driver = _driver!;
                HideBrowser(driver);
                var maxMonth = year == DateTime.Today.Year ? DateTime.Today.Month : 12;
                if (year > DateTime.Today.Year) maxMonth = 0;
                if (maxMonth <= 0) return result;
                var startMonth = month ?? 1;
                var endMonth = month ?? maxMonth;
                if (startMonth > maxMonth) return result;

                var sisbolBaseUrl = ResolveSisbolBaseUrl(driver);
                using var http = BuildAuthenticatedSisbolClient(driver, sisbolBaseUrl);
                for (var currentMonth = startMonth; currentMonth <= endMonth; currentMonth++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"Consultando {typeName} {currentMonth:00}/{year}...");
                    var pageUrl = $"{sisbolBaseUrl.TrimEnd('/')}/band/baixar_boletim.php?codTipoBol={bulletinTypeCode}&ano={year}&mes={currentMonth}";
                    try
                    {
                        driver.Navigate().GoToUrl(pageUrl);
                        WaitReady(driver, 20);
                        WaitForDomIdle(driver, TimeSpan.FromSeconds(2), cancellationToken);
                        if (LooksLikeLoginOrCaptcha(driver))
                        {
                            // Normalmente isso acontecia porque o download era aberto no domínio
                            // sisbol.4ciape.eb.mil.br enquanto a sessão preparada estava no IP
                            // 10.122.8.31. Agora a rotina usa o mesmo host autenticado do navegador.
                            _confirmed = false;
                            throw new InvalidOperationException("A sessão do SisBol não foi aceita nesta página de download. Prepare/valide o SisBol novamente e tente baixar outra vez.");
                        }

                        var links = ExtractSisbolDownloadLinks(driver, pageUrl);
                        if (links.Count == 0)
                        {
                            progress?.Report($"Nenhum arquivo encontrado em {currentMonth:00}/{year}.");
                            continue;
                        }

                        var currentCookies = BuildSisbolCookieHeader(driver);
                        if (!string.IsNullOrWhiteSpace(currentCookies))
                        {
                            if (http.DefaultRequestHeaders.Contains("Cookie"))
                                http.DefaultRequestHeaders.Remove("Cookie");
                            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", currentCookies);
                        }

                        for (var i = 0; i < links.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var link = links[i];
                            var fileName = NormalizeSisbolBulletinFileName(link.FileName, bulletinTypeCode, year, currentMonth, i + 1);
                            var destination = Path.Combine(outputDirectory, fileName);
                            if (File.Exists(destination) && !replaceExisting)
                            {
                                result.SkippedFiles.Add(destination);
                                continue;
                            }

                            progress?.Report($"Baixando {fileName}...");
                            await DownloadSisbolFileAsync(http, link.Url, destination, cancellationToken);
                            result.DownloadedFiles.Add(destination);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{currentMonth:00}/{year}: {ex.Message}");
                        try { await _log.WriteAsync($"Falha ao baixar {typeName} {currentMonth:00}/{year}.", ex); } catch { }
                    }
                }

                HideBrowser(driver);
                return result;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }


    public async Task<SisbolPersonIndexDownloadResult> DownloadPersonIndexAsync(
        DateTime startDate,
        DateTime endDate,
        int bulletinTypeCode,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (bulletinTypeCode <= 0) throw new ArgumentOutOfRangeException(nameof(bulletinTypeCode), "Tipo de boletim inválido.");
        if (endDate.Date < startDate.Date) (startDate, endDate) = (endDate.Date, startDate.Date);
        if (startDate.Year < 2000 || endDate.Year > DateTime.Today.Year + 1)
            throw new ArgumentOutOfRangeException(nameof(startDate), "Informe um período válido para o índice do SisBol.");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Informe a pasta de destino do Índice por Pessoa.", nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);
        var result = new SisbolPersonIndexDownloadResult();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null || !_confirmed)
                throw new InvalidOperationException("Prepare o SisBol primeiro. Faça login/captcha uma vez em ‘Preparar SisBol’ e deixe a sessão pronta.");

            return await Task.Run(async () =>
            {
                var driver = _driver!;
                HideBrowser(driver);
                var sisbolBaseUrl = ResolveSisbolBaseUrl(driver);
                using var http = BuildAuthenticatedSisbolClient(driver, sisbolBaseUrl);
                var indexUrl = $"{sisbolBaseUrl.TrimEnd('/')}/band/gerarindice.php?codTipoBol={bulletinTypeCode}";
                var startText = startDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                var endText = endDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                progress?.Report("Abrindo tela de geração do Índice por Pessoa no SisBol...");
                var beforeHandles = driver.WindowHandles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                driver.Navigate().GoToUrl(indexUrl);
                WaitReady(driver, 20);
                WaitForDomIdle(driver, TimeSpan.FromSeconds(2), cancellationToken);
                if (LooksLikeLoginOrCaptcha(driver))
                {
                    _confirmed = false;
                    throw new InvalidOperationException("A sessão do SisBol expirou. Prepare/valide o SisBol novamente.");
                }

                progress?.Report($"Preenchendo período {startText} a {endText}...");
                object? scriptResult = null;
                try
                {
                    scriptResult = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    const tipo = arguments[0], inicio = arguments[1], fim = arguments[2];
                    function normalizeText(value) {
                        return String(value || '')
                            .normalize('NFD')
                            .replace(/[\u0300-\u036f]/g, '')
                            .toLowerCase()
                            .trim();
                    }
                    function setValue(names, value) {
                        for (const name of names) {
                            const el = document.querySelector(`[name='${name}'], #${name}`);
                            if (el) {
                                el.value = value;
                                el.setAttribute('value', value);
                                el.dispatchEvent(new Event('input', { bubbles: true }));
                                el.dispatchEvent(new Event('change', { bubbles: true }));
                                el.dispatchEvent(new Event('blur', { bubbles: true }));
                                return true;
                            }
                        }
                        return false;
                    }
                    const okTipo = setValue(['selTipoBol', 'tipoBol', 'codTipoBol'], String(tipo));
                    const okInicio = setValue(['dt_inicial', 'dtInicial', 'data_inicial'], inicio);
                    const okFim = setValue(['dt_final', 'dtFinal', 'data_final'], fim);
                    if (!okInicio || !okFim) {
                        return { ok: false, message: 'Campos de data do índice não encontrados.' };
                    }
                    const candidates = Array.from(document.querySelectorAll('input,button,a'));
                    const btn = candidates.find(x => {
                        const name = normalizeText(x.getAttribute('name'));
                        const value = normalizeText(x.getAttribute('value'));
                        const text = normalizeText(x.textContent);
                        const onclick = normalizeText(x.getAttribute('onclick'));
                        return name === 'pessoa'
                            || value.includes('indice por pessoa')
                            || text.includes('indice por pessoa')
                            || onclick.includes('pessoa');
                    });
                    if (btn) {
                        btn.click();
                        return { ok: true, message: 'Comando enviado pelo botão pessoa.', tipoOk: okTipo };
                    }
                    if (typeof window.gerarIndice === 'function') {
                        window.gerarIndice('pessoa');
                        return { ok: true, message: 'Comando enviado por gerarIndice(\'pessoa\').', tipoOk: okTipo };
                    }
                    const form = document.querySelector('form');
                    if (form) {
                        const hidden = document.createElement('input');
                        hidden.type = 'hidden';
                        hidden.name = 'pessoa';
                        hidden.value = 'Gerar Índice por Pessoa';
                        form.appendChild(hidden);
                        form.submit();
                        return { ok: true, message: 'Comando enviado por submit do formulário.', tipoOk: okTipo };
                    }
                    return { ok: false, message: 'Botão Gerar Índice por Pessoa não encontrado. A página não possui name=pessoa, onclick com pessoa nem função gerarIndice.' };
                    ", bulletinTypeCode, startText, endText);
                }
                catch (UnhandledAlertException)
                {
                    // O SisBol abre um confirm() nativo depois do clique em "Gerar Índice por Pessoa".
                    // Quando o alerta aparece muito rápido, o Selenium pode interromper o ExecuteScript.
                    // Aceitamos o alerta e seguimos aguardando o PDF.
                }
                var confirmText = WaitForAndAcceptAlert(driver, TimeSpan.FromSeconds(8));
                if (!string.IsNullOrWhiteSpace(confirmText))
                    progress?.Report("Confirmação do SisBol aceita. Gerando PDF do Índice por Pessoa...");

                if (scriptResult is IDictionary<string, object> scriptMap && scriptMap.TryGetValue("ok", out var okValue) && okValue is bool ok && !ok)
                {
                    var message = scriptMap.TryGetValue("message", out var m) ? m?.ToString() : null;
                    throw new InvalidOperationException(message ?? "Não consegui acionar o botão Gerar Índice por Pessoa.");
                }

                progress?.Report("Aguardando geração do PDF do Índice por Pessoa...");
                Thread.Sleep(1600);
                try
                {
                    var newHandle = driver.WindowHandles.FirstOrDefault(handle => !beforeHandles.Contains(handle));
                    if (!string.IsNullOrWhiteSpace(newHandle)) driver.SwitchTo().Window(newHandle);
                }
                catch { }
                var delayedConfirm = WaitForAndAcceptAlert(driver, TimeSpan.FromSeconds(3));
                if (!string.IsNullOrWhiteSpace(delayedConfirm))
                    progress?.Report("Confirmação adicional do SisBol aceita. Aguardando PDF...");
                try { WaitReady(driver, 25); }
                catch (UnhandledAlertException)
                {
                    WaitForAndAcceptAlert(driver, TimeSpan.FromSeconds(3));
                    WaitReady(driver, 25);
                }
                WaitForDomIdle(driver, TimeSpan.FromSeconds(2), cancellationToken);
                if (LooksLikeLoginOrCaptcha(driver))
                {
                    _confirmed = false;
                    throw new InvalidOperationException("A sessão do SisBol expirou ao gerar o índice. Prepare/valide o SisBol novamente.");
                }

                var fileName = $"{startDate:yyyy-MM-dd}_{endDate:yyyy-MM-dd}_indice_por_pessoa.pdf";
                var destination = Path.Combine(outputDirectory, fileName);
                var currentUrl = driver.Url ?? indexUrl;
                var links = ExtractSisbolDownloadLinks(driver, currentUrl);
                if (links.Count > 0)
                {
                    progress?.Report("Baixando PDF do Índice por Pessoa...");
                    await DownloadSisbolFileAsync(http, links[0].Url, destination, cancellationToken);
                    result.FilePath = destination;
                    HideBrowser(driver);
                    return result;
                }

                progress?.Report("Baixando PDF gerado pelo SisBol...");
                await DownloadSisbolFileAsync(http, currentUrl, destination, cancellationToken);
                result.FilePath = destination;
                HideBrowser(driver);
                return result;
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ShowCurrentBrowser()
    {
        if (_driver is not null && DriverAlive()) ShowBrowser(_driver);
    }

    public void HideCurrentBrowser()
    {
        if (_driver is not null && DriverAlive()) HideBrowser(_driver);
    }

    public string? LastDiagnosticDirectory => _diagnosticDirectory;
    public string? LastDiagnosticZipFile => _diagnosticZipFile;
    public string DiagnosticSubjectExample => DiagnosticSubjectText;
    public string DiagnosticBodyExample => DiagnosticBodyText;

    public async Task<string> StartSisbolDiagnosticAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null)
                throw new InvalidOperationException("Abra o SisBol primeiro. Use ‘Abrir SisBol’, faça login/captcha manualmente e então inicie o diagnóstico.");

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_paths.SisbolDiagnosticDirectory);
                _diagnosticSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                _diagnosticDirectory = Path.Combine(_paths.SisbolDiagnosticDirectory, "fluxo_" + _diagnosticSessionId);
                _diagnosticZipFile = null;
                Directory.CreateDirectory(_diagnosticDirectory);
                ShowBrowser(_driver);

                try
                {
                    _driver.Navigate().GoToUrl(MatterUrl);
                    WaitReady(_driver, 15);
                    WaitForDomIdle(_driver, TimeSpan.FromSeconds(2));
                }
                catch { }

                ResetDiagnosticStorage(_driver, _diagnosticSessionId);
                InjectDiagnosticRecorder(_driver, _diagnosticSessionId);
                RecordDiagnosticManualEvent(_driver, "inicio", "Diagnóstico iniciado pelo SIGFUR. Login/captcha e ações reais permanecem manuais.", string.Empty);
                SaveDiagnosticQuickFiles(_driver, _diagnosticDirectory, "inicio");
                return _diagnosticDirectory;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> PasteDiagnosticSubjectAsync(CancellationToken cancellationToken = default)
        => await PasteDiagnosticTextAsync("assunto_especifico", DiagnosticSubjectText, cancellationToken);

    public async Task<string> PasteDiagnosticBodyAsync(CancellationToken cancellationToken = default)
        => await PasteDiagnosticTextAsync("corpo_boletim", DiagnosticBodyText, cancellationToken);

    public async Task<string> PasteDiagnosticTextAsync(string label, string text, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null)
                throw new InvalidOperationException("O navegador do SisBol não está aberto.");
            if (string.IsNullOrWhiteSpace(_diagnosticSessionId))
                throw new InvalidOperationException("Inicie o diagnóstico antes de colar marcadores.");

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShowBrowser(_driver);
                InjectDiagnosticRecorder(_driver, _diagnosticSessionId);
                var result = InsertTextInActiveElement(_driver, label, text);
                RecordDiagnosticManualEvent(_driver, "colar_" + label, result, text);
                return result;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> AddDiagnosticCheckpointAsync(string description, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null)
                throw new InvalidOperationException("O navegador do SisBol não está aberto.");
            if (string.IsNullOrWhiteSpace(_diagnosticSessionId))
                throw new InvalidOperationException("Inicie o diagnóstico antes de marcar checkpoints.");

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                InjectDiagnosticRecorder(_driver, _diagnosticSessionId);
                RecordDiagnosticManualEvent(_driver, "checkpoint", description, string.Empty);
                return description;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> FinishSisbolDiagnosticAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DriverAlive() || _driver is null)
                throw new InvalidOperationException("O navegador do SisBol não está aberto.");
            if (string.IsNullOrWhiteSpace(_diagnosticSessionId) || string.IsNullOrWhiteSpace(_diagnosticDirectory))
                throw new InvalidOperationException("Nenhum diagnóstico foi iniciado nesta sessão.");

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                InjectDiagnosticRecorder(_driver, _diagnosticSessionId);
                RecordDiagnosticManualEvent(_driver, "fim", "Pacote de diagnóstico gerado pelo SIGFUR.", string.Empty);
                var folder = _diagnosticDirectory!;
                Directory.CreateDirectory(folder);
                SaveDiagnosticPackage(_driver, folder, _diagnosticSessionId);
                var zip = Path.Combine(Path.GetDirectoryName(folder) ?? folder, Path.GetFileName(folder) + ".zip");
                if (File.Exists(zip)) File.Delete(zip);
                ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
                _diagnosticZipFile = zip;
                return zip;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public Task SendAsync(
        string plainText,
        IReadOnlyList<MilitaryRecord> military,
        string templateName,
        CancellationToken cancellationToken = default)
        => SendAsync(plainText, military, templateName, true, SisbolTexts.ForSubject(templateName), cancellationToken);

    public Task SendAsync(
        string plainText,
        IReadOnlyList<MilitaryRecord> military,
        string templateName,
        bool includeConsequences,
        CancellationToken cancellationToken = default)
        => SendAsync(plainText, military, templateName, includeConsequences, SisbolTexts.ForSubject(templateName), cancellationToken);

    public Task SendAsync(
        string plainText,
        IReadOnlyList<MilitaryRecord> military,
        string templateName,
        bool includeConsequences,
        string? consequencesText,
        CancellationToken cancellationToken = default)
    {
        if (includeConsequences && string.IsNullOrWhiteSpace(consequencesText))
            throw new InvalidOperationException("Informe o texto de consequências ou desmarque a opção de fechamento.");

        var openingText = RemoveTrailingConsequences(plainText);
        var closingText = includeConsequences ? consequencesText!.Trim() : string.Empty;
        var payload = new SisbolMatterPayload
        {
            SpecificSubject = templateName ?? string.Empty,
            OpeningTextPlain = openingText,
            OpeningTextHtml = BuildHtml(openingText, military),
            ClosingTextPlain = closingText,
            ClosingTextHtml = includeConsequences ? SisbolTexts.ToHtml(closingText) : string.Empty,
            IncludeConsequences = includeConsequences
        };
        return SendAsync(payload, military, cancellationToken);
    }

    public async Task SendAsync(
        SisbolMatterPayload payload,
        IReadOnlyList<MilitaryRecord> military,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.OpeningTextPlain)) throw new InvalidOperationException("Não há texto de boletim para enviar.");
        if (string.IsNullOrWhiteSpace(payload.OpeningTextHtml))
            payload.OpeningTextHtml = BuildHtml(payload.OpeningTextPlain, military);
        if (!payload.IncludeConsequences)
        {
            payload.ClosingTextPlain = string.Empty;
            payload.ClosingTextHtml = string.Empty;
        }

        var settings = await LoadSettingsAsync();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            NormalizeCompact(payload.SpecificSubject) + "\n" +
            NormalizeCompact(payload.OpeningTextPlain) + "\n" +
            NormalizeCompact(payload.ClosingTextPlain))));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsReady || _driver is null)
                throw new InvalidOperationException("O SisBol não está preparado ou a sessão expirou. Clique em ‘Preparar SisBol’ na janela principal, conclua o captcha e valide a sessão.");
            if (fingerprint.Equals(_lastSubmissionFingerprint, StringComparison.Ordinal) &&
                DateTime.UtcNow - _lastSubmissionAtUtc < TimeSpan.FromMinutes(2))
                throw new InvalidOperationException("Esta mesma matéria já foi enviada ao SisBol nos últimos 2 minutos. A repetição foi bloqueada para evitar lançamento duplicado.");

            var result = await Task.Run(() => SendCore(_driver, payload, settings, cancellationToken), cancellationToken);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            _lastSubmissionFingerprint = fingerprint;
            _lastSubmissionAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (_driver is not null && DriverAlive())
            {
                SaveSisbolFailureDiagnostic(_driver, "falha_envio", ex.Message);
                LogSisbolStep(_driver, "Falha no envio", false, string.Empty, ex.Message, ex);
            }
            await _log.WriteAsync("Falha no lançamento automático do SisBol.", ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    private SendResult SendCore(
        IWebDriver driver,
        SisbolMatterPayload payload,
        SisbolSettings settings,
        CancellationToken cancellationToken)
    {
        var plainText = payload.OpeningTextPlain;
        var templateName = payload.SpecificSubject;
        cancellationToken.ThrowIfCancellationRequested();
        LogSisbolStep(driver, "Início do envio", true, "sessão autenticada", "Preparando nova matéria.");
        HideBrowser(driver);
        if (!OpenNewMatter(driver))
        {
            ShowBrowser(driver);
            return new(false, "Não consegui abrir uma matéria nova no SisBol. O navegador ficou visível para conferência.");
        }
        HideBrowser(driver);
        LogSisbolStep(driver, "Abrir nova matéria", true, "form[name='cadMateriaBI']", "Tela de cadastro localizada.");

        if (LooksLikeLoginOrCaptcha(driver))
        {
            _confirmed = false;
            _cachedAlive = true;
            _cachedReady = false;
            ShowBrowser(driver);
            return new(false, "A sessão do SisBol expirou e voltou para a tela de login. O navegador foi exibido para refazer o captcha.");
        }

        var fields = FillStandardFields(driver, templateName, plainText, cancellationToken);
        if (!fields.Success)
        {
            ShowBrowser(driver);
            return new(false, fields.Message + " O navegador ficou visível para conferência.");
        }
        LogSisbolStep(driver, "Preencher editores", true,
            "#texto_abert, #texto_abert___Frame, #texto_fech, #texto_fech___Frame",
            "Editores recriados e estáveis; iniciando preenchimento separado.");
        if (!FillEditorsWithRetry(driver, payload, TimeSpan.FromSeconds(22), cancellationToken))
        {
            ShowBrowser(driver);
            return new(false, "Falhou após selecionar o Assunto Específico: os editores de abertura/fechamento não confirmaram o conteúdo em 22s. O navegador ficou visível para conferência manual.");
        }

        var editor = WaitForEditorFilled(driver, plainText, TimeSpan.FromSeconds(18));
        if (!editor.Success)
        {
            ShowBrowser(driver);
            return new(false, editor.Message + " O navegador ficou visível e o SIGFUR não clicou em Incluir para evitar uma nota cortada.");
        }

        // Depois de validar o conteúdo inteiro, sincroniza novamente o campo ligado ao
        // FCKEditor e só então aciona INCLUIR. Em falha, nunca esconde a página do usuário.
        _ = FillEditors(driver, payload);
        WaitForDomIdle(driver, TimeSpan.FromSeconds(2));

        // Última trava antes de enviar: o editor FCK/alterações de seção podem
        // reexecutar scripts do SisBol e limpar o assunto. Aqui não mexemos de novo
        // em Parte/Seção para não disparar onchange e não tocar no texto já validado.
        if (!EnsurePaymentPersonalSubjectForSubmit(driver, templateName, plainText))
        {
            ShowBrowser(driver);
            return new(false, "O SIGFUR não clicou em Incluir porque o Assunto Geral 1077 e/ou o Assunto Específico não ficaram confirmados no SisBol.");
        }

        // Última sincronização: no FCKEditor antigo, ao submeter, o SisBol pode puxar
        // o conteúdo do iframe visual e sobrescrever o hidden texto_abert. Por isso,
        // reforçamos o corpo no hidden E no iframe logo antes de Incluir.
        _ = FillEditors(driver, payload);
        var finalEditor = WaitForEditorFilled(driver, plainText, TimeSpan.FromSeconds(6));
        if (!finalEditor.Success)
        {
            ShowBrowser(driver);
            return new(false, finalEditor.Message + " O SIGFUR não clicou em Incluir para evitar salvar sem corpo de texto.");
        }

        if (payload.IncludeConsequences)
        {
            var finalClosing = WaitForClosingEditorFilled(driver, payload.ClosingTextPlain, TimeSpan.FromSeconds(6));
            if (!finalClosing.Success)
            {
                ShowBrowser(driver);
                return new(false, finalClosing.Message + " O SIGFUR não clicou em Incluir para evitar salvar sem o texto de fechamento.");
            }
        }

        var preInclude = ValidateBeforeInclude(driver, templateName, plainText);
        if (!preInclude.Success)
        {
            ShowBrowser(driver);
            return new(false, preInclude.Message + " O SIGFUR não clicou em Incluir.");
        }
        LogSisbolStep(driver, "Validação pré-Incluir", true,
            "seleParteBi, seleSecaoParteBi, inputCodAssGeral, inputCodAssEspec, texto_abert",
            preInclude.Message);

        var include = ClickIncludeOnceAndConfirm(driver, cancellationToken);
        if (include.Success)
        {
            LogSisbolStep(driver, "Incluir matéria", true, "input[name='btnSalvar']", include.Message);
            HideBrowser(driver);
        }
        else
        {
            SaveSisbolFailureDiagnostic(driver, "incluir_materia", include.Message);
            LogSisbolStep(driver, "Incluir matéria", false, "input[name='btnSalvar']", include.Message);
            ShowBrowser(driver);
        }
        return include;
    }

    private void LogSisbolStep(
        IWebDriver driver,
        string stage,
        bool success,
        string selector,
        string detail,
        Exception? exception = null)
    {
        try
        {
            driver.SwitchTo().DefaultContent();
            var url = string.Empty;
            var title = string.Empty;
            var relevant = "{}";
            try { url = driver.Url ?? string.Empty; } catch { }
            try { title = driver.Title ?? string.Empty; } catch { }
            try
            {
                relevant = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    try{
                        function protectedField(e){const s=String((e&&e.type||'')+' '+(e&&e.id||'')+' '+(e&&e.name||'')+' '+(e&&e.autocomplete||'')).toLowerCase();return /password|senha|captcha|token|csrf|auth/.test(s)}
                        function item(e){if(!e)return null;const value=protectedField(e)?'[PROTEGIDO]':String(e.value||e.textContent||'').slice(0,420);return {tag:e.tagName||'',id:e.id||'',name:e.name||'',type:e.type||'',value:value,html:String(e.outerHTML||'').replace(/value=(['"])[\s\S]*?\1/i,'value="[REDUZIDO]"').slice(0,600)};}
                        const selectors=['[name="seleParteBi"]','[name="seleSecaoParteBi"]','[name="inputCodAssGeral"]','[name="inputAssuntoGeral"]','[name="inputCodAssEspec"]','[name="inputAssuntoEspecifico"]','#texto_abert','#texto_abert___Frame','[name="btnSalvar"]'];
                        return JSON.stringify({readyState:document.readyState,controls:selectors.map(s=>item(document.querySelector(s))).filter(Boolean)});
                    }catch(e){return JSON.stringify({error:String(e&&e.message||e)})}
                    """)?.ToString() ?? "{}";
            }
            catch { }

            var message = $"SISBOL | etapa='{stage}' | sucesso={success} | URL='{url}' | título='{title}' | seletor='{selector}' | detalhe='{detail}' | HTML reduzido={relevant}";
            Task.Run(() => _log.WriteAsync(message, exception)).GetAwaiter().GetResult();
        }
        catch { }
        finally { try { driver.SwitchTo().DefaultContent(); } catch { } }
    }

    private void SaveSisbolFailureDiagnostic(IWebDriver driver, string stage, string message)
    {
        try
        {
            driver.SwitchTo().DefaultContent();
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var folder = Path.Combine(_paths.SisbolDiagnosticDirectory, "falhas_automacao", stamp + "_" + SafeFileName(stage));
            Directory.CreateDirectory(folder);
            var loginOrCaptcha = LooksLikeLoginOrCaptcha(driver);

            var metadata = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.Now,
                ["stage"] = stage,
                ["message"] = message,
                ["url"] = SafeDriverValue(() => driver.Url),
                ["title"] = SafeDriverValue(() => driver.Title),
                ["loginOrCaptcha"] = loginOrCaptcha
            };
            File.WriteAllText(
                Path.Combine(folder, "falha.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            // Em tela de login/captcha não salva imagem nem HTML para não registrar segredo.
            if (loginOrCaptcha) return;

            if (driver is ITakesScreenshot shooter)
                shooter.GetScreenshot().SaveAsFile(Path.Combine(folder, "falha.png"));

            var framesFolder = Path.Combine(folder, "frames");
            Directory.CreateDirectory(framesFolder);
            foreach (var frame in CaptureDiagnosticFrames(driver))
            {
                var file = frame.Index.ToString("000", CultureInfo.InvariantCulture) + "_" + SafeFileName(frame.FramePath) + ".html";
                File.WriteAllText(Path.Combine(framesFolder, file), SanitizeDiagnosticHtml(frame.Html), Encoding.UTF8);
                File.WriteAllText(Path.Combine(framesFolder, Path.ChangeExtension(file, ".summary.json")), PrettyJson(frame.SummaryJson), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Task.Run(() => _log.WriteAsync("SISBOL | falha ao salvar diagnóstico automático.", ex)).GetAwaiter().GetResult();
        }
        finally { try { driver.SwitchTo().DefaultContent(); } catch { } }
    }

    private static string SafeDriverValue(Func<string> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SanitizeDiagnosticHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return Regex.Replace(html, @"<input\b[^>]*>", match =>
        {
            var tag = match.Value;
            if (!Regex.IsMatch(tag, @"password|senha|captcha|token|csrf|auth", RegexOptions.IgnoreCase))
                return tag;
            if (Regex.IsMatch(tag, @"\bvalue\s*=", RegexOptions.IgnoreCase))
                return Regex.Replace(tag, @"\bvalue\s*=\s*(""[^""]*""|'[^']*')", "value=\"[PROTEGIDO]\"", RegexOptions.IgnoreCase);
            return tag.Insert(tag.Length - 1, " value=\"[PROTEGIDO]\"");
        }, RegexOptions.IgnoreCase);
    }

    public async Task<SisbolSettings> LoadSettingsAsync()
    {
        try { return await _json.LoadAsync<SisbolSettings>(_paths.SisbolSettingsFile) ?? new SisbolSettings(); }
        catch { return new SisbolSettings(); }
    }

    public string ReadSavedPassword(SisbolSettings settings)
        => settings.SavePassword ? WindowsSecretProtector.Unprotect(settings.ProtectedPassword) : string.Empty;

    public async Task SaveSettingsAsync(SisbolSettings settings, string password)
    {
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.ProtectedPassword = settings.SavePassword && !string.IsNullOrEmpty(password)
            ? WindowsSecretProtector.Protect(password)
            : string.Empty;
        await _json.SaveAsync(_paths.SisbolSettingsFile, settings);
    }

    private DriverLaunch CreateDriver(string browser)
    {
        browser = NormalizeBrowser(browser);
        var browserProcessNames = BrowserProcessNames(browser);
        var driverProcessNames = DriverProcessNames(browser);
        var browserProcessesBefore = SnapshotProcessIds(browserProcessNames);
        var driverProcessesBefore = SnapshotProcessIds(driverProcessNames);
        Directory.CreateDirectory(_paths.SisbolBrowserProfileDirectory);
        var profile = Path.Combine(_paths.SisbolBrowserProfileDirectory, $"{browser}_{Environment.ProcessId}");
        Directory.CreateDirectory(profile);

        if (browser == "chrome")
        {
            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            service.SuppressInitialDiagnosticInformation = true;
            var options = new ChromeOptions { PageLoadStrategy = PageLoadStrategy.Eager, AcceptInsecureCertificates = true };
            AddChromiumArguments(options.AddArgument, profile);
            var driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(90));
            return CompleteDriverLaunch(driver, service, browserProcessNames, driverProcessNames, browserProcessesBefore, driverProcessesBefore);
        }

        if (browser == "firefox")
        {
            var service = FirefoxDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            service.SuppressInitialDiagnosticInformation = true;
            var options = new FirefoxOptions { PageLoadStrategy = PageLoadStrategy.Eager, AcceptInsecureCertificates = true };
            options.SetPreference("security.enterprise_roots.enabled", true);
            options.SetPreference("dom.webnotifications.enabled", false);
            options.Profile = new FirefoxProfile(profile);
            var driver = new FirefoxDriver(service, options, TimeSpan.FromSeconds(90));
            return CompleteDriverLaunch(driver, service, browserProcessNames, driverProcessNames, browserProcessesBefore, driverProcessesBefore);
        }

        {
            var service = EdgeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            service.SuppressInitialDiagnosticInformation = true;
            var options = new EdgeOptions { PageLoadStrategy = PageLoadStrategy.Eager, AcceptInsecureCertificates = true };
            AddChromiumArguments(options.AddArgument, profile);
            var driver = new EdgeDriver(service, options, TimeSpan.FromSeconds(90));
            return CompleteDriverLaunch(driver, service, browserProcessNames, driverProcessNames, browserProcessesBefore, driverProcessesBefore);
        }
    }

    private static DriverLaunch CompleteDriverLaunch(
        IWebDriver driver,
        object service,
        IReadOnlyCollection<string> browserProcessNames,
        IReadOnlyCollection<string> driverProcessNames,
        HashSet<int> browserProcessesBefore,
        HashSet<int> driverProcessesBefore)
    {
        var serviceProcessId = ReadServiceProcessId(service);
        var driverProcessIds = SnapshotProcessIds(driverProcessNames);
        driverProcessIds.ExceptWith(driverProcessesBefore);
        if (serviceProcessId > 0) driverProcessIds.Add(serviceProcessId);

        var browserProcessIds = SnapshotProcessIds(browserProcessNames);
        browserProcessIds.ExceptWith(browserProcessesBefore);
        if (serviceProcessId > 0)
        {
            foreach (var processId in DescendantProcessIds(serviceProcessId))
                if (ProcessNameMatches(processId, browserProcessNames)) browserProcessIds.Add(processId);
        }

        return new(driver, service, serviceProcessId, driverProcessIds.ToArray(), browserProcessIds.ToArray());
    }

    private static void AddChromiumArguments(Action<string> add, string profile)
    {
        add($"--user-data-dir={profile}");
        add("--ignore-certificate-errors");
        add("--allow-running-insecure-content");
        add("--disable-notifications");
        add("--disable-popup-blocking");
        add("--no-first-run");
        add("--no-default-browser-check");
        add("--start-maximized");
    }

    private static int ReadServiceProcessId(object service)
    {
        try
        {
            var property = service.GetType().GetProperty(
                "ProcessId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var value = property?.GetValue(service);
            if (value is not null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);

            for (var type = service.GetType(); type is not null; type = type.BaseType)
            {
                var field = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .FirstOrDefault(x => typeof(Process).IsAssignableFrom(x.FieldType));
                if (field?.GetValue(service) is Process process) return process.Id;
            }
        }
        catch { }
        return 0;
    }

    private static string[] BrowserProcessNames(string browser) => NormalizeBrowser(browser) switch
    {
        "chrome" => ["chrome"],
        "firefox" => ["firefox"],
        _ => ["msedge"]
    };

    private static string[] DriverProcessNames(string browser) => NormalizeBrowser(browser) switch
    {
        "chrome" => ["chromedriver"],
        "firefox" => ["geckodriver"],
        _ => ["msedgedriver"]
    };

    private static HashSet<int> SnapshotProcessIds(IEnumerable<string> processNames)
    {
        var result = new HashSet<int>();
        foreach (var processName in processNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try { result.Add(process.Id); } catch { }
                }
            }
        }
        return result;
    }

    private static bool ProcessNameMatches(int processId, IEnumerable<string> processNames)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return processNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryFillLogin(IWebDriver driver, string login, string password)
    {
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript(@"
                function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();}
                const login=arguments[0]||'', senha=arguments[1]||'';
                const els=Array.from(document.querySelectorAll('input'));
                const meta=e=>norm((e.name||'')+' '+(e.id||'')+' '+(e.placeholder||'')+' '+(e.title||'')+' '+(e.autocomplete||''));
                const visible=e=>{try{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}catch(_){return false}};
                const p=els.find(e=>visible(e)&&(String(e.type||'').toLowerCase()==='password'||/senha|password/.test(meta(e))));
                const candidates=els.filter(e=>visible(e)&&['','text','email','number'].includes(String(e.type||'').toLowerCase())&&!/captcha|caracter|seguranca|codigo/.test(meta(e)));
                const u=candidates.sort((a,b)=>{const score=e=>(/login|usuario|user|cpf|identidade/.test(meta(e))?20:0)+(p&&e.getBoundingClientRect().top<=p.getBoundingClientRect().top?5:0);return score(b)-score(a)})[0];
                function set(e,v){if(!e||!v)return; try{e.focus()}catch(_){}; e.value=''; e.value=v; ['input','change','keyup','blur'].forEach(n=>{try{e.dispatchEvent(new Event(n,{bubbles:true}))}catch(_){}});}
                set(u,login); set(p,senha); return !!u || !!p;", login ?? string.Empty, password ?? string.Empty);
        }
        catch { }
    }

    private FieldFillResult FillStandardFields(IWebDriver driver, string templateName, string plainText, CancellationToken cancellationToken)
    {
        var specific = SpecificSubject(templateName, plainText);
        var code = SpecificSubjectCode(templateName, plainText);
        var searchTerm = SpecificSubjectSearchTerm(templateName, plainText, specific);

        // A página real do SisBol é antiga e o onchange da Parte pode reconstruir
        // a combo da Seção alguns instantes depois. Se selecionar tudo de uma vez,
        // ela volta para ALTERAÇÕES DE OFICIAIS. Então fazemos em ciclos estáveis:
        // 1) 3ª Parte; 2) aguarda o AJAX antigo; 3) OUTROS ASSUNTOS; 4) confirma.
        if (!ForcePartAndSectionStable(driver, TimeSpan.FromSeconds(10)))
            return new(false, "Não consegui fixar a estrutura 3ª Parte / OUTROS ASSUNTOS no SisBol.");

        LogSisbolStep(driver, "Selecionar Parte e Seção", true,
            "select[name='seleParteBi'], select[name='seleSecaoParteBi']",
            "3ª Parte / OUTROS ASSUNTOS confirmados.");

        var generalFilled = ForcePaymentPersonalSubjectStable(driver, clearSpecific: string.IsNullOrWhiteSpace(specific), TimeSpan.FromSeconds(8));
        WaitForSisbolPageStable(driver, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(600), requireEditor: false, cancellationToken);
        if (generalFilled)
            LogSisbolStep(driver, "Selecionar Assunto Geral", true, "input[name='inputCodAssGeral']", "1077 - PAGAMENTO PESSOAL confirmado.");

        var specificFilled = false;
        if (!string.IsNullOrWhiteSpace(specific) || !string.IsNullOrWhiteSpace(searchTerm))
        {
            // Fluxo real capturado no SisBol: abrir Busca Assunto Específico,
            // pesquisar o termo, selecionar a linha e deixar o AJAX do próprio
            // SisBol carregar o modelo. Só depois o SIGFUR escreve o corpo.
            specificFilled = TrySearchAndSelectSpecificSubject(driver, specific, code, searchTerm, TimeSpan.FromSeconds(18));
            // O SisBol recria a página/modelo depois de escolher o Assunto Específico.
            // Só libera o preenchimento do corpo quando o campo específico e o iframe
            // texto_abert já voltaram a existir.
            if (specificFilled)
            {
                specificFilled = WaitForSisbolPageStable(
                                     driver,
                                     TimeSpan.FromSeconds(15),
                                     TimeSpan.FromMilliseconds(900),
                                     requireEditor: true,
                                     cancellationToken)
                                 && VerifySubjectFields(driver, specific, code).Specific;
            }
            if (specificFilled)
                LogSisbolStep(driver, "Selecionar Assunto Específico", true,
                    "#textBusca / input[name='inputCodAssEspec']",
                    $"Pesquisa '{searchTerm}', assunto '{specific}', código '{code}'. Editor recriado e estável.");
        }

        var state = VerifyStandardSisbolFields(driver);
        if (!state.Part)
            return new(false, "Não consegui selecionar a 3ª Parte no SisBol.");
        if (!state.Section)
            return new(false, "Não consegui selecionar a seção OUTROS ASSUNTOS no SisBol.");
        if (!state.General || !generalFilled)
            return new(false, "Não consegui preencher o Assunto Geral 1077 - PAGAMENTO PESSOAL no SisBol.");
        if (!specificFilled)
            return new(false, $"Não consegui preencher o Assunto Específico no SisBol. Pesquisa usada: {searchTerm}. Valor esperado: {specific}" );

        return new(true, "Campos do SisBol preenchidos.");
    }

    private static bool ForcePaymentPersonalSubjectStable(IWebDriver driver, bool clearSpecific, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        do
        {
            _ = ForcePartAndSectionStable(driver, TimeSpan.FromSeconds(2));
            var filled = ForcePaymentPersonalSubject(driver, clearSpecific);
            Thread.Sleep(250);
            _ = ForcePartAndSectionPass(driver, triggerPartChange: false, triggerSectionChange: false);
            var state = VerifyStandardSisbolFields(driver);
            if (filled && state.Part && state.Section && state.General)
                return true;
            Thread.Sleep(350);
        }
        while (DateTime.UtcNow < deadline);

        var finalState = VerifyStandardSisbolFields(driver);
        return finalState.Part && finalState.Section && finalState.General;
    }

    private static bool ForcePaymentPersonalSubject(IWebDriver driver, bool clearSpecific)
    {
        var result = ExecuteInFrames(driver, @"
            try {
                const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x && (x.seleParteBi||x.seleSecaoParteBi||x.inputCodAssGeral));
                if(!f)return {ok:false,diag:'form'};
                function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                function fire(el){if(!el)return; ['input','change','keyup','blur'].forEach(n=>{try{el.dispatchEvent(new Event(n,{bubbles:true}))}catch(e){}})}
                function byNames(names){for(const name of names){const el=(f.elements&&f.elements[name])||f[name]||document.querySelector('[name=\''+name+'\'],#'+name); if(el)return el;} return null;}
                function set(el,v){if(!el)return false;try{el.value=String(v);try{el.setAttribute('value',String(v))}catch(e){};fire(el);return String(el.value||'').trim()===String(v).trim()||String(el.getAttribute('value')||'').trim()===String(v).trim()}catch(e){return false}}
                function setSelect(sel,wantedValue,wantedText){
                    if(!sel)return false;let opt=null;const want=norm(wantedText);
                    for(const o of Array.from(sel.options||[])){const val=String(o.value||'').trim();const txt=norm((o.text||'')+' '+val);if(txt.includes(want)||(wantedValue&&val===String(wantedValue))){opt=o;if(txt.includes(want))break;}}
                    if(!opt)return false;try{sel.value=opt.value;opt.selected=true;fire(sel);return true}catch(e){return false}
                }
                try{ if(f.codTipoBol) set(f.codTipoBol,'3'); }catch(e){}
                // Parte/Seção são fixadas pelo ciclo estável fora daqui. Aqui só
                // limpamos flags de Alterações e preenchemos o assunto geral.
                for(const name of ['vai_altr','texto_fech_vai_altr']){const cb=byNames([name]); if(cb){try{cb.checked=false; cb.removeAttribute('checked'); fire(cb)}catch(e){}}}

                // Não chama selecionaAssuntoGeral quando a janela de busca está aberta:
                // no SisBol essa mesma função também aparece na busca do assunto específico.
                try{ if(typeof window.setaAssuntoGeral==='function') window.setaAssuntoGeral('1077','PAGAMENTO PESSOAL'); }catch(e){}
                const codeOk=set(byNames(['inputCodAssGeral','codAssuntoGeral','codAssGeral','assuntoGeralCodigo','idAssuntoGeral','idtAssuntoGeral']),'1077');
                const textOk=set(byNames(['inputAssuntoGeral','assuntoGeral','descAssuntoGeral','descricaoAssuntoGeral']),'PAGAMENTO PESSOAL');
                try{ const div=document.getElementById('divAssuntoGeral'); if(div) div.innerHTML='PAGAMENTO PESSOAL'; }catch(e){}
                if(arguments[0]){
                    set(byNames(['inputCodAssEspec','codAssuntoEspecifico','codAssEspec','assuntoEspecificoCodigo']),'');
                    set(byNames(['inputAssuntoEspecifico','assuntoEspecifico','descAssuntoEspecifico','descricaoAssuntoEspecifico']),'');
                    try{ const d=document.getElementById('divAssuntoEspecifico'); if(d) d.innerHTML=''; }catch(e){}
                }
                try{ if(typeof window.escondeFly==='function') window.escondeFly(); }catch(e){}
                try{ const fly=document.getElementById('flyframe'); if(fly) fly.style.visibility='hidden'; }catch(e){}
                return {ok:(codeOk||textOk),code:String((byNames(['inputCodAssGeral'])||{}).value||''),text:String((byNames(['inputAssuntoGeral'])||{}).value||'')};
            } catch(e) { return {ok:false,error:String(e&&e.message||e)}; }
            ", clearSpecific);
        if (result is IDictionary<string, object> map && map.TryGetValue("ok", out var ok))
        {
            try { return Convert.ToBoolean(ok, CultureInfo.InvariantCulture); }
            catch { return false; }
        }
        return VerifySubjectFields(driver, string.Empty, string.Empty).General;
    }

    private static bool TrySearchAndSelectSpecificSubject(IWebDriver driver, string specific, string code, string searchTerm, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) && string.IsNullOrWhiteSpace(specific)) return true;
        var query = string.IsNullOrWhiteSpace(searchTerm) ? specific : searchTerm;
        var deadline = DateTime.UtcNow.Add(timeout);
        var transitionToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        // Abre a busca oficial do SisBol e pesquisa exatamente como foi capturado no diagnóstico:
        // buscaAssuntoEspecifico('') -> #textBusca -> listaAssuntoEspecificoLike(query) -> clicar linha.
        _ = ExecuteInFrames(driver, @"
            try {
                const query=arguments[0]||'';
                const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x && (x.inputCodAssGeral||x.inputCodAssEspec));
                if(f){
                    if(f.inputCodAssGeral){f.inputCodAssGeral.value='1077'; try{f.inputCodAssGeral.setAttribute('value','1077')}catch(e){}}
                    if(f.inputAssuntoGeral){f.inputAssuntoGeral.value='PAGAMENTO PESSOAL'; try{f.inputAssuntoGeral.setAttribute('value','PAGAMENTO PESSOAL')}catch(e){}}
                }
                try{ if(typeof window.buscaAssuntoEspecifico==='function') window.buscaAssuntoEspecifico(''); }catch(e){}
                const box=document.querySelector('#textBusca,input[name=textBusca]');
                if(box){
                    try{box.focus()}catch(e){}
                    box.value=query;
                    try{box.dispatchEvent(new Event('input',{bubbles:true}))}catch(e){}
                    try{box.dispatchEvent(new Event('change',{bubbles:true}))}catch(e){}
                }
                if(typeof window.listaAssuntoEspecificoLike==='function'){
                    window.listaAssuntoEspecificoLike(query);
                    return {ok:true,mode:'listaAssuntoEspecificoLike'};
                }
                const btn=Array.from(document.querySelectorAll('input[type=button],button')).find(e=>String(e.value||e.innerText||'').toLowerCase().includes('buscar') && String(e.getAttribute('onclick')||'').includes('listaAssuntoEspecificoLike'));
                if(btn){try{btn.click(); return {ok:true,mode:'buscar.click'}}catch(e){}}
                return {ok:false,diag:'busca específica não encontrada'};
            } catch(e) { return {ok:false,diag:String(e&&e.message||e)}; }
            ", query);

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(450);
            WaitForDomIdle(driver, TimeSpan.FromMilliseconds(400));
            var selected = ExecuteInFrames(driver, @"
                try {
                    const wantedName=arguments[0]||'', wantedCode=arguments[1]||'', query=arguments[2]||'', transitionToken=arguments[3]||'';
                    function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    function clean(s){return String(s||'').replace(/\s+/g,' ').trim()}
                    const rows=Array.from(document.querySelectorAll('tr')).map(tr=>{
                        const cells=Array.from(tr.querySelectorAll('td,th')).map(td=>clean(td.innerText||td.textContent||''));
                        const txt=clean((tr.innerText||tr.textContent||'')+' '+(tr.getAttribute('onclick')||''));
                        const onclick=String(tr.getAttribute('onclick')||'');
                        const code=(cells[0]||'').replace(/\D+/g,'');
                        const desc=cells.length>1?cells.slice(1).join(' '):txt;
                        return {tr,cells,txt,onclick,code,desc};
                    }).filter(x=>x.code && x.desc && !/cod assunto especifico/i.test(x.txt));
                    if(rows.length===0)return {ok:false,diag:'sem linhas'};
                const words=s=>norm(s).replace(/[^a-z0-9]+/g,' ').split(' ').filter(w=>w.length>2&&!['com','das','dos','para','por','uma'].includes(w));
                const want=norm(wantedName).replace(/[^a-z0-9]+/g,' ').trim(), q=norm(query).replace(/[^a-z0-9]+/g,' ').trim(), c=String(wantedCode||'').trim();
                const wantedWords=words(want||q);
                function score(x){
                    const hay=norm(x.desc).replace(/[^a-z0-9]+/g,' ').trim();
                    if(c)return x.code===c?2000:0;
                    if(!want||wantedWords.length===0)return 0;
                    if(hay===want)return 1500;
                    if(hay.includes(want)||want.includes(hay))return 1200;
                    const matched=wantedWords.filter(w=>words(hay).some(h=>h===w||h.startsWith(w)||w.startsWith(h))).length;
                    return matched===wantedWords.length?900+matched:0;
                }
                    const ranked=rows.map(x=>({row:x,score:score(x)})).filter(x=>x.score>0).sort((a,b)=>b.score-a.score);
                    const chosen=ranked.length?ranked[0].row:null;
                    if(!chosen)return {ok:false,diag:'nenhuma linha compatível com código/texto pesquisado'};
                    const cod=chosen.code;
                    const desc=chosen.desc;
                    try{
                        const host=(window.parent&&window.parent.document)?window.parent.document:document;
                        host.documentElement.setAttribute('data-sigfur-specific-transition',transitionToken);
                        const oldFrame=host.getElementById('texto_abert___Frame');
                        if(oldFrame){
                            oldFrame.setAttribute('data-sigfur-specific-transition',transitionToken);
                            try{
                                const outerDoc=oldFrame.contentDocument||(oldFrame.contentWindow&&oldFrame.contentWindow.document);
                                for(const inner of Array.from((outerDoc&&outerDoc.querySelectorAll('iframe,frame'))||[])){
                                    try{const innerDoc=inner.contentDocument||(inner.contentWindow&&inner.contentWindow.document);if(innerDoc&&innerDoc.body)innerDoc.body.setAttribute('data-sigfur-specific-transition',transitionToken)}catch(e){}
                                }
                            }catch(e){}
                        }
                    }catch(e){}
                    try{ if(window.parent && typeof window.parent.setaAssuntoEspecifico==='function'){ window.parent.setaAssuntoEspecifico(cod, desc); return {ok:true,code:cod,desc:desc,mode:'parent.setaAssuntoEspecifico'}; } }catch(e){}
                    try{ if(typeof window.setaAssuntoEspecifico==='function'){ window.setaAssuntoEspecifico(cod, desc); return {ok:true,code:cod,desc:desc,mode:'setaAssuntoEspecifico'}; } }catch(e){}
                    try{ chosen.tr.scrollIntoView({block:'center'}); chosen.tr.click(); return {ok:true,code:cod,desc:desc,mode:'row.click'}; }catch(e){}
                    return {ok:false,diag:'não consegui acionar linha'};
                } catch(e) { return {ok:false,diag:String(e&&e.message||e)}; }
                ", specific, code, query, transitionToken);

            if (selected is IDictionary<string, object> map && map.TryGetValue("ok", out var ok) && Convert.ToBoolean(ok, CultureInfo.InvariantCulture))
            {
                // A rotina oficial setaAssuntoEspecifico faz um AJAX que troca/recarrega
                // a matéria e recria os iframes do FCKEditor. Se preencher o corpo antes
                // desse ponto, o SisBol apaga o texto. Aguarda a tela confirmar o específico
                // e o editor de abertura existir de novo.
                var selectedCode = map.TryGetValue("code", out var selectedCodeValue) ? selectedCodeValue?.ToString() ?? code : code;
                var selectedDescription = map.TryGetValue("desc", out var selectedDescriptionValue) ? selectedDescriptionValue?.ToString() ?? specific : specific;
                var committed = WaitForSpecificSubjectCommitted(driver, selectedDescription, selectedCode, transitionToken, TimeSpan.FromSeconds(12));
                WaitForDomIdle(driver, TimeSpan.FromSeconds(1));
                CloseSisbolSearchOverlay(driver);
                var verify = VerifySubjectFields(driver, selectedDescription, selectedCode);
                if (committed && verify.General && verify.Specific) return true;
                // Se a linha oficial foi clicada, mas o texto esperado mapeado era muito rígido,
                // aceita o campo específico preenchido com qualquer valor não vazio.
            }
        }

        return false;
    }

    private static bool WaitForSpecificSubjectCommitted(IWebDriver driver, string specific, string code, string transitionToken, TimeSpan timeout)
    {
        var end = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < end)
        {
            WaitForDomIdle(driver, TimeSpan.FromMilliseconds(350));
            CloseSisbolSearchOverlay(driver);

            var result = ExecuteInFrames(driver, @"
                try{
                    function n(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    const wantedName=n(arguments[0]||''), wantedCode=String(arguments[1]||'').trim(), transitionToken=String(arguments[2]||'');
                    const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x&&(x.inputCodAssEspec||x.inputAssuntoEspecifico||x.texto_abert));
                    if(!f)return {ok:false,reason:'sem form cadMateriaBI'};
                    const code=String((f.inputCodAssEspec&&f.inputCodAssEspec.value)||'').trim();
                    const text=String((f.inputAssuntoEspecifico&&f.inputAssuntoEspecifico.value)||'').trim();
                    const canonical=s=>n(s).replace(/[^a-z0-9]+/g,' ').trim();
                    const textValue=canonical(text), wantedValue=canonical(wantedName);
                    const textOk=!!text && (!wantedValue || textValue.includes(wantedValue) || wantedValue.includes(textValue));
                    const codeOk=!!code && (!wantedCode || code===wantedCode);
                    const opening=document.getElementById('texto_abert___Frame');
                    const openingFrame=!!opening;
                    let transitioned=document.documentElement.getAttribute('data-sigfur-specific-transition')!==transitionToken;
                    if(opening){
                        transitioned=transitioned||opening.getAttribute('data-sigfur-specific-transition')!==transitionToken;
                        try{
                            const outerDoc=opening.contentDocument||(opening.contentWindow&&opening.contentWindow.document);
                            const inner=outerDoc&&outerDoc.querySelector('iframe,frame');
                            const innerDoc=inner&&(inner.contentDocument||(inner.contentWindow&&inner.contentWindow.document));
                            transitioned=transitioned||(innerDoc&&innerDoc.body&&innerDoc.body.getAttribute('data-sigfur-specific-transition')!==transitionToken);
                        }catch(e){}
                    }
                    return {ok:(wantedCode?codeOk:true)&&textOk&&openingFrame&&transitioned,code:code,text:text,frame:openingFrame,transitioned:transitioned};
                }catch(e){return {ok:false,reason:String(e&&e.message||e)}}
                ", specific, code, transitionToken);

            if (result is IDictionary<string, object> map && map.TryGetValue("ok", out var ok))
            {
                try
                {
                    if (Convert.ToBoolean(ok, CultureInfo.InvariantCulture)) return true;
                }
                catch { }
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool TryFillSpecificSubject(IWebDriver driver, string specific, string code)
    {
        if (string.IsNullOrWhiteSpace(specific)) return true;

        bool TryPass()
        {
            var result = ExecuteInFrames(driver, @"
            try {
                const wantedName=arguments[0]||'', wantedCode=arguments[1]||'';
                function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                function fire(el){if(!el)return; ['input','change','keyup','blur'].forEach(n=>{try{el.dispatchEvent(new Event(n,{bubbles:true,cancelable:true}))}catch(e){}})}
                function first(el){ if(!el) return null; if(el.tagName) return el; if(typeof el.length==='number') return Array.from(el).find(x=>x&&x.tagName)||null; return null; }
                const forms=Array.from(document.forms||[]);
                const f=forms.find(x=>x && (x.inputCodAssEspec||x.inputAssuntoEspec||x.inputAssuntoEspecifico||x.inputCodAssGeral||x.seleParteBi))
                    || forms.find(x=>/cadmateriabi|materia|boletim/.test(norm((x.name||'')+' '+(x.id||'')+' '+(x.action||''))))
                    || forms[0] || null;
                function byNames(names){for(const name of names){const el=first((f&&f.elements&&f.elements[name])||(f&&f[name])||document.querySelector('[name=\''+name+'\'],#'+name)); if(el)return el;} return null;}
                function set(el,v,allowBlank){el=first(el); if(!el||(!allowBlank&&(v===null||v===undefined||v==='')))return false;try{el.value=String(v||'');try{el.setAttribute('value',String(v||''))}catch(e){};fire(el);return true}catch(e){return false}}
                function valueOf(names){const el=byNames(names); return el?String(el.value||el.getAttribute('value')||'').trim():'';}
                function setNearestSpecificText(v){
                    let done=false;
                    const inputs=Array.from(document.querySelectorAll('input:not([type]),input[type=text],input[type=search],textarea'));
                    for(const el of inputs){
                        const meta=norm((el.name||'')+' '+(el.id||'')+' '+(el.placeholder||'')+' '+(el.title||''));
                        const parent=norm((el.closest('tr,td,div,table')||document.body).innerText||'');
                        if(/assunto.*espec|especif/.test(meta+' '+parent)) done=set(el,v,false)||done;
                    }
                    return done;
                }
                function closeSearch(){
                    try{ if(typeof window.escondeFly==='function') window.escondeFly(); }catch(e){}
                    try{ if(typeof window.fechaFly==='function') window.fechaFly(); }catch(e){}
                    try{ if(typeof window.FecharFly==='function') window.FecharFly(); }catch(e){}
                    try{ const fly=document.getElementById('flyframe'); if(fly){fly.style.visibility='hidden'; fly.style.display='none';} }catch(e){}
                    try{
                        for(const el of Array.from(document.querySelectorAll('div,iframe,table'))){
                            const id=norm((el.id||'')+' '+(el.className||''));
                            const text=norm(el.innerText||el.title||'');
                            if(text.includes('busca assunto especific') && /fly|popup|modal|busca|janela|assunto/.test(id)){
                                el.style.visibility='hidden'; el.style.display='none';
                            }
                        }
                    }catch(e){}
                }
                function callSetters(){
                    let ok=false;
                    const names=['setaAssuntoEspecifico','setaAssuntoEspec','selecionaAssuntoEspecifico','selecionaAssuntoEspec','setAssuntoEspecifico','setAssuntoEspec'];
                    for(const name of names){
                        try{ if(typeof window[name]==='function'){ window[name](wantedCode,wantedName); ok=true; } }catch(e){}
                        try{ if(typeof window[name]==='function'){ window[name](wantedName,wantedCode); ok=true; } }catch(e){}
                    }
                    return ok;
                }
                function clickMatchingRow(){
                    const want=norm(wantedName), code=String(wantedCode||'').trim();
                    let nodes=Array.from(document.querySelectorAll('tr,td,a,span,div,input[type=button],button'));
                    nodes=nodes.filter(e=>{
                        const txt=norm((e.innerText||e.textContent||e.value||'')+' '+(e.title||'')+' '+(e.getAttribute&&e.getAttribute('onclick')||''));
                        if(!txt) return false;
                        return (code && txt.includes(code)) || (want && txt.includes(want));
                    });
                    let node=nodes.find(e=>e.tagName==='TR') || nodes.map(e=>e.closest&&e.closest('tr')).find(Boolean) || nodes[0];
                    if(!node) return false;
                    try{node.scrollIntoView({block:'center',inline:'center'})}catch(e){}
                    for(const target of [node.querySelector&&node.querySelector('a,button,input'), node]){
                        if(!target) continue;
                        try{target.click(); return true}catch(e){}
                        try{target.dispatchEvent(new MouseEvent('dblclick',{view:window,bubbles:true,cancelable:true})); return true}catch(e){}
                        try{target.dispatchEvent(new MouseEvent('click',{view:window,bubbles:true,cancelable:true})); return true}catch(e){}
                    }
                    return false;
                }

                // Ordem correta: primeiro tenta acionar a rotina oficial/linha da busca; depois regrava os campos.
                const clicked=clickMatchingRow();
                const called=callSetters();
                set(byNames(['inputCodAssGeral','codAssuntoGeral','codAssGeral','assuntoGeralCodigo','idAssuntoGeral','idtAssuntoGeral']),'1077',false);
                set(byNames(['inputAssuntoGeral','assuntoGeral','descAssuntoGeral','descricaoAssuntoGeral']),'PAGAMENTO PESSOAL',false);
                try{ const d=document.getElementById('divAssuntoGeral'); if(d) d.innerHTML='PAGAMENTO PESSOAL'; }catch(e){}

                let ok=false;
                if(wantedCode) ok=set(byNames(['inputCodAssEspec','inputCodAssEspecifico','codAssuntoEspecifico','codAssuntoEspec','codAssEspec','assuntoEspecificoCodigo','assuntoEspecCodigo','idAssuntoEspecifico','idtAssuntoEspecifico']),wantedCode,false)||ok;
                ok=set(byNames(['inputAssuntoEspec','inputAssuntoEspecifico','assuntoEspec','assuntoEspecifico','descAssuntoEspec','descAssuntoEspecifico','descricaoAssuntoEspec','descricaoAssuntoEspecifico','txtAssuntoEspec','txtAssuntoEspecifico']),wantedName,false)||ok;
                ok=setNearestSpecificText(wantedName)||ok;
                try{ const d=document.getElementById('divAssuntoEspecifico')||document.getElementById('divAssuntoEspec'); if(d && wantedName) d.innerHTML=wantedName; }catch(e){}
                closeSearch();
                const gotCode=valueOf(['inputCodAssEspec','inputCodAssEspecifico','codAssuntoEspecifico','codAssuntoEspec','codAssEspec','assuntoEspecificoCodigo','assuntoEspecCodigo']);
                const gotText=norm(valueOf(['inputAssuntoEspec','inputAssuntoEspecifico','assuntoEspec','assuntoEspecifico','descAssuntoEspec','descAssuntoEspecifico','descricaoAssuntoEspec','descricaoAssuntoEspecifico','txtAssuntoEspec','txtAssuntoEspecifico']));
                const want=norm(wantedName);
                const verified=((!wantedCode || gotCode===String(wantedCode)) && (!want || gotText.includes(want))) || clicked || called || ok;
                return {ok:verified,code:gotCode,text:gotText,clicked:clicked,called:called};
            } catch(e) { return {ok:false,diag:String(e&&e.message||e)}; }
            ", specific, code);
            if (result is IDictionary<string, object> map && map.TryGetValue("ok", out var ok))
            {
                try { return Convert.ToBoolean(ok, CultureInfo.InvariantCulture); }
                catch { return false; }
            }
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            TryPass();
            WaitForDomIdle(driver, TimeSpan.FromMilliseconds(450));
            var verify = VerifySubjectFields(driver, specific, code);
            if (verify.General && verify.Specific)
            {
                CloseSisbolSearchOverlay(driver);
                return true;
            }
            Thread.Sleep(250);
        }
        return VerifySubjectFields(driver, specific, code).Specific;
    }

    private static void CloseSisbolSearchOverlay(IWebDriver driver)
    {
        _ = ExecuteInFrames(driver, @"
            try {
                function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                try{ if(typeof window.escondeFly==='function') window.escondeFly(); }catch(e){}
                try{ if(typeof window.fechaFly==='function') window.fechaFly(); }catch(e){}
                try{ if(typeof window.FecharFly==='function') window.FecharFly(); }catch(e){}
                try{ const fly=document.getElementById('flyframe'); if(fly){fly.style.visibility='hidden'; fly.style.display='none';} }catch(e){}
                let closed=false;
                for(const el of Array.from(document.querySelectorAll('div,iframe,table'))){
                    const id=norm((el.id||'')+' '+(el.className||'')+' '+(el.name||''));
                    const text=norm((el.innerText||el.title||'').slice(0,500));
                    if(text.includes('busca assunto especific') && /fly|popup|modal|busca|janela|assunto|frame/.test(id)){
                        try{el.style.visibility='hidden'; el.style.display='none'; closed=true;}catch(e){}
                    }
                }
                return {ok:true,closed:closed};
            } catch(e) { return {ok:false}; }
            ");
    }

    private static (bool Part, bool Section, bool General) VerifyStandardSisbolFields(IWebDriver driver)
    {
        var part = false;
        var section = false;
        var general = false;

        bool Walk(int depth)
        {
            try
            {
                var result = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/[ª]/g,'a').replace(/[º]/g,'o').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x && (x.seleParteBi||x.seleSecaoParteBi||x.inputCodAssGeral));
                    if(!f)return {part:false,section:false,general:false};
                    function selText(sel){try{return norm((sel.options&&sel.selectedIndex>=0?sel.options[sel.selectedIndex].text:'')+' '+(sel.value||''))}catch(e){return ''}}
                    function val(name){const e=(f.elements&&f.elements[name])||f[name]||document.querySelector('[name=\''+name+'\'],#'+name);return e?String(e.value||'').trim():''}
                    const p=selText(f.seleParteBi||document.querySelector('[name=seleParteBi]'));
                    const s=selText(f.seleSecaoParteBi||document.querySelector('[name=seleSecaoParteBi]'));
                    const gc=val('inputCodAssGeral');
                    const gt=norm(val('inputAssuntoGeral'));
                    const pv=String((f.seleParteBi||document.querySelector('[name=seleParteBi]')||{}).value||'').trim();
                    const sv=String((f.seleSecaoParteBi||document.querySelector('[name=seleSecaoParteBi]')||{}).value||'').trim();
                    return {part:(pv==='3')||(p.includes('3')&&p.includes('parte')),section:(sv==='3')||s.includes('outros assuntos'),general:(gc==='1077')||(gt.includes('pagamento pessoal')&&String(gc||'').trim().length>0)};
                    ") as IDictionary<string, object>;
                if (result is not null)
                {
                    part |= result.TryGetValue("part", out var p) && Convert.ToBoolean(p, CultureInfo.InvariantCulture);
                    section |= result.TryGetValue("section", out var s) && Convert.ToBoolean(s, CultureInfo.InvariantCulture);
                    general |= result.TryGetValue("general", out var g) && Convert.ToBoolean(g, CultureInfo.InvariantCulture);
                    if (part && section && general) return true;
                }
            }
            catch { }
            if (depth >= 5) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); } catch { return false; }
            foreach (var frame in frames)
            {
                try { driver.SwitchTo().Frame(frame); if (Walk(depth + 1)) return true; }
                catch { }
                finally { try { driver.SwitchTo().ParentFrame(); } catch { try { driver.SwitchTo().DefaultContent(); } catch { } } }
            }
            return false;
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return (part, section, general);
    }

    private static void ForcePartAndSection(IWebDriver driver)
    {
        _ = ForcePartAndSectionStable(driver, TimeSpan.FromSeconds(4));
    }

    private static bool ForcePartAndSectionStable(IWebDriver driver, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        do
        {
            // 1º ciclo: dispara a troca da Parte para a página reconstruir a seção.
            _ = ForcePartAndSectionPass(driver, triggerPartChange: true, triggerSectionChange: false);
            Thread.Sleep(350);
            WaitForDomIdle(driver, TimeSpan.FromMilliseconds(800));

            // 2º ciclo: com a seção já reconstruída, fixa OUTROS ASSUNTOS.
            _ = ForcePartAndSectionPass(driver, triggerPartChange: false, triggerSectionChange: true);
            Thread.Sleep(250);

            // 3º ciclo: alguns scripts antigos do SisBol rodam depois do onchange;
            // por isso regrava a seleção SEM novo onchange para não voltar para Oficiais.
            var state = ForcePartAndSectionPass(driver, triggerPartChange: false, triggerSectionChange: false);
            var verified = VerifyStandardSisbolFields(driver);
            if ((state.Part || verified.Part) && (state.Section || verified.Section))
                return true;

            Thread.Sleep(350);
        }
        while (DateTime.UtcNow < deadline);

        var finalState = VerifyStandardSisbolFields(driver);
        return finalState.Part && finalState.Section;
    }

    private static (bool Part, bool Section) ForcePartAndSectionPass(IWebDriver driver, bool triggerPartChange, bool triggerSectionChange)
    {
        var result = ExecuteInFrames(driver, @"
            try {
                const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x && (x.seleParteBi||x.seleSecaoParteBi));
                if(!f)return {ok:false,part:false,section:false,diag:'form'};
                function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/[ª]/g,'a').replace(/[º]/g,'o').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                function first(el){ if(!el) return null; if(el.tagName) return el; if(typeof el.length==='number') return Array.from(el).find(x=>x&&x.tagName)||null; return null; }
                function byName(name){ return first((f.elements&&f.elements[name])||f[name]||document.querySelector('[name=\''+name+'\'],#'+name)); }
                function fire(el){if(!el)return; ['input','change','keyup','blur'].forEach(n=>{try{el.dispatchEvent(new Event(n,{bubbles:true,cancelable:true}))}catch(e){}})}
                function callInline(el){
                    try{ if(!el) return; const h=el.getAttribute&&el.getAttribute('onchange'); if(h) (new Function(h)).call(el); }catch(e){}
                }
                function setSelect(sel, wantedValue, wantedTexts, dispatch){
                    sel=first(sel); if(!sel||!sel.options)return false;
                    const wants=Array.isArray(wantedTexts)?wantedTexts.map(norm):[norm(wantedTexts||'')];
                    let opt=null, idx=-1;
                    const options=Array.from(sel.options||[]);
                    for(let i=0;i<options.length;i++){
                        const o=options[i];
                        const val=String(o.value||'').trim();
                        const txt=norm((o.text||'')+' '+val);
                        if(wants.some(w=>w && txt.includes(w))){ opt=o; idx=i; break; }
                        if(!opt && wantedValue && val===String(wantedValue)){ opt=o; idx=i; }
                    }
                    if(!opt)return false;
                    try{
                        for(const o of options){o.selected=false; try{o.removeAttribute('selected')}catch(e){}}
                        opt.selected=true;
                        try{opt.setAttribute('selected','selected')}catch(e){}
                        sel.selectedIndex=idx;
                        sel.value=opt.value;
                        try{sel.setAttribute('value',opt.value)}catch(e){}
                        if(dispatch){ fire(sel); callInline(sel); }
                        return String(sel.value||'').trim()===String(opt.value||'').trim() || sel.selectedIndex===idx;
                    }catch(e){return false}
                }
                const partSel=byName('seleParteBi');
                const sectionSel=byName('seleSecaoParteBi');
                try{ const cod=byName('codTipoBol'); if(cod){cod.value='3'; try{cod.setAttribute('value','3')}catch(e){}} }catch(e){}
                const partOk=setSelect(partSel,'3',['3 parte','3a parte','3ª parte'],!!arguments[0]);
                const sectionOk=setSelect(sectionSel,'3',['outros assuntos'],!!arguments[1]);
                for(const name of ['vai_altr','texto_fech_vai_altr']){
                    const cb=byName(name);
                    if(cb){try{cb.checked=false; cb.removeAttribute('checked'); if(arguments[1]) fire(cb)}catch(e){}}
                }
                const readPart=partSel?norm(((partSel.options&&partSel.selectedIndex>=0)?partSel.options[partSel.selectedIndex].text:'')+' '+(partSel.value||'')):'';
                const readSec=sectionSel?norm(((sectionSel.options&&sectionSel.selectedIndex>=0)?sectionSel.options[sectionSel.selectedIndex].text:'')+' '+(sectionSel.value||'')):'';
                return {ok:partOk||sectionOk,part:(String(partSel&&partSel.value||'')==='3'||(readPart.includes('3')&&readPart.includes('parte'))),section:(String(sectionSel&&sectionSel.value||'')==='3'||readSec.includes('outros assuntos')),p:readPart,s:readSec};
            } catch(e) { return {ok:false,part:false,section:false,diag:String(e&&e.message||e)}; }
            ", triggerPartChange, triggerSectionChange);

        if (result is IDictionary<string, object> map)
        {
            var part = map.TryGetValue("part", out var p) && Convert.ToBoolean(p, CultureInfo.InvariantCulture);
            var section = map.TryGetValue("section", out var sec) && Convert.ToBoolean(sec, CultureInfo.InvariantCulture);
            return (part, section);
        }
        return (false, false);
    }

    private static bool EnsurePaymentPersonalSubjectForSubmit(IWebDriver driver, string templateName, string plainText)
    {
        var specific = SpecificSubject(templateName, plainText);
        var code = SpecificSubjectCode(templateName, plainText);

        // Trava importante: depois que o editor já recebeu o texto do SIGFUR,
        // não podemos chamar setaAssuntoGeral/setaAssuntoEspecifico novamente.
        // No SisBol real essas funções limpam ou recarregam texto_abert/texto_fech.
        CloseSisbolSearchOverlay(driver);
        var state = VerifyStandardSisbolFields(driver);
        var subject = VerifySubjectFields(driver, specific, code);
        if (state.Part && state.Section && state.General && subject.General && subject.Specific)
            return true;

        // Reforço seguro: somente grava os inputs ocultos/readonly já existentes,
        // sem onchange, sem AJAX e sem limpar o editor.
        // Não regrava assunto nesta fase: qualquer setter do SisBol pode apagar o editor.
        state = VerifyStandardSisbolFields(driver);
        subject = VerifySubjectFields(driver, specific, code);
        return state.Part && state.Section && state.General && subject.General && subject.Specific;
    }

    private static PreIncludeValidation ValidateBeforeInclude(IWebDriver driver, string templateName, string plainText)
    {
        var specific = SpecificSubject(templateName, plainText);
        var code = SpecificSubjectCode(templateName, plainText);
        var standard = VerifyStandardSisbolFields(driver);
        var subject = VerifySubjectFields(driver, specific, code);
        var specificOk = subject.Specific;
        var visualBody = WaitForEditorFilled(driver, plainText, TimeSpan.FromSeconds(3)).Success;
        var linkedBody = ValidateLinkedOpeningField(driver, plainText);
        var bodyOk = visualBody || linkedBody;
        var includeAvailable = HasIncludeControl(driver);

        var missing = new List<string>();
        if (!standard.Part) missing.Add("Parte 3");
        if (!standard.Section) missing.Add("seção OUTROS ASSUNTOS");
        if (!standard.General || !subject.General) missing.Add("Assunto Geral 1077 - PAGAMENTO PESSOAL");
        if (!specificOk) missing.Add("Assunto Específico");
        if (!bodyOk) missing.Add("corpo do texto no editor/FCKEditor");
        if (!includeAvailable) missing.Add("botão Incluir");

        if (missing.Count == 0)
        {
            var proof = visualBody && linkedBody ? "visual e vinculado" : visualBody ? "visual" : "campo vinculado";
            return new(true, $"Parte, Seção, Assunto Geral, Assunto Específico e corpo confirmados ({proof}).");
        }

        return new(false, "Validação pré-Incluir falhou: " + string.Join(", ", missing) + ".");
    }

    private static bool ValidateLinkedOpeningField(IWebDriver driver, string expectedText)
    {
        var expected = NormalizeCompact(expectedText);
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var beginning = expected[..Math.Min(48, expected.Length)];
        var ending = expected.Length > 48 ? expected[^48..] : expected;
        var middle = expected.Length > 140 ? expected.Substring(expected.Length / 2 - 24, 48) : expected;
        var minimumLength = Math.Max(12, (int)Math.Floor(expected.Length * 0.72));

        var result = ExecuteInFrames(driver, """
            try{
                function textOnly(value){const raw=String(value||'');if(!/[<>]/.test(raw))return raw;const box=document.createElement('div');box.innerHTML=raw;return box.innerText||box.textContent||raw;}
                function norm(s){try{return String(textOnly(s)||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(textOnly(s)||'').toLowerCase().replace(/\s+/g,' ').trim()}}
                const f=document.cadMateriaBI||document.forms['cadMateriaBI'];
                const el=(f&&((f.elements&&f.elements['texto_abert'])||f.texto_abert))||document.querySelector('#texto_abert,[name="texto_abert"]');
                if(!el)return {ok:false,reason:'texto_abert ausente'};
                const text=norm(el.value||el.getAttribute('value')||'');
                const parts=[arguments[0],arguments[1],arguments[2]].filter(Boolean);
                const matches=parts.filter(x=>text.includes(x)).length;
                const required=Number(arguments[4])<=80?1:2;
                return {ok:text.length>=Number(arguments[3])&&matches>=required,length:text.length,matches:matches,id:el.id||'',name:el.name||''};
            }catch(e){return {ok:false,reason:String(e&&e.message||e)}}
            """, beginning, middle, ending, minimumLength, expected.Length);

        return result is IDictionary<string, object> map &&
               map.TryGetValue("ok", out var ok) &&
               Convert.ToBoolean(ok, CultureInfo.InvariantCulture);
    }

    private static bool HasIncludeControl(IWebDriver driver)
    {
        var result = ExecuteInFrames(driver, """
            try{
                function n(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase()}catch(e){return String(s||'').toLowerCase()}}
                const controls=Array.from(document.querySelectorAll('input,button,a')).filter(e=>!e.disabled);
                const found=controls.find(e=>{const label=n((e.value||'')+' '+(e.innerText||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.getAttribute&&e.getAttribute('onclick')||''));return /(^|[^a-z])incluir([^a-z]|$)|salvamateriabi/.test(label)&&!/excluir|cancelar/.test(label)});
                return {ok:!!found,id:found&&found.id||'',name:found&&found.name||'',label:found&&(found.value||found.innerText)||''};
            }catch(e){return {ok:false}}
            """);
        return result is IDictionary<string, object> map &&
               map.TryGetValue("ok", out var ok) &&
               Convert.ToBoolean(ok, CultureInfo.InvariantCulture);
    }

    private static void DirectReinforceSubjectFields(IWebDriver driver, string specific, string code)
    {
        _ = ExecuteInFrames(driver, @"
            try {
                const wantedName=arguments[0]||'', wantedCode=arguments[1]||'';
                const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x && (x.inputCodAssGeral||x.inputCodAssEspec||x.texto_abert));
                if(!f)return {ok:false};
                function first(el){ if(!el) return null; if(el.tagName) return el; if(typeof el.length==='number') return Array.from(el).find(x=>x&&x.tagName)||null; return null; }
                function byNames(names){for(const n of names){const el=first((f.elements&&f.elements[n])||f[n]||document.querySelector('[name=\''+n+'\'],#'+n)); if(el)return el;} return null;}
                function fire(el){if(!el)return; ['input','change','keyup','blur'].forEach(n=>{try{el.dispatchEvent(new Event(n,{bubbles:true,cancelable:true}))}catch(e){}})}
                function set(el,v){el=first(el); if(!el || v===null || v===undefined || v==='') return false; try{el.value=String(v); try{el.setAttribute('value',String(v))}catch(e){}; fire(el); return true;}catch(e){return false}}
                set(byNames(['codTipoBol']),'3');
                set(byNames(['inputCodAssGeral','codAssuntoGeral','codAssGeral']),'1077');
                set(byNames(['inputAssuntoGeral','assuntoGeral','descAssuntoGeral']),'PAGAMENTO PESSOAL');
                if(wantedCode) set(byNames(['inputCodAssEspec','inputCodAssEspecifico','codAssuntoEspec','codAssuntoEspecifico','codAssEspec']),wantedCode);
                if(wantedName) set(byNames(['inputAssuntoEspec','inputAssuntoEspecifico','assuntoEspec','assuntoEspecifico','descAssuntoEspec','descAssuntoEspecifico']),wantedName);
                return {ok:true};
            } catch(e) { return {ok:false,diag:String(e&&e.message||e)}; }
            ", specific, code);
    }

    private static object? ExecuteInFrames(IWebDriver driver, string script, params object[] args)
    {
        object? Walk(int depth)
        {
            try
            {
                var result = ((IJavaScriptExecutor)driver).ExecuteScript(script, args);
                if (result is IDictionary<string, object> map && map.Values.Any(v => v is bool b && b)) return result;
            }
            catch { }
            if (depth >= 5) return null;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return null; }
            foreach (var frame in frames)
            {
                try { driver.SwitchTo().Frame(frame); var result = Walk(depth + 1); if (result is not null) return result; }
                catch { }
                finally { try { driver.SwitchTo().ParentFrame(); } catch { try { driver.SwitchTo().DefaultContent(); } catch { } } }
            }
            return null;
        }
        try { driver.SwitchTo().DefaultContent(); } catch { }
        var value = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return value;
    }

    private static (bool General, bool Specific) VerifySubjectFields(IWebDriver driver, string specific, string code)
    {
        var wantedName = NormalizeSubject(specific);
        var wantedCode = NormalizeCompact(code);
        var general = false;
        var specificOk = string.IsNullOrWhiteSpace(specific);

        bool Walk(int depth)
        {
            try
            {
                var result = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function n(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x && (x.inputCodAssGeral||x.inputAssuntoGeral));
                    const val=(names)=>{for(const name of names){const e=(f&&((f.elements&&f.elements[name])||f[name]))||document.querySelector('[name=\''+name+'\'],#'+name);const v=n(e?(e.value||e.textContent||''):''); if(v) return v;} return '';};
                    return {gCode:val(['inputCodAssGeral','codAssuntoGeral','codAssGeral']),gText:val(['inputAssuntoGeral','assuntoGeral','descAssuntoGeral']),sCode:val(['inputCodAssEspec','inputCodAssEspecifico','codAssuntoEspec','codAssuntoEspecifico','codAssEspec','assuntoEspecCodigo','assuntoEspecificoCodigo']),sText:val(['inputAssuntoEspec','inputAssuntoEspecifico','assuntoEspec','assuntoEspecifico','descAssuntoEspec','descAssuntoEspecifico','descricaoAssuntoEspec','descricaoAssuntoEspecifico','txtAssuntoEspec','txtAssuntoEspecifico'])};") as IDictionary<string, object>;
                if (result is not null)
                {
                    var gCode = NormalizeCompact(result.TryGetValue("gCode", out var gc) ? gc?.ToString() : string.Empty);
                    var gText = NormalizeCompact(result.TryGetValue("gText", out var gt) ? gt?.ToString() : string.Empty);
                    var specificCode = NormalizeCompact(result.TryGetValue("sCode", out var sc) ? sc?.ToString() : string.Empty);
                    var specificText = NormalizeSubject(result.TryGetValue("sText", out var st) ? st?.ToString() : string.Empty);
                    var wantedLoose = NormalizeSubjectLoose(specific);
                    var specificLoose = NormalizeSubjectLoose(specificText);
                    // Para evitar o erro fatal do PHP, não basta existir uma opção 1077 na combo.
                    // O hidden de código precisa estar preenchido com 1077.
                    general |= gCode.Equals("1077", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(gText) || gText.Contains("pagamento pessoal"));
                    var codeMatches = string.IsNullOrWhiteSpace(wantedCode) || specificCode.Equals(wantedCode, StringComparison.OrdinalIgnoreCase);
                    var nameMatches = !string.IsNullOrWhiteSpace(wantedName) &&
                                      (specificText.Contains(wantedName, StringComparison.OrdinalIgnoreCase) ||
                                       wantedName.Contains(specificText, StringComparison.OrdinalIgnoreCase) ||
                                       (!string.IsNullOrWhiteSpace(wantedLoose) && !string.IsNullOrWhiteSpace(specificLoose) &&
                                        (specificLoose.Contains(wantedLoose, StringComparison.OrdinalIgnoreCase) ||
                                         wantedLoose.Contains(specificLoose, StringComparison.OrdinalIgnoreCase))));
                    specificOk |= codeMatches && (nameMatches || (!string.IsNullOrWhiteSpace(wantedCode) && !string.IsNullOrWhiteSpace(specificCode)));
                    if (general && specificOk) return true;
                }
            }
            catch { }
            if (depth >= 5) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); } catch { return false; }
            foreach (var frame in frames)
            {
                try { driver.SwitchTo().Frame(frame); if (Walk(depth + 1)) return true; }
                catch { }
                finally { try { driver.SwitchTo().ParentFrame(); } catch { try { driver.SwitchTo().DefaultContent(); } catch { } } }
            }
            return false;
        }
        try { driver.SwitchTo().DefaultContent(); } catch { }
        Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return (general, specificOk);
    }

    private static bool SelectByVisibleTextLike(IWebDriver driver, string wanted, IReadOnlyList<string> hints)
    {
        bool Walk(int depth)
        {
            try
            {
                var selects = driver.FindElements(By.TagName("select")).Where(IsVisible).ToList();
                var normalizedWanted = Normalize(wanted);
                foreach (var select in selects.OrderByDescending(s => hints.Any(h => Normalize(Context(s)).Contains(Normalize(h)))))
                {
                    var options = select.FindElements(By.TagName("option"));
                    var option = options.FirstOrDefault(o => Normalize(o.Text).Contains(normalizedWanted) || normalizedWanted.Contains(Normalize(o.Text)));
                    if (option is null) continue;
                    var value = option.GetAttribute("value");
                    if (!string.IsNullOrWhiteSpace(value)) new SelectElement(select).SelectByValue(value); else option.Click();
                    try { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].dispatchEvent(new Event('input',{bubbles:true}));arguments[0].dispatchEvent(new Event('change',{bubbles:true}));", select); } catch { }
                    return true;
                }
            }
            catch { }
            if (depth >= 5) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); } catch { return false; }
            foreach (var frame in frames)
            {
                try { driver.SwitchTo().Frame(frame); if (Walk(depth + 1)) return true; }
                catch { }
                finally { try { driver.SwitchTo().ParentFrame(); } catch { try { driver.SwitchTo().DefaultContent(); } catch { } } }
            }
            return false;
        }
        try { driver.SwitchTo().DefaultContent(); } catch { }
        var result = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return result;
    }

    private static bool FillEditorsWithRetry(IWebDriver driver, SisbolMatterPayload payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitReady(driver, 3);
            if (FillEditors(driver, payload) &&
                WaitForEditorFilled(driver, payload.OpeningTextPlain, TimeSpan.FromSeconds(2)).Success &&
                (!payload.IncludeConsequences || WaitForClosingEditorFilled(driver, payload.ClosingTextPlain, TimeSpan.FromSeconds(2)).Success))
                return true;
            if (cancellationToken.WaitHandle.WaitOne(350)) throw new OperationCanceledException(cancellationToken);
        }
        return false;
    }

    private static bool FillEditors(IWebDriver driver, SisbolMatterPayload payload)
    {
        var plain = payload.OpeningTextPlain;
        var html = payload.OpeningTextHtml;
        var closingPlain = payload.IncludeConsequences ? payload.ClosingTextPlain : string.Empty;
        var closingHtml = payload.IncludeConsequences ? payload.ClosingTextHtml : string.Empty;
        bool Walk(int depth)
        {
            try
            {
                var result = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    try {
                        const html=arguments[0]||'', plain=arguments[1]||'';
                        const closingHtml=arguments[2]||'', closingPlain=arguments[3]||'';
                        let filled=0;
                        function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                        function fire(el){
                            try{el.dispatchEvent(new Event('input',{bubbles:true}))}catch(e){}
                            try{el.dispatchEvent(new Event('change',{bubbles:true}))}catch(e){}
                            try{el.dispatchEvent(new Event('keyup',{bubbles:true}))}catch(e){}
                            try{el.dispatchEvent(new Event('blur',{bubbles:true}))}catch(e){}
                        }
                        function meta(el){return norm((el&&el.id||'')+' '+(el&&el.name||'')+' '+(el&&el.className||'')+' '+(el&&el.getAttribute&&el.getAttribute('src')||'')+' '+(el&&el.getAttribute&&el.getAttribute('title')||''))}
                        function docMeta(){
                            let s='';
                            try{s+=' '+location.href}catch(e){}
                            try{const fe=window.frameElement;if(fe)s+=' '+(fe.id||'')+' '+(fe.name||'')+' '+(fe.src||'')+' '+(fe.title||'')}catch(e){}
                            return norm(s);
                        }
                        function isClosingText(s){s=norm(s);return /texto[_ -]*fech|fechamento|fckfech|fech___frame|texto_fech/.test(s)}
                        function isOpeningText(s){s=norm(s);return /texto[_ -]*abert|abertura|fckabert|abert___frame|texto_abert/.test(s)}
                        function style(el){try{el.style.fontFamily='Times New Roman';el.style.fontSize='10pt';el.style.lineHeight='normal'}catch(e){}}
                        function setValue(el,value){
                            if(!el)return false;
                            try{
                                const proto=el&&el.tagName==='TEXTAREA'&&window.HTMLTextAreaElement?window.HTMLTextAreaElement.prototype:
                                    el&&el.tagName==='INPUT'&&window.HTMLInputElement?window.HTMLInputElement.prototype:null;
                                const setter=proto&&Object.getOwnPropertyDescriptor(proto,'value')?.set;
                                if(setter)setter.call(el,value);else el.value=value;
                                try{el.setAttribute('value', value)}catch(e){}
                                fire(el);filled++;return true;
                            }catch(e){return false}
                        }
                        function setBody(el,value){
                            if(!el)return false;
                            try{style(el);el.innerHTML=value;fire(el);filled++;return true}catch(e){return false}
                        }
                        function fillClosing(){
                            try{
                                if(window.FCKeditorAPI&&typeof window.FCKeditorAPI.GetInstance==='function'){
                                    const ed=window.FCKeditorAPI.GetInstance('texto_fech');
                                    if(ed){try{ed.SetHTML(closingHtml)}catch(e){};try{if(ed.EditorDocument&&ed.EditorDocument.body)setBody(ed.EditorDocument.body,closingHtml)}catch(e){};try{if(typeof ed.UpdateLinkedField==='function')ed.UpdateLinkedField()}catch(e){};filled++;}
                                }
                            }catch(e){}
                            try{
                                const form=document.cadMateriaBI||document.forms['cadMateriaBI'];
                                if(form){for(const n of ['texto_fech','texto_fechamento','fechamento']){const el=(form.elements&&form.elements[n])||form[n]||document.querySelector('[name=\''+n+'\'],#'+n);if(el)setValue(el,closingHtml)}}
                            }catch(e){}
                            try{for(const el of Array.from(document.querySelectorAll('textarea,input[type=hidden]'))){if(isClosingText(meta(el)))setValue(el,closingHtml)}}catch(e){}
                        }

                        function forceFckFrame(instanceName,value){
                            let done=false;
                            try{
                                const frame=document.getElementById(instanceName+'___Frame');
                                if(!frame)return false;
                                const fd=frame.contentDocument||(frame.contentWindow&&frame.contentWindow.document);
                                if(!fd)return false;
                                const nested=Array.from(fd.querySelectorAll('iframe,frame'));
                                for(const fr of nested){
                                    try{
                                        const d=fr.contentDocument||(fr.contentWindow&&fr.contentWindow.document);
                                        const b=d&&d.body;
                                        if(!b)continue;
                                        const href=String((d.location&&d.location.href)||'')+' '+String((fr.id||'')+' '+(fr.name||'')+' '+(fr.src||''));
                                        // O iframe interno do FCKEditor 2 não traz sempre contenteditable,
                                        // mas é ele que salva o corpo visual da nota.
                                        style(b);
                                        b.innerHTML=value;
                                        try{b.focus()}catch(e){}
                                        try{b.dispatchEvent(new Event('input',{bubbles:true,cancelable:true}))}catch(e){}
                                        try{b.dispatchEvent(new Event('change',{bubbles:true,cancelable:true}))}catch(e){}
                                        filled++;
                                        done=true;
                                    }catch(e){}
                                }
                                return done;
                            }catch(e){return false;}
                        }

                        fillClosing();
                        forceFckFrame('texto_abert',html);
                        forceFckFrame('texto_fech',closingHtml);
                        if(isClosingText(docMeta())){
                            if(document.body)setBody(document.body,closingHtml);
                            return {ok:true,count:filled,editor:'fechamento'};
                        }

                        // FCKeditor 2 do SisBol: preencher somente Texto de Abertura.
                        try{
                            if(window.FCKeditorAPI&&typeof window.FCKeditorAPI.GetInstance==='function'){
                                const ed=window.FCKeditorAPI.GetInstance('texto_abert');
                                if(ed){
                                    try{if(typeof ed.SetHTML==='function')ed.SetHTML(html)}catch(e){}
                                    try{const body=ed.EditorDocument&&ed.EditorDocument.body;if(body)setBody(body,html)}catch(e){}
                                    try{if(typeof ed.UpdateLinkedField==='function')ed.UpdateLinkedField()}catch(e){}
                                    filled++;
                                }
                            }
                        }catch(e){}

                        // CKEditor clássico: abertura e fechamento recebem conteúdos distintos.
                        try{
                            if(window.CKEDITOR&&CKEDITOR.instances){
                                for(const name of Object.keys(CKEDITOR.instances)){
                                    if(isClosingText(name)){
                                        try{const ed=CKEDITOR.instances[name];ed.setData(closingHtml,function(){try{ed.updateElement()}catch(e){}});try{ed.updateElement()}catch(e){};filled++;}catch(e){}
                                        continue;
                                    }
                                    if(Object.keys(CKEDITOR.instances).length>1 && !isOpeningText(name))continue;
                                    try{const ed=CKEDITOR.instances[name];ed.setData(html,function(){try{ed.updateElement()}catch(e){}});try{ed.updateElement()}catch(e){};filled++;}catch(e){}
                                }
                            }
                        }catch(e){}

                        // Campos ligados reais do formulário recebem abertura e fechamento separados.
                        try{
                            const form=document.cadMateriaBI||document.forms['cadMateriaBI'];
                            if(form){
                                for(const name of ['texto_abert','texto_aberto']){
                                    const el=(form.elements&&form.elements[name])||form[name]||document.querySelector('[name=\''+name+'\'],#'+name);
                                    if(el)setValue(el,html);
                                }
                                for(const name of ['texto_fech','texto_fechamento']){
                                    const el=(form.elements&&form.elements[name])||form[name]||document.querySelector('[name=\''+name+'\'],#'+name);
                                    if(el)setValue(el,closingHtml);
                                }
                            }
                        }catch(e){}

                        const areas=Array.from(document.querySelectorAll('textarea'));
                        for(const area of areas){
                            const m=meta(area);
                            if(isClosingText(m)){setValue(area,closingHtml);continue;}
                            if(isOpeningText(m)||(/texto|materia|conteudo|abert|editor|observ/.test(m)&&areas.length===1))setValue(area,isOpeningText(m)?html:plain);
                        }

                        const editables=Array.from(document.querySelectorAll(""body[contenteditable='true'],[contenteditable='true']""));
                        for(const el of editables){
                            const m=meta(el)+' '+docMeta();
                            if(isClosingText(m)){setBody(el,closingHtml);continue;}
                            if(editables.length===1||isOpeningText(m)||/abert|materia|conteudo|editor/.test(m))setBody(el,html);
                        }
                        if(document.body&&String(document.body.contentEditable).toLowerCase()==='true')setBody(document.body,isClosingText(docMeta())?closingHtml:html);

                        const hidden=Array.from(document.querySelectorAll(""input[type='hidden']""));
                        for(const el of hidden){
                            const m=meta(el);
                            if(isClosingText(m)){setValue(el,closingHtml);continue;}
                            if(isOpeningText(m))setValue(el,html);
                        }
                        return {ok:filled>0,count:filled};
                    } catch(e){return {ok:false,count:0,error:String(e&&e.message||e)};}", html, plain, closingHtml, closingPlain) as IDictionary<string, object>;

                if (result is not null &&
                    result.TryGetValue("ok", out var ok) &&
                    Convert.ToBoolean(ok, CultureInfo.InvariantCulture))
                    return true;
            }
            catch { }

            if (depth >= 7) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return false; }
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    if (Walk(depth + 1)) return true;
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
            return false;
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        var filled = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        WaitForDomIdle(driver, TimeSpan.FromSeconds(2));
        try { driver.SwitchTo().DefaultContent(); } catch { }
        filled = Walk(0) || filled;
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return filled;
    }

    private static SendResult WaitForEditorFilled(IWebDriver driver, string expectedText, TimeSpan timeout)
    {
        var expected = NormalizeCompact(expectedText);
        var expectedLength = expected.Length;
        var beginning = expected[..Math.Min(48, expectedLength)];
        var ending = expectedLength > 48 ? expected[^48..] : expected;
        var middle = expectedLength > 140 ? expected.Substring(expectedLength / 2 - 24, 48) : expected;
        var minimumLength = Math.Max(18, (int)Math.Floor(expectedLength * 0.72));
        var end = DateTime.UtcNow + timeout;

        (bool Success, int Length, int Matches) Walk(int depth)
        {
            try
            {
                var rows = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function textOnly(value){
                        const raw=String(value||'');
                        if(!/[<>]/.test(raw))return raw;
                        try{const box=document.createElement('div');box.innerHTML=raw;return box.innerText||box.textContent||raw}catch(e){return raw}
                    }
                    function norm(s){try{return String(textOnly(s)||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(textOnly(s)||'').toLowerCase().replace(/\s+/g,' ').trim()}}
                    function docMeta(){let s='';try{s+=' '+location.href}catch(e){};try{const fe=window.frameElement;if(fe)s+=' '+(fe.id||'')+' '+(fe.name||'')+' '+(fe.src||'')+' '+(fe.title||'')}catch(e){};return norm(s)}
                    function isClosingText(s){s=norm(s);return /texto[_ -]*fech|fechamento|fckfech|fech___frame|texto_fech/.test(s)}
                    function isOpeningText(s){s=norm(s);return /texto[_ -]*abert|abertura|fckabert|abert___frame|texto_abert/.test(s)}
                    const rows=[];
                    if(isClosingText(docMeta()))return rows;
                    function add(text,source){const n=norm(text);if(n)rows.push({text:n,len:n.length,source:source||''});}
                    try{
                        if(window.FCKeditorAPI&&typeof window.FCKeditorAPI.GetInstance==='function'){
                            const names=new Set(['texto_abert','texto_aberto']);
                            try{for(const key of Object.keys(window.FCKeditorAPI.Instances||window.FCKeditorAPI.__Instances||{})){if(isOpeningText(key)&&!isClosingText(key))names.add(key)}}catch(e){}
                            for(const name of names){
                                if(isClosingText(name))continue;
                                try{const ed=window.FCKeditorAPI.GetInstance(name);if(ed){let v='';try{v=ed.GetXHTML(true)}catch(e){try{v=ed.GetHTML()}catch(_){}};add(v,'fck:'+name)}}catch(e){}
                            }
                        }
                    }catch(e){}
                    try{
                        if(window.CKEDITOR&&CKEDITOR.instances){
                            for(const name of Object.keys(CKEDITOR.instances)){if(isClosingText(name))continue;try{add(CKEDITOR.instances[name].getData(),'cke:'+name)}catch(e){}}
                        }
                    }catch(e){}
                    try{
                        const dm=docMeta();
                        // Iframe interno do FCKEditor 2: o body visual pode não ter
                        // contenteditable, mas é o conteúdo que o SisBol mostra e submete.
                        if(document.body && !isClosingText(dm) && /fckeditor|texto_abert|abert___frame|instanceName=texto_abert/i.test(String(location.href||'')+' '+dm)){
                            add(document.body.innerHTML||document.body.innerText||document.body.textContent||'', 'visual-body:'+dm);
                        }
                    }catch(e){}
                    for(const el of Array.from(document.querySelectorAll(`textarea,[contenteditable='true'],body[contenteditable='true'],input[type='hidden']`))){
                        const meta=norm((el.id||'')+' '+(el.name||'')+' '+(el.className||'')+' '+(el.title||''));
                        if(isClosingText(meta+' '+docMeta()))continue;
                        if(isOpeningText(meta)||/texto|materia|conteudo|abert|editor|observ/.test(meta)||el.isContentEditable||el.tagName==='TEXTAREA'){
                            try{add(el.value||el.innerText||el.textContent||el.innerHTML||'',meta)}catch(e){}
                        }
                    }
                    return rows;") as IReadOnlyCollection<object>;

                var bestLength = 0;
                var bestMatches = 0;
                if (rows is not null)
                {
                    foreach (var raw in rows)
                    {
                        if (raw is not IDictionary<string, object> row) continue;
                        var text = NormalizeCompact(row.TryGetValue("text", out var textValue) ? textValue?.ToString() : string.Empty);
                        var sourceRaw = row.TryGetValue("source", out var sourceValue) ? (sourceValue?.ToString() ?? string.Empty) : string.Empty;
                        var source = sourceRaw.ToLowerInvariant();
                        var visualSource = source.Contains("fck:", StringComparison.Ordinal) ||
                                           source.Contains("cke:", StringComparison.Ordinal) ||
                                           source.Contains("visual-body", StringComparison.Ordinal) ||
                                           source.Contains("contenteditable", StringComparison.Ordinal);
                        var length = text.Length;
                        bestLength = Math.Max(bestLength, length);
                        var matches = 0;
                        if (!string.IsNullOrWhiteSpace(beginning) && text.Contains(beginning, StringComparison.Ordinal)) matches++;
                        if (!string.IsNullOrWhiteSpace(middle) && text.Contains(middle, StringComparison.Ordinal)) matches++;
                        if (!string.IsNullOrWhiteSpace(ending) && text.Contains(ending, StringComparison.Ordinal)) matches++;
                        bestMatches = Math.Max(bestMatches, matches);

                        // Exige tamanho próximo e trechos distribuídos. Para o SisBol/FCKEditor,
                        // não aceita somente o input hidden texto_abert como prova: o corpo também
                        // precisa aparecer no editor visual ou na API do FCK.
                        if (visualSource &&
                            ((expectedLength <= 80 && matches >= 1 && length >= Math.Max(12, expectedLength / 2)) ||
                             (expectedLength > 80 && matches >= 2 && length >= minimumLength)))
                            return (true, length, matches);
                    }
                }
                return (false, bestLength, bestMatches);
            }
            catch { }

            if (depth >= 7) return (false, 0, 0);
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return (false, 0, 0); }
            var best = (Success: false, Length: 0, Matches: 0);
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    var found = Walk(depth + 1);
                    if (found.Success) return found;
                    if (found.Length > best.Length) best = found;
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
            return best;
        }

        var largest = 0;
        while (DateTime.UtcNow < end)
        {
            try { driver.SwitchTo().DefaultContent(); } catch { }
            var result = Walk(0);
            largest = Math.Max(largest, result.Length);
            try { driver.SwitchTo().DefaultContent(); } catch { }
            if (result.Success)
                return new(true, $"Texto completo confirmado no editor ({result.Length}/{expectedLength} caracteres normalizados).");
            WaitForDomIdle(driver, TimeSpan.FromMilliseconds(300));
        }
        return new(false,
            $"O SisBol recebeu apenas parte do texto ({largest}/{expectedLength} caracteres normalizados). " +
            "A inclusão foi interrompida para evitar salvar uma nota cortada.");
    }

    private static SendResult WaitForClosingEditorFilled(IWebDriver driver, string expectedText, TimeSpan timeout)
    {
        var expected = NormalizeCompact(expectedText);
        if (string.IsNullOrWhiteSpace(expected)) return new(true, "Texto de fechamento desativado.");
        var deadline = DateTime.UtcNow + timeout;
        var largest = 0;

        (bool Success, int Length) Walk(int depth)
        {
            try
            {
                var rows = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    function textOnly(value){
                        const raw=String(value||'');
                        if(!/[<>]/.test(raw))return raw;
                        try{const box=document.createElement('div');box.innerHTML=raw;return box.innerText||box.textContent||raw}catch(e){return raw}
                    }
                    function norm(s){try{return String(textOnly(s)||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(textOnly(s)||'').toLowerCase().replace(/\s+/g,' ').trim()}}
                    function meta(el){return norm((el&&el.id||'')+' '+(el&&el.name||'')+' '+(el&&el.className||'')+' '+(el&&el.title||''))}
                    function docMeta(){let s='';try{s+=' '+location.href}catch(e){};try{const fe=window.frameElement;if(fe)s+=' '+(fe.id||'')+' '+(fe.name||'')+' '+(fe.src||'')+' '+(fe.title||'')}catch(e){};return norm(s)}
                    function isClosing(s){return /texto[_ -]*fech|fechamento|fckfech|fech___frame|texto_fech/.test(norm(s))}
                    const rows=[];
                    function add(value,source,visual){const text=norm(value);if(text)rows.push({text,source:source||'',visual:!!visual});}
                    try{
                        if(window.FCKeditorAPI&&typeof window.FCKeditorAPI.GetInstance==='function'){
                            const ed=window.FCKeditorAPI.GetInstance('texto_fech');
                            if(ed){let value='';try{value=ed.GetXHTML(true)}catch(e){try{value=ed.GetHTML()}catch(_){}};add(value,'fck:texto_fech',true);try{if(ed.EditorDocument&&ed.EditorDocument.body)add(ed.EditorDocument.body.innerHTML,'fck-body:texto_fech',true)}catch(e){}}
                        }
                    }catch(e){}
                    try{
                        if(window.CKEDITOR&&CKEDITOR.instances){
                            for(const name of Object.keys(CKEDITOR.instances)){if(isClosing(name)){try{add(CKEDITOR.instances[name].getData(),'cke:'+name,true)}catch(e){}}}
                        }
                    }catch(e){}
                    if(isClosing(docMeta())&&document.body)add(document.body.innerHTML||document.body.innerText||document.body.textContent||'','visual-body:'+docMeta(),true);
                    for(const el of Array.from(document.querySelectorAll("textarea,input[type='hidden'],[contenteditable='true']"))){
                        const m=meta(el)+' '+docMeta();
                        if(isClosing(m))add(el.value||el.innerHTML||el.innerText||el.textContent||'',m,!!el.isContentEditable);
                    }
                    return rows;
                    """) as IReadOnlyCollection<object>;

                if (rows is not null)
                {
                    foreach (var raw in rows)
                    {
                        if (raw is not IDictionary<string, object> row) continue;
                        var text = NormalizeCompact(row.TryGetValue("text", out var value) ? value?.ToString() : string.Empty);
                        var visual = row.TryGetValue("visual", out var visualValue) && Convert.ToBoolean(visualValue, CultureInfo.InvariantCulture);
                        largest = Math.Max(largest, text.Length);
                        if (visual && (text.Contains(expected, StringComparison.Ordinal) || expected.Contains(text, StringComparison.Ordinal) && text.Length >= expected.Length * 0.8))
                            return (true, text.Length);
                    }
                }
            }
            catch { }

            if (depth >= 7) return (false, 0);
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return (false, 0); }
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    var found = Walk(depth + 1);
                    if (found.Success) return found;
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
            return (false, 0);
        }

        while (DateTime.UtcNow < deadline)
        {
            try { driver.SwitchTo().DefaultContent(); } catch { }
            var result = Walk(0);
            try { driver.SwitchTo().DefaultContent(); } catch { }
            if (result.Success) return new(true, $"Texto de fechamento confirmado no editor ({result.Length}/{expected.Length} caracteres normalizados).");
            WaitForDomIdle(driver, TimeSpan.FromMilliseconds(250));
        }

        return new(false, $"O editor Texto de Fechamento não confirmou o conteúdo ({largest}/{expected.Length} caracteres normalizados).");
    }

    private static bool OpenNewMatter(IWebDriver driver)
    {
        try
        {
            try { driver.SwitchTo().DefaultContent(); } catch { }
            var current = CaptureSnapshot(driver);
            if (IsFatalSisbolPage(current)) return false;

            // Se já estiver em uma matéria antiga/editada, não reutiliza a tela.
            // O SisBol mantém combo/assunto/checkbox antigos e isso derruba o envio.
            if (HasMatterDraftPage(driver))
            {
                var openedNew = TryOpenNewMatterCommand(driver) || ClickByLabel(driver, "nova nota");
                if (openedNew)
                {
                    WaitReady(driver, 8);
                    WaitForDomIdle(driver, TimeSpan.FromSeconds(2));
                    var afterNew = CaptureSnapshot(driver);
                    if (!IsFatalSisbolPage(afterNew) && HasMatterDraftPage(driver))
                        return true;
                }
            }

            var url = MatterUrl + "&_sigfur=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            driver.Navigate().GoToUrl(url);
            WaitReady(driver, 10);
            WaitForDomIdle(driver, TimeSpan.FromSeconds(2));
            if (LooksLikeLoginOrCaptcha(driver)) return true;
            var snapshot = CaptureSnapshot(driver);
            if (IsFatalSisbolPage(snapshot)) return false;

            if (!HasMatterDraftPage(driver))
            {
                if (TryOpenNewMatterCommand(driver) || ClickByLabel(driver, "nova nota") || ClickByLabel(driver, "nova matéria") || ClickByLabel(driver, "incluir matéria"))
                {
                    WaitReady(driver, 8);
                    WaitForDomIdle(driver, TimeSpan.FromSeconds(2));
                    snapshot = CaptureSnapshot(driver);
                    if (IsFatalSisbolPage(snapshot)) return false;
                }
            }

            return HasMatterDraftPage(driver);
        }
        catch { return false; }
    }

    private static bool TryOpenNewMatterCommand(IWebDriver driver)
    {
        var result = ExecuteInFrames(driver, @"
            try {
                if(typeof window.novaMateria==='function'){
                    window.novaMateria(3);
                    return {ok:true,method:'novaMateria'};
                }
                const btn=Array.from(document.querySelectorAll('input,button,a')).find(e=>String((e.value||'')+' '+(e.innerText||'')+' '+(e.textContent||'')).toLowerCase().includes('nova nota'));
                if(btn){try{btn.click();return {ok:true,method:'click'}}catch(e){}}
                return {ok:false};
            } catch(e) { return {ok:false}; }
            ");
        if (result is IDictionary<string, object> map && map.TryGetValue("ok", out var ok))
        {
            try { return Convert.ToBoolean(ok, CultureInfo.InvariantCulture); }
            catch { return false; }
        }
        return false;
    }

    private static bool HasMatterDraftPage(IWebDriver driver)
    {
        bool Walk(int depth)
        {
            try
            {
                var result = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().replace(/\s+/g,' ').trim()}}
                    function vis(e){if(!e)return false;try{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}catch(_){return false}}
                    const body=norm(document.body?document.body.innerText:'');
                    const form=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(f=>/cadmateriabi|materia|boletim/.test(norm((f.name||'')+' '+(f.id||'')+' '+(f.action||''))));
                    const hasEditor=!!(document.querySelector('textarea,[contenteditable=true],body[contenteditable=true],input[name=texto_abert],iframe[id*=texto_abert]')||window.FCKeditorAPI||window.CKEDITOR);
                    const controls=Array.from(document.querySelectorAll('input,button,a,img,span[onclick],td[onclick],div[onclick]')).filter(vis).map(e=>norm((e.value||'')+' '+(e.innerText||'')+' '+(e.textContent||'')+' '+(e.alt||'')+' '+(e.title||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.src||'')+' '+(e.href||'')+' '+(e.getAttribute&&e.getAttribute('onclick')||''))).join(' | ');
                    const hasInclude=/(^|[^a-z])incluir([^a-z]|$)|salvamateriabi|btnsalvar/.test(controls)&&!/excluir|remover|cancelar/.test(controls);
                    const looksCad=/lancar nota para|texto de abertura|materia bi|parte do bi|assunto geral/.test(body+controls);
                    return Boolean((form||hasEditor||looksCad) && (hasInclude||form));");
                if (result is bool ok && ok) return true;
            }
            catch { }
            if (depth >= 5) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return false; }
            foreach (var frame in frames)
            {
                try { driver.SwitchTo().Frame(frame); if (Walk(depth + 1)) return true; }
                catch { }
                finally { try { driver.SwitchTo().ParentFrame(); } catch { try { driver.SwitchTo().DefaultContent(); } catch { } } }
            }
            return false;
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        var found = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return found;
    }

    private static bool IsFatalSisbolPage(PageSnapshot snapshot)
        => NormalizeCompact(snapshot.Body).Contains("fatal error")
           || NormalizeCompact(snapshot.Body).Contains("call to a member function")
           || NormalizeCompact(snapshot.Body).Contains("getassuntogeral")
           || NormalizeCompact(snapshot.Body).Contains("erro fatal");

    private static bool ClickByLabel(IWebDriver driver, string wanted)
    {
        var normalized = NormalizeCompact(wanted);
        bool Walk(int depth)
        {
            try
            {
                var clicked = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    const wanted=arguments[0];
                    function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    function vis(e){if(!e||e.disabled)return false;try{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}catch(_){return false}}
                    function lab(e){return norm((e.value||'')+' '+(e.innerText||'')+' '+(e.textContent||'')+' '+(e.alt||'')+' '+(e.title||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.src||'')+' '+(e.href||'')+' '+(e.getAttribute&&e.getAttribute('onclick')||''));}
                    const all=Array.from(document.querySelectorAll('input,button,a,img,span[onclick],td[onclick],div[onclick]')).filter(vis);
                    let e=all.find(x=>lab(x).includes(wanted));if(!e)return false;if(e.tagName==='IMG'&&e.closest('a,button'))e=e.closest('a,button');
                    try{e.scrollIntoView({block:'center'})}catch(_){};try{e.click();return true}catch(_){};try{e.dispatchEvent(new MouseEvent('click',{view:window,bubbles:true,cancelable:true}));return true}catch(_){};return false;", normalized);
                if (clicked is bool ok && ok) return true;
            }
            catch (WebDriverException) { return true; }
            catch { }
            if (depth >= 5) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return false; }
            foreach (var frame in frames)
            {
                try { driver.SwitchTo().Frame(frame); if (Walk(depth + 1)) return true; }
                catch { }
                finally { try { driver.SwitchTo().ParentFrame(); } catch { try { driver.SwitchTo().DefaultContent(); } catch { } } }
            }
            return false;
        }
        try { driver.SwitchTo().DefaultContent(); } catch { }
        var result = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return result;
    }

    private static SendResult ClickIncludeOnceAndConfirm(IWebDriver driver, CancellationToken cancellationToken)
    {
        var before = CaptureSnapshot(driver);
        var beforeState = CaptureMatterDraftState(driver);
        if (IsFatalSisbolPage(before))
            return new(false, "O SisBol está em uma página de erro fatal antes da inclusão. Reabra/prepare a sessão e tente novamente.");

        if (!TryClickStrictIncludeOnce(driver, out var diagnostic))
            return new(false, "Não encontrei o controle Incluir dentro do formulário da matéria." +
                              (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : " Controles do formulário: " + diagnostic));

        var alerts = new List<string>();
        var started = DateTime.UtcNow;
        var end = started + TimeSpan.FromSeconds(2.4);
        while (DateTime.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alert = AcceptOneAlert(driver);
            if (!string.IsNullOrWhiteSpace(alert)) alerts.Add(alert);

            var snapshot = CaptureSnapshot(driver);
            if (IsFatalSisbolPage(snapshot))
                return new(false, "O SisBol retornou uma página de erro após o único clique em Incluir. A janela foi mantida visível para conferência.");
            if (snapshot.Saved) return new(true, "Matéria incluída e confirmada no SisBol.");

            if (!string.IsNullOrWhiteSpace(snapshot.Url) && !snapshot.Url.Equals(before.Url, StringComparison.OrdinalIgnoreCase))
            {
                WaitReady(driver, 5);
                var afterNavigation = CaptureSnapshot(driver);
                if (afterNavigation.Saved || Regex.IsMatch(afterNavigation.Url ?? string.Empty, @"[?&]idmilass=", RegexOptions.IgnoreCase))
                    return new(true, "Matéria incluída e confirmada pela nova tela do SisBol.");
            }

            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(0.9))
            {
                var state = CaptureMatterDraftState(driver);
                if (LooksLikeFreshMatterAfterSubmit(beforeState, snapshot, state, alerts))
                    return new(true, "Matéria incluída no SisBol; a página voltou limpa para uma nova nota.");
            }

            if (cancellationToken.WaitHandle.WaitOne(150)) throw new OperationCanceledException(cancellationToken);
        }

        var combined = NormalizeCompact(string.Join(" ", alerts));
        if (new[] { "campo obrigatorio", "obrigatorio", "nao foi possivel", "erro", "falha", "fatal" }.Any(combined.Contains))
            return new(false, "O SisBol recusou a inclusão: " + string.Join(" / ", alerts));

        var finalSnapshot = CaptureSnapshot(driver);
        var finalState = CaptureMatterDraftState(driver);
        if (LooksLikeFreshMatterAfterSubmit(beforeState, finalSnapshot, finalState, alerts))
            return new(true, "Matéria incluída no SisBol; a página voltou limpa para uma nova nota.");

        return new(true, "Matéria incluída no SisBol. Esta versão do sistema não exibiu mensagem de confirmação, mas o único clique em Incluir foi concluído sem erro.");
    }

    private static bool TryClickStrictIncludeOnce(IWebDriver driver, out string diagnostic)
    {
        var labels = new List<string>();
        var attempted = false;

        bool Walk(int depth)
        {
            try
            {
                var form = driver.FindElements(By.CssSelector("form[name='cadMateriaBI'],form#cadMateriaBI")).FirstOrDefault();
                var controls = form is null
                    ? driver.FindElements(By.CssSelector("input[name='btnSalvar'],button[name='btnSalvar'],input[onclick*='salvaMateriaBI'],button[onclick*='salvaMateriaBI']"))
                    : form.FindElements(By.CssSelector("input,button"));

                foreach (var element in controls)
                {
                    var label = NormalizeCompact(Context(element));
                    if (!string.IsNullOrWhiteSpace(label) && labels.Count < 12) labels.Add(label);
                    if (!IsStrictIncludeControl(element)) continue;
                    try { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({block:'center',inline:'center'});", element); } catch { }
                    attempted = true;
                    try { element.Click(); }
                    catch (WebDriverException) { }
                    return true;
                }

                var canInvoke = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    const f=document.cadMateriaBI||document.forms['cadMateriaBI'];
                    return !!f && typeof window.salvaMateriaBI==='function';") is true;
                if (canInvoke)
                {
                    attempted = true;
                    try { ((IJavaScriptExecutor)driver).ExecuteScript("window.salvaMateriaBI('Incluir','','1');"); }
                    catch (WebDriverException) { }
                    return true;
                }
            }
            catch (WebDriverException)
            {
                if (attempted) return true;
            }
            catch { }

            if (attempted || depth >= 5) return attempted;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return false; }
            foreach (var frame in frames)
            {
                var found = false;
                try
                {
                    driver.SwitchTo().Frame(frame);
                    found = Walk(depth + 1);
                }
                catch { found = attempted; }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
                if (found) return true;
            }
            return false;
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        var result = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        diagnostic = string.Join(" | ", labels.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
        return result || attempted;
    }

    private static bool IsStrictIncludeControl(IWebElement element)
    {
        try
        {
            if (!element.Enabled) return false;
            var value = NormalizeSubject(element.GetAttribute("value"));
            var text = NormalizeSubject(element.Text);
            var name = NormalizeSubject(element.GetAttribute("name"));
            var id = NormalizeSubject(element.GetAttribute("id"));
            var onclick = NormalizeSubject(element.GetAttribute("onclick"));
            if (new[] { value, text, name, id, onclick }.Any(item => item.Contains("excluir", StringComparison.Ordinal))) return false;
            return value.Equals("incluir", StringComparison.Ordinal) || text.Equals("incluir", StringComparison.Ordinal) ||
                   name.Equals("btnsalvar", StringComparison.Ordinal) && value.Contains("incluir", StringComparison.Ordinal) ||
                   onclick.Contains("salvamateriabi", StringComparison.Ordinal) && onclick.Contains("incluir", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static SendResult ClickIncludeAndConfirm(IWebDriver driver, CancellationToken cancellationToken)
    {
        var before = CaptureSnapshot(driver);
        var beforeState = CaptureMatterDraftState(driver);
        if (IsFatalSisbolPage(before))
            return new(false, "O SisBol está em uma página de erro fatal antes da inclusão. Reabra/prepare a sessão e tente novamente.");

        string diagnostic = string.Empty;
        var clicked = false;

        // Na página real do SisBol o botão é:
        // <input name="btnSalvar" value="Incluir" onclick="salvaMateriaBI(this.value,'','1')">
        // Chamar essa função diretamente é mais estável do que depender do Selenium
        // clicar visualmente no botão, principalmente com a janela oculta/minimizada.
        try
        {
            var direct = ExecuteInFrames(driver, @"
                try {
                    const forms=Array.from(document.forms||[]);
                    const f=document.cadMateriaBI||document.forms['cadMateriaBI']||forms.find(x=>x && (x.btnSalvar||x.seleParteBi||x.seleSecaoParteBi||x.inputCodAssGeral||x.texto_abert))||forms.find(x=>/cadmateriabi|materia|boletim/.test(String((x.name||'')+' '+(x.id||'')+' '+(x.action||'')).toLowerCase()));
                    if(!f)return {ok:false,diag:'form cadMateriaBI não encontrado'};
                    function fire(el){if(!el)return; ['input','change','keyup','blur'].forEach(n=>{try{el.dispatchEvent(new Event(n,{bubbles:true}))}catch(e){}})}
                    function set(el,v){if(!el||v===null||v===undefined)return false;try{el.value=String(v);try{el.setAttribute('value',String(v))}catch(e){};fire(el);return true}catch(e){return false}}
                    function byNames(names){for(const n of names){const el=(f.elements&&f.elements[n])||f[n]||document.querySelector('[name=\''+n+'\'],#'+n);if(el)return el;}return null;}
                    function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/[ª]/g,'a').replace(/[º]/g,'o').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    function setSelect(sel,wantedValue,wantedText){if(!sel)return false;let opt=null,idx=-1;const want=norm(wantedText);const options=Array.from(sel.options||[]);for(let i=0;i<options.length;i++){const o=options[i];const val=String(o.value||'').trim();const txt=norm((o.text||'')+' '+val);if(txt.includes(want)||(wantedValue&&val===String(wantedValue))){opt=o;idx=i;if(txt.includes(want))break;}}if(!opt)return false;try{for(const o of options){o.selected=false;try{o.removeAttribute('selected')}catch(e){}}opt.selected=true;try{opt.setAttribute('selected','selected')}catch(e){};sel.selectedIndex=idx;sel.value=opt.value;try{sel.setAttribute('value',opt.value)}catch(e){};return String(sel.value||'').trim()===String(opt.value||'').trim()||sel.selectedIndex===idx}catch(e){return false}}
                    // Não chama setaAssuntoGeral aqui: no SisBol essa função limpa texto_abert/texto_fech.
                    try{ if(typeof window.escondeFly==='function') window.escondeFly(); }catch(e){}
                    try{ const fly=document.getElementById('flyframe'); if(fly){fly.style.visibility='hidden'; fly.style.display='none';} }catch(e){}
                    try{ if(typeof window.escondeFly==='function') window.escondeFly(); }catch(e){}
                    if(typeof window.salvaMateriaBI==='function'){
                        window.salvaMateriaBI('Incluir','','1');
                        return {ok:true,method:'salvaMateriaBI'};
                    }
                    const btn=(f.btnSalvar||document.querySelector('[name=btnSalvar]'));
                    if(btn){try{btn.click();return {ok:true,method:'btnSalvar.click'}}catch(e){try{btn.dispatchEvent(new MouseEvent('click',{view:window,bubbles:true,cancelable:true}));return {ok:true,method:'btnSalvar.event'}}catch(_){}}}
                    return {ok:false,diag:'salvaMateriaBI e btnSalvar indisponíveis'};
                } catch(e) { return {ok:false,diag:String(e&&e.message||e)}; }
                ");
            if (direct is IDictionary<string, object> directMap)
            {
                if (directMap.TryGetValue("ok", out var ok) && Convert.ToBoolean(ok, CultureInfo.InvariantCulture))
                    clicked = true;
                else if (directMap.TryGetValue("diag", out var diag))
                    diagnostic = diag?.ToString() ?? string.Empty;
            }
        }
        catch (WebDriverException)
        {
            // A própria navegação/AJAX de salvar pode cortar o retorno do JS.
            clicked = true;
        }
        catch { }

        bool Walk(int depth)
        {
            try
            {
                var result = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim();}
                    function vis(e){if(!e||e.disabled)return false;try{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}catch(_){return false}}
                    const selector='input[type=submit],input[type=button],input[type=image],button,a,img,span[onclick],td[onclick],div[onclick]';
                    const all=Array.from(document.querySelectorAll(selector)).filter(vis);
                    const lab=e=>norm((e.value||'')+' '+(e.innerText||'')+' '+(e.textContent||'')+' '+(e.alt||'')+' '+(e.title||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.src||'')+' '+(e.href||'')+' '+(e.getAttribute&&e.getAttribute('onclick')||''));
                    let e=all.find(x=>/(^|[^a-z])incluir([^a-z]|$)/.test(lab(x))&&!/excluir|remover|cancelar|voltar/.test(lab(x)));
                    if(!e)e=all.find(x=>/inclu/.test(lab(x))&&!/excluir|remover|cancelar|voltar/.test(lab(x)));
                    if(!e)return {ok:false,diag:all.slice(-25).map(lab).filter(Boolean).join(' | ')};
                    if(e.tagName==='IMG'&&e.closest('a,button'))e=e.closest('a,button');
                    const label=lab(e);try{e.scrollIntoView({block:'center',inline:'center'})}catch(_){};try{e.focus()}catch(_){ };
                    try{e.click();return {ok:true,label:label,method:'click'}}catch(_){ };
                    try{e.dispatchEvent(new MouseEvent('click',{view:window,bubbles:true,cancelable:true}));return {ok:true,label:label,method:'event'}}catch(_){ };
                    try{if(e.form&&typeof e.form.requestSubmit==='function'){e.form.requestSubmit(e);return {ok:true,label:label,method:'requestSubmit'}}}catch(_){ };
                    return {ok:false,diag:label};") as IDictionary<string, object>;

                if (result is not null)
                {
                    if (result.TryGetValue("ok", out var ok) && Convert.ToBoolean(ok, CultureInfo.InvariantCulture)) return true;
                    if (string.IsNullOrWhiteSpace(diagnostic) && result.TryGetValue("diag", out var diag)) diagnostic = diag?.ToString() ?? string.Empty;
                }
            }
            catch (WebDriverException)
            {
                return true;
            }
            catch { }

            if (depth >= 5) return false;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return false; }
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    if (Walk(depth + 1)) return true;
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
            return false;
        }

        if (!clicked)
        {
            try { driver.SwitchTo().DefaultContent(); } catch { }
            clicked = Walk(0);
            try { driver.SwitchTo().DefaultContent(); } catch { }
        }

        if (!clicked)
        {
            try
            {
                var fallback = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();}
                    const forms=Array.from(document.forms||[]);
                    const f=forms.find(x=>x && (x.btnSalvar||x.seleParteBi||x.seleSecaoParteBi||x.inputCodAssGeral||x.texto_abert))||forms.find(x=>{const meta=norm((x.action||'')+' '+(x.id||'')+' '+(x.name||''));const hasEditor=!!x.querySelector('textarea,[contenteditable=true],iframe,input[name=texto_abert]');return hasEditor&&(/cadmateriabi|materia|boletim/.test(meta)||forms.length===1)});
                    if(!f)return false;
                    const submit=Array.from(f.querySelectorAll('button,input[type=submit],input[type=button],input[type=image]')).find(e=>/inclu/.test(norm((e.value||'')+' '+(e.innerText||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.title||'')+' '+(e.alt||''))));
                    try{if(typeof window.salvaMateriaBI==='function'){window.salvaMateriaBI('Incluir','','1');return true}}catch(e){};
                    try{if(typeof f.requestSubmit==='function'){f.requestSubmit(submit||undefined);return true}}catch(e){};
                    try{f.submit();return true}catch(e){};return false;");
                clicked = fallback is bool submitted && submitted;
            }
            catch (WebDriverException) { clicked = true; }
            catch { }

            if (!clicked)
            {
                var snapshot = CaptureSnapshot(driver);
                if (snapshot.Saved) return new(true, "Matéria incluída no SisBol.");
                var suffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : " Controles encontrados: " + diagnostic[..Math.Min(420, diagnostic.Length)];
                return new(false, "Não encontrei o botão Incluir na página." + suffix);
            }
        }

        var alerts = new List<string>();
        var started = DateTime.UtcNow;
        var end = started + TimeSpan.FromSeconds(2.8);
        while (DateTime.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alert = AcceptOneAlert(driver);
            if (!string.IsNullOrWhiteSpace(alert)) alerts.Add(alert);

            var snapshot = CaptureSnapshot(driver);
            if (IsFatalSisbolPage(snapshot))
                return new(false, "O SisBol retornou erro fatal ao incluir: Assunto Geral ficou nulo no servidor. O SIGFUR manteve a janela visível; refaça o preparo do SisBol e tente novamente.");
            if (snapshot.Saved) return new(true, "Matéria incluída e confirmada no SisBol.");
            if (!string.IsNullOrWhiteSpace(snapshot.Url) && !snapshot.Url.Equals(before.Url, StringComparison.OrdinalIgnoreCase))
            {
                WaitReady(driver, 5);
                var afterNavigation = CaptureSnapshot(driver);
                if (afterNavigation.Saved || Regex.IsMatch(afterNavigation.Url ?? string.Empty, @"[?&]idmilass=", RegexOptions.IgnoreCase))
                    return new(true, "Matéria incluída e confirmada pela nova tela do SisBol.");
            }

            // O SisBol da OM não exibe sempre mensagem de confirmação. Em várias
            // telas ele simplesmente salva, limpa o editor e volta para uma nova
            // matéria. Esse reset, sem alerta de erro e sem página fatal, é sucesso.
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(0.9))
            {
                var state = CaptureMatterDraftState(driver);
                if (LooksLikeFreshMatterAfterSubmit(beforeState, snapshot, state, alerts))
                    return new(true, "Matéria incluída no SisBol; a página voltou limpa para nova nota.");
            }

            if (cancellationToken.WaitHandle.WaitOne(300)) throw new OperationCanceledException(cancellationToken);
        }

        var combined = NormalizeCompact(string.Join(" ", alerts));
        if (new[] { "campo obrigatorio", "obrigatorio", "nao foi possivel", "erro", "falha", "fatal" }.Any(combined.Contains))
            return new(false, "O SisBol recusou a inclusão: " + string.Join(" / ", alerts));

        var finalSnapshot = CaptureSnapshot(driver);
        var finalState = CaptureMatterDraftState(driver);
        if (LooksLikeFreshMatterAfterSubmit(beforeState, finalSnapshot, finalState, alerts))
            return new(true, "Matéria incluída no SisBol; a página voltou limpa para nova nota.");

        return new(true, "Matéria enviada ao SisBol; não houve alerta de erro após o acionamento único do botão Incluir.");
    }

    private static PageSnapshot CaptureSnapshot(IWebDriver driver)
    {
        try
        {
            var url = driver.Url ?? string.Empty;
            var data = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                function n(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase()}catch(e){return String(s||'').toLowerCase()}}
                const body=n(document.body?document.body.innerText:'');
                const controls=Array.from(document.querySelectorAll('input,button,a')).map(e=>n((e.value||'')+' '+(e.innerText||'')+' '+(e.textContent||'')+' '+(e.alt||'')+' '+(e.title||'')+' '+(e.src||'')+' '+(e.href||''))).join(' | ');
                return {body:body.slice(0,30000),controls:controls.slice(0,10000)};") as IDictionary<string, object>;
            var body = NormalizeCompact(data is not null && data.TryGetValue("body", out var bodyValue) ? bodyValue?.ToString() : string.Empty);
            var controls = NormalizeCompact(data is not null && data.TryGetValue("controls", out var controlsValue) ? controlsValue?.ToString() : string.Empty);
            var idMatch = Regex.Match(url, @"[?&]idmilass=([^&#]+)", RegexOptions.IgnoreCase);
            var id = idMatch.Success ? idMatch.Groups[1].Value.Trim() : string.Empty;
            var fatal = body.Contains("fatal error") || body.Contains("call to a member function") || body.Contains("erro fatal");
            var idSaved = !fatal && !string.IsNullOrWhiteSpace(id) && id is not "0" and not "00" and not "none" and not "null";
            var editControls = !fatal && controls.Contains("alterar") && controls.Contains("excluir");
            var noteMarker = body.Contains("nota n") && body.Contains("nova nota") && editControls;
            var successText = Regex.IsMatch(body, "materia (incluida|cadastrada|salva)|inclusao realizada|operacao realizada|registro incluido|alteracao realizada", RegexOptions.IgnoreCase);
            return new(url, body, !fatal && (idSaved || editControls || noteMarker || successText));
        }
        catch { return new(string.Empty, string.Empty, false); }
    }

    private static MatterDraftState CaptureMatterDraftState(IWebDriver driver)
    {
        MatterDraftState best = new(false, false, false, 0, string.Empty, string.Empty, string.Empty);

        MatterDraftState Walk(int depth)
        {
            try
            {
                var data = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                    function stripHtml(value){const raw=String(value||''); if(!/[<>]/.test(raw))return raw; try{const box=document.createElement('div');box.innerHTML=raw;return box.innerText||box.textContent||raw}catch(e){return raw}}
                    function norm(s){try{return String(stripHtml(s)||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(stripHtml(s)||'').toLowerCase().replace(/\s+/g,' ').trim()}}
                    const body=norm(document.body?document.body.innerText:'');
                    const f=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>/cadmateriabi|materia|boletim/.test(norm((x.name||'')+' '+(x.id||'')+' '+(x.action||''))));
                    function val(names){for(const name of names){const e=(f&&((f.elements&&f.elements[name])||f[name]))||document.querySelector('[name=\''+name+'\'],#'+name); if(e){const v=String(e.value||e.innerHTML||e.innerText||e.textContent||''); if(v.trim()) return v;}} return '';}
                    let txt=val(['texto_abert','texto_aberto','texto','materia','conteudo','txtMateria','descricao']);
                    try{
                        if(!txt && window.FCKeditorAPI&&typeof window.FCKeditorAPI.GetInstance==='function'){
                            const ed=window.FCKeditorAPI.GetInstance('texto_abert')||window.FCKeditorAPI.GetInstance('texto')||window.FCKeditorAPI.GetInstance('materia');
                            if(ed){try{txt=ed.GetXHTML(true)}catch(e){try{txt=ed.GetHTML()}catch(_){}}}
                        }
                    }catch(e){}
                    try{ if(!txt && window.CKEDITOR&&CKEDITOR.instances){for(const k of Object.keys(CKEDITOR.instances)){txt=CKEDITOR.instances[k].getData(); if(txt)break;}} }catch(e){}
                    const controls=Array.from(document.querySelectorAll('input,button,a,img,span[onclick],td[onclick],div[onclick]')).map(e=>norm((e.value||'')+' '+(e.innerText||'')+' '+(e.textContent||'')+' '+(e.alt||'')+' '+(e.title||'')+' '+(e.name||'')+' '+(e.id||'')+' '+(e.src||'')+' '+(e.href||'')+' '+(e.getAttribute&&e.getAttribute('onclick')||''))).join(' | ');
                    const hasMatterForm=!!f || /lancar nota para|texto de abertura|materia bi|cadmateriabi/.test(body+controls);
                    const hasInclude=/(^|[^a-z])incluir([^a-z]|$)|salvamateriabi|btnsalvar/.test(controls) && !/excluir beneficio/.test(controls);
                    const hasNewNote=/nova nota|novamateria/.test(controls+body);
                    const gCode=String(val(['inputCodAssGeral','codAssuntoGeral','codAssGeral','assuntoGeralCodigo','idAssuntoGeral','idtAssuntoGeral'])||'');
                    const gText=String(val(['inputAssuntoGeral','assuntoGeral','descAssuntoGeral','descricaoAssuntoGeral'])||'');
                    return {hasMatterForm:hasMatterForm,hasInclude:hasInclude,hasNewNote:hasNewNote,textLength:norm(txt).length,generalCode:gCode,generalText:gText,url:String(location.href||'')};") as IDictionary<string, object>;

                if (data is not null)
                {
                    var state = new MatterDraftState(
                        data.TryGetValue("hasMatterForm", out var hasForm) && Convert.ToBoolean(hasForm, CultureInfo.InvariantCulture),
                        data.TryGetValue("hasInclude", out var hasInclude) && Convert.ToBoolean(hasInclude, CultureInfo.InvariantCulture),
                        data.TryGetValue("hasNewNote", out var hasNewNote) && Convert.ToBoolean(hasNewNote, CultureInfo.InvariantCulture),
                        data.TryGetValue("textLength", out var len) ? Convert.ToInt32(len, CultureInfo.InvariantCulture) : 0,
                        data.TryGetValue("generalCode", out var gc) ? gc?.ToString() ?? string.Empty : string.Empty,
                        data.TryGetValue("generalText", out var gt) ? gt?.ToString() ?? string.Empty : string.Empty,
                        data.TryGetValue("url", out var url) ? url?.ToString() ?? string.Empty : string.Empty);
                    if (state.HasMatterForm && state.HasIncludeButton) return state;
                    if (state.TextLength > best.TextLength || (state.HasMatterForm && !best.HasMatterForm)) best = state;
                }
            }
            catch { }

            if (depth >= 5) return best;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return best; }
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    var state = Walk(depth + 1);
                    if (state.HasMatterForm && state.HasIncludeButton) return state;
                    if (state.TextLength > best.TextLength || (state.HasMatterForm && !best.HasMatterForm)) best = state;
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
            return best;
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        var result = Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return result;
    }

    private static bool LooksLikeFreshMatterAfterSubmit(MatterDraftState beforeState, PageSnapshot snapshot, MatterDraftState state, IReadOnlyList<string> alerts)
    {
        if (IsFatalSisbolPage(snapshot)) return false;
        var alertText = NormalizeCompact(string.Join(" ", alerts));
        if (new[] { "campo obrigatorio", "obrigatorio", "nao foi possivel", "erro", "falha", "fatal" }.Any(alertText.Contains)) return false;
        if (!state.HasMatterForm) return false;
        if (!state.HasIncludeButton && !state.HasNewNote) return false;

        var body = NormalizeCompact(snapshot.Body);
        var isMatterScreen = body.Contains("lancar nota para") || body.Contains("texto de abertura") || body.Contains("materia bi") || (state.Url ?? string.Empty).Contains("cadmateriabi", StringComparison.OrdinalIgnoreCase);
        if (!isMatterScreen) return false;

        var beforeHadText = beforeState.TextLength >= 12;
        var textCleared = state.TextLength <= Math.Max(8, beforeState.TextLength / 12);
        var general = NormalizeCompact((state.GeneralCode ?? string.Empty) + " " + (state.GeneralText ?? string.Empty));
        var subjectClearedOrBlank = string.IsNullOrWhiteSpace(general) || (!general.Contains("1077") && !general.Contains("pagamento pessoal"));

        // Sinal mais comum do SisBol: salvou e abriu automaticamente um cadastro novo.
        if (beforeHadText && textCleared && (state.HasNewNote || state.HasIncludeButton) && subjectClearedOrBlank)
            return true;

        // Algumas versões mantêm Assunto Geral na tela nova, mas limpam o editor.
        if (beforeHadText && textCleared && state.HasNewNote && state.HasIncludeButton)
            return true;

        return false;
    }

    private static string AcceptOneAlert(IWebDriver driver)
    {
        try
        {
            var alert = driver.SwitchTo().Alert();
            var text = alert.Text ?? string.Empty;
            alert.Accept();
            return text;
        }
        catch { return string.Empty; }
    }

    private static string WaitForAndAcceptAlert(IWebDriver driver, TimeSpan timeout)
    {
        var limit = DateTime.UtcNow + timeout;
        var lastText = string.Empty;
        while (DateTime.UtcNow <= limit)
        {
            var text = AcceptOneAlert(driver);
            if (!string.IsNullOrWhiteSpace(text))
            {
                lastText = text;
                // Alguns fluxos do SisBol podem abrir mais de um alerta em sequência.
                Thread.Sleep(250);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(lastText)) return lastText;
            Thread.Sleep(200);
        }
        return lastText;
    }

    private static bool LooksLikeLoginOrCaptcha(IWebDriver driver)
    {
        try
        {
            var url = (driver.Url ?? string.Empty).ToLowerInvariant();
            // A página sisbol.php também pode ser o menu inicial após login.
            // A versão anterior tratava qualquer sisbol.php como login e deixava
            // a preparação lenta/instável; agora a decisão vem dos campos reais.
            var result = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();}
                const body=norm(document.body?document.body.innerText:'');
                const inputs=Array.from(document.querySelectorAll('input'));
                const meta=e=>norm((e.name||'')+' '+(e.id||'')+' '+(e.placeholder||'')+' '+(e.title||''));
                const pass=inputs.some(i=>String(i.type||'').toLowerCase()==='password');
                const cap=inputs.some(i=>/captcha|caracter|codigo|seguranca/.test(meta(i)))||/captcha|caracteres|codigo de seguranca/.test(body);
                return Boolean(pass&&(cap||/login|usuario|senha|entrar/.test(body)));");
            return result is bool flag && flag;
        }
        catch { return false; }
    }

    private static void ResetDiagnosticStorage(IWebDriver driver, string sessionId)
    {
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                try {
                    localStorage.setItem('SIGFUR_SISBOL_DIAG_SESSION', arguments[0] || '');
                    localStorage.setItem('SIGFUR_SISBOL_DIAG_EVENTS', '[]');
                } catch(e) { window.__sigfurDiagEventsFallback = []; }
                return true;
                """, sessionId);
        }
        catch { }
    }

    private static void InjectDiagnosticRecorder(IWebDriver driver, string sessionId)
    {
        ExecuteInEveryFrame(driver, """
            (function(sessionId){
                try{
                    if(window.__sigfurDiagRecorderInstalled === sessionId) return {ok:true,already:true};
                    window.__sigfurDiagRecorderInstalled = sessionId;
                    const KEY='SIGFUR_SISBOL_DIAG_EVENTS';
                    const SESSION='SIGFUR_SISBOL_DIAG_SESSION';
                    try{localStorage.setItem(SESSION, sessionId||'')}catch(_){ }
                    function read(){
                        try{const raw=localStorage.getItem(KEY)||'[]'; const arr=JSON.parse(raw); return Array.isArray(arr)?arr:[];}catch(e){return window.__sigfurDiagEventsFallback||[];}
                    }
                    function write(arr){
                        const cut=arr.slice(-2200);
                        try{localStorage.setItem(KEY, JSON.stringify(cut));}catch(e){window.__sigfurDiagEventsFallback=cut;}
                    }
                    function now(){try{return new Date().toISOString()}catch(_){return String(Date.now())}}
                    function norm(s){try{return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/\s+/g,' ').trim()}catch(e){return String(s||'').toLowerCase().trim()}}
                    function isProtected(el){const s=norm((el&&el.type||'')+' '+(el&&el.id||'')+' '+(el&&el.name||'')+' '+(el&&el.placeholder||'')+' '+(el&&el.autocomplete||''));return /password|senha|captcha|caracter|seguranca|token|csrf|auth/.test(s)}
                    function safeValue(el){
                        if(!el) return '';
                        if(isProtected(el)) return '[PROTEGIDO]';
                        try{
                            const tag=String(el.tagName||'').toLowerCase();
                            let v='';
                            if(tag==='select'){
                                const opt=el.options&&el.selectedIndex>=0?el.options[el.selectedIndex]:null;
                                v=String((opt&&((opt.text||'')+' | '+(opt.value||'')))||el.value||'');
                            } else if('value' in el) v=String(el.value||'');
                            else if(el.isContentEditable) v=String(el.innerText||el.textContent||'');
                            if(v.length>1400) v=v.slice(0,1400)+'...[cortado]';
                            return v;
                        }catch(e){return ''}
                    }
                    function shortText(s, max){s=String(s||'').replace(/\s+/g,' ').trim(); return s.length>max?s.slice(0,max)+'...[cortado]':s;}
                    function cssEscape(v){try{return CSS.escape(String(v||''))}catch(e){return String(v||'').replace(/([ #;?%&,.+*~\':"!^$[\]()=>|/@])/g,'\\$1')}}
                    function cssPath(el){
                        try{
                            if(!el||!el.tagName) return '';
                            if(el.id) return '#'+cssEscape(el.id);
                            if(el.name) return el.tagName.toLowerCase()+'[name="'+String(el.name).replace(/"/g,'\\"')+'"]';
                            const parts=[]; let node=el;
                            while(node&&node.nodeType===1&&parts.length<8){
                                let part=node.tagName.toLowerCase();
                                if(node.id){part+='#'+cssEscape(node.id); parts.unshift(part); break;}
                                let index=1, sib=node;
                                while((sib=sib.previousElementSibling)){if(sib.tagName===node.tagName)index++;}
                                part+=':nth-of-type('+index+')'; parts.unshift(part); node=node.parentElement;
                            }
                            return parts.join(' > ');
                        }catch(e){return ''}
                    }
                    function xpath(el){
                        try{
                            if(!el||!el.tagName) return '';
                            if(el.id) return '//*[@id="'+String(el.id).replace(/"/g,'\\"')+'"]';
                            const parts=[]; let node=el;
                            while(node&&node.nodeType===1){
                                let ix=1, sib=node.previousSibling;
                                while(sib){if(sib.nodeType===1&&sib.nodeName===node.nodeName)ix++; sib=sib.previousSibling;}
                                parts.unshift(node.nodeName.toLowerCase()+'['+ix+']'); node=node.parentNode;
                            }
                            return '/'+parts.join('/');
                        }catch(e){return ''}
                    }
                    function labelText(el){
                        try{
                            const id=el&&el.id;
                            if(id){const l=document.querySelector('label[for="'+cssEscape(id)+'"]'); if(l&&shortText(l.innerText,160)) return shortText(l.innerText,160);}
                            const label=el&&el.closest&&el.closest('label'); if(label&&shortText(label.innerText,160)) return shortText(label.innerText,160);
                            const row=el&&el.closest&&el.closest('tr'); if(row&&shortText(row.innerText,280)) return shortText(row.innerText,280);
                            const cell=el&&el.closest&&el.closest('td,div,fieldset'); if(cell&&shortText(cell.innerText,220)) return shortText(cell.innerText,220);
                        }catch(e){}
                        return '';
                    }
                    function outer(el){try{let h=String(el&&el.outerHTML||''); h=h.replace(/value=(['\"]).*?\1/gi,'value="[omitido]"'); return shortText(h,1800);}catch(e){return ''}}
                    function optionList(el){
                        try{
                            if(!el||String(el.tagName||'').toLowerCase()!=='select') return [];
                            return Array.from(el.options||[]).slice(0,120).map(o=>({text:shortText(o.text,180),value:String(o.value||''),selected:!!o.selected}));
                        }catch(e){return []}
                    }
                    function snap(el){
                        try{
                            if(!el) el=document.activeElement;
                            const r=el&&el.getBoundingClientRect?el.getBoundingClientRect():null;
                            return {
                                tag:String(el&&el.tagName||''), id:String(el&&el.id||''), name:String(el&&el.name||''), className:shortText(String(el&&el.className||''),260),
                                type:String(el&&el.type||''), role:String(el&&el.getAttribute&&el.getAttribute('role')||''), title:String(el&&el.title||''), placeholder:String(el&&el.placeholder||''),
                                text:shortText(String((el&&el.innerText)||(el&&el.textContent)||(el&&el.value)||''),420), value:safeValue(el), label:labelText(el),
                                css:cssPath(el), xpath:xpath(el), onclick:shortText(String(el&&el.getAttribute&&el.getAttribute('onclick')||''),600),
                                href:shortText(String(el&&el.href||''),600), options:optionList(el),
                                rect:r?{x:Math.round(r.x),y:Math.round(r.y),w:Math.round(r.width),h:Math.round(r.height)}:null,
                                outerHtml:outer(el)
                            };
                        }catch(e){return {erro:String(e&&e.message||e)}}
                    }
                    function record(eventName, ev, extra){
                        try{
                            const arr=read();
                            const target=(ev&&ev.target)||document.activeElement;
                            const item={
                                seq:arr.length+1, session:sessionId, when:now(), event:eventName,
                                url:String(location.href||''), title:String(document.title||''), frameName:String(window.name||''),
                                key:ev&&ev.key?String(ev.key):'', ctrl:!!(ev&&ev.ctrlKey), alt:!!(ev&&ev.altKey), shift:!!(ev&&ev.shiftKey), button:ev&&typeof ev.button==='number'?ev.button:null,
                                target:snap(target), active:snap(document.activeElement), extra:extra||null
                            };
                            arr.push(item); write(arr);
                        }catch(e){}
                    }
                    window.__sigfurDiagRecordManual=function(action,note,value){record('sigfur:'+String(action||'manual'), null, {manual:true,note:String(note||''),value:String(value||'')}); return true;};
                    document.addEventListener('click', e=>record('click',e,null), true);
                    document.addEventListener('dblclick', e=>record('dblclick',e,null), true);
                    document.addEventListener('focus', e=>record('focus',e,null), true);
                    document.addEventListener('change', e=>record('change',e,null), true);
                    document.addEventListener('paste', e=>record('paste',e,{clipboard:'evento paste detectado'}), true);
                    document.addEventListener('submit', e=>record('submit',e,{form:true}), true);
                    document.addEventListener('keydown', e=>{if(e.key==='Enter'||e.key==='Tab'||e.key==='Escape'||(e.ctrlKey&&String(e.key||'').toLowerCase()==='v')||(e.ctrlKey&&String(e.key||'').toLowerCase()==='f'))record('keydown',e,null)}, true);
                    let inputTimer=null;
                    document.addEventListener('input', e=>{clearTimeout(inputTimer); inputTimer=setTimeout(()=>record('input',e,{debounced:true}),220)}, true);
                    window.addEventListener('beforeunload', e=>record('beforeunload',e,{navigation:true}), true);
                    record('recorder_installed', null, {href:String(location.href||'')});
                    return {ok:true,installed:true,url:String(location.href||'')};
                }catch(e){return {ok:false,error:String(e&&e.message||e)}}
            })(arguments[0]);
            """, sessionId);
    }

    private static string InsertTextInActiveElement(IWebDriver driver, string label, string text)
    {
        var result = ExecuteInFrames(driver, """
            try{
                const label=arguments[0]||'', text=arguments[1]||'';
                function fire(el){['beforeinput','input','change','keyup','blur'].forEach(n=>{try{el.dispatchEvent(new Event(n,{bubbles:true,cancelable:true}))}catch(e){}})}
                function htmlEscape(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\n/g,'<br>')}
                function insertText(el, value){
                    if(!el) return {ok:false,reason:'sem elemento ativo'};
                    const tag=String(el.tagName||'').toLowerCase();
                    try{el.focus()}catch(e){}
                    if(tag==='input'||tag==='textarea'){
                        const start=typeof el.selectionStart==='number'?el.selectionStart:String(el.value||'').length;
                        const end=typeof el.selectionEnd==='number'?el.selectionEnd:start;
                        const old=String(el.value||'');
                        el.value=old.slice(0,start)+value+old.slice(end);
                        try{el.selectionStart=el.selectionEnd=start+value.length}catch(e){}
                        fire(el);
                        return {ok:true,mode:'value',tag:tag,id:el.id||'',name:el.name||'',label:label,value:el.value||''};
                    }
                    if(el.isContentEditable || tag==='body'){
                        try{document.execCommand('insertText', false, value); fire(el); return {ok:true,mode:'execCommand',tag:tag,id:el.id||'',name:el.getAttribute('name')||'',label:label,value:value};}catch(e){}
                        try{el.innerHTML=(el.innerHTML||'')+htmlEscape(value); fire(el); return {ok:true,mode:'innerHTML',tag:tag,id:el.id||'',name:el.getAttribute('name')||'',label:label,value:value};}catch(e){}
                    }
                    return {ok:false,reason:'elemento ativo não aceita texto',tag:tag,id:el.id||'',name:el.getAttribute&&el.getAttribute('name')||'',label:label};
                }

                let response=insertText(document.activeElement, text);

                // Quando o usuário clica no iframe do FCKEditor, o elemento ativo no topo
                // pode ser apenas o <iframe>. Nesse caso, entra no editor de abertura.
                if(!response.ok){
                    const active=document.activeElement;
                    if(active && String(active.tagName||'').toLowerCase()==='iframe'){
                        try{
                            const d=active.contentDocument||active.contentWindow&&active.contentWindow.document;
                            const b=d&&d.body;
                            if(b) response=insertText(b,text);
                        }catch(e){}
                    }
                }

                // Fallback específico do FCKEditor 2: se existir texto_abert, cola nele.
                if(!response.ok && /corpo|boletim|texto|abert/i.test(label)){
                    try{
                        if(window.FCKeditorAPI&&typeof window.FCKeditorAPI.GetInstance==='function'){
                            const ed=window.FCKeditorAPI.GetInstance('texto_abert');
                            if(ed){
                                try{ed.SetHTML(htmlEscape(text))}catch(e){}
                                try{if(ed.EditorDocument&&ed.EditorDocument.body)ed.EditorDocument.body.innerHTML=htmlEscape(text)}catch(e){}
                                try{if(typeof ed.UpdateLinkedField==='function')ed.UpdateLinkedField()}catch(e){}
                                response={ok:true,mode:'FCKeditorAPI',tag:'FCKEDITOR',id:'texto_abert',name:'texto_abert',label:label,value:text};
                            }
                        }
                    }catch(e){}
                    try{
                        const f=document.cadMateriaBI||document.forms['cadMateriaBI'];
                        if(!response.ok && f && f.texto_abert){f.texto_abert.value=htmlEscape(text); response={ok:true,mode:'hidden_texto_abert',tag:'INPUT',id:'texto_abert',name:'texto_abert',label:label,value:text};}
                    }catch(e){}
                }

                try{if(window.__sigfurDiagRecordManual) window.__sigfurDiagRecordManual('colar_'+label, JSON.stringify(response), text);}catch(e){}
                return response;
            }catch(e){return {ok:false,reason:String(e&&e.message||e)}}
            """, label, text);

        if (result is IDictionary<string, object> map)
        {
            var ok = map.TryGetValue("ok", out var okValue) && Convert.ToBoolean(okValue, CultureInfo.InvariantCulture);
            var tag = map.TryGetValue("tag", out var tagValue) ? tagValue?.ToString() : string.Empty;
            var id = map.TryGetValue("id", out var idValue) ? idValue?.ToString() : string.Empty;
            var name = map.TryGetValue("name", out var nameValue) ? nameValue?.ToString() : string.Empty;
            var reason = map.TryGetValue("reason", out var reasonValue) ? reasonValue?.ToString() : string.Empty;
            return ok
                ? $"Marcador ‘{label}’ colado no elemento ativo ({tag} id='{id}' name='{name}')."
                : $"Não consegui colar o marcador ‘{label}’: {reason}";
        }
        return $"Não consegui confirmar a colagem do marcador ‘{label}’. Confira o campo no navegador.";
    }

    private static void RecordDiagnosticManualEvent(IWebDriver driver, string action, string note, string value)
    {
        try
        {
            driver.SwitchTo().DefaultContent();
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                try{
                    if(window.__sigfurDiagRecordManual) return window.__sigfurDiagRecordManual(arguments[0], arguments[1], arguments[2] || '');
                    return false;
                }catch(e){return false;}
                """, action, note, value ?? string.Empty);
        }
        catch { }
        finally { try { driver.SwitchTo().DefaultContent(); } catch { } }
    }

    private static void SaveDiagnosticQuickFiles(IWebDriver driver, string folder, string prefix)
    {
        Directory.CreateDirectory(folder);
        try
        {
            if (driver is ITakesScreenshot shooter)
            {
                var path = Path.Combine(folder, prefix + "_print.png");
                shooter.GetScreenshot().SaveAsFile(path);
            }
        }
        catch { }

        try
        {
            File.WriteAllText(Path.Combine(folder, prefix + "_pagina_topo.html"), SanitizeDiagnosticHtml(driver.PageSource ?? string.Empty), Encoding.UTF8);
        }
        catch { }
    }

    private static void SaveDiagnosticPackage(IWebDriver driver, string folder, string sessionId)
    {
        Directory.CreateDirectory(folder);
        var eventsJson = TryReadDiagnosticEventsJson(driver);
        File.WriteAllText(Path.Combine(folder, "sisbol_fluxo_eventos.json"), PrettyJson(eventsJson), Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, "sisbol_fluxo_resumo.txt"), BuildDiagnosticTextSummary(eventsJson), Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, "LEIA-ME.txt"), BuildDiagnosticReadme(sessionId), Encoding.UTF8);

        SaveDiagnosticQuickFiles(driver, folder, "final");

        var framesFolder = Path.Combine(folder, "html_frames");
        Directory.CreateDirectory(framesFolder);
        var frameSnapshots = CaptureDiagnosticFrames(driver).ToList();
        var frameIndex = new List<Dictionary<string, string>>();
        foreach (var frame in frameSnapshots)
        {
            var safe = frame.Index.ToString("000", CultureInfo.InvariantCulture) + "_" + SafeFileName(frame.FramePath) + ".html";
            var htmlPath = Path.Combine(framesFolder, safe);
            File.WriteAllText(htmlPath, SanitizeDiagnosticHtml(frame.Html), Encoding.UTF8);
            var summaryPath = Path.Combine(framesFolder, Path.ChangeExtension(safe, ".summary.json"));
            File.WriteAllText(summaryPath, PrettyJson(frame.SummaryJson), Encoding.UTF8);
            frameIndex.Add(new Dictionary<string, string>
            {
                ["index"] = frame.Index.ToString(CultureInfo.InvariantCulture),
                ["framePath"] = frame.FramePath,
                ["url"] = frame.Url,
                ["title"] = frame.Title,
                ["htmlFile"] = Path.Combine("html_frames", safe),
                ["summaryFile"] = Path.Combine("html_frames", Path.GetFileName(summaryPath))
            });
        }

        File.WriteAllText(
            Path.Combine(folder, "sisbol_frames_indice.json"),
            JsonSerializer.Serialize(frameIndex, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static string TryReadDiagnosticEventsJson(IWebDriver driver)
    {
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                try{return localStorage.getItem('SIGFUR_SISBOL_DIAG_EVENTS') || '[]';}
                catch(e){try{return JSON.stringify(window.__sigfurDiagEventsFallback||[]);}catch(_){return '[]';}}
                """)?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? "[]" : raw!;
        }
        catch { return "[]"; }
    }

    private static string PrettyJson(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "[]" : rawJson);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return rawJson; }
    }

    private static string BuildDiagnosticTextSummary(string eventsJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SIGFUR — RESUMO DO FLUXO SISBOL");
        sb.AppendLine("Gerado em: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("Sequência capturada:");
        sb.AppendLine();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(eventsJson) ? "[]" : eventsJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string Get(string name) => item.TryGetProperty(name, out var p) ? p.ToString() : string.Empty;
                var target = item.TryGetProperty("target", out var t) ? t : default;
                string T(string name) => target.ValueKind == JsonValueKind.Object && target.TryGetProperty(name, out var p) ? p.ToString() : string.Empty;
                sb.Append('[').Append(Get("seq")).Append("] ").Append(Get("when")).Append(" — ").Append(Get("event")).AppendLine();
                sb.Append("    URL: ").AppendLine(Get("url"));
                sb.Append("    Elemento: ").Append(T("tag")).Append(" id='").Append(T("id")).Append("' name='").Append(T("name")).Append("'").AppendLine();
                if (!string.IsNullOrWhiteSpace(T("label"))) sb.Append("    Texto/linha próxima: ").AppendLine(T("label"));
                if (!string.IsNullOrWhiteSpace(T("value"))) sb.Append("    Valor: ").AppendLine(T("value"));
                if (!string.IsNullOrWhiteSpace(T("css"))) sb.Append("    CSS: ").AppendLine(T("css"));
                if (!string.IsNullOrWhiteSpace(T("xpath"))) sb.Append("    XPath: ").AppendLine(T("xpath"));
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Não foi possível resumir o JSON automaticamente: " + ex.Message);
            sb.AppendLine(eventsJson);
        }
        return sb.ToString();
    }

    private static string BuildDiagnosticReadme(string sessionId)
        => """
        SIGFUR — PACOTE DE DIAGNÓSTICO SISBOL

        Este pacote foi gerado para corrigir a automação do lançamento no SisBol sem depender de chute por coordenada.
        Ele não contém senha nem captcha: campos de senha, captcha e token são mascarados como [PROTEGIDO].

        Arquivos principais:
        - sisbol_fluxo_eventos.json: sequência completa dos cliques, focos, colagens, mudanças de campo e tentativa de envio.
        - sisbol_fluxo_resumo.txt: leitura humana do passo a passo.
        - final_print.png: print da tela no momento da geração do pacote.
        - final_pagina_topo.html: HTML do frame principal no momento final.
        - html_frames/: HTML e resumo técnico de cada frame/iframe localizado.
        - sisbol_frames_indice.json: índice dos frames salvos.

        Como usar:
        1. Envie o ZIP gerado para análise.
        2. Não edite manualmente os JSON/HTML antes de enviar, para preservar os seletores reais.
        3. Se o botão Incluir foi clicado manualmente, confira se a ação foi autorizada no ambiente usado.

        Sessão: 
        """ + sessionId + Environment.NewLine;

    private static IEnumerable<DiagnosticFrameSnapshot> CaptureDiagnosticFrames(IWebDriver driver)
    {
        var list = new List<DiagnosticFrameSnapshot>();
        var index = 0;

        void Walk(string path, int depth)
        {
            try
            {
                var url = string.Empty;
                var title = string.Empty;
                var html = string.Empty;
                var summary = "{}";
                try { url = ((IJavaScriptExecutor)driver).ExecuteScript("try{return location.href}catch(e){return ''}")?.ToString() ?? string.Empty; } catch { }
                try { title = ((IJavaScriptExecutor)driver).ExecuteScript("try{return document.title}catch(e){return ''}")?.ToString() ?? string.Empty; } catch { }
                try { html = driver.PageSource ?? string.Empty; } catch { }
                try { summary = ((IJavaScriptExecutor)driver).ExecuteScript(DiagnosticFormSummaryScript)?.ToString() ?? "{}"; } catch { }
                list.Add(new DiagnosticFrameSnapshot(index++, path, url, title, html, summary));
            }
            catch { }

            if (depth >= 5) return;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return; }
            var frameList = frames.ToList();
            for (var i = 0; i < frameList.Count; i++)
            {
                try
                {
                    driver.SwitchTo().Frame(frameList[i]);
                    Walk(path + "/frame[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1);
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        Walk("top", 0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return list;
    }

    private const string DiagnosticFormSummaryScript = """
        try{
            function short(s,max){s=String(s||'').replace(/\s+/g,' ').trim();return s.length>max?s.slice(0,max)+'...[cortado]':s;}
            function cssEscape(v){try{return CSS.escape(String(v||''))}catch(e){return String(v||'').replace(/([ #;?%&,.+*~\':"!^$[\]()=>|/@])/g,'\\$1')}}
            function cssPath(el){try{if(!el||!el.tagName)return''; if(el.id)return '#'+cssEscape(el.id); if(el.name)return el.tagName.toLowerCase()+'[name="'+String(el.name).replace(/"/g,'\\"')+'"]'; let parts=[],n=el; while(n&&n.nodeType===1&&parts.length<8){let p=n.tagName.toLowerCase(); if(n.id){p+='#'+cssEscape(n.id); parts.unshift(p);break;} let ix=1,s=n; while((s=s.previousElementSibling)){if(s.tagName===n.tagName)ix++;} p+=':nth-of-type('+ix+')'; parts.unshift(p); n=n.parentElement;} return parts.join(' > ');}catch(e){return''}}
            function xpath(el){try{if(!el||!el.tagName)return''; if(el.id)return '//*[@id="'+String(el.id).replace(/"/g,'\\"')+'"]'; const parts=[]; let n=el; while(n&&n.nodeType===1){let ix=1,s=n.previousSibling; while(s){if(s.nodeType===1&&s.nodeName===n.nodeName)ix++; s=s.previousSibling;} parts.unshift(n.nodeName.toLowerCase()+'['+ix+']'); n=n.parentNode;} return '/'+parts.join('/');}catch(e){return''}}
            function item(el){return {tag:String(el.tagName||''),id:String(el.id||''),name:String(el.name||''),type:String(el.type||''),value:/(password|senha|captcha|token|csrf)/i.test((el.type||'')+' '+(el.id||'')+' '+(el.name||''))?'[PROTEGIDO]':short(el.value||el.innerText||el.textContent||'',500),text:short(el.innerText||el.textContent||'',500),onclick:short(el.getAttribute&&el.getAttribute('onclick')||'',600),css:cssPath(el),xpath:xpath(el)};}
            const forms=Array.from(document.forms||[]).map(f=>({id:f.id||'',name:f.name||'',action:f.action||'',method:f.method||'',elements:Array.from(f.elements||[]).map(item)}));
            const controls=Array.from(document.querySelectorAll('input,textarea,select,button,a,[contenteditable=true]')).map(item);
            const scripts=Array.from(document.scripts||[]).map(s=>({src:s.src||'',text:short(s.textContent||'',1000)})).filter(x=>x.src||x.text);
            return JSON.stringify({url:location.href,title:document.title,forms:forms,controls:controls,scripts:scripts},null,2);
        }catch(e){return JSON.stringify({error:String(e&&e.message||e)});}
        """;

    private static void ExecuteInEveryFrame(IWebDriver driver, string script, params object[] args)
    {
        void Walk(int depth)
        {
            try { ((IJavaScriptExecutor)driver).ExecuteScript(script, args); } catch { }
            if (depth >= 5) return;
            IReadOnlyCollection<IWebElement> frames;
            try { frames = driver.FindElements(By.CssSelector("iframe,frame")); }
            catch { return; }
            foreach (var frame in frames.ToList())
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    Walk(depth + 1);
                }
                catch { }
                finally
                {
                    try { driver.SwitchTo().ParentFrame(); }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        Walk(0);
        try { driver.SwitchTo().DefaultContent(); } catch { }
    }


    private static string ResolveSisbolBaseUrl(IWebDriver driver)
    {
        // O SisBol do quartel costuma autenticar pelo IP interno 10.122.8.31.
        // Se depois a automação trocar para sisbol.4ciape.eb.mil.br, o cookie não acompanha
        // e a página de download volta para login/captcha. Por isso o downloader deve usar
        // exatamente o mesmo host da sessão já preparada no Selenium.
        try
        {
            var current = driver.Url ?? string.Empty;
            if (Uri.TryCreate(current, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var host = uri.Host.Trim();
                if (host.Equals("10.122.8.31", StringComparison.OrdinalIgnoreCase) ||
                    host.Contains("sisbol", StringComparison.OrdinalIgnoreCase) ||
                    host.Contains("4ciape", StringComparison.OrdinalIgnoreCase))
                {
                    var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
                    return $"{uri.Scheme}://{host}{port}";
                }
            }
        }
        catch { }
        return "https://10.122.8.31";
    }

    private static HttpClient BuildAuthenticatedSisbolClient(IWebDriver driver, string sisbolBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(sisbolBaseUrl) ? "https://10.122.8.31" : sisbolBaseUrl.TrimEnd('/');
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(90),
            BaseAddress = new Uri(baseUrl + "/")
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SIGFUR/SisBolDownloader");
        http.DefaultRequestHeaders.Referrer = new Uri(baseUrl + "/band/");
        var cookie = BuildSisbolCookieHeader(driver);
        if (!string.IsNullOrWhiteSpace(cookie)) http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        return http;
    }

    private static string BuildSisbolCookieHeader(IWebDriver driver)
    {
        try
        {
            return string.Join("; ", driver.Manage().Cookies.AllCookies
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => $"{x.Name}={x.Value}"));
        }
        catch { return string.Empty; }
    }

    private sealed record SisbolDownloadLink(string Url, string FileName);

    private static List<SisbolDownloadLink> ExtractSisbolDownloadLinks(IWebDriver driver, string pageUrl)
    {
        var output = new List<SisbolDownloadLink>();
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                return Array.from(document.querySelectorAll('a[href]')).map(a => {
                    const href = a.getAttribute('href') || '';
                    const abs = a.href || href;
                    const text = (a.textContent || '').trim();
                    const img = a.querySelector('img');
                    const title = img ? (img.getAttribute('title') || img.getAttribute('alt') || '') : '';
                    return { href, abs, text, title };
                });
                """);
            if (raw is IEnumerable<object> rows)
            {
                foreach (var row in rows)
                {
                    if (row is not IDictionary<string, object> map) continue;
                    var href = map.TryGetValue("href", out var h) ? h?.ToString() ?? string.Empty : string.Empty;
                    var abs = map.TryGetValue("abs", out var a) ? a?.ToString() ?? string.Empty : string.Empty;
                    var text = map.TryGetValue("text", out var t) ? t?.ToString() ?? string.Empty : string.Empty;
                    var title = map.TryGetValue("title", out var ti) ? ti?.ToString() ?? string.Empty : string.Empty;
                    var candidate = !string.IsNullOrWhiteSpace(abs) ? abs : href;
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    if (!candidate.Contains("down.php", StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Contains("filename=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!title.Contains("download", StringComparison.OrdinalIgnoreCase) &&
                        !href.Contains("down.php", StringComparison.OrdinalIgnoreCase) &&
                        !abs.Contains("down.php", StringComparison.OrdinalIgnoreCase)) continue;

                    var fileName = ExtractFileNameFromSisbolLink(candidate, pageUrl);
                    if (string.IsNullOrWhiteSpace(fileName)) fileName = text;
                    var url = AbsoluteSisbolUrl(candidate, pageUrl);
                    if (!output.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                        output.Add(new SisbolDownloadLink(url, fileName));
                }
            }
        }
        catch { }
        return output;
    }

    private static string AbsoluteSisbolUrl(string value, string pageUrl)
    {
        var s = (value ?? string.Empty).Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var absolute)) return absolute.ToString();
        try
        {
            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
                return new Uri(baseUri, s).ToString();
        }
        catch { }
        if (s.StartsWith("/", StringComparison.Ordinal)) return "https://10.122.8.31" + s;
        return "https://10.122.8.31/band/" + s.TrimStart('/');
    }

    private static string ExtractFileNameFromSisbolLink(string link, string pageUrl)
    {
        try
        {
            var uri = new Uri(AbsoluteSisbolUrl(link, pageUrl));
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in query)
            {
                var idx = part.IndexOf('=');
                var key = idx >= 0 ? part[..idx] : part;
                if (!key.Equals("filename", StringComparison.OrdinalIgnoreCase)) continue;
                var value = idx >= 0 ? part[(idx + 1)..] : string.Empty;
                value = Uri.UnescapeDataString(value.Replace('+', ' '));
                value = value.Replace('\\', '/');
                return Path.GetFileName(value);
            }
            return Path.GetFileName(uri.LocalPath);
        }
        catch { return string.Empty; }
    }

    private static string NormalizeSisbolBulletinFileName(string original, int bulletinTypeCode, int year, int month, int sequence)
    {
        var clean = SafeFileName(Path.GetFileName(original ?? string.Empty));
        if (string.IsNullOrWhiteSpace(clean) || !clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            clean = $"{year}-{month:00}-{sequence:000}_{(bulletinTypeCode == 3 ? "aditamento_do_furriel" : "boletim_interno")}.pdf";
        return clean;
    }

    private static async Task DownloadSisbolFileAsync(HttpClient http, string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".download";
        await using (var target = File.Create(temp))
            await source.CopyToAsync(target, cancellationToken);
        var info = new FileInfo(temp);
        if (info.Length < 500)
        {
            try { File.Delete(temp); } catch { }
            throw new InvalidOperationException("O arquivo retornado pelo SisBol veio vazio ou inválido.");
        }
        if (!contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            var header = await File.ReadAllBytesAsync(temp, cancellationToken);
            var isPdf = header.Length >= 4 && header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46;
            if (!isPdf)
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidOperationException("O SisBol não retornou PDF. A sessão pode ter expirado.");
            }
        }
        File.Move(temp, destination, true);
    }

    private static string SafeFileName(string value)
    {
        var cleaned = new string((value ?? string.Empty).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, "_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "frame" : cleaned[..Math.Min(cleaned.Length, 80)];
    }

    private static string BuildHtml(string plain, IReadOnlyList<MilitaryRecord> military)
        => BulletinTextFormatter.BuildHtml(plain, military);

    private static string RemoveTrailingConsequences(string? text)
    {
        var value = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = Regex.Replace(
            value,
            @"(?:^|\n+)\s*Em\s+consequ(?:ê|e)ncia\b[\s\S]*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return value.Trim();
    }

    private static string SpecificSubject(string templateName, string? plainText = null)
    {
        var currentModel = Regex.Replace(templateName ?? string.Empty, @"\s+", " ").Trim();
        var subject = Regex.Replace(currentModel, @"^\s*(?:COD(?:IGO)?\s*[:#-]?\s*)?\d{2,6}\s*[-\u2013\u2014:]\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        return CanonicalSisbolSpecificSubject(subject);
    }

    private static string SpecificSubjectSearchTerm(string templateName, string plainText, string specific)
    {
        var subject = CanonicalSisbolSpecificSubject(specific);
        if (IsAuxTransportSubject(subject)) return "AUXILIO-TRANSPORTE";
        if (IsAuxFoodAidSubject(subject)) return "AUXILIO-ALIMENTAÇÃO";
        return Regex.Replace(subject ?? string.Empty, @"\s+", " ").Trim();
    }

    private static string SpecificSubjectCode(string templateName, string? plainText = null)
    {
        var explicitCode = Regex.Match(templateName ?? string.Empty, @"^\s*(?:COD(?:IGO)?\s*[:#-]?\s*)?(\d{2,6})\s*[-\u2013\u2014:]", RegexOptions.IgnoreCase);
        if (explicitCode.Success) return explicitCode.Groups[1].Value;

        var subject = SpecificSubject(templateName, plainText);
        var normalized = NormalizeSubjectLoose(subject);
        if (normalized.Contains("auxilio alimentacao ordem saque", StringComparison.OrdinalIgnoreCase) && normalized.Contains("cb", StringComparison.OrdinalIgnoreCase) && normalized.Contains("sd", StringComparison.OrdinalIgnoreCase)) return "1516";
        if (normalized.Contains("auxilio alimentacao saque atrasado", StringComparison.OrdinalIgnoreCase)) return "2928";
        if (normalized.Contains("auxilio transporte despesa anular ferias", StringComparison.OrdinalIgnoreCase)) return "2442";
        if (normalized.Contains("auxilio transporte despesa anular correcao", StringComparison.OrdinalIgnoreCase)) return "2477";
        if (normalized.Contains("auxilio transporte despesa anular", StringComparison.OrdinalIgnoreCase)) return "3754";
        if (normalized.Contains("auxilio transporte saque atrasado", StringComparison.OrdinalIgnoreCase)) return "1485";
        if (normalized.Contains("auxilio transporte exclusao beneficiarios", StringComparison.OrdinalIgnoreCase)) return "1456";
        if (normalized.Contains("auxilio transporte diferenca", StringComparison.OrdinalIgnoreCase)) return "1560";
        if (normalized.Contains("auxilio transporte ajuste contas", StringComparison.OrdinalIgnoreCase)) return "1558";
        if (normalized.Contains("auxilio transporte atualizacao valores", StringComparison.OrdinalIgnoreCase)) return "1527";
        if (normalized.Contains("auxilio transporte concessao", StringComparison.OrdinalIgnoreCase)) return "1491";
        return string.Empty;
    }

    private static bool IsAuxTransportSubject(string? subject)
    {
        var normalized = NormalizeSubjectLoose(subject).Replace("trasnporte", "transporte", StringComparison.OrdinalIgnoreCase);
        return normalized.Contains("auxilio transporte", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuxFoodAidSubject(string? subject)
    {
        var normalized = NormalizeSubjectLoose(subject);
        return normalized.Contains("auxilio alimentacao", StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalSisbolSpecificSubject(string? subject)
    {
        var text = Regex.Replace(subject ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // O SisBol diferencia travessão de hífen e a lista oficial usa hífen simples.
        // Também houve modelo salvo com TRASNPORTE digitado errado; aqui normalizamos
        // somente para pesquisar/validar o assunto específico, sem mexer no corpo.
        text = text.Replace('–', '-').Replace('—', '-');
        text = Regex.Replace(text, @"\s*-\s*", " - ").Trim();
        text = Regex.Replace(text, "TRASNPORTE", "TRANSPORTE", RegexOptions.IgnoreCase);

        var normalized = NormalizeSubjectLoose(text);
        if (normalized.Contains("auxilio alimentacao", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains("ordem saque", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("cb", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("sd", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("ferias", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-ALIMENTAÇÃO - Ordem de Saque (Cb e Sd Férias)";
            if (normalized.Contains("saque atrasado", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-ALIMENTAÇÃO - Saque de atrasado";
            if (normalized.Contains("despesa anular", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-ALIMENTAÇÃO - Despesa anular";
            if (normalized.Contains("ordem saque", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-ALIMENTAÇÃO - Ordem de saque";
        }

        if (normalized.Contains("auxilio transporte", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains("despesa anular", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.Contains("correcao", StringComparison.OrdinalIgnoreCase))
                    return "AUXILIO-TRANSPORTE - Despesa a Anular - Correção";
                if (normalized.Contains("ferias", StringComparison.OrdinalIgnoreCase))
                    return "AUXILIO-TRANSPORTE - Despesa a anular - Férias";
                return "AUXILIO-TRANSPORTE - Despesa a anular";
            }
            if (normalized.Contains("saque atrasado", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-TRANSPORTE - Saque de Atrasado";
            if (normalized.Contains("exclusao beneficiarios", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-TRANSPORTE - Exclusão de Beneficiários";
            if (normalized.Contains("diferenca", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-TRANSPORTE - Diferença";
            if (normalized.Contains("ajuste contas", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-TRANSPORTE - Ajuste de Contas";
            if (normalized.Contains("atualizacao valores", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-TRANSPORTE - Atualização de valores";
            if (normalized.Contains("concessao", StringComparison.OrdinalIgnoreCase) || normalized.Contains("implantacao", StringComparison.OrdinalIgnoreCase))
                return "AUXILIO-TRANSPORTE - Concessão";
        }

        return text;
    }

    private static string Context(IWebElement element)
    {
        try { return $"{element.GetAttribute("name")} {element.GetAttribute("id")} {element.GetAttribute("title")} {element.GetAttribute("aria-label")} {element.Text}"; }
        catch { return string.Empty; }
    }

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).ToArray());
    }

    private static string NormalizeCompact(string? value)
        => Regex.Replace(Normalize(value).ToLowerInvariant(), @"\s+", " ").Trim();

    private static string NormalizeSubject(string? value)
        => Regex.Replace(NormalizeCompact(value), @"[^a-z0-9]+", " ").Trim();

    private static string NormalizeSubjectLoose(string? value)
    {
        var normalized = NormalizeSubject(value);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 1 && word is not "de" and not "da" and not "do" and not "das" and not "dos" and not "em" and not "por" and not "para")
            .ToArray();
        return string.Join(' ', words);
    }

    private static string NormalizeBrowser(string? browser)
    {
        var value = Normalize(browser).ToLowerInvariant();
        if (value.Contains("firefox") || value.Contains("mozilla")) return "firefox";
        if (value.Contains("chrome") || value.Contains("google")) return "chrome";
        return "edge";
    }

    private static bool IsVisible(IWebElement element)
    {
        try { return element.Displayed && element.Enabled; }
        catch { return false; }
    }

    private static void WaitReady(IWebDriver driver, int seconds)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(seconds));
        wait.IgnoreExceptionTypes(typeof(WebDriverException));
        wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() is "complete" or "interactive");
    }

    private static bool WaitUntil(IWebDriver driver, Func<IWebDriver, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            var wait = new WebDriverWait(driver, timeout)
            {
                PollingInterval = TimeSpan.FromMilliseconds(220)
            };
            wait.IgnoreExceptionTypes(typeof(WebDriverException), typeof(InvalidOperationException));
            return wait.Until(d =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return predicate(d);
            });
        }
        catch (WebDriverTimeoutException) { return false; }
    }

    private static bool WaitForSisbolPageStable(
        IWebDriver driver,
        TimeSpan timeout,
        TimeSpan quietPeriod,
        bool requireEditor,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                driver.SwitchTo().DefaultContent();
                var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    try{
                        const quietMs=Number(arguments[0]||750), requireEditor=!!arguments[1];
                        if(!window.__sigfurSisbolStability){
                            const state={lastChange:Date.now(),count:0};
                            state.observer=new MutationObserver(()=>{state.lastChange=Date.now();state.count++;});
                            state.observer.observe(document.documentElement,{subtree:true,childList:true,attributes:true,characterData:true});
                            window.__sigfurSisbolStability=state;
                        }
                        const visible=e=>{try{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity||1)>0}catch(_){return false}};
                        const loaders=Array.from(document.querySelectorAll('#loading,#loader,.loading,.loader,.carregando,.aguarde,.ui-widget-overlay,[aria-busy="true"]')).filter(visible);
                        const form=document.cadMateriaBI||document.forms['cadMateriaBI']||Array.from(document.forms||[]).find(x=>x&&(x.texto_abert||x.inputCodAssGeral||x.seleParteBi));
                        const outer=document.getElementById('texto_abert___Frame');
                        let editorReady=false, innerReady=false;
                        if(outer){
                            try{
                                const outerDoc=outer.contentDocument||(outer.contentWindow&&outer.contentWindow.document);
                                const inner=outerDoc&&outerDoc.querySelector('iframe,frame');
                                const innerDoc=inner&&(inner.contentDocument||(inner.contentWindow&&inner.contentWindow.document));
                                innerReady=!!(innerDoc&&innerDoc.body&&(innerDoc.readyState==='complete'||innerDoc.readyState==='interactive'));
                            }catch(e){}
                        }
                        try{
                            const ed=window.FCKeditorAPI&&window.FCKeditorAPI.GetInstance&&window.FCKeditorAPI.GetInstance('texto_abert');
                            editorReady=!!(ed&&ed.EditorDocument&&ed.EditorDocument.body);
                        }catch(e){}
                        editorReady=editorReady||innerReady;
                        const idleFor=Date.now()-window.__sigfurSisbolStability.lastChange;
                        const ready=document.readyState==='complete';
                        const jqueryIdle=!window.jQuery||Number(window.jQuery.active||0)===0;
                        const ok=ready&&jqueryIdle&&loaders.length===0&&!!form&&idleFor>=quietMs&&(!requireEditor||(!!outer&&editorReady));
                        return {ok:ok,ready:ready,jqueryIdle:jqueryIdle,loaders:loaders.length,form:!!form,outer:!!outer,editorReady:editorReady,idleFor:idleFor,mutations:window.__sigfurSisbolStability.count};
                    }catch(e){return {ok:false,error:String(e&&e.message||e)}}
                    """, quietPeriod.TotalMilliseconds, requireEditor) as IDictionary<string, object>;
                if (result is not null && result.TryGetValue("ok", out var ok) && Convert.ToBoolean(ok, CultureInfo.InvariantCulture))
                    return true;
            }
            catch (WebDriverException) { }
            catch (InvalidOperationException) { }

            if (cancellationToken.WaitHandle.WaitOne(150))
                throw new OperationCanceledException(cancellationToken);
        }

        try { driver.SwitchTo().DefaultContent(); } catch { }
        return false;
    }

    private static void WaitForDomIdle(IWebDriver driver, TimeSpan timeout, CancellationToken cancellationToken = default)
        => WaitUntil(driver, d =>
        {
            try
            {
                return Convert.ToBoolean(((IJavaScriptExecutor)d).ExecuteScript("""
                    const ready = document.readyState === 'complete' || document.readyState === 'interactive';
                    const jqueryIdle = !window.jQuery || window.jQuery.active === 0;
                    return ready && jqueryIdle;
                    """));
            }
            catch { return false; }
        }, timeout, cancellationToken);

    private bool DriverAlive()
    {
        try { _ = _driver?.WindowHandles; return _driver is not null; }
        catch { return false; }
    }

    private void ShowBrowser(IWebDriver driver)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var handles = _hiddenWindows.Where(IsWindow)
                    .Concat(FindBrowserWindows(visibleOnly: false))
                    .Distinct()
                    .ToList();
                foreach (var handle in handles)
                {
                    ShowWindow(handle, SwShow);
                    ShowWindow(handle, SwRestore);
                    SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                    SetForegroundWindow(handle);
                }
                _hiddenWindows.Clear();
            }
            catch { }
        }

        try
        {
            driver.Manage().Window.Position = new System.Drawing.Point(60, 40);
            driver.Manage().Window.Size = new System.Drawing.Size(1280, 900);
            driver.Manage().Window.Maximize();
        }
        catch { }
    }

    private bool HideBrowser(IWebDriver driver)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { driver.Manage().Window.Minimize(); return true; }
            catch { return false; }
        }

        HideDriverServiceWindows();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var handles = FindBrowserWindows(visibleOnly: false);
                if (handles.Count > 0)
                {
                    foreach (var handle in handles)
                    {
                        ShowWindow(handle, SwHide);
                        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpHideWindow);
                    }

                    _hiddenWindows.Clear();
                    _hiddenWindows.AddRange(handles.Where(IsWindow));
                    if (_hiddenWindows.Count > 0 && _hiddenWindows.All(handle => !IsWindowVisible(handle))) return true;
                }
            }
            catch { }
            Thread.Sleep(100);
        }

        // Nao minimiza como fallback: isso manteria a janela na barra de tarefas.
        return false;
    }

    private void HideDriverServiceWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var processIds = new HashSet<int>(_driverProcessIds);
        if (_serviceProcessId > 0) processIds.Add(_serviceProcessId);
        if (processIds.Count == 0) return;
        try
        {
            EnumWindows((handle, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(handle, out var pid);
                    if (processIds.Contains((int)pid))
                    {
                        ShowWindow(handle, SwHide);
                        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpHideWindow);
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    private List<IntPtr> FindBrowserWindows(bool visibleOnly)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var pids = ResolveBrowserProcessIds();
        pids.Remove(_serviceProcessId); // não restaura/mostra o console do driver; só o navegador.
        if (pids.Count == 0) return [];
        var result = new List<IntPtr>();
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero && (!visibleOnly || IsWindowVisible(handle))) result.Add(handle);
            }
            catch { }
        }

        EnumWindows((handle, _) =>
        {
            try
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (!pids.Contains((int)pid)) return true;
                if (visibleOnly && !IsWindowVisible(handle)) return true;
                if (!GetWindowRect(handle, out var rect)) return true;
                if (rect.Right - rect.Left > 20 && rect.Bottom - rect.Top > 20) result.Add(handle);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result.Distinct().ToList();
    }

    private HashSet<int> ResolveBrowserProcessIds()
    {
        var candidates = new HashSet<int>(_browserProcessIds);
        var roots = new HashSet<int>(_driverProcessIds);
        if (_serviceProcessId > 0) roots.Add(_serviceProcessId);
        roots.UnionWith(_browserProcessIds);
        foreach (var root in roots)
            candidates.UnionWith(DescendantProcessIds(root));

        var browserProcessNames = BrowserProcessNames(_browser);
        candidates.RemoveWhere(processId => !ProcessNameMatches(processId, browserProcessNames));
        _browserProcessIds.UnionWith(candidates);
        return candidates;
    }

    private static HashSet<int> DescendantProcessIds(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        if (!OperatingSystem.IsWindows()) return result;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            var pairs = new List<(int Pid, int Parent)>();
            if (Process32First(snapshot, ref entry))
            {
                do { pairs.Add(((int)entry.ProcessId, (int)entry.ParentProcessId)); entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>(); }
                while (Process32Next(snapshot, ref entry));
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var pair in pairs)
                    if (result.Contains(pair.Parent) && result.Add(pair.Pid)) changed = true;
            }
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    private void DisposeDriver(bool fast)
    {
        var driver = _driver;
        var processRoots = new HashSet<int>(_driverProcessIds);
        processRoots.UnionWith(_browserProcessIds);
        if (_serviceProcessId > 0) processRoots.Add(_serviceProcessId);
        _driver = null;
        _driverService = null;
        _serviceProcessId = 0;
        _driverProcessIds.Clear();
        _browserProcessIds.Clear();
        _browser = string.Empty;
        _confirmed = false;
        _hiddenWindows.Clear();
        if (driver is null) return;

        try
        {
            var quit = Task.Run(() => { try { driver.Quit(); } catch { } });
            if (!quit.Wait(fast ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromSeconds(3)))
                foreach (var processId in processRoots) KillProcessTree(processId);
        }
        catch { foreach (var processId in processRoots) KillProcessTree(processId); }
        try { driver.Dispose(); } catch { }
    }

    private static void KillProcessTree(int rootPid)
    {
        if (rootPid <= 0) return;
        foreach (var pid in DescendantProcessIds(rootPid).OrderByDescending(x => x))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cachedAlive = false;
        _cachedReady = false;
        DisposeDriver(fast: true);
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly record struct DriverLaunch(
        IWebDriver Driver,
        object Service,
        int ServiceProcessId,
        IReadOnlyCollection<int> DriverProcessIds,
        IReadOnlyCollection<int> BrowserProcessIds);
    private readonly record struct FieldFillResult(bool Success, string Message);
    private readonly record struct PreIncludeValidation(bool Success, string Message);

    private readonly record struct SendResult(bool Success, string Message);
    private readonly record struct PageSnapshot(string Url, string Body, bool Saved);
    private readonly record struct MatterDraftState(bool HasMatterForm, bool HasIncludeButton, bool HasNewNote, int TextLength, string GeneralCode, string GeneralText, string Url);
    private readonly record struct DiagnosticFrameSnapshot(int Index, string FramePath, string Url, string Title, string Html, string SummaryJson);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr handle);
}
