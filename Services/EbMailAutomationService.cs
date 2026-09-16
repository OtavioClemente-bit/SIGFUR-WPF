using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class EbMailAutomationService
{
    public const string LoginUrl = "https://ebmail.webmail.eb.mil.br/";
    public const string ModernUrl = "https://ebmail.webmail.eb.mil.br/modern/";
    public const string ComposeUrl = "https://ebmail.webmail.eb.mil.br/modern/email/Drafts/message/new?full=true";

    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private static IWebDriver? SharedDriver;
    private readonly AppPaths _paths;
    private readonly LogService _log;

    public EbMailAutomationService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<EbMailAutomationResult> PrepareAsync(
        EbMailComposeRequest request,
        IProgress<EbMailProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = request.Documents
            .SelectMany(x => x.SelectedFilePaths)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = files.Where(x => !File.Exists(x)).ToList();
        if (missing.Count > 0) throw new FileNotFoundException("Um ou mais anexos não foram encontrados: " + string.Join(", ", missing.Select(Path.GetFileName)));
        if (files.Count == 0) throw new InvalidOperationException("Selecione ao menos um BI ou ADT para enviar.");

        await BrowserGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => PrepareCore(request, files, progress, cancellationToken), cancellationToken);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    public async Task OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        await BrowserGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                var driver = EnsureDriver();
                Navigate(driver, ModernUrl, force: true);
            }, cancellationToken);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    private EbMailAutomationResult PrepareCore(
        EbMailComposeRequest request,
        IReadOnlyList<string> files,
        IProgress<EbMailProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new EbMailProgress("NAVEGADOR", "Abrindo a sessão segura do EBMail…", 5));
        var driver = EnsureDriver();
        // A sessão, os cookies do 2FA e as credenciais salvas pelo navegador ficam no
        // perfil exclusivo do SIGFUR. Abrir /modern/ primeiro permite reutilizar o
        // dispositivo confiável sem obrigar um novo login a cada mensagem.
        Navigate(driver, ModernUrl, force: true);

        if (!WaitUntil(driver, IsModernSession, TimeSpan.FromSeconds(12), cancellationToken) && !IsLoginPage(driver))
            Navigate(driver, LoginUrl, force: true);

        if (IsLoginPage(driver))
        {
            progress?.Report(new EbMailProgress("LOGIN", "Conclua o login e o 2FA no navegador. Aceite salvar a senha e marque este dispositivo como confiável; o perfil protegido será reutilizado.", 12));
            if (!WaitUntil(driver, d => IsModernSession(d), TimeSpan.FromMinutes(12), cancellationToken))
                throw new TimeoutException("O tempo para concluir o login no EBMail terminou. Tente novamente quando estiver pronto para autenticar.");
        }

        progress?.Report(new EbMailProgress("MENSAGEM", "Abrindo uma nova mensagem…", 25));
        Navigate(driver, ComposeUrl + "&tabid=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), force: true);
        if (IsLoginPage(driver))
        {
            progress?.Report(new EbMailProgress("LOGIN", "A sessão expirou. Conclua novamente o login no navegador.", 25));
            if (!WaitUntil(driver, d => IsModernSession(d), TimeSpan.FromMinutes(12), cancellationToken))
                throw new TimeoutException("O login do EBMail não foi concluído.");
            Navigate(driver, ComposeUrl + "&tabid=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), force: true);
        }

        if (!WaitComposeReady(driver, TimeSpan.FromSeconds(35), cancellationToken))
        {
            Navigate(driver, ModernUrl, force: true);
            if (!WaitUntil(driver, IsModernSession, TimeSpan.FromSeconds(45), cancellationToken))
                throw new InvalidOperationException("A caixa principal do EBMail não ficou disponível após o login.");
            var newEmail = FindClickableByText(driver, ["novo e-mail", "novo email", "new email", "compor"])
                           ?? throw new InvalidOperationException("A opção Novo e-mail não foi localizada na caixa principal do EBMail.");
            newEmail.Click();
            if (!WaitComposeReady(driver, TimeSpan.FromSeconds(45), cancellationToken))
                throw new InvalidOperationException("O editor de nova mensagem do EBMail não ficou disponível.");
        }
        progress?.Report(new EbMailProgress("DESTINATÁRIOS", "Preenchendo destinatários…", 38));
        FillRecipients(driver, request.To, "para", ["to", "para", "destinatario", "recipient"]);
        if (!string.IsNullOrWhiteSpace(request.Cc))
        {
            RevealRecipientField(driver, ["cc"]);
            FillRecipients(driver, request.Cc, "cc", ["cc", "copia"]);
        }
        if (!string.IsNullOrWhiteSpace(request.Bcc))
        {
            RevealRecipientField(driver, ["cco", "bcc"]);
            FillRecipients(driver, request.Bcc, "cco", ["cco", "bcc", "copia oculta"]);
        }

        progress?.Report(new EbMailProgress("CONTEÚDO", "Preenchendo assunto e corpo da mensagem…", 52));
        var subject = FindBestTextControl(driver, ["assunto", "subject"], allowContentEditable: false)
                      ?? throw new InvalidOperationException("O campo Assunto não foi localizado na tela atual do EBMail.");
        ReplaceText(subject, request.Subject);
        FillBody(driver, request.Body);

        progress?.Report(new EbMailProgress("ANEXOS", $"Anexando {files.Count} documento(s)…", 68));
        AttachFiles(driver, files, cancellationToken);

        progress?.Report(new EbMailProgress("REVISÃO", "Mensagem preparada. Confira destinatários, texto e anexos.", 90));
        if (!request.SendAutomatically)
            return new EbMailAutomationResult(false, "Mensagem preparada no EBMail. Confira tudo e clique em Enviar quando estiver pronto.");

        var send = FindClickableByText(driver, ["enviar", "send"])
                   ?? throw new InvalidOperationException("O botão Enviar não foi localizado. A mensagem foi mantida aberta para revisão manual.");
        send.Click();
        var sent = WaitUntil(driver, d => !IsComposePage(d) || PageContainsAny(d, ["mensagem enviada", "message sent", "enviado"]), TimeSpan.FromSeconds(30), cancellationToken);
        if (!sent) throw new InvalidOperationException("O EBMail não confirmou o envio. Confira a mensagem aberta no navegador antes de tentar novamente.");
        progress?.Report(new EbMailProgress("CONCLUÍDO", "O EBMail confirmou o envio da mensagem.", 100));
        return new EbMailAutomationResult(true, "Mensagem enviada pelo EBMail.");
    }

    private IWebDriver EnsureDriver()
    {
        if (SharedDriver is not null && IsAlive(SharedDriver)) return SharedDriver;
        Directory.CreateDirectory(_paths.EbMailBrowserProfileDirectory);
        Exception? edgeError = null;
        try
        {
            var options = new EdgeOptions();
            ConfigureOptions(options, Path.Combine(_paths.EbMailBrowserProfileDirectory, "edge"));
            var service = EdgeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            SharedDriver = new EdgeDriver(service, options, TimeSpan.FromSeconds(90));
            return SharedDriver;
        }
        catch (Exception ex) { edgeError = ex; }

        try
        {
            var options = new ChromeOptions();
            ConfigureOptions(options, Path.Combine(_paths.EbMailBrowserProfileDirectory, "chrome"));
            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            SharedDriver = new ChromeDriver(service, options, TimeSpan.FromSeconds(90));
            return SharedDriver;
        }
        catch (Exception chromeError)
        {
            throw new InvalidOperationException("Não foi possível abrir Microsoft Edge nem Google Chrome para acessar o EBMail.", new AggregateException(edgeError!, chromeError));
        }
    }

    private static void ConfigureOptions(ChromiumOptions options, string profileDirectory)
    {
        Directory.CreateDirectory(profileDirectory);
        options.AddArgument($"--user-data-dir={profileDirectory}");
        options.AddArgument("--profile-directory=Default");
        options.AddArgument("--start-maximized");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--disable-popup-blocking");
        options.AddExcludedArgument("enable-automation");
        options.AddUserProfilePreference("credentials_enable_service", true);
        options.AddUserProfilePreference("profile.password_manager_enabled", true);
        options.AddUserProfilePreference("profile.default_content_setting_values.notifications", 2);
    }

    private static void Navigate(IWebDriver driver, string url, bool force = false)
    {
        if (!force && driver.Url.StartsWith(url, StringComparison.OrdinalIgnoreCase)) return;
        try { driver.Navigate().GoToUrl(url); }
        catch (WebDriverTimeoutException) { }
    }

    private static bool IsAlive(IWebDriver driver)
    {
        try { return driver.WindowHandles.Count > 0; }
        catch { return false; }
    }

    private static bool IsLoginPage(IWebDriver driver)
    {
        try { return driver.FindElements(By.CssSelector("input[type='password']")).Any(IsVisible); }
        catch { return false; }
    }

    private static bool IsModernSession(IWebDriver driver)
    {
        try { return driver.Url.Contains("/modern/", StringComparison.OrdinalIgnoreCase) && !IsLoginPage(driver); }
        catch { return false; }
    }

    private static bool IsComposePage(IWebDriver driver)
    {
        try { return driver.Url.Contains("/message/new", StringComparison.OrdinalIgnoreCase) || PageContainsAny(driver, ["novo e-mail", "assunto", "anexar"]); }
        catch { return false; }
    }

    private static bool WaitComposeReady(IWebDriver driver, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!WaitUntil(driver, d =>
            {
                try { return ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() is "interactive" or "complete"; }
                catch { return false; }
            }, TimeSpan.FromSeconds(45), cancellationToken))
            throw new TimeoutException("O editor de mensagens do EBMail não terminou de carregar.");
        return WaitUntil(driver, d => FindBestTextControl(d, ["assunto", "subject"], false) is not null, timeout, cancellationToken);
    }

    private static void FillRecipients(IWebDriver driver, string raw, string label, IReadOnlyList<string> hints)
    {
        var values = SplitAddresses(raw);
        if (values.Count == 0)
        {
            if (label == "para") throw new InvalidOperationException("Informe ao menos um destinatário no campo Para.");
            return;
        }
        var field = FindRecipientControl(driver, label, hints)
                    ?? throw new InvalidOperationException($"O campo {label.ToUpperInvariant()} não foi localizado no EBMail.");
        field.Click();
        foreach (var value in values)
        {
            field.SendKeys(value);
            field.SendKeys(Keys.Enter);
            if (!WaitUntil(driver, d => PageContainsAny(d, [value]), TimeSpan.FromSeconds(2), CancellationToken.None))
                field.SendKeys(Keys.Tab);
        }
    }

    private static List<string> SplitAddresses(string value)
        => Regex.Split(value ?? string.Empty, @"[;,\r\n]+")
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void RevealRecipientField(IWebDriver driver, IReadOnlyList<string> labels)
    {
        var control = FindClickableByText(driver, labels);
        if (control is null) return;
        try { control.Click(); Thread.Sleep(250); } catch { }
    }

    private static IWebElement? FindBestTextControl(IWebDriver driver, IReadOnlyList<string> hints, bool allowContentEditable)
    {
        driver.SwitchTo().DefaultContent();
        var selector = allowContentEditable ? "input:not([type='hidden']),textarea,[contenteditable='true']" : "input:not([type='hidden']),textarea";
        IWebElement? best = null;
        var bestScore = 0;
        foreach (var element in driver.FindElements(By.CssSelector(selector)))
        {
            if (!IsVisible(element) || !element.Enabled) continue;
            var descriptor = Descriptor(element);
            var score = hints.Sum(hint => descriptor.Contains(Normalize(hint), StringComparison.OrdinalIgnoreCase) ? 20 : 0);
            if (element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (score <= bestScore) continue;
            best = element;
            bestScore = score;
        }
        return best;
    }

    private static IWebElement? FindRecipientControl(IWebDriver driver, string label, IReadOnlyList<string> hints)
    {
        driver.SwitchTo().DefaultContent();
        var allHints = hints.Select(Normalize).Append(Normalize(label)).Distinct().ToList();
        var controls = driver.FindElements(By.CssSelector("input:not([type='hidden']),textarea,[contenteditable='true']"))
            .Where(element => IsVisible(element) && element.Enabled)
            .ToList();

        var strong = controls
            .Select(element =>
            {
                var semantic = Normalize(string.Join(" ",
                    SafeAttribute(element, "aria-label"),
                    SafeAttribute(element, "placeholder"),
                    SafeAttribute(element, "name"),
                    SafeAttribute(element, "id"),
                    SafeAttribute(element, "data-testid")));
                var score = allHints.Sum(hint =>
                    semantic.Equals(hint, StringComparison.OrdinalIgnoreCase) ? 100 :
                    Regex.IsMatch(semantic, $@"(^|\W){Regex.Escape(hint)}(\W|$)", RegexOptions.IgnoreCase) ? 55 : 0);
                return (Element: element, Score: score);
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Element)
            .FirstOrDefault();
        if (strong is not null) return strong;

        foreach (var textElement in driver.FindElements(By.XPath("//*[self::label or self::span or self::div or self::button]")))
        {
            if (!IsVisible(textElement) || !Normalize(textElement.Text).Equals(Normalize(label), StringComparison.OrdinalIgnoreCase)) continue;
            var targetId = SafeAttribute(textElement, "for");
            if (targetId.Length > 0)
            {
                var linked = driver.FindElements(By.Id(targetId)).FirstOrDefault(element => IsVisible(element) && element.Enabled);
                if (linked is not null) return linked;
            }

            IWebElement? container = textElement;
            for (var level = 0; level < 3 && container is not null; level++)
            {
                var nearby = container.FindElements(By.CssSelector("input:not([type='hidden']),textarea,[contenteditable='true']"))
                    .FirstOrDefault(element => IsVisible(element) && element.Enabled);
                if (nearby is not null) return nearby;
                try { container = container.FindElement(By.XPath("..")); }
                catch { container = null; }
            }
        }

        return null;
    }

    private static string Descriptor(IWebElement element)
    {
        var pieces = new[] { "aria-label", "placeholder", "name", "id", "data-testid", "role" }
            .Select(name => SafeAttribute(element, name));
        var nearby = string.Empty;
        try { nearby = element.FindElement(By.XPath("..")).Text; } catch { }
        return Normalize(string.Join(" ", pieces.Append(nearby)));
    }

    private static string SafeAttribute(IWebElement element, string name)
    {
        try { return element.GetAttribute(name) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void ReplaceText(IWebElement element, string value)
    {
        element.Click();
        element.SendKeys(Keys.Control + "a");
        element.SendKeys(value ?? string.Empty);
    }

    private static void FillBody(IWebDriver driver, string body)
    {
        driver.SwitchTo().DefaultContent();
        var editor = LargestEditor(driver);
        if (editor is null)
        {
            foreach (var frame in driver.FindElements(By.CssSelector("iframe")))
            {
                try
                {
                    driver.SwitchTo().DefaultContent();
                    driver.SwitchTo().Frame(frame);
                    editor = LargestEditor(driver, includeBody: true);
                    if (editor is not null) break;
                }
                catch { editor = null; }
            }
        }
        if (editor is null)
        {
            driver.SwitchTo().DefaultContent();
            throw new InvalidOperationException("O corpo da mensagem não foi localizado no editor do EBMail.");
        }
        ReplaceText(editor, body);
        driver.SwitchTo().DefaultContent();
    }

    private static IWebElement? LargestEditor(IWebDriver driver, bool includeBody = false)
    {
        var selector = includeBody ? "[contenteditable='true'],body" : "[contenteditable='true']";
        return driver.FindElements(By.CssSelector(selector))
            .Where(IsVisible)
            .Where(x => !Descriptor(x).Contains("para") && !Descriptor(x).Contains("recipient"))
            .OrderByDescending(x =>
            {
                try { return x.Size.Width * x.Size.Height; } catch { return 0; }
            })
            .FirstOrDefault(x => { try { return x.Size.Width * x.Size.Height > 3000; } catch { return false; } });
    }

    private static void AttachFiles(IWebDriver driver, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var input = FindFileInput(driver);
        if (input is null)
        {
            IWebElement? fromComputer = FindClickableByText(driver, ["anexar do meu computador", "attach from computer"]);
            if (fromComputer is null)
            {
                var attach = FindClickableByExactText(driver, ["anexar", "attach"]) ?? FindClickableByText(driver, ["anexar", "attach"]);
                try { attach?.Click(); } catch { }
                WaitUntil(driver, d => (fromComputer = FindClickableByText(d, ["anexar do meu computador", "attach from computer"])) is not null,
                    TimeSpan.FromSeconds(4), cancellationToken);
            }

            if (fromComputer is not null)
            {
                try
                {
                    fromComputer.Click();
                    if (TrySubmitNativeFileDialog(files, cancellationToken))
                    {
                        WaitForAttachments(driver, files, cancellationToken);
                        return;
                    }
                }
                catch (WebDriverException) { }
            }

            WaitUntil(driver, d => (input = FindFileInput(d)) is not null, TimeSpan.FromSeconds(5), cancellationToken);
        }
        input ??= FindFileInput(driver);
        if (input is null)
            throw new InvalidOperationException("O EBMail não disponibilizou o seletor de arquivos. A mensagem ficou aberta: clique em Anexar uma vez e tente novamente no SIGFUR.");

        var multiple = !string.IsNullOrWhiteSpace(SafeAttribute(input, "multiple"));
        if (multiple)
        {
            input.SendKeys(string.Join("\n", files));
        }
        else
        {
            foreach (var file in files)
            {
                input.SendKeys(file);
                Thread.Sleep(120);
                input = FindFileInput(driver) ?? input;
            }
        }
        WaitForAttachments(driver, files, cancellationToken);
    }

    private static void WaitForAttachments(IWebDriver driver, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        WaitUntil(driver, d =>
        {
            var text = BodyTextAcrossContexts(d);
            var countMatch = Regex.Match(text, @"\banexos?\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase);
            if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value, out var count) && count >= files.Count)
                return true;
            return files.All(file => text.Contains(Normalize(Path.GetFileName(file)), StringComparison.OrdinalIgnoreCase));
        }, TimeSpan.FromSeconds(Math.Clamp(files.Count * 3, 8, 30)), cancellationToken);
    }

    private static string BodyTextAcrossContexts(IWebDriver driver)
    {
        var parts = new List<string>();
        driver.SwitchTo().DefaultContent();
        CollectBodyText(driver, parts, 0);
        return Normalize(string.Join(" ", parts));
    }

    private static void CollectBodyText(IWebDriver driver, List<string> parts, int depth)
    {
        try { parts.Add(driver.FindElement(By.TagName("body")).Text); } catch { }
        if (depth >= 4) return;
        foreach (var frame in driver.FindElements(By.CssSelector("iframe,frame")).ToList())
        {
            try
            {
                driver.SwitchTo().Frame(frame);
                CollectBodyText(driver, parts, depth + 1);
                driver.SwitchTo().ParentFrame();
            }
            catch (WebDriverException)
            {
                try { driver.SwitchTo().DefaultContent(); } catch { }
                return;
            }
        }
    }

    private static bool TrySubmitNativeFileDialog(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        IntPtr dialog = IntPtr.Zero;
        while (DateTime.UtcNow < deadline && dialog == IntPtr.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dialog = FindOpenFileDialog();
            if (dialog == IntPtr.Zero) Thread.Sleep(100);
        }
        if (dialog == IntPtr.Zero) return false;

        var fileNameControl = GetDlgItem(dialog, FileNameControlId);
        if (fileNameControl != IntPtr.Zero && !ClassName(fileNameControl).Equals("Edit", StringComparison.OrdinalIgnoreCase))
            fileNameControl = FindDescendantByClass(fileNameControl, "Edit");
        if (fileNameControl == IntPtr.Zero)
            fileNameControl = FindDescendantByClass(dialog, "Edit");
        var openButton = GetDlgItem(dialog, OpenButtonControlId);
        if (fileNameControl == IntPtr.Zero || openButton == IntPtr.Zero) return false;

        var value = string.Join(" ", files.Select(path => $"\"{path}\""));
        SetForegroundWindow(dialog);
        SendMessage(fileNameControl, WmSetText, IntPtr.Zero, value);
        SendMessage(openButton, BmClick, IntPtr.Zero, IntPtr.Zero);

        var closeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < closeDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(dialog)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static IntPtr FindOpenFileDialog()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && IsWindowVisible(foreground) &&
            ClassName(foreground).Equals("#32770", StringComparison.Ordinal) &&
            GetDlgItem(foreground, OpenButtonControlId) != IntPtr.Zero)
            return foreground;

        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !ClassName(window).Equals("#32770", StringComparison.Ordinal)) return true;
            var title = WindowText(window);
            if (!Normalize(title).Contains("abrir") && !Normalize(title).Contains("open")) return true;
            if (GetDlgItem(window, OpenButtonControlId) == IntPtr.Zero) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindDescendantByClass(IntPtr parent, string className)
    {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parent, (window, _) =>
        {
            if (!ClassName(window).Equals(className, StringComparison.OrdinalIgnoreCase)) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static string ClassName(IntPtr window)
    {
        var value = new StringBuilder(256);
        _ = GetClassName(window, value, value.Capacity);
        return value.ToString();
    }

    private static string WindowText(IntPtr window)
    {
        var value = new StringBuilder(512);
        _ = GetWindowText(window, value, value.Capacity);
        return value.ToString();
    }

    private static IWebElement? FindFileInput(IWebDriver driver)
    {
        driver.SwitchTo().DefaultContent();
        return FindFileInputInCurrentContext(driver, 0);
    }

    private static IWebElement? FindFileInputInCurrentContext(IWebDriver driver, int depth)
    {
        try
        {
            var direct = driver.FindElements(By.CssSelector("input[type='file']"))
                .LastOrDefault(element => element.Enabled);
            if (direct is not null) return direct;

            var shadow = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const visit = root => {
                  const input = root.querySelector?.("input[type='file']:not([disabled])");
                  if (input) return input;
                  for (const element of root.querySelectorAll?.("*") || []) {
                    if (!element.shadowRoot) continue;
                    const nested = visit(element.shadowRoot);
                    if (nested) return nested;
                  }
                  return null;
                };
                return visit(document);
                """) as IWebElement;
            if (shadow is not null) return shadow;

            if (depth >= 5) return null;
            var frames = driver.FindElements(By.CssSelector("iframe,frame")).ToList();
            foreach (var frame in frames)
            {
                try
                {
                    driver.SwitchTo().Frame(frame);
                    var nested = FindFileInputInCurrentContext(driver, depth + 1);
                    if (nested is not null) return nested;
                    driver.SwitchTo().ParentFrame();
                }
                catch (WebDriverException)
                {
                    try { driver.SwitchTo().DefaultContent(); } catch { }
                    return null;
                }
            }
        }
        catch (WebDriverException) { }
        return null;
    }

    private static bool PageContainsAnyContext(IWebDriver driver, IReadOnlyList<string> values)
    {
        driver.SwitchTo().DefaultContent();
        return PageContainsAnyContextRecursive(driver, values, 0);
    }

    private static bool PageContainsAnyContextRecursive(IWebDriver driver, IReadOnlyList<string> values, int depth)
    {
        if (PageContainsAny(driver, values)) return true;
        if (depth >= 5) return false;
        foreach (var frame in driver.FindElements(By.CssSelector("iframe,frame")).ToList())
        {
            try
            {
                driver.SwitchTo().Frame(frame);
                if (PageContainsAnyContextRecursive(driver, values, depth + 1)) return true;
                driver.SwitchTo().ParentFrame();
            }
            catch (WebDriverException)
            {
                try { driver.SwitchTo().DefaultContent(); } catch { }
                return false;
            }
        }
        return false;
    }

    private static IWebElement? FindClickableByText(IWebDriver driver, IReadOnlyList<string> labels)
    {
        driver.SwitchTo().DefaultContent();
        foreach (var element in driver.FindElements(By.CssSelector("button,a,[role='button'],label")))
        {
            if (!IsVisible(element) || !element.Enabled) continue;
            var text = Normalize(string.Join(" ", element.Text, SafeAttribute(element, "aria-label"), SafeAttribute(element, "title")));
            if (labels.Any(label => text.Equals(Normalize(label), StringComparison.OrdinalIgnoreCase) || text.Contains(Normalize(label), StringComparison.OrdinalIgnoreCase)))
                return element;
        }
        return null;
    }

    private static IWebElement? FindClickableByExactText(IWebDriver driver, IReadOnlyList<string> labels)
    {
        driver.SwitchTo().DefaultContent();
        foreach (var element in driver.FindElements(By.CssSelector("button,a,[role='button'],label")))
        {
            if (!IsVisible(element) || !element.Enabled) continue;
            var text = Normalize(string.Join(" ", element.Text, SafeAttribute(element, "aria-label"), SafeAttribute(element, "title")));
            if (labels.Any(label => text.Equals(Normalize(label), StringComparison.OrdinalIgnoreCase)))
                return element;
        }
        return null;
    }

    private static bool PageContainsAny(IWebDriver driver, IReadOnlyList<string> values)
    {
        try
        {
            var text = Normalize(driver.FindElement(By.TagName("body")).Text);
            return values.Any(value => text.Contains(Normalize(value), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool IsVisible(IWebElement element)
    {
        try { return element.Displayed; }
        catch { return false; }
    }

    private static bool WaitUntil(IWebDriver driver, Func<IWebDriver, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (condition(driver)) return true; } catch (WebDriverException) { }
            Thread.Sleep(150);
        }
        return false;
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var chars = decomposed.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray();
        return Regex.Replace(new string(chars).Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"\s+", " ").Trim();
    }

    private const uint WmSetText = 0x000C;
    private const uint BmClick = 0x00F5;
    private const int FileNameControlId = 1148;
    private const int OpenButtonControlId = 1;
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int itemId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
