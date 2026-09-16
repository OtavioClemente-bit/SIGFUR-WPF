using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class WhatsAppAutomationService
{
    public const string WebUrl = "https://web.whatsapp.com/";

    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private static IWebDriver? SharedDriver;
    private readonly AppPaths _paths;
    private readonly LogService _log;

    public WhatsAppAutomationService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<WhatsAppShareResult> ShareAsync(
        WhatsAppShareRequest request,
        IProgress<WhatsAppShareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = request.Files
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = files.Where(path => !File.Exists(path)).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Nenhum contracheque foi informado para compartilhar.");
        if (missing.Count > 0) throw new FileNotFoundException("Um ou mais contracheques exportados não foram localizados: " + string.Join(", ", missing.Select(Path.GetFileName)));
        if (string.IsNullOrWhiteSpace(request.Recipient)) throw new InvalidOperationException("Informe o telefone ou o nome da conversa do WhatsApp.");

        await BrowserGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => ShareCore(request, files, progress, cancellationToken), cancellationToken);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    public async Task<InteractiveShareSession> OpenInteractiveShareAsync(
        IProgress<WhatsAppShareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await BrowserGate.WaitAsync(cancellationToken);
        try
        {
            var driver = await Task.Run(
                () => OpenInteractiveCore(progress, cancellationToken),
                cancellationToken);
            return new InteractiveShareSession(this, driver);
        }
        catch
        {
            BrowserGate.Release();
            throw;
        }
    }

    private IWebDriver OpenInteractiveCore(
        IProgress<WhatsAppShareProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new("WHATSAPP", "Abrindo a sessão protegida do WhatsApp Web…", 10));
        var driver = EnsureDriver();
        Navigate(driver, WebUrl);

        if (!WaitUntil(driver, IsAuthenticated, TimeSpan.FromSeconds(12), cancellationToken))
        {
            progress?.Report(new("CONECTAR", "No primeiro acesso, leia o QR Code. Esta sessão ficará salva neste computador.", 18));
            if (!WaitUntil(driver, IsAuthenticated, TimeSpan.FromMinutes(5), cancellationToken))
                throw new TimeoutException("O WhatsApp não foi conectado dentro do tempo esperado.");
        }

        try { driver.Manage().Window.Maximize(); } catch { }
        progress?.Report(new("CONVERSA", "Escolha visualmente a conversa no WhatsApp e depois confirme no SIGFUR.", 35));
        return driver;
    }

    private Task<WhatsAppShareResult> PrepareInteractiveShareAsync(
        IWebDriver driver,
        WhatsAppShareRequest request,
        IProgress<WhatsAppShareProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = request.Files
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = files.Where(path => !File.Exists(path)).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Nenhum contracheque foi informado para compartilhar.");
        if (missing.Count > 0)
            throw new FileNotFoundException("Um ou mais contracheques exportados não foram localizados: " + string.Join(", ", missing.Select(Path.GetFileName)));

        return Task.Run(() =>
        {
            try
            {
                return PrepareInteractiveShareCore(driver, request, files, progress, cancellationToken);
            }
            catch (WebDriverException ex)
            {
                throw new InvalidOperationException(
                    "O WhatsApp abriu, mas não foi possível preencher a mensagem no painel de anexos. Tente novamente; se a tela do WhatsApp tiver mudado, reinicie o compartilhamento.",
                    ex);
            }
        }, cancellationToken);
    }

    private static WhatsAppShareResult PrepareInteractiveShareCore(
        IWebDriver driver,
        WhatsAppShareRequest request,
        IReadOnlyList<string> files,
        IProgress<WhatsAppShareProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!WaitUntil(driver, HasOpenConversation, TimeSpan.FromSeconds(8), cancellationToken))
            throw new InvalidOperationException("Escolha uma conversa no WhatsApp antes de confirmar o compartilhamento.");

        progress?.Report(new("ANEXOS", $"Anexando {files.Count} arquivo(s)…", 55));
        AttachDocuments(driver, files, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            progress?.Report(new("MENSAGEM", "Preenchendo a mensagem…", 72));
            IWebElement? messageBox = null;
            WaitUntil(driver, current => IsAttachmentPreviewOpen(current)
                                         && (messageBox = FindAttachmentCaptionBox(current)) is not null,
                TimeSpan.FromSeconds(5), cancellationToken);
            if (messageBox is null)
                throw new InvalidOperationException("Os anexos foram preparados, mas o campo de legenda não foi localizado.");
            FillMessage(driver, messageBox, request.Message);
        }

        if (!WaitUntil(driver, current => FindSendButton(current) is not null, TimeSpan.FromSeconds(6), cancellationToken))
            throw new InvalidOperationException("Os anexos foram selecionados, mas o botão Enviar não apareceu no WhatsApp.");

        progress?.Report(new("REVISÃO", "Arquivos e mensagem preparados. Confira a conversa e clique em Enviar.", 90));
        return new(false, "Arquivos e mensagem preparados no WhatsApp. Confira a conversa e clique em Enviar.");
    }

    public sealed class InteractiveShareSession : IAsyncDisposable
    {
        private readonly WhatsAppAutomationService _owner;
        private readonly IWebDriver _driver;
        private int _released;

        internal InteractiveShareSession(WhatsAppAutomationService owner, IWebDriver driver)
        {
            _owner = owner;
            _driver = driver;
        }

        public Task<WhatsAppShareResult> PrepareAsync(
            WhatsAppShareRequest request,
            IProgress<WhatsAppShareProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => _owner.PrepareInteractiveShareAsync(_driver, request, progress, cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) BrowserGate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private WhatsAppShareResult ShareCore(
        WhatsAppShareRequest request,
        IReadOnlyList<string> files,
        IProgress<WhatsAppShareProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new("WHATSAPP", "Abrindo a sessão protegida do WhatsApp Web…", 10));
        var driver = EnsureDriver();
        Navigate(driver, WebUrl);

        if (!WaitUntil(driver, IsAuthenticated, TimeSpan.FromSeconds(12), cancellationToken))
        {
            progress?.Report(new("CONECTAR", "No primeiro acesso, leia o QR Code. Esta sessão ficará salva neste computador.", 18));
            if (!WaitUntil(driver, IsAuthenticated, TimeSpan.FromMinutes(5), cancellationToken))
                throw new TimeoutException("O WhatsApp não foi conectado dentro do tempo esperado.");
        }

        progress?.Report(new("CONVERSA", $"Abrindo a conversa de {request.Recipient}…", 35));
        OpenConversation(driver, request.Recipient, cancellationToken);
        if (!WaitUntil(driver, HasOpenConversation, TimeSpan.FromSeconds(45), cancellationToken))
            throw new InvalidOperationException("A conversa do WhatsApp não ficou disponível. Confira o telefone ou o nome informado.");

        progress?.Report(new("ANEXOS", $"Anexando {files.Count} contracheque(s)…", 55));
        AttachDocuments(driver, files, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            progress?.Report(new("MENSAGEM", "Preenchendo a mensagem…", 72));
            IWebElement? messageBox = null;
            WaitUntil(driver, current => IsAttachmentPreviewOpen(current)
                                         && (messageBox = FindAttachmentCaptionBox(current)) is not null,
                TimeSpan.FromSeconds(5), cancellationToken);
            if (messageBox is null)
                throw new InvalidOperationException("Os anexos foram preparados, mas o campo de legenda não foi localizado.");
            FillMessage(driver, messageBox, request.Message);
        }

        if (!WaitUntil(driver, d => FindSendButton(d) is not null, TimeSpan.FromSeconds(6), cancellationToken))
            throw new InvalidOperationException("Os anexos foram selecionados, mas o botão Enviar não apareceu no WhatsApp.");

        if (!request.SendAutomatically)
        {
            progress?.Report(new("REVISÃO", "Contracheques preparados. Confira a conversa e clique em Enviar.", 90));
            return new(false, "Contracheques preparados no WhatsApp. Confira o destinatário e clique em Enviar.");
        }

        progress?.Report(new("ENVIANDO", "Enviando os contracheques pelo WhatsApp…", 92));
        var send = FindSendButton(driver) ?? throw new InvalidOperationException("O botão Enviar não foi localizado.");
        send.Click();
        WaitUntil(driver, d => FindSendButton(d) is null, TimeSpan.FromSeconds(25), cancellationToken);
        progress?.Report(new("CONCLUÍDO", "Contracheques enviados pelo WhatsApp.", 100));
        return new(true, "Contracheques enviados pelo WhatsApp.");
    }

    private IWebDriver EnsureDriver()
    {
        if (SharedDriver is not null && IsAlive(SharedDriver)) return SharedDriver;
        Directory.CreateDirectory(_paths.WhatsAppBrowserProfileDirectory);
        Exception? edgeError = null;
        try
        {
            var options = new EdgeOptions();
            ConfigureOptions(options, Path.Combine(_paths.WhatsAppBrowserProfileDirectory, "edge"));
            var service = EdgeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            SharedDriver = new EdgeDriver(service, options, TimeSpan.FromSeconds(30));
            ConfigureFastTimeouts(SharedDriver);
            return SharedDriver;
        }
        catch (Exception ex) { edgeError = ex; }

        try
        {
            var options = new ChromeOptions();
            ConfigureOptions(options, Path.Combine(_paths.WhatsAppBrowserProfileDirectory, "chrome"));
            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            SharedDriver = new ChromeDriver(service, options, TimeSpan.FromSeconds(30));
            ConfigureFastTimeouts(SharedDriver);
            return SharedDriver;
        }
        catch (Exception chromeError)
        {
            throw new InvalidOperationException("Não foi possível abrir Edge nem Chrome para acessar o WhatsApp.", new AggregateException(edgeError!, chromeError));
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
        options.AddUserProfilePreference("profile.default_content_setting_values.notifications", 2);
        options.PageLoadStrategy = PageLoadStrategy.Eager;
    }

    private static void ConfigureFastTimeouts(IWebDriver driver)
    {
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(18);
        driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(8);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
    }

    private static void OpenConversation(IWebDriver driver, string recipient, CancellationToken cancellationToken)
    {
        var digits = Regex.Replace(recipient, @"\D", string.Empty);
        if (digits.Length is 10 or 11) digits = "55" + digits;
        if (digits.Length >= 12)
        {
            Navigate(driver, $"{WebUrl}send?phone={digits}", force: true);
            return;
        }

        Navigate(driver, WebUrl);
        var search = FindSearchBox(driver)
                     ?? throw new InvalidOperationException("A pesquisa de conversas não foi localizada no WhatsApp.");
        search.Click();
        search.SendKeys(Keys.Control + "a");
        search.SendKeys(recipient);

        IWebElement? chat = null;
        if (!WaitUntil(driver, d => (chat = FindChatByName(d, recipient)) is not null, TimeSpan.FromSeconds(20), cancellationToken))
            throw new InvalidOperationException($"A conversa '{recipient}' não foi localizada.");
        chat!.Click();
    }

    private static IWebElement? FindSearchBox(IWebDriver driver)
        => driver.FindElements(By.CssSelector("div[contenteditable='true'],input"))
            .Where(IsVisible)
            .Select(element => (Element: element, Descriptor: Descriptor(element)))
            .Where(item => item.Descriptor.Contains("pesquisar") || item.Descriptor.Contains("search"))
            .Select(item => item.Element)
            .FirstOrDefault();

    private static IWebElement? FindChatByName(IWebDriver driver, string name)
    {
        var target = Normalize(name);
        return driver.FindElements(By.CssSelector("[title],[aria-label],span,div[role='button']"))
            .Where(IsVisible)
            .FirstOrDefault(element =>
            {
                var text = Normalize(string.Join(" ", element.Text, SafeAttribute(element, "title"), SafeAttribute(element, "aria-label")));
                return text.Equals(target, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static bool IsAuthenticated(IWebDriver driver)
        => driver.FindElements(By.CssSelector(
                "#pane-side,[data-testid='conversation-compose-box-input'],[data-testid='chat-list']"))
            .Any(IsVisible)
           || HasOpenConversation(driver)
           || FindSearchBox(driver) is not null;

    private static bool HasOpenConversation(IWebDriver driver)
        => driver.FindElements(By.CssSelector("footer")).Any(IsVisible) && FindMessageBox(driver) is not null;

    private static IWebElement? FindMessageBox(IWebDriver driver)
    {
        var candidates = driver.FindElements(By.CssSelector("footer div[contenteditable='true'],div[contenteditable='true']"))
            .Where(element => IsVisible(element) && element.Enabled)
            .ToList();
        return candidates
            .Select(element => (Element: element, Score: MessageBoxScore(element)))
            .OrderByDescending(item => item.Score)
            .Select(item => item.Element)
            .FirstOrDefault();
    }

    private static bool IsAttachmentPreviewOpen(IWebDriver driver)
        => driver.FindElements(By.CssSelector("button[aria-label],[role='button'][aria-label]"))
            .Where(IsVisible)
            .Any(element =>
            {
                var descriptor = Descriptor(element);
                return descriptor.Contains("adicionar arquivo") || descriptor.Contains("add file");
            });

    private static IWebElement? FindAttachmentCaptionBox(IWebDriver driver)
    {
        return driver.FindElements(By.CssSelector("div[contenteditable='true']"))
            .Where(element => IsVisible(element) && element.Enabled)
            .Where(element => !SafeAttribute(element, "data-testid")
                .Equals("conversation-compose-box-input", StringComparison.OrdinalIgnoreCase))
            .Where(element =>
            {
                var descriptor = Descriptor(element);
                var testId = Normalize(SafeAttribute(element, "data-testid"));
                var looksLikeCaption = descriptor.Contains("legenda")
                                       || descriptor.Contains("caption")
                                       || descriptor.Contains("digite uma mensagem")
                                       || descriptor.Contains("type a message")
                                       || testId.Contains("caption");
                return looksLikeCaption
                       && !descriptor.Contains("pesquisar")
                       && !descriptor.Contains("search");
            })
            .Select(element => (Element: element, Score: AttachmentCaptionScore(driver, element)))
            .OrderByDescending(item => item.Score)
            .Where(item => item.Score > 0)
            .Select(item => item.Element)
            .FirstOrDefault();
    }

    private static int AttachmentCaptionScore(IWebDriver driver, IWebElement element)
    {
        var descriptor = Descriptor(element);
        var testId = Normalize(SafeAttribute(element, "data-testid"));
        var score = 0;
        if (descriptor.Contains("legenda") || descriptor.Contains("caption")) score += 400;
        if (descriptor.Contains("digite uma mensagem") || descriptor.Contains("type a message")) score += 180;
        if (testId.Contains("caption")) score += 350;
        if (!string.IsNullOrWhiteSpace(SafeAttribute(element, "data-lexical-editor"))) score += 80;
        try
        {
            var isTopmost = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                const el = arguments[0];
                const rect = el.getBoundingClientRect();
                const x = rect.left + Math.min(Math.max(rect.width * 0.25, 8), Math.max(rect.width - 8, 8));
                const y = rect.top + rect.height / 2;
                const top = document.elementFromPoint(x, y);
                return !!top && (top === el || el.contains(top) || top.contains(el));", element);
            if (isTopmost is true)
                score += 250;
        }
        catch { }
        try { score += Math.Max(0, element.Location.Y / 30); } catch { }
        return score;
    }

    private static void FillMessage(IWebDriver driver, IWebElement messageBox, string message)
    {
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].focus();", messageBox);
            messageBox.SendKeys(Keys.Control + "a");
            messageBox.SendKeys(Keys.Backspace);
            messageBox.SendKeys(message);
            if (MessageWasFilled(messageBox, message)) return;
            throw new ElementNotInteractableException("O campo de legenda não recebeu o texto.");
        }
        catch (WebDriverException)
        {
            ((IJavaScriptExecutor)driver).ExecuteScript(@"
                const el = arguments[0];
                const text = arguments[1];
                el.focus();
                el.textContent = text;
                el.dispatchEvent(new InputEvent('input', {
                    bubbles: true,
                    inputType: 'insertText',
                    data: text
                }));", messageBox, message);
            if (!MessageWasFilled(messageBox, message))
                throw new InvalidOperationException("O campo de legenda foi localizado, mas o WhatsApp não aceitou a mensagem.");
        }
    }

    private static bool MessageWasFilled(IWebElement messageBox, string message)
    {
        try
        {
            var expected = Normalize(message);
            var actual = Normalize(string.Join(" ", messageBox.Text, SafeAttribute(messageBox, "textContent")));
            var sampleLength = Math.Min(18, expected.Length);
            return sampleLength > 0 && actual.Contains(expected[..sampleLength], StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int MessageBoxScore(IWebElement element)
    {
        var descriptor = Descriptor(element);
        var score = descriptor.Contains("mensagem") || descriptor.Contains("message") || descriptor.Contains("legenda") || descriptor.Contains("caption") ? 100 : 0;
        try { score += Math.Max(0, element.Location.Y / 20); } catch { }
        return score;
    }

    private static void AttachDocuments(IWebDriver driver, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var input = FindDocumentInput(driver);
        if (input is null)
        {
            var attach = FindAttachButton(driver) ?? throw new InvalidOperationException("O botão de anexos não foi localizado no WhatsApp.");
            attach.Click();

            // O WhatsApp normalmente cria os inputs de arquivo assim que o menu de
            // anexos abre. Usar esse input diretamente evita abrir a janela nativa e
            // deixa o envio de vários PDFs bem mais rápido.
            WaitUntil(driver, d => (input = FindDocumentInput(d)) is not null,
                TimeSpan.FromSeconds(1), cancellationToken);
            if (input is not null)
            {
                input.SendKeys(string.Join("\n", files));
                return;
            }

            IWebElement? documentOption = null;
            WaitUntil(driver, d => (documentOption = FindDocumentMenuOption(d)) is not null,
                TimeSpan.FromSeconds(2), cancellationToken);
            if (documentOption is not null)
            {
                ClickMenuOption(driver, documentOption);
                if (TrySubmitNativeFileDialog(files, cancellationToken)) return;
            }

            WaitUntil(driver, d => (input = FindDocumentInput(d)) is not null, TimeSpan.FromSeconds(2), cancellationToken);
        }
        input ??= FindDocumentInput(driver);
        if (input is null) throw new InvalidOperationException("O WhatsApp não disponibilizou o seletor de documentos. Abra novamente a conversa e tente outra vez.");
        input.SendKeys(string.Join("\n", files));
    }

    private static IWebElement? FindDocumentMenuOption(IWebDriver driver)
    {
        foreach (var element in driver.FindElements(By.CssSelector("span,div,button,li,[role='button']")).Where(IsVisible))
        {
            var text = Normalize(string.Join(" ", element.Text, SafeAttribute(element, "aria-label"), SafeAttribute(element, "title")));
            if (text is not ("documento" or "document")) continue;

            // O texto fica dentro de vários elementos; o manipulador do WhatsApp
            // está na linha ancestral do menu, e não necessariamente no <span>.
            try
            {
                return element.FindElement(By.XPath(
                    "ancestor-or-self::*[self::button or self::li or @role='button' or @tabindex][1]"));
            }
            catch { return element; }
        }
        return null;
    }

    private static void ClickMenuOption(IWebDriver driver, IWebElement option)
    {
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript(
                "arguments[0].scrollIntoView({block:'center'}); arguments[0].click();", option);
        }
        catch
        {
            option.Click();
        }
    }

    private static bool TrySubmitNativeFileDialog(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
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

        var closeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
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
            var title = Normalize(WindowText(window));
            if (!title.Contains("abrir") && !title.Contains("open")) return true;
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

    private static IWebElement? FindDocumentInput(IWebDriver driver)
        => driver.FindElements(By.CssSelector("input[type='file']"))
            .Where(element => element.Enabled)
            .Select(element =>
            {
                var accept = Normalize(SafeAttribute(element, "accept"));
                var score = accept.Contains("application") || accept == "*" || accept.Length == 0 ? 100 : 0;
                if (!string.IsNullOrWhiteSpace(SafeAttribute(element, "multiple"))) score += 20;
                if (accept.Contains("image") && !accept.Contains("application")) score -= 80;
                return (Element: element, Score: score);
            })
            .OrderByDescending(item => item.Score)
            .Where(item => item.Score > 0)
            .Select(item => item.Element)
            .FirstOrDefault();

    private static IWebElement? FindAttachButton(IWebDriver driver)
    {
        foreach (var element in driver.FindElements(By.CssSelector("button,[role='button'],span[data-icon]")).Where(IsVisible))
        {
            var descriptor = Descriptor(element);
            var icon = Normalize(SafeAttribute(element, "data-icon"));
            if (descriptor.Contains("anexar") || descriptor.Contains("attach") || icon.Contains("plus") || icon.Contains("clip"))
            {
                try
                {
                    if (element.TagName.Equals("span", StringComparison.OrdinalIgnoreCase))
                        return element.FindElement(By.XPath(".."));
                }
                catch { }
                return element;
            }
        }
        return null;
    }

    private static IWebElement? FindSendButton(IWebDriver driver)
    {
        var icon = driver.FindElements(By.CssSelector("[data-icon='send'],[data-icon='wds-ic-send-filled']"))
            .LastOrDefault(IsVisible);
        if (icon is null) return null;
        try { return icon.FindElement(By.XPath("ancestor::*[@role='button' or self::button][1]")); }
        catch
        {
            try { return icon.FindElement(By.XPath("..")); }
            catch { return icon; }
        }
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

    private static bool IsVisible(IWebElement element)
    {
        try { return element.Displayed; }
        catch { return false; }
    }

    private static string Descriptor(IWebElement element)
        => Normalize(string.Join(" ",
            element.Text,
            SafeAttribute(element, "aria-label"),
            SafeAttribute(element, "placeholder"),
            SafeAttribute(element, "title"),
            SafeAttribute(element, "data-tab"),
            SafeAttribute(element, "data-icon"),
            SafeAttribute(element, "role")));

    private static string SafeAttribute(IWebElement element, string name)
    {
        try { return element.GetAttribute(name) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool WaitUntil(IWebDriver driver, Func<IWebDriver, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (condition(driver)) return true; } catch (WebDriverException) { }
            Thread.Sleep(200);
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
