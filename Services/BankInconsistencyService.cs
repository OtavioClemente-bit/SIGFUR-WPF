using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.UI;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class BankInconsistencyService : IDisposable
{
    private const string CpexUrl = "https://cpex-intranet.eb.mil.br/rel_inconsistencias/inconsistencias.asp";
    private const string SinfoppesLoginUrl = "http://sinfoppes.sippes.eb.mil.br/sinfoppes/";
    private const string SinfoppesReportUrl = "http://sinfoppes.sippes.eb.mil.br/sinfoppes/relatorio/rellanccriticadoscpex";
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly SemaphoreSlim _driverGate = new(1, 1);
    private readonly SemaphoreSlim _automationGate = new(1, 1);
    private IWebDriver? _driver;

    public BankInconsistencyService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths; _json = json; _log = log;
        Directory.CreateDirectory(_paths.BankInconsistencyOutputDirectory);
        Directory.CreateDirectory(_paths.SinfoppesCriticizedOutputDirectory);
    }

    public async Task<BankInconsistencySettings> LoadCpexSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = (await _json.LoadAsync<BankInconsistencySettings>(_paths.BankInconsistencySettingsFile)) ?? new BankInconsistencySettings();
        settings.OutputDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : _paths.BankInconsistencyOutputDirectory;
        settings.CodomHistory ??= [];
        return settings;
    }
    public Task SaveCpexSettingsAsync(BankInconsistencySettings settings, CancellationToken cancellationToken = default) => _json.SaveAsync(_paths.BankInconsistencySettingsFile, settings);

    public async Task<SinfoppesCriticizedSettings> LoadSinfoppesSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = (await _json.LoadAsync<SinfoppesCriticizedSettings>(_paths.SinfoppesCriticizedSettingsFile)) ?? new SinfoppesCriticizedSettings();
        settings.OutputDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : _paths.SinfoppesCriticizedOutputDirectory;
        return settings;
    }
    public Task SaveSinfoppesSettingsAsync(SinfoppesCriticizedSettings settings, CancellationToken cancellationToken = default) => _json.SaveAsync(_paths.SinfoppesCriticizedSettingsFile, settings);

    public Task<BankAutomationResult> RunCpexAsync(BankInconsistencySettings settings, IProgress<AutomationProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => RunCpex(settings, progress, cancellationToken), cancellationToken);

    public Task<BankAutomationResult> RunSinfoppesAsync(SinfoppesCriticizedSettings settings, string password, IProgress<AutomationProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => RunSinfoppes(settings, password, progress, cancellationToken), cancellationToken);

    public void OpenCpexSite() => ShellService.OpenPath(CpexUrl);
    public void OpenSinfoppesSite() => ShellService.OpenPath(SinfoppesLoginUrl);

    public async Task CloseBrowserAsync()
    {
        await _driverGate.WaitAsync();
        try { try { _driver?.Quit(); } catch { } try { _driver?.Dispose(); } catch { } _driver = null; }
        finally { _driverGate.Release(); }
    }

    private BankAutomationResult RunCpex(BankInconsistencySettings settings, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        IWebDriver? driver = null;
        var gateHeld = false;
        try
        {
            _automationGate.Wait(ct);
            gateHeld = true;
            progress?.Report(new AutomationProgress { Percent = 5, Message = "Abrindo CPEX..." });
            driver = GetDriver(settings.OutputDirectory, settings.Headless, settings.KeepBrowserOpen, ct);
            driver.Navigate().GoToUrl(CpexUrl);
            WaitReady(driver, ct, 90);
            ct.ThrowIfCancellationRequested();
            SwitchToFormContext(driver);
            progress?.Report(new AutomationProgress { Percent = 22, Message = "Preenchendo consulta..." });
            FillCpexForm(driver, settings, ct);

            var handlesBefore = driver.WindowHandles.ToHashSet(StringComparer.Ordinal);
            var previousUrl = driver.Url;
            ClickByText(driver, ["visualizar"]);
            SwitchToNewWindow(driver, handlesBefore, previousUrl, ct);
            WaitForDomIdle(driver, ct, 4);
            var alertText = AcceptAlerts(driver);
            try { WaitReady(driver, ct, 60); } catch (WebDriverTimeoutException) { }
            WaitForDomIdle(driver, ct, 4);
            alertText = string.Join(" ", new[] { alertText, AcceptAlerts(driver) }.Where(x => !string.IsNullOrWhiteSpace(x)));
            ct.ThrowIfCancellationRequested();
            SwitchToResultContext(driver);
            var bodyRaw = SafeBody(driver);
            var body = Normalize(bodyRaw);
            if (!HasCpexResult(driver, body) || ContainsAny(body, ["nao existe", "nao existem", "nenhum", "sem inconsistencia", "nao ha", "consulta sem resultado"]))
            {
                var message = string.IsNullOrWhiteSpace(alertText) ? "Nenhuma inconsistência encontrada." : Regex.Replace(alertText, @"\s+", " ").Trim();
                return new BankAutomationResult { Success = true, NoRecords = true, Message = message };
            }

            progress?.Report(new AutomationProgress { Percent = 64, Message = "Gerando PDF..." });
            Directory.CreateDirectory(settings.OutputDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var baseName = $"Inconsistencia_Bancaria_{Safe(settings.SystemName)}_{settings.Year}_{Safe(settings.Month)}_{Safe(settings.MilitaryRegion)}_CODOM_{Safe(settings.Codom)}_{stamp}";
            var pdf = UniquePath(Path.Combine(settings.OutputDirectory, baseName + ".pdf"));
            PrintPdf(driver, pdf, landscape: true);
            var spreadsheet = string.Empty;
            if (settings.DownloadSpreadsheet)
            {
                progress?.Report(new AutomationProgress { Percent = 80, Message = "Exportando planilha..." });
                var before = SnapshotDownloads(settings.OutputDirectory);
                if (ClickByText(driver, ["exportar", "excel", "xlsm", "planilha"], throwIfMissing: false))
                {
                    var downloaded = WaitForDownload(settings.OutputDirectory, before, ct, 75);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        var extension = Path.GetExtension(downloaded);
                        if (string.IsNullOrWhiteSpace(extension)) extension = ".xlsm";
                        var target = UniquePath(Path.Combine(settings.OutputDirectory, baseName + extension));
                        try { File.Move(downloaded, target, true); spreadsheet = target; }
                        catch { spreadsheet = downloaded; }
                    }
                }
            }
            settings.LastPdf = pdf;
            settings.LastSpreadsheet = spreadsheet;
            RememberCodom(settings);
            SaveCpexSettingsAsync(settings, ct).GetAwaiter().GetResult();
            if (settings.OpenPdf) ShellService.OpenPath(pdf);
            progress?.Report(new AutomationProgress { Percent = 100, Message = "Consulta concluída." });
            return new BankAutomationResult { Success = true, Message = "Relatório salvo.", PdfPath = pdf, SpreadsheetPath = spreadsheet };
        }
        catch (OperationCanceledException) { return new BankAutomationResult { Message = "Operação cancelada." }; }
        catch (Exception ex)
        {
            _log.WriteAsync("Falha na Inconsistência Bancária CPEX.", ex).GetAwaiter().GetResult();
            return new BankAutomationResult { Message = ex.Message };
        }
        finally
        {
            if (!settings.KeepBrowserOpen) ReleaseDriver(driver);
            if (gateHeld) _automationGate.Release();
        }
    }

    private BankAutomationResult RunSinfoppes(SinfoppesCriticizedSettings settings, string password, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        IWebDriver? driver = null;
        var gateHeld = false;
        try
        {
            _automationGate.Wait(ct);
            gateHeld = true;
            if (string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(settings.Cpf)) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("Informe CPF e senha do SINFOPPES.");
            progress?.Report(new AutomationProgress { Percent = 5, Message = "Abrindo SINFOPPES..." });
            driver = GetDriver(settings.OutputDirectory, settings.Headless, settings.KeepBrowserOpen, ct);
            driver.Navigate().GoToUrl(SinfoppesLoginUrl);
            WaitReady(driver, ct, 90);
            LoginSinfoppes(driver, settings.Cpf, password, ct);
            progress?.Report(new AutomationProgress { Percent = 28, Message = "Abrindo lançamentos criticados..." });
            driver.Navigate().GoToUrl(SinfoppesReportUrl);
            WaitReady(driver, ct, 90);
            AcceptAlerts(driver);
            FillSinfoppesFilters(driver, settings);
            var bodyBeforeSearch = BodySignature(driver);
            ClickByText(driver, ["pesquisar", "consultar", "filtrar", "buscar"]);
            WaitUntil(driver, d => AlertPresent(d) || !string.Equals(BodySignature(d), bodyBeforeSearch, StringComparison.Ordinal), TimeSpan.FromSeconds(18), ct);
            AcceptAlerts(driver);
            try { WaitReady(driver, ct, 60); } catch (WebDriverTimeoutException) { }
            ct.ThrowIfCancellationRequested();

            progress?.Report(new AutomationProgress { Percent = 52, Message = "Lendo os registros..." });
            var rows = ScrapeSinfoppesAllRows(driver, progress, ct);
            if (rows.Count == 0)
            {
                var body = SafeBody(driver);
                if (IsSinfoppesNoData(body) || !SinfoppesBodyHasRecords(body))
                    return new BankAutomationResult { Success = true, NoRecords = true, Message = "Nenhum lançamento criticado encontrado." };
                throw new InvalidOperationException("A tabela foi localizada, mas os registros não puderam ser lidos.");
            }

            progress?.Report(new AutomationProgress { Percent = 76, Message = "Montando o relatório..." });
            Directory.CreateDirectory(settings.OutputDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var baseName = $"Lancamentos_Criticados_{settings.Year}_{Safe(settings.Month)}_{Safe(settings.Run)}_{stamp}";
            var htmlPath = UniquePath(Path.Combine(settings.OutputDirectory, baseName + ".html"));
            File.WriteAllText(htmlPath, BuildSinfoppesHtml(rows, settings), new UTF8Encoding(false));
            driver.Navigate().GoToUrl(new Uri(htmlPath).AbsoluteUri);
            WaitReady(driver, ct, 30);
            var pdf = UniquePath(Path.Combine(settings.OutputDirectory, baseName + ".pdf"));
            PrintPdf(driver, pdf, landscape: true);
            try { File.Delete(htmlPath); } catch { }

            settings.LastPdf = pdf;
            if (!settings.SaveCpf) settings.Cpf = string.Empty;
            settings.SetPassword(password);
            SaveSinfoppesSettingsAsync(settings, ct).GetAwaiter().GetResult();
            if (settings.OpenPdf) ShellService.OpenPath(pdf);
            progress?.Report(new AutomationProgress { Percent = 100, Message = "Relatório salvo." });
            return new BankAutomationResult { Success = true, Message = "Relatório salvo.", PdfPath = pdf, RecordCount = rows.Count };
        }
        catch (OperationCanceledException) { return new BankAutomationResult { Message = "Operação cancelada." }; }
        catch (Exception ex)
        {
            _log.WriteAsync("Falha nos Lançamentos Criticados SINFOPPES.", ex).GetAwaiter().GetResult();
            return new BankAutomationResult { Message = ex.Message };
        }
        finally
        {
            if (!settings.KeepBrowserOpen) ReleaseDriver(driver);
            if (gateHeld) _automationGate.Release();
        }
    }

    private IWebDriver GetDriver(string downloadDirectory, bool headless, bool keepOpen, CancellationToken ct)
    {
        _driverGate.Wait(ct);
        try
        {
            if (keepOpen && DriverAlive(_driver)) return _driver!;
            if (DriverAlive(_driver)) { try { _driver!.Quit(); } catch { } _driver = null; }
            Directory.CreateDirectory(downloadDirectory);
            Directory.CreateDirectory(_paths.BankAutomationProfileDirectory);
            var service = EdgeDriverService.CreateDefaultService(); service.HideCommandPromptWindow = true; service.SuppressInitialDiagnosticInformation = true;
            var options = new EdgeOptions { PageLoadStrategy = PageLoadStrategy.Normal, AcceptInsecureCertificates = true };
            if (headless) options.AddArgument("--headless=new");
            foreach (var arg in new[] { $"--user-data-dir={_paths.BankAutomationProfileDirectory}", "--ignore-certificate-errors", "--allow-running-insecure-content", "--disable-notifications", "--disable-popup-blocking", "--window-size=1500,1050", "--no-first-run", "--no-default-browser-check" }) options.AddArgument(arg);
            options.AddUserProfilePreference("download.default_directory", downloadDirectory);
            options.AddUserProfilePreference("download.prompt_for_download", false);
            options.AddUserProfilePreference("plugins.always_open_pdf_externally", true);
            var created = new EdgeDriver(service, options, TimeSpan.FromSeconds(120));
            try { created.ExecuteCdpCommand("Browser.setDownloadBehavior", new Dictionary<string, object?> { ["behavior"] = "allow", ["downloadPath"] = downloadDirectory }); } catch { }
            _driver = created; return created;
        }
        finally { _driverGate.Release(); }
    }

    private void ReleaseDriver(IWebDriver? driver)
    {
        if (driver is null) return;
        try { driver.Quit(); } catch { } try { driver.Dispose(); } catch { }
        _driverGate.Wait(); try { if (ReferenceEquals(_driver, driver)) _driver = null; } finally { _driverGate.Release(); }
    }

    private static bool DriverAlive(IWebDriver? driver) { if (driver is null) return false; try { _ = driver.Url; return true; } catch { return false; } }

    private static void FillCpexForm(IWebDriver driver, BankInconsistencySettings settings, CancellationToken ct)
    {
        var selects = driver.FindElements(By.TagName("select")).Where(Display).ToList();
        if (selects.Count < 4) throw new InvalidOperationException("Os campos Sistema, Ano, Mês e RM não foram encontrados.");
        SelectFlexible(driver, selects[0], [settings.SystemName, "SIPPES", "SIAPPES"], ct); ct.ThrowIfCancellationRequested();
        SwitchToFormContext(driver); selects = driver.FindElements(By.TagName("select")).Where(Display).ToList();
        SelectFlexible(driver, selects.ElementAtOrDefault(1) ?? throw new InvalidOperationException("Campo Ano não encontrado."), [settings.Year.ToString(CultureInfo.InvariantCulture)], ct);
        SwitchToFormContext(driver); selects = driver.FindElements(By.TagName("select")).Where(Display).ToList();
        SelectFlexible(driver, selects.ElementAtOrDefault(2) ?? throw new InvalidOperationException("Campo Mês não encontrado."), MonthAlternatives(settings.Month), ct);
        SwitchToFormContext(driver); selects = driver.FindElements(By.TagName("select")).Where(Display).ToList();
        SelectFlexible(driver, selects.ElementAtOrDefault(3) ?? throw new InvalidOperationException("Campo RM não encontrado."), RegionAlternatives(settings.MilitaryRegion));
        var inputs = driver.FindElements(By.CssSelector("input[type=text],input[type=number],input:not([type])")).Where(Display).ToList();
        var codom = inputs.FirstOrDefault(x => Signature(x).Contains("codom", StringComparison.OrdinalIgnoreCase)) ?? inputs.LastOrDefault();
        if (codom is null) throw new InvalidOperationException("Campo CODOM não encontrado.");
        SetValue(driver, codom, MilitaryFormatting.Digits(settings.Codom));
        var radios = driver.FindElements(By.CssSelector("input[type=radio]")).Where(Display).ToList();
        if (radios.Count > 0)
        {
            var report = Normalize(settings.ReportType);
            var useSecond = report.Contains("falta", StringComparison.Ordinal) || report.Contains("apresentacao", StringComparison.Ordinal);
            var radio = useSecond && radios.Count > 1 ? radios[1] : radios[0];
            try { radio.Click(); } catch { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", radio); }
        }
    }

    private static void LoginSinfoppes(IWebDriver driver, string cpf, string password, CancellationToken ct)
    {
        var fields = driver.FindElements(By.CssSelector("input")).Where(Display).ToList();
        var pass = fields.FirstOrDefault(x => string.Equals(x.GetAttribute("type"), "password", StringComparison.OrdinalIgnoreCase));
        var user = fields.FirstOrDefault(x => x != pass && ContainsAny(Normalize(Signature(x)), ["cpf", "usuario", "login"]))
                   ?? fields.FirstOrDefault(x => x != pass && !IsButton(x));
        if (user is null || pass is null)
            throw new InvalidOperationException("Campos de acesso do SINFOPPES não encontrados.");

        // O fluxo correto do portal é CPF -> senha -> ENTER. Evitamos procurar links por texto,
        // pois o link de recuperação de senha também contém a palavra "senha" e podia receber o clique.
        SetValue(driver, user, MilitaryFormatting.Digits(cpf));
        SetValue(driver, pass, password);
        try { pass.Click(); } catch { }
        pass.SendKeys(Keys.Enter);

        var loginUrl = string.Empty;
        try { loginUrl = driver.Url; } catch { }
        var end = DateTime.UtcNow.AddSeconds(18);
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            AcceptAlert(driver);
            try
            {
                var currentPass = driver.FindElements(By.CssSelector("input[type=password]")).FirstOrDefault(Display);
                var currentUrl = driver.Url;
                if (currentPass is null || !string.Equals(currentUrl, loginUrl, StringComparison.OrdinalIgnoreCase)) break;
            }
            catch { break; }
            if (ct.WaitHandle.WaitOne(300)) throw new OperationCanceledException(ct);
        }

        // Fallback estrito: somente botão/submit de autenticação; nunca links de recuperação.
        try
        {
            var passwordStillVisible = driver.FindElements(By.CssSelector("input[type=password]")).Any(Display);
            if (passwordStillVisible)
            {
                var submit = driver.FindElements(By.CssSelector("button,input[type=submit],input[type=button]"))
                    .FirstOrDefault(x => Display(x) &&
                        ContainsAny(Normalize(Context(x)), ["entrar", "acessar", "login"]) &&
                        !ContainsAny(Normalize(Context(x)), ["recuper", "esqueci", "nova senha"]));
                if (submit is not null)
                {
                    try { submit.Click(); }
                    catch { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", submit); }
                }
            }
        }
        catch { }

        WaitForDomIdle(driver, ct, 6);
        AcceptAlert(driver);
        WaitReady(driver, ct, 90);
        var body = Normalize(SafeBody(driver));
        if (ContainsAny(body, ["senha invalida", "usuario invalido", "acesso negado"]))
            throw new InvalidOperationException("O SINFOPPES recusou o CPF ou a senha.");
    }

    private static void FillSinfoppesFilters(IWebDriver driver, SinfoppesCriticizedSettings settings)
    {
        // No SINFOPPES legado a ordem real dos combos é MÊS, ANO e CORRIDA.
        // A versão anterior aplicava 2026 ao primeiro combo (mês), originando
        // "Opção não encontrada: 2026". Primeiro tentamos identificar cada campo
        // pela assinatura; o fallback usa a ordem comprovada do formulário antigo.
        var selects = driver.FindElements(By.TagName("select")).Where(Display).ToList();
        var filled = new HashSet<IWebElement>();

        foreach (var select in selects)
        {
            var sign = Normalize(Signature(select));
            if (ContainsAny(sign, ["ano", "exercicio"]))
            {
                SelectFlexible(driver, select, [settings.Year.ToString(CultureInfo.InvariantCulture)]);
                filled.Add(select);
            }
            else if (ContainsAny(sign, ["mes", "competencia"]))
            {
                SelectFlexible(driver, select, MonthAlternatives(settings.Month));
                filled.Add(select);
            }
            else if (ContainsAny(sign, ["corrida", "processamento"]))
            {
                SelectFlexible(driver, select, [settings.Run, Normalize(settings.Run), Regex.Replace(settings.Run, @"\D", string.Empty)]);
                filled.Add(select);
            }
        }

        if (selects.Count < 3) throw new InvalidOperationException("Os campos Mês, Ano e Corrida não foram encontrados no SINFOPPES.");

        // Fallback do ASP antigo: selects[0] = mês, selects[1] = ano, selects[2] = corrida.
        if (!filled.Contains(selects[0])) SelectFlexible(driver, selects[0], MonthAlternatives(settings.Month));
        if (!filled.Contains(selects[1])) SelectFlexible(driver, selects[1], [settings.Year.ToString(CultureInfo.InvariantCulture)]);
        if (!filled.Contains(selects[2])) SelectFlexible(driver, selects[2], [settings.Run, Normalize(settings.Run), Regex.Replace(settings.Run, @"\D", string.Empty)]);
    }

    private static void SwitchToFormContext(IWebDriver driver)
    {
        driver.SwitchTo().DefaultContent();
        if (driver.FindElements(By.TagName("select")).Count >= 4) return;
        var frames = driver.FindElements(By.CssSelector("frame,iframe"));
        foreach (var frame in frames)
        {
            try { driver.SwitchTo().DefaultContent(); driver.SwitchTo().Frame(frame); if (driver.FindElements(By.TagName("select")).Count >= 4) return; } catch { }
        }
        driver.SwitchTo().DefaultContent();
    }
    private static void SwitchToResultContext(IWebDriver driver)
    {
        try { driver.SwitchTo().DefaultContent(); } catch { }
        var frames = driver.FindElements(By.CssSelector("frame,iframe"));
        foreach (var frame in frames)
        {
            try { driver.SwitchTo().DefaultContent(); driver.SwitchTo().Frame(frame); if (SafeBody(driver).Length > 100) return; } catch { }
        }
        try { driver.SwitchTo().DefaultContent(); } catch { }
    }

    private static void SelectFlexible(IWebDriver driver, IWebElement element, IEnumerable<string> alternatives, CancellationToken ct = default)
    {
        var rawAlternatives = alternatives.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var alts = rawAlternatives.Select(Normalize).Where(x => x.Length > 0).Distinct().ToList();
        var options = new SelectElement(element).Options;
        var match = options.FirstOrDefault(x => alts.Contains(Normalize(x.Text)) || alts.Contains(Normalize(x.GetAttribute("value"))))
                    ?? options.FirstOrDefault(x => alts.Any(a =>
                    {
                        var text = Normalize(x.Text);
                        var value = Normalize(x.GetAttribute("value"));
                        return text.Contains(a, StringComparison.Ordinal) || a.Contains(text, StringComparison.Ordinal)
                               || value.Contains(a, StringComparison.Ordinal) || a.Contains(value, StringComparison.Ordinal);
                    }));
        if (match is null)
        {
            var available = string.Join(" | ", options.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).Take(20));
            throw new InvalidOperationException($"Opção não encontrada: {string.Join(", ", rawAlternatives)}. Disponíveis: {available}");
        }

        ((IJavaScriptExecutor)driver).ExecuteScript(
            """
            const select=arguments[0], option=arguments[1];
            option.selected=true;
            select.selectedIndex=Array.from(select.options).indexOf(option);
            select.value=option.value;
            ['input','change','blur'].forEach(name=>select.dispatchEvent(new Event(name,{bubbles:true})));
            if (typeof select.onchange === 'function') { try { select.onchange(); } catch(e) {} }
            """, element, match);
        WaitForDomIdle(driver, ct, 2);
        AcceptAlert(driver);
    }

    private static bool ClickByText(IWebDriver driver, IEnumerable<string> labels, bool throwIfMissing = true)
    {
        var wanted = labels.Select(Normalize).ToArray();
        foreach (var context in new[] { 0, 1 })
        {
            if (context == 1)
            {
                try
                {
                    driver.SwitchTo().DefaultContent();
                    foreach (var frame in driver.FindElements(By.CssSelector("frame,iframe")))
                    {
                        try { driver.SwitchTo().DefaultContent(); driver.SwitchTo().Frame(frame); if (TryClick(driver, wanted)) return true; } catch { }
                    }
                }
                catch { }
            }
            else if (TryClick(driver, wanted)) return true;
        }
        if (throwIfMissing) throw new InvalidOperationException($"Botão não encontrado: {string.Join("/", labels)}.");
        return false;
    }
    private static bool TryClick(IWebDriver driver, string[] wanted)
    {
        foreach (var element in driver.FindElements(By.CssSelector("button,input[type=button],input[type=submit],input[type=image],a,[onclick]")))
        {
            if (!Display(element)) continue; var text = Normalize(Context(element)); if (!wanted.Any(x => text.Contains(x, StringComparison.Ordinal))) continue;
            try { element.Click(); } catch { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element); } return true;
        }
        return false;
    }

    private static void SwitchToNewWindow(IWebDriver driver, HashSet<string> handlesBefore, string previousUrl, CancellationToken ct)
    {
        var end = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var newHandle = driver.WindowHandles.FirstOrDefault(x => !handlesBefore.Contains(x));
                if (!string.IsNullOrWhiteSpace(newHandle))
                {
                    driver.SwitchTo().Window(newHandle);
                    return;
                }
                if (!string.Equals(driver.Url, previousUrl, StringComparison.OrdinalIgnoreCase)) return;
                var body = Normalize(SafeBody(driver));
                if (HasCpexResult(driver, body) || ContainsAny(body, ["nao existe", "nenhum", "sem inconsistencia"])) return;
            }
            catch (UnhandledAlertException) { return; }
            catch { }
            if (ct.WaitHandle.WaitOne(350)) throw new OperationCanceledException(ct);
        }
    }

    private static string AcceptAlerts(IWebDriver driver)
    {
        var messages = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                var alert = driver.SwitchTo().Alert();
                var text = alert.Text;
                alert.Accept();
                if (!string.IsNullOrWhiteSpace(text)) messages.Add(text);
                WaitForAlert(driver, TimeSpan.FromMilliseconds(120));
            }
            catch (NoAlertPresentException) { break; }
            catch { break; }
        }
        return string.Join(" ", messages);
    }

    private static bool HasCpexResult(IWebDriver driver, string normalizedBody)
    {
        if (ContainsAny(normalizedBody, ["exportar", "imprimir", "inconsistencia bancaria"])) return true;
        try
        {
            foreach (var table in driver.FindElements(By.TagName("table")))
            {
                var text = Normalize(table.Text);
                if (text.Contains("cpf", StringComparison.Ordinal) && text.Contains("nome", StringComparison.Ordinal) &&
                    (text.Contains("conta", StringComparison.Ordinal) || text.Contains("banco", StringComparison.Ordinal))) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool SinfoppesBodyHasRecords(string body)
    {
        var text = Normalize(body);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var headers = text.Contains("dados militar", StringComparison.Ordinal) && text.Contains("justificativa", StringComparison.Ordinal)
                      || text.Contains("dados om", StringComparison.Ordinal) && text.Contains("dados lancamento", StringComparison.Ordinal);
        var rowMarkers = ContainsAny(text, ["nome", "prec cp", "identidade", "codigo", "rubrica", "valor"]);
        return headers && rowMarkers;
    }

    private static bool IsSinfoppesNoData(string body)
    {
        if (SinfoppesBodyHasRecords(body)) return false;
        var text = Normalize(body);
        return ContainsAny(text, ["nao temos lancamentos criticados", "sem resultado", "nenhum registro", "nao existe", "nao foram encontrados"]);
    }

    private static List<List<string>> ScrapeSinfoppesAllRows(IWebDriver driver, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        var direct = ScrapeSinfoppesDataTables(driver);
        if (direct.Count > 0) return direct;

        SetSinfoppesPageLength(driver, 100);
        var result = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 60; page++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in ScrapeVisibleSinfoppesRows(driver))
            {
                var key = string.Join("\u001f", row);
                if (seen.Add(key)) result.Add(row);
            }
            progress?.Report(new AutomationProgress { Percent = Math.Min(70, 50 + page), Message = $"Lendo página {page} · {result.Count} registro(s)..." });
            if (!ClickSinfoppesNextPage(driver, ct)) break;
        }
        return result;
    }

    private static List<List<string>> ScrapeSinfoppesDataTables(IWebDriver driver)
    {
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript(
                """
                function htmlToText(v) {
                    if (v === null || v === undefined) return '';
                    const div = document.createElement('div');
                    div.innerHTML = String(v).replace(/<br\s*\/?>/gi, '\n');
                    return (div.innerText || div.textContent || String(v) || '').trim();
                }
                const out = [];
                if (window.jQuery && window.jQuery.fn && window.jQuery.fn.DataTable) {
                    document.querySelectorAll('table').forEach(function(t) {
                        try {
                            const headers = Array.from(t.querySelectorAll('thead th')).map(th => (th.innerText || th.textContent || '').trim()).join(' ').toUpperCase();
                            if (!(headers.includes('DADOS') || headers.includes('JUSTIFICATIVA') || headers.includes('MILITAR'))) return;
                            const dt = window.jQuery(t).DataTable();
                            dt.rows({search:'applied'}).data().toArray().forEach(function(row) {
                                const cells = Array.isArray(row) ? row.map(htmlToText) : (row && typeof row === 'object' ? Object.values(row).map(htmlToText) : [htmlToText(row)]);
                                out.push(cells);
                            });
                        } catch(e) {}
                    });
                }
                return out;
                """);
            return ParseRows(raw);
        }
        catch { return []; }
    }

    private static List<List<string>> ParseRows(object? raw)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(raw));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<List<string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rowElement in document.RootElement.EnumerateArray())
            {
                if (rowElement.ValueKind != JsonValueKind.Array) continue;
                var row = rowElement.EnumerateArray().Select(x => CleanCell(x.ToString())).Take(5).ToList();
                if (row.Count < 4 || row.All(string.IsNullOrWhiteSpace)) continue;
                while (row.Count < 5) row.Add(string.Empty);
                var joined = Normalize(string.Join(" ", row));
                if (joined.Contains("dados militar", StringComparison.Ordinal) && joined.Contains("justificativa", StringComparison.Ordinal)) continue;
                var key = string.Join("\u001f", row);
                if (seen.Add(key)) result.Add(row);
            }
            return result;
        }
        catch { return []; }
    }

    private static IWebElement FindSinfoppesTable(IWebDriver driver)
    {
        IWebElement? best = null;
        var bestScore = 0;
        foreach (var table in driver.FindElements(By.TagName("table")))
        {
            try
            {
                var text = Normalize(table.Text);
                var score = new[] { "dados om", "dados militar", "dados lancamento", "justificativa" }.Count(marker => text.Contains(marker, StringComparison.Ordinal)) * 2;
                if (text.Contains("nome", StringComparison.Ordinal)) score++;
                if (score > bestScore) { best = table; bestScore = score; }
            }
            catch { }
        }
        return bestScore > 0 && best is not null ? best : throw new InvalidOperationException("Tabela de resultados não encontrada.");
    }

    private static List<List<string>> ScrapeVisibleSinfoppesRows(IWebDriver driver)
    {
        var result = new List<List<string>>();
        var table = FindSinfoppesTable(driver);
        var rows = table.FindElements(By.CssSelector("tbody tr"));
        if (rows.Count == 0) rows = table.FindElements(By.CssSelector("tr"));
        foreach (var tr in rows)
        {
            try
            {
                var cells = tr.FindElements(By.CssSelector("td"));
                if (cells.Count < 4) continue;
                var row = cells.Take(5).Select(td => CleanCell(((IJavaScriptExecutor)driver).ExecuteScript("return arguments[0].innerText || arguments[0].textContent || '';", td)?.ToString())).ToList();
                while (row.Count < 5) row.Add(string.Empty);
                if (row.Any(x => !string.IsNullOrWhiteSpace(x))) result.Add(row);
            }
            catch { }
        }
        return result;
    }

    private static void SetSinfoppesPageLength(IWebDriver driver, int length)
    {
        try
        {
            foreach (var select in driver.FindElements(By.TagName("select")))
            {
                var option = new SelectElement(select).Options.FirstOrDefault(x => Normalize(x.Text) == length.ToString(CultureInfo.InvariantCulture) || x.GetAttribute("value") == length.ToString(CultureInfo.InvariantCulture));
                if (option is null) continue;
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].selected=true; arguments[0].parentElement.value=arguments[0].value; arguments[0].parentElement.dispatchEvent(new Event('change',{bubbles:true}));", option);
                WaitForDomIdle(driver, CancellationToken.None, 4);
                return;
            }
        }
        catch { }
    }

    private static bool ClickSinfoppesNextPage(IWebDriver driver, CancellationToken ct)
    {
        var candidates = driver.FindElements(By.CssSelector("a.paginate_button.next,button.paginate_button.next,li.next a,a[aria-label='Next'],a[aria-label='Próximo'],a[aria-label='Proximo']")).ToList();
        candidates.AddRange(driver.FindElements(By.XPath("//*[self::a or self::button][contains(translate(normalize-space(.),'abcdefghijklmnopqrstuvwxyz','ABCDEFGHIJKLMNOPQRSTUVWXYZ'),'PROXIMO') or contains(translate(normalize-space(.),'abcdefghijklmnopqrstuvwxyz','ABCDEFGHIJKLMNOPQRSTUVWXYZ'),'NEXT')]")));
        foreach (var element in candidates)
        {
            try
            {
                var disabled = Normalize(element.GetAttribute("class")).Contains("disabled", StringComparison.Ordinal) || string.Equals(element.GetAttribute("aria-disabled"), "true", StringComparison.OrdinalIgnoreCase);
                if (disabled || !Display(element)) continue;
                var before = Normalize(FindSinfoppesTable(driver).Text[..Math.Min(300, FindSinfoppesTable(driver).Text.Length)]);
                try { element.Click(); } catch { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element); }
                var end = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < end)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ct.WaitHandle.WaitOne(250)) throw new OperationCanceledException(ct);
                    var tableText = FindSinfoppesTable(driver).Text;
                    var after = Normalize(tableText[..Math.Min(300, tableText.Length)]);
                    if (!string.Equals(before, after, StringComparison.Ordinal)) return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return false;
    }

    private static string CleanCell(string? value)
    {
        var text = value ?? string.Empty;
        text = Regex.Replace(text, @"(?i)<\s*br\s*/?>", "\n");
        text = Regex.Replace(text, @"(?i)</\s*(div|p|span|li|tr|td|th)\s*>", "\n");
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        return string.Join("\n", Regex.Split(text, "[\\r\\n]+").Select(x => Regex.Replace(x, @"\s+", " ").Trim()).Where(x => x.Length > 0));
    }

    private static string BuildSinfoppesHtml(IReadOnlyList<List<string>> rows, SinfoppesCriticizedSettings settings)
    {
        var body = new StringBuilder();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index].Take(5).ToList();
            while (row.Count < 5) row.Add(string.Empty);
            var number = string.IsNullOrWhiteSpace(row[0]) ? (index + 1).ToString(CultureInfo.InvariantCulture) : row[0];
            body.Append("<tr><td class='num'>").Append(Html(number)).Append("</td><td class='om'>").Append(HtmlLines(row[1])).Append("</td><td class='mil'>").Append(HtmlLines(row[2])).Append("</td><td class='lan'>").Append(HtmlLines(row[3])).Append("</td><td class='jus'>").Append(HtmlLines(row[4])).Append("</td></tr>");
        }

        var html = new StringBuilder();
        html.Append("""
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>Lançamentos Criticados</title><style>
@page { size:A4 landscape; margin:9mm 8mm; } *{box-sizing:border-box} body{font-family:Arial,'Segoe UI',sans-serif;color:#1f2937;margin:0;font-size:9px} .top{border-bottom:2px solid #1f5f8b;padding-bottom:8px;margin-bottom:10px} h1{margin:0 0 6px;color:#173f66;font-size:18px;text-transform:uppercase} .meta{display:grid;grid-template-columns:repeat(5,auto);gap:6px 14px;font-size:10px} table{width:100%;border-collapse:collapse;table-layout:fixed} thead{display:table-header-group} tr{page-break-inside:avoid} th{background:#1f5f8b;color:white;border:1px solid #164c72;padding:6px 5px;font-size:9px} td{border:1px solid #d1d5db;vertical-align:top;padding:5px;line-height:1.28;overflow-wrap:anywhere} tbody tr:nth-child(even) td{background:#f8fafc} .num{width:4%;text-align:center;font-weight:bold} .om{width:15%} .mil{width:20%} .lan{width:16%} .jus{width:45%}
</style></head><body><div class="top"><h1>Relação de Lançamentos Criticados pelo CPEX / Outra OM</h1><div class="meta"><span><b>Mês:</b> 
""");
        html.Append(Html(settings.Month));
        html.Append("</span><span><b>Ano:</b> ").Append(settings.Year);
        html.Append("</span><span><b>Corrida:</b> ").Append(Html(settings.Run));
        html.Append("</span><span><b>Total:</b> ").Append(rows.Count);
        html.Append("</span><span><b>Gerado:</b> ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")));
        html.Append("</span></div></div><table><thead><tr><th class=\"num\">#</th><th class=\"om\">Dados OM</th><th class=\"mil\">Dados Militar</th><th class=\"lan\">Dados Lançamento</th><th class=\"jus\">Justificativa</th></tr></thead><tbody>");
        html.Append(body);
        html.Append("</tbody></table></body></html>");
        return html.ToString();
    }

    private static string Html(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    private static string HtmlLines(string? value) => string.Join("<br>", CleanCell(value).Split('\n').Where(x => x.Length > 0).Select(Html));

    private static void SetValue(IWebDriver driver, IWebElement element, string value)
    {
        try { element.Clear(); element.SendKeys(value); }
        catch { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].value=arguments[1]; arguments[0].dispatchEvent(new Event('input',{bubbles:true})); arguments[0].dispatchEvent(new Event('change',{bubbles:true}));", element, value); }
    }
    private static void WaitReady(IWebDriver driver, CancellationToken ct, int seconds)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(seconds));
        wait.Until(d => { ct.ThrowIfCancellationRequested(); try { return ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() is "interactive" or "complete"; } catch { return false; } });
    }

    private static bool WaitUntil(IWebDriver driver, Func<IWebDriver, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var wait = new WebDriverWait(driver, timeout)
            {
                PollingInterval = TimeSpan.FromMilliseconds(250)
            };
            wait.IgnoreExceptionTypes(typeof(WebDriverException), typeof(InvalidOperationException));
            return wait.Until(d =>
            {
                ct.ThrowIfCancellationRequested();
                return predicate(d);
            });
        }
        catch (WebDriverTimeoutException) { return false; }
    }

    private static void WaitForDomIdle(IWebDriver driver, CancellationToken ct, int seconds)
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
        }, TimeSpan.FromSeconds(seconds), ct);

    private static string BodySignature(IWebDriver driver)
    {
        try
        {
            var text = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return (document.body && document.body.innerText || '').slice(0, 2000);"), CultureInfo.InvariantCulture) ?? string.Empty;
            return Normalize(text);
        }
        catch { return string.Empty; }
    }

    private static bool AlertPresent(IWebDriver driver)
    {
        try { _ = driver.SwitchTo().Alert(); return true; }
        catch { return false; }
    }

    private static void WaitForAlert(IWebDriver driver, TimeSpan timeout)
    {
        try
        {
            var wait = new WebDriverWait(driver, timeout)
            {
                PollingInterval = TimeSpan.FromMilliseconds(40)
            };
            wait.Until(d => AlertPresent(d));
        }
        catch { }
    }

    private static void AcceptAlert(IWebDriver driver) { try { driver.SwitchTo().Alert().Accept(); } catch (NoAlertPresentException) { } catch { } }
    private static bool Display(IWebElement x) { try { return x.Displayed && x.Enabled; } catch { return false; } }
    private static bool IsButton(IWebElement x) => (x.GetAttribute("type") ?? string.Empty).ToLowerInvariant() is "submit" or "button" or "image";
    private static string Signature(IWebElement x) => string.Join(" ", x.GetAttribute("name"), x.GetAttribute("id"), x.GetAttribute("title"), x.GetAttribute("placeholder"), x.GetAttribute("value"), x.Text);
    private static string Context(IWebElement x) => string.Join(" ", x.Text, x.GetAttribute("value"), x.GetAttribute("title"), x.GetAttribute("alt"), x.GetAttribute("href"), x.GetAttribute("src"), x.GetAttribute("onclick"));
    private static string SafeBody(IWebDriver driver) { try { return driver.FindElement(By.TagName("body")).Text ?? string.Empty; } catch { return string.Empty; } }
    private static bool ContainsAny(string text, IEnumerable<string> values) => values.Any(x => text.Contains(Normalize(x), StringComparison.Ordinal));
    private static string Normalize(string? value)
    {
        var s = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        s = new string(s.Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        return Regex.Replace(s, @"[^a-z0-9]+", " ").Trim();
    }
    private static IEnumerable<string> MonthAlternatives(string? month)
    {
        var monthValue = month ?? string.Empty;
        var months = new[] { "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO" };
        var norm = Normalize(monthValue);
        var index = Array.FindIndex(months, x => Normalize(x) == norm);
        if (index < 0 && int.TryParse(Regex.Replace(monthValue, @"\D", string.Empty), out var number) && number is >= 1 and <= 12)
            index = number - 1;
        if (index < 0 || norm.Contains("todo", StringComparison.Ordinal))
            return new[] { monthValue, "TODOS", "TODAS", "00", "0" };

        var selectedMonth = months[index];
        return new[]
        {
            monthValue,
            selectedMonth,
            (index + 1).ToString("00", CultureInfo.InvariantCulture),
            (index + 1).ToString(CultureInfo.InvariantCulture),
            selectedMonth[..Math.Min(3, selectedMonth.Length)]
        };
    }

    private static IEnumerable<string> RegionAlternatives(string? region)
    {
        var regionValue = region ?? string.Empty;
        if (Normalize(regionValue).Contains("toda", StringComparison.Ordinal))
            return new[] { regionValue, "TODAS", "TODOS", "0", "00" };

        var number = Regex.Replace(regionValue, @"\D", string.Empty);
        return new[] { regionValue, number, number.PadLeft(2, '0'), $"{number}ª RM", $"{number}º RM", $"{number} RM" };
    }
    private static void PrintPdf(IWebDriver driver, string path, bool landscape)
    {
        if (driver is not ChromiumDriver chromium) throw new InvalidOperationException("A geração de PDF exige Microsoft Edge.");
        var result = chromium.ExecuteCdpCommand("Page.printToPDF", new Dictionary<string, object?> { ["printBackground"] = true, ["landscape"] = landscape, ["preferCSSPageSize"] = false, ["paperWidth"] = landscape ? 11.69 : 8.27, ["paperHeight"] = landscape ? 8.27 : 11.69, ["marginTop"] = 0.25, ["marginBottom"] = 0.25, ["marginLeft"] = 0.25, ["marginRight"] = 0.25, ["scale"] = 0.9 });
        var json = JsonSerializer.Serialize(result); using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || string.IsNullOrWhiteSpace(data.GetString())) throw new InvalidOperationException("O navegador não retornou o PDF.");
        File.WriteAllBytes(path, Convert.FromBase64String(data.GetString()!));
    }
    private static void ExpandDataTable(IWebDriver driver)
    {
        try { ((IJavaScriptExecutor)driver).ExecuteScript("document.querySelectorAll('select[name$=_length]').forEach(s=>{let o=document.createElement('option');o.value='-1';o.text='Todos';s.appendChild(o);s.value='-1';s.dispatchEvent(new Event('change',{bubbles:true}));});"); WaitForDomIdle(driver, CancellationToken.None, 4); } catch { }
    }
    private static int CountRows(IWebDriver driver) { try { var value = ((IJavaScriptExecutor)driver).ExecuteScript("return document.querySelectorAll('table tbody tr').length;"); return Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return 0; } }
    private static HashSet<string> SnapshotDownloads(string directory) => Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
    private static string? WaitForDownload(string directory, HashSet<string> before, CancellationToken ct, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = Directory.EnumerateFiles(directory).Where(x => !before.Contains(Path.GetFullPath(x)) && !x.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) && !x.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (candidate is not null) return candidate;
            if (ct.WaitHandle.WaitOne(500)) throw new OperationCanceledException(ct);
        }
        return null;
    }
    private static void RememberCodom(BankInconsistencySettings settings)
    {
        var codom = MilitaryFormatting.Digits(settings.Codom); if (string.IsNullOrWhiteSpace(codom)) return;
        settings.CodomHistory.RemoveAll(x => MilitaryFormatting.Digits(x) == codom); settings.CodomHistory.Insert(0, codom); if (settings.CodomHistory.Count > 15) settings.CodomHistory.RemoveRange(15, settings.CodomHistory.Count - 15);
    }
    private static string Safe(string value) => Regex.Replace(Normalize(value), @"\s+", "_");
    private static string UniquePath(string path) { if (!File.Exists(path)) return path; for (var i = 2; i < 1000; i++) { var candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}_{i}{Path.GetExtension(path)}"); if (!File.Exists(candidate)) return candidate; } return path; }
    public void Dispose() { try { CloseBrowserAsync().GetAwaiter().GetResult(); } catch { } _driverGate.Dispose(); _automationGate.Dispose(); }
}
