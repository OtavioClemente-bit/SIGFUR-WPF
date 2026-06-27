using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class CpexExerciseAutomationService
{
    public const string LoginUrl = "https://cpex-intranet.eb.mil.br/asplogon_nova.asp?url=area_ua_cpex/index.asp";
    public const string TargetUrl = "https://cpex-intranet.eb.mil.br/Exerc_Anterior/sel_opcao.asp";
    private readonly AppPaths _paths;
    private readonly ExercisePreviousRepository _repository;
    private static readonly List<IWebDriver> OpenDrivers = [];

    public CpexExerciseAutomationService(AppPaths paths, ExercisePreviousRepository repository)
    {
        _paths = paths;
        _repository = repository;
    }

    public CpexExerciseSettings LoadSettings()
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SIGFUR", "EA", "config", "cpex_online_config.json");
            var path = File.Exists(_paths.ExercisePreviousCpexSettingsFile) ? _paths.ExercisePreviousCpexSettingsFile : legacy;
            if (!File.Exists(path)) return new CpexExerciseSettings();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string Read(params string[] names)
            {
                foreach (var name in names) if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
                return string.Empty;
            }
            bool ReadBool(bool fallback, params string[] names)
            {
                foreach (var name in names) if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
                return fallback;
            }
            int ReadInt(int fallback, params string[] names)
            {
                foreach (var name in names) if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number;
                return fallback;
            }
            var settings = new CpexExerciseSettings
            {
                Browser = First(Read("Browser", "browser"), "edge"),
                LoginCpf = Read("LoginCpf", "login_cpf"),
                LoginPasswordBase64 = Read("LoginPasswordBase64", "login_senha_b64"),
                DriverDirectory = Read("DriverDirectory", "driver_directory", "driver_dir"),
                KeepBrowserOpen = ReadBool(true, "KeepBrowserOpen", "keep_browser_open"),
                Headless = ReadBool(false, "Headless", "headless"),
                ManualLoginTimeoutSeconds = ReadInt(300, "ManualLoginTimeoutSeconds", "manual_login_timeout"),
                OperatorName = Read("OperatorName", "operador_nome"), OperatorCpf = Read("OperatorCpf", "operador_cpf"),
                OperatorEmail = Read("OperatorEmail", "operador_email_om"), OperatorPhone = Read("OperatorPhone", "operador_celular")
            };
            if (!path.Equals(_paths.ExercisePreviousCpexSettingsFile, StringComparison.OrdinalIgnoreCase)) SaveSettings(settings);
            return settings;
        }
        catch { return new CpexExerciseSettings(); }
    }

    public void SaveSettings(CpexExerciseSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ExercisePreviousCpexSettingsFile)!);
        File.WriteAllText(_paths.ExercisePreviousCpexSettingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<CpexExercisePayload> BuildPayloadAsync(ExercisePreviousProcess p, CpexExerciseSettings settings, CancellationToken ct = default)
    {
        var totals = new Dictionary<int, (decimal Original, decimal Corrected)>();
        foreach (var entry in p.Entries)
        {
            entry.Factor = await _repository.GetIpcaFactorAsync(entry.Competence, ct);
            var current = totals.GetValueOrDefault(entry.CodeOrder);
            totals[entry.CodeOrder] = (current.Original + entry.Net, current.Corrected + entry.CorrectedNet);
        }

        var rows = new List<CpexExerciseCodeRow>();
        decimal gross = 0, correctedGross = 0, discounts = 0, correctedDiscounts = 0;
        var discountByCode = new Dictionary<string, (decimal Original, decimal Corrected)>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in p.Codes.OrderBy(x => x.Order))
        {
            var total = totals.GetValueOrDefault(code.Order);
            if (Math.Abs(total.Original) < 0.005m && Math.Abs(total.Corrected) < 0.005m) continue;
            var extracted = ExtractCode(code.Description);
            if (code.Type.Equals("Despesa", StringComparison.OrdinalIgnoreCase))
            {
                discounts += Math.Abs(total.Original); correctedDiscounts += Math.Abs(total.Corrected);
            }
            else
            {
                gross += Math.Max(total.Original, 0); correctedGross += Math.Max(total.Corrected, 0);
            }
            if (!string.IsNullOrWhiteSpace(extracted.Code)) discountByCode[extracted.Code] = (Math.Abs(total.Original), Math.Abs(total.Corrected));
            rows.Add(new CpexExerciseCodeRow
            {
                Code = extracted.Code, Description = extracted.Description, Type = code.Type,
                Original = Money(total.Original), Corrected = Money(total.Corrected)
            });
        }

        string Discount(string code, bool corrected = false)
        {
            var d = discountByCode.GetValueOrDefault(code);
            return Money(corrected ? d.Corrected : d.Original);
        }

        var payload = new CpexExercisePayload { Codes = rows.Take(10).ToList() };
        void F(string key, string? value) => payload.Fields[key] = value?.Trim() ?? string.Empty;
        F("cpf", ExercisePreviousRepository.Digits(p.Cpf)); F("prec_cp", ExercisePreviousRepository.Digits(p.PrecCp));
        F("codom", ExercisePreviousRepository.Digits(p.Codom)); F("sigla_om", p.OrganizationName);
        F("situacao", MapSituation(p.Situation)); F("indicativo", MapIndicative(p.EaIndicative));
        F("nome", p.FullName); F("posto_grad", MapRank(p.Rank));
        F("representante_nome", FirstCleanName(p.RepresentativeName, p.CompanyCommander, p.OdNameRank, p.FormerOdName));
        F("representante_cpf", ExercisePreviousRepository.Digits(string.IsNullOrWhiteSpace(p.RepresentativeCpf) ? p.FormerOdCpf : p.RepresentativeCpf));
        F("representante_idt", ExercisePreviousRepository.Digits(string.IsNullOrWhiteSpace(p.RepresentativeIdentity) ? p.FormerOdIdentity : p.RepresentativeIdentity));
        F("periodo_inicio", DateBr(p.PeriodStart)); F("periodo_fim", DateBr(p.PeriodEnd));
        F("qtd_meses", MonthsBetween(p.PeriodStart, p.PeriodEnd).ToString("0000", CultureInfo.InvariantCulture));
        F("tipo_exercicio_anterior", string.IsNullOrWhiteSpace(p.PreviousExerciseType) ? p.DebtType : p.PreviousExerciseType);
        F("data_requerimento", DateBr(p.RequestDate)); F("averbacao_bi_adt", ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber));
        F("averbacao_data", DateBr(p.BulletinDate)); F("documento_materializou", p.RightMaterializationDocument);
        F("objeto_justificativa", First(p.NonPaymentExplanation, p.PaymentReason, p.Object));
        F("possui_pensao_judiciaria", "Não"); F("pesquisa_ficha_cadastro", "Sim"); F("pesquisa_ficha_financeira", "Sim"); F("pesquisa_levantamento_siafi", "Sim");
        F("documento_remessa", IsYesNo(p.RemittanceDocument) ? string.Empty : p.RemittanceDocument);
        F("banco", MapBank(p.Bank)); F("agencia", ExercisePreviousRepository.Digits(p.Agency)); F("conta", p.Account);
        F("operador_nome", settings.OperatorName); F("operador_cpf", ExercisePreviousRepository.Digits(settings.OperatorCpf));
        F("operador_email_om", settings.OperatorEmail); F("operador_celular", settings.OperatorPhone);

        payload.Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["valor_bruto_devido"] = Money(gross), ["valor_bruto_devido_corrigido"] = Money(correctedGross),
            ["desconto_pensao_3_zeh"] = Discount("ZEH"), ["desconto_pensao_3_zeh_corrigido"] = Discount("ZEH", true),
            ["desconto_pensao_zeb"] = Discount("ZEB"), ["desconto_pensao_zeb_corrigido"] = Discount("ZEB", true),
            ["desconto_pensao_15_zec"] = Discount("ZEC"), ["desconto_pensao_15_zec_corrigido"] = Discount("ZEC", true),
            ["desconto_fusex_zea"] = Discount("ZEA"), ["desconto_fusex_zea_corrigido"] = Discount("ZEA", true),
            ["da_ex_ant_ded_gea"] = Discount("GEA"), ["da_ex_ant_ded_gea_corrigido"] = Discount("GEA", true),
            ["da_ea_aj_con_n_d_geb"] = Discount("GEB"), ["da_ea_aj_con_n_d_geb_corrigido"] = Discount("GEB", true),
            ["desconto_pnr_z13"] = Discount("Z13"), ["desconto_pnr_z13_corrigido"] = Discount("Z13", true),
            ["desconto_pnr_z14"] = Discount("Z14"), ["desconto_pnr_z14_corrigido"] = Discount("Z14", true),
            ["fusex_dep_zef"] = Discount("ZEF"), ["fusex_dep_zef_corrigido"] = Discount("ZEF", true),
            ["valor_liquido_devido"] = Money(gross - discounts), ["valor_liquido_devido_corrigido"] = Money(correctedGross - correctedDiscounts)
        };
        return payload;
    }

    public async Task<string> OpenAndFillAsync(ExercisePreviousProcess process, CpexExerciseSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(process.Cpf) || string.IsNullOrWhiteSpace(process.PrecCp))
            throw new InvalidOperationException("CPF e PREC-CP são obrigatórios para abrir o CPEX Online.");
        SaveSettings(settings);
        var payload = await BuildPayloadAsync(process, settings, ct);
        var debugFile = SavePayload(payload);
        return await Task.Run(() => RunBrowser(payload, settings, debugFile, ct), ct);
    }

    private string RunBrowser(CpexExercisePayload payload, CpexExerciseSettings settings, string debugFile, CancellationToken ct)
    {
        var log = new List<string> { "Payload salvo: " + debugFile };
        IWebDriver? driver = null;
        try
        {
            Directory.CreateDirectory(_paths.ExercisePreviousCpexDownloadsDirectory);
            driver = CreateDriver(settings);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(90);
            driver.Manage().Window.Maximize();
            driver.Navigate().GoToUrl(LoginUrl);
            WaitReady(driver, 60);
            FillLogin(driver, settings.LoginCpf, settings.GetPassword(), log);
            if (IsLoginPage(driver))
            {
                log.Add("Aguardando conclusão manual do login/captcha na Área da UA.");
                var loggedIn = WaitUntil(driver, d => !IsLoginPage(d), TimeSpan.FromSeconds(Math.Max(30, settings.ManualLoginTimeoutSeconds)), ct);
                WaitForDomIdle(driver, TimeSpan.FromSeconds(3), ct);
                if (!loggedIn) ct.ThrowIfCancellationRequested();
                if (IsLoginPage(driver)) throw new InvalidOperationException("O login da Área da UA não foi concluído dentro do tempo configurado.");
            }
            log.Add("Login da Área da UA concluído.");
            driver.Navigate().GoToUrl(TargetUrl);
            WaitReady(driver, 60);
            if (IsLoginPage(driver)) throw new InvalidOperationException("A sessão voltou para o logon. Confira usuário, senha, captcha e acesso à intranet.");

            FillFirstPage(driver, payload, log);
            WaitReady(driver, 60);
            FillFullForm(driver, payload, log);
            AcceptAlertIfPresent(driver, log, "Formulário");
            log.Add("Nenhum envio/protocolo foi confirmado. Confira tudo na tela e envie manualmente.");
            // Após um preenchimento bem-sucedido, a conferência humana é obrigatória.
            // Portanto, o navegador permanece aberto independentemente da preferência
            // usada para conservar a janela em cenários de erro.
            lock (OpenDrivers) OpenDrivers.Add(driver);
            driver = null;
            log.Add("Navegador mantido aberto para conferência e protocolo manual.");
            return string.Join(Environment.NewLine, log);
        }
        catch
        {
            if (driver is not null && settings.KeepBrowserOpen)
            {
                lock (OpenDrivers) OpenDrivers.Add(driver);
                driver = null;
            }
            throw;
        }
        finally
        {
            if (driver is not null) { try { driver.Quit(); } catch { } driver.Dispose(); }
        }
    }

    private IWebDriver CreateDriver(CpexExerciseSettings settings)
    {
        var browser = (settings.Browser ?? "edge").Trim().ToLowerInvariant();
        if (browser.Contains("chrome"))
        {
            var options = new ChromeOptions { PageLoadStrategy = PageLoadStrategy.Normal };
            options.AddArgument("--start-maximized"); options.AddArgument("--disable-notifications"); options.AddArgument("--disable-popup-blocking");
            options.AddUserProfilePreference("download.default_directory", Path.GetFullPath(_paths.ExercisePreviousCpexDownloadsDirectory));
            options.AddUserProfilePreference("download.prompt_for_download", false);
            options.AddUserProfilePreference("plugins.always_open_pdf_externally", true);
            if (settings.Headless) options.AddArgument("--headless=new");
            options.LeaveBrowserRunning = true;
            if (!string.IsNullOrWhiteSpace(settings.DriverDirectory))
                return new ChromeDriver(ChromeDriverService.CreateDefaultService(settings.DriverDirectory), options, TimeSpan.FromSeconds(120));
            return new ChromeDriver(options);
        }
        else
        {
            var options = new EdgeOptions { PageLoadStrategy = PageLoadStrategy.Normal };
            options.AddArgument("--start-maximized"); options.AddArgument("--disable-notifications"); options.AddArgument("--disable-popup-blocking");
            options.AddUserProfilePreference("download.default_directory", Path.GetFullPath(_paths.ExercisePreviousCpexDownloadsDirectory));
            options.AddUserProfilePreference("download.prompt_for_download", false);
            options.AddUserProfilePreference("plugins.always_open_pdf_externally", true);
            if (settings.Headless) options.AddArgument("--headless=new");
            options.LeaveBrowserRunning = true;
            if (!string.IsNullOrWhiteSpace(settings.DriverDirectory))
                return new EdgeDriver(EdgeDriverService.CreateDefaultService(settings.DriverDirectory), options, TimeSpan.FromSeconds(120));
            return new EdgeDriver(options);
        }
    }

    private static void FillLogin(IWebDriver driver, string cpf, string password, List<string> log)
    {
        var fields = VisibleControls(driver).Where(x => x.TagName.Equals("input", StringComparison.OrdinalIgnoreCase)).ToList();
        var loginOk = FillBest(driver, cpf, ["cpf", "usuario", "usuário", "login"], ["cpf", "usuario", "login"]);
        var passwordOk = FillBest(driver, password, ["senha", "password"], ["senha", "password"]);
        if (!loginOk && fields.Count > 0) loginOk = Fill(fields[0], cpf);
        var passwordField = fields.FirstOrDefault(x => string.Equals(x.GetAttribute("type"), "password", StringComparison.OrdinalIgnoreCase));
        if (!passwordOk && passwordField is not null) passwordOk = Fill(passwordField, password);
        log.Add($"Credenciais: CPF={(loginOk ? "OK" : "não localizado")}; senha={(passwordOk ? "OK" : "não localizada")}.");
        if (loginOk && passwordOk)
        {
            var button = driver.FindElements(By.CssSelector("button,input[type=submit],input[type=button],a"))
                .FirstOrDefault(x => IsVisible(x) && (Normalize(Context(driver, x)).Contains("entrar") || Normalize(Context(driver, x)).Contains("acessar")));
            var loginPage = IsLoginPage(driver);
            try { button?.Click(); } catch { }
            WaitUntil(driver, d => !loginPage || !IsLoginPage(d) || AlertPresent(d), TimeSpan.FromSeconds(8), CancellationToken.None);
            AcceptAlertIfPresent(driver, log, "Login");
            WaitForDomIdle(driver, TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    private static void FillFirstPage(IWebDriver driver, CpexExercisePayload payload, List<string> log)
    {
        var cpf = payload.Fields.GetValueOrDefault("cpf", string.Empty);
        var prec = payload.Fields.GetValueOrDefault("prec_cp", string.Empty);
        var cpfOk = FillBest(driver, cpf, ["cpf do requerente", "cpf"], ["cpf"]);
        var precOk = FillBest(driver, prec, ["prec cp", "prec/cp", "prec"], ["prec", "preccp", "prec_cp"]);
        var textFields = VisibleControls(driver).Where(IsTextControl).ToList();
        if (!cpfOk && textFields.Count > 0) cpfOk = Fill(textFields[0], cpf);
        if (!precOk && textFields.Count > 1) precOk = Fill(textFields[1], prec);
        log.Add($"Tela inicial: CPF={(cpfOk ? "OK" : "não localizado")}; PREC-CP={(precOk ? "OK" : "não localizado")}.");
        var next = driver.FindElements(By.CssSelector("button,input[type=submit],input[type=button],a,img"))
            .FirstOrDefault(x => IsVisible(x) && ContainsAny(Normalize(Context(driver, x)), ["avancar", "avançar", "continuar", "proximo", "próximo", "seta", ">>"]));
        if (next is null)
        {
            next = driver.FindElements(By.CssSelector("input[type=image],img[src*='seta'],img[src*='avanc']")).FirstOrDefault(IsVisible);
        }
        var before = BodySignature(driver);
        try { next?.Click(); log.Add(next is null ? "Seta de avançar não localizada; avance manualmente." : "Seta de avançar acionada."); }
        catch { log.Add("Não foi possível clicar na seta; avance manualmente."); }
        WaitUntil(driver, d => AlertPresent(d) || !string.Equals(BodySignature(d), before, StringComparison.Ordinal), TimeSpan.FromSeconds(12), CancellationToken.None);
        AcceptAlertIfPresent(driver, log, "Tela inicial");
        WaitForDomIdle(driver, TimeSpan.FromSeconds(3), CancellationToken.None);
    }

    private static void FillFullForm(IWebDriver driver, CpexExercisePayload p, List<string> log)
    {
        var f = p.Fields;
        SelectBest(driver, f.GetValueOrDefault("indicativo"), ["indicativo"], log, "Indicativo");
        SelectBest(driver, f.GetValueOrDefault("posto_grad"), ["posto graduacao", "posto/graduação", "posto grad", "graduacao"], log, "Posto/graduação");
        FillBestLogged(driver, f.GetValueOrDefault("representante_nome"), ["nome do representante legal", "representante legal ou preposto"], log, "Representante — nome");
        FillBestLogged(driver, f.GetValueOrDefault("representante_cpf"), ["cpf do representante legal", "cpf do representante"], log, "Representante — CPF");
        FillBestLogged(driver, f.GetValueOrDefault("representante_idt"), ["identidade do representante legal", "identidade do representante"], log, "Representante — identidade");
        FillBestLogged(driver, f.GetValueOrDefault("periodo_inicio"), ["data inicio do periodo da divida", "data início do período da dívida", "data inicio"], log, "Período — início");
        FillBestLogged(driver, f.GetValueOrDefault("periodo_fim"), ["data final do periodo da divida", "data final do período da dívida", "data final"], log, "Período — final");
        FillBestLogged(driver, f.GetValueOrDefault("qtd_meses"), ["quantidade de meses", "meses que se refere"], log, "Quantidade de meses");

        var codeCount = FillCodeRows(driver, p.Codes);
        log.Add($"Tabela de códigos: {codeCount} linha(s) preenchida(s).");
        SelectBest(driver, f.GetValueOrDefault("tipo_exercicio_anterior"), ["tipo de exercicio anterior", "tipo de exercício anterior"], log, "Tipo de EA");

        var values = new (string Key, string CorrectedKey, string[] Labels)[]
        {
            ("valor_bruto_devido","valor_bruto_devido_corrigido",["valor bruto devido"]),
            ("desconto_pensao_3_zeh","desconto_pensao_3_zeh_corrigido",["desconto pensao militar 3","zeh"]),
            ("desconto_pensao_zeb","desconto_pensao_zeb_corrigido",["desconto pensao militar 7","9,5","10,5","zeb"]),
            ("desconto_pensao_15_zec","desconto_pensao_15_zec_corrigido",["desconto pensao militar 1,5","zec"]),
            ("desconto_fusex_zea","desconto_fusex_zea_corrigido",["desconto fusex","zea"]),
            ("da_ex_ant_ded_gea","da_ex_ant_ded_gea_corrigido",["da ex ant ded","gea"]),
            ("da_ea_aj_con_n_d_geb","da_ea_aj_con_n_d_geb_corrigido",["da ea aj con","geb"]),
            ("desconto_pnr_z13","desconto_pnr_z13_corrigido",["desconto pnr","z13"]),
            ("desconto_pnr_z14","desconto_pnr_z14_corrigido",["desconto pnr","z14"]),
            ("fusex_dep_zef","fusex_dep_zef_corrigido",["fusex dep","zef"]),
            ("valor_liquido_devido","valor_liquido_devido_corrigido",["valor liquido devido","valor líquido devido"])
        };
        foreach (var row in values)
        {
            var original = p.Values.GetValueOrDefault(row.Key, string.Empty); var corrected = p.Values.GetValueOrDefault(row.CorrectedKey, string.Empty);
            if (!HasMoney(original) && !HasMoney(corrected)) continue;
            var ok = FillControlsNearLabel(driver, row.Labels, [HasMoney(original) ? original : string.Empty, HasMoney(corrected) ? corrected : string.Empty]);
            log.Add($"Valores — {row.Key}: {(ok ? "OK" : "confira manualmente")}.");
        }

        FillBestLogged(driver, f.GetValueOrDefault("data_requerimento"), ["data do requerimento", "data requerimento"], log, "Data do requerimento");
        FillControlsNearLabel(driver, ["averbacao bi", "averbação bi", "bi/adt", "adt"], [f.GetValueOrDefault("averbacao_bi_adt"), f.GetValueOrDefault("averbacao_data")]);
        FillBestLogged(driver, f.GetValueOrDefault("documento_materializou"), ["documento que materializou o direito", "materializou o direito", "materializou"], log, "Documento que materializou");
        FillBestLogged(driver, f.GetValueOrDefault("objeto_justificativa"), ["objeto da divida", "objeto da dívida", "justificativa", "motivo da divida"], log, "Objeto/justificativa");
        FillRadioOrSelect(driver, "Não", ["possui pensao judiciaria", "pensão judiciária", "zed"]);
        FillRadioOrSelect(driver, "Sim", ["ficha cadastro"]); FillRadioOrSelect(driver, "Sim", ["ficha financeira"]); FillRadioOrSelect(driver, "Sim", ["levantamento siafi", "siafi"]);
        var remittance = f.GetValueOrDefault("documento_remessa"); if (!string.IsNullOrWhiteSpace(remittance)) FillBest(driver, remittance, ["documento de remessa", "remessa do processo"], []);
        SelectBest(driver, f.GetValueOrDefault("banco"), ["banco", "instituicao bancaria", "domicilio bancario", "banco para credito"], log, "Banco");
        FillBestLogged(driver, f.GetValueOrDefault("agencia"), ["agencia sem digito", "agência sem dígito", "agencia"], log, "Agência");
        FillBestLogged(driver, f.GetValueOrDefault("conta"), ["conta corrente", "conta corrente com dv", "conta"], log, "Conta");
        FillControlsAfterHeading(driver, ["informacoes do operador", "informações do operador"],
            [f.GetValueOrDefault("operador_nome"), f.GetValueOrDefault("operador_cpf"), f.GetValueOrDefault("operador_email_om"), f.GetValueOrDefault("operador_celular")]);
    }

    private static int FillCodeRows(IWebDriver driver, IReadOnlyList<CpexExerciseCodeRow> rows)
    {
        if (rows.Count == 0) return 0;
        var controls = VisibleControls(driver)
            .Where(IsTextControl)
            .Select(x =>
            {
                var position = GetElementPosition(x);
                return new { Element = x, X = position.X, Y = position.Y, Context = Normalize(Context(driver, x)) };
            })
            .ToList();
        var candidates = controls
            .Where(x => x.Context.Contains("codigo") || x.Context.Contains("descricao") || x.Context.Contains("valor original") || x.Context.Contains("valor corrigido"))
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();
        var filled = 0;
        foreach (var row in rows)
        {
            var group = candidates.Skip(filled * 4).Take(4).Select(x => x.Element).ToList();
            if (group.Count < 3) break;
            var values = new[] { row.Code, row.Description, row.Original, row.Corrected };
            for (var i = 0; i < Math.Min(group.Count, values.Length); i++) Fill(group[i], values[i]);
            filled++;
        }
        if (filled > 0) return filled;
        // fallback: procura linhas da tabela e preenche os primeiros controles de cada linha.
        foreach (var tr in driver.FindElements(By.CssSelector("tr")))
        {
            if (filled >= rows.Count) break;
            var rowControls = tr.FindElements(By.CssSelector("input:not([type=hidden]),select,textarea")).Where(IsVisible).ToList();
            if (rowControls.Count < 3) continue;
            var item = rows[filled]; var values = new[] { item.Code, item.Description, item.Original, item.Corrected };
            for (var i = 0; i < Math.Min(values.Length, rowControls.Count); i++) Fill(rowControls[i], values[i]);
            filled++;
        }
        return filled;
    }

    private static bool FillBest(IWebDriver driver, string? value, IEnumerable<string> labels, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalizedLabels = labels.Select(Normalize).ToArray(); var normalizedNames = names.Select(Normalize).ToArray();
        var best = VisibleControls(driver).Select(el =>
        {
            var context = Normalize(Context(driver, el)); var key = Normalize(string.Join(" ", el.GetAttribute("id"), el.GetAttribute("name"), el.GetAttribute("placeholder"), el.GetAttribute("aria-label")));
            var score = normalizedLabels.Sum(t => context.Contains(t) ? 20 : 0) + normalizedNames.Sum(t => key.Contains(t) ? 30 : 0);
            if (string.IsNullOrWhiteSpace(el.GetAttribute("value"))) score += 2;
            return (Element: el, Score: score);
        }).OrderByDescending(x => x.Score).FirstOrDefault();
        return best.Score > 0 && Fill(best.Element, value);
    }

    private static void FillBestLogged(IWebDriver driver, string? value, IEnumerable<string> labels, List<string> log, string title)
        => log.Add($"{title}: {(FillBest(driver, value, labels, []) ? "OK" : "não localizado / vazio")}.");

    private static bool SelectBest(IWebDriver driver, string? value, IEnumerable<string> labels, List<string> log, string title)
    {
        if (string.IsNullOrWhiteSpace(value)) { log.Add(title + ": vazio."); return false; }
        var controls = VisibleControls(driver).Where(x => x.TagName.Equals("select", StringComparison.OrdinalIgnoreCase)).ToList();
        var terms = labels.Select(Normalize).ToArray();
        var best = controls.Select(x => (Element: x, Score: terms.Sum(t => Normalize(Context(driver, x)).Contains(t) ? 20 : 0))).OrderByDescending(x => x.Score).FirstOrDefault();
        var ok = best.Score > 0 && Fill(best.Element, value);
        if (!ok) ok = controls.Any(x => FillSelectIfContains(x, value));
        log.Add($"{title}: {(ok ? "OK" : "não localizado")}.");
        return ok;
    }

    private static bool FillControlsNearLabel(IWebDriver driver, IEnumerable<string> labels, IReadOnlyList<string?> values)
    {
        var terms = labels.Select(Normalize).ToArray();
        foreach (var container in driver.FindElements(By.CssSelector("tr,div,td,fieldset,p")))
        {
            if (!IsVisible(container) || !ContainsAny(Normalize(container.Text), terms)) continue;
            var controls = container.FindElements(By.CssSelector("input:not([type=hidden]),select,textarea"))
                .Where(IsVisible)
                .OrderBy(x => GetElementPosition(x).X)
                .ToList();
            if (controls.Count == 0) continue;
            var changed = false;
            for (var i = 0; i < Math.Min(values.Count, controls.Count); i++) if (!string.IsNullOrWhiteSpace(values[i])) changed |= Fill(controls[i], values[i]);
            if (changed) return true;
        }
        return false;
    }

    private static bool FillControlsAfterHeading(IWebDriver driver, IEnumerable<string> headings, IReadOnlyList<string?> values)
    {
        var terms = headings.Select(Normalize).ToArray();
        var heading = driver.FindElements(By.XPath("//*[self::h1 or self::h2 or self::h3 or self::h4 or self::b or self::strong or self::td or self::div]"))
            .FirstOrDefault(x => IsVisible(x) && ContainsAny(Normalize(x.Text), terms));
        if (heading is null) return false;
        var headingPosition = GetElementPosition(heading);
        var controls = VisibleControls(driver)
            .Select(x =>
            {
                var position = GetElementPosition(x);
                return new { Element = x, X = position.X, Y = position.Y };
            })
            .Where(x => x.Y > headingPosition.Y)
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .Take(values.Count)
            .Select(x => x.Element)
            .ToList();
        var changed = false;
        for (var i = 0; i < Math.Min(values.Count, controls.Count); i++) if (!string.IsNullOrWhiteSpace(values[i])) changed |= Fill(controls[i], values[i]);
        return changed;
    }

    private static bool FillRadioOrSelect(IWebDriver driver, string wanted, IEnumerable<string> labels)
    {
        var terms = labels.Select(Normalize).ToArray();
        foreach (var container in driver.FindElements(By.CssSelector("tr,div,td,fieldset,p")))
        {
            if (!IsVisible(container) || !ContainsAny(Normalize(container.Text), terms)) continue;
            foreach (var radio in container.FindElements(By.CssSelector("input[type=radio],input[type=checkbox]")))
            {
                var context = Normalize(Context(driver, radio));
                if (context.Contains(Normalize(wanted))) { try { if (!radio.Selected) radio.Click(); return true; } catch { } }
            }
            var select = container.FindElements(By.TagName("select")).FirstOrDefault(IsVisible);
            if (select is not null && Fill(select, wanted)) return true;
        }
        return false;
    }

    private static bool Fill(IWebElement element, string? value)
    {
        if (element is null || !IsVisible(element) || !element.Enabled) return false;
        var text = value ?? string.Empty;
        try
        {
            if (element.TagName.Equals("select", StringComparison.OrdinalIgnoreCase)) return FillSelectIfContains(element, text);
            if (string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase) || string.Equals(element.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase))
            {
                var yes = Normalize(text) is "sim" or "s" or "yes";
                if (element.Selected != yes) element.Click(); return true;
            }
            element.Click();
            try { element.Clear(); } catch { }
            element.SendKeys(text);
            return true;
        }
        catch
        {
            try
            {
                var driver = ((IWrapsDriver)element).WrappedDriver;
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].value=arguments[1]; arguments[0].dispatchEvent(new Event('input',{bubbles:true})); arguments[0].dispatchEvent(new Event('change',{bubbles:true}));", element, text);
                return true;
            }
            catch { return false; }
        }
    }

    private static bool FillSelectIfContains(IWebElement element, string? value)
    {
        try
        {
            var select = new SelectElement(element); var wanted = Normalize(value);
            var match = select.Options.Select(x => (Element: x, Text: Normalize(x.Text))).OrderByDescending(x => ScoreOption(x.Text, wanted)).FirstOrDefault();
            if (match.Element is null || ScoreOption(match.Text, wanted) <= 0) return false;
            select.SelectByText(match.Element.Text); return true;
        }
        catch { return false; }
    }

    private static int ScoreOption(string option, string wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return 0;
        if (option == wanted) return 100;
        if (option.Contains(wanted) || wanted.Contains(option)) return 70;
        return wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(option.Contains) * 10;
    }

    private static List<IWebElement> VisibleControls(IWebDriver driver)
        => driver.FindElements(By.CssSelector("input:not([type=hidden]),select,textarea")).Where(IsVisible).ToList();
    private static bool IsTextControl(IWebElement e)
    {
        if (e.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase)) return true;
        if (!e.TagName.Equals("input", StringComparison.OrdinalIgnoreCase)) return false;
        return (e.GetAttribute("type") ?? "text").ToLowerInvariant() is "" or "text" or "tel" or "number" or "date" or "email";
    }
    private static bool IsVisible(IWebElement e) { try { return e.Displayed && e.Enabled; } catch { return false; } }

    private static (int X, int Y) GetElementPosition(IWebElement element)
    {
        try
        {
            var location = element.Location;
            return (location.X, location.Y);
        }
        catch
        {
            return (int.MaxValue, int.MaxValue);
        }
    }

    private static string Context(IWebDriver driver, IWebElement element)
    {
        try
        {
            return Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const e=arguments[0]; let out=[];
                if(e.labels) for(const l of e.labels) out.push(l.innerText||l.textContent||'');
                for(const a of ['id','name','placeholder','aria-label','title','value']) out.push(e.getAttribute(a)||'');
                let p=e; for(let i=0;i<3&&p;i++,p=p.parentElement) out.push((p.innerText||p.textContent||'').slice(0,500));
                return out.join(' ');
                """, element), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool AcceptAlertIfPresent(IWebDriver driver, List<string>? log = null, string stage = "CPEx")
    {
        try
        {
            var alert = driver.SwitchTo().Alert();
            var text = alert.Text;
            alert.Accept();
            log?.Add($"{stage}: aviso do site confirmado{(string.IsNullOrWhiteSpace(text) ? string.Empty : " — " + text)}.");
            return true;
        }
        catch (NoAlertPresentException) { return false; }
        catch (WebDriverException) { return false; }
    }

    private static void WaitReady(IWebDriver driver, int seconds)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(seconds));
        wait.IgnoreExceptionTypes(typeof(WebDriverException), typeof(InvalidOperationException));
        wait.Until(d => Convert.ToString(((IJavaScriptExecutor)d).ExecuteScript("return document.readyState"), CultureInfo.InvariantCulture) is "complete" or "interactive");
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

    private static void WaitForDomIdle(IWebDriver driver, TimeSpan timeout, CancellationToken ct)
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
        }, timeout, ct);

    private static bool AlertPresent(IWebDriver driver)
    {
        try { _ = driver.SwitchTo().Alert(); return true; }
        catch { return false; }
    }

    private static string BodySignature(IWebDriver driver)
    {
        try
        {
            var text = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return (document.body && document.body.innerText || '').slice(0, 2000);"), CultureInfo.InvariantCulture) ?? string.Empty;
            return Normalize(text);
        }
        catch { return string.Empty; }
    }

    private static bool IsLoginPage(IWebDriver driver)
    {
        try
        {
            var url = Normalize(driver.Url); if (url.Contains("asplogon") || url.Contains("formlogin")) return true;
            return driver.FindElements(By.CssSelector("input[type=password]")).Any(IsVisible) && ContainsAny(Normalize(driver.PageSource), ["login", "senha", "acesso"]);
        }
        catch { return false; }
    }

    private string SavePayload(CpexExercisePayload payload)
    {
        Directory.CreateDirectory(_paths.ExercisePreviousLogsDirectory);
        var path = Path.Combine(_paths.ExercisePreviousLogsDirectory, $"cpex_payload_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static (string Code, string Description) ExtractCode(string text)
    {
        var raw = (text ?? string.Empty).Trim();
        var match = System.Text.RegularExpressions.Regex.Match(raw.ToUpperInvariant(), @"\b([A-Z]\d{2}|Z[A-Z]{2}|G[A-Z]{2}|Z\d{2}|[A-Z]\d[A-Z])\b");
        if (!match.Success) return (string.Empty, raw);
        var code = match.Groups[1].Value;
        var desc = System.Text.RegularExpressions.Regex.Replace(raw, @"\b" + System.Text.RegularExpressions.Regex.Escape(code) + @"\b", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', '-', '–', '—', ':');
        return (code, string.IsNullOrWhiteSpace(desc) ? raw : desc);
    }
    private static string Money(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
    private static bool HasMoney(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out var number) && Math.Abs(number) >= 0.005m;
    private static string DateBr(string value) => DateTime.TryParseExact(value, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy") : value ?? string.Empty;
    private static int MonthsBetween(string start, string end)
    {
        if (!DateTime.TryParseExact(start, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var a) ||
            !DateTime.TryParseExact(end, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var b)) return 0;
        if (b < a) (a, b) = (b, a); return ((b.Year - a.Year) * 12) + b.Month - a.Month + 1;
    }
    private static string MapSituation(string value)
    {
        var n = Normalize(value);
        if (string.IsNullOrWhiteSpace(n)) return "Ativo";
        // Verifica INATIVO antes de ATIVO, pois a palavra "inativo" contém "ativo".
        if (n.Contains("inativo") || n.Contains("reserva") || n.Contains("reform")) return "Inativo";
        if (n.Contains("pension")) return "Pensionista";
        if (n.Contains("ativo") || n.Contains("ativa")) return "Ativo";
        return value?.Trim() ?? string.Empty;
    }
    private static string MapIndicative(string value)
    {
        var n = Normalize(value); if (string.IsNullOrWhiteSpace(n)) return "Militar de Carreira";
        if (n.Contains("tempor")) return "Militar Temporário"; if (n.Contains("reintegr")) return "Militar Reintegrado";
        if (n.Contains("carreira") || n.Contains("militar")) return "Militar de Carreira"; return value?.Trim() ?? string.Empty;
    }
    private static string MapBank(string value)
    {
        var n = Normalize(value); if (n.Contains("bradesco") || n.Contains("237")) return "BRADESCO";
        if (n.Contains("brasil") || n.Contains("001")) return "BANCO DO BRASIL"; if (n.Contains("caixa") || n.Contains("104")) return "CAIXA ECONÔMICA FEDERAL";
        if (n.Contains("itau") || n.Contains("341")) return "ITAÚ"; if (n.Contains("santander") || n.Contains("033")) return "SANTANDER";
        if (n.Contains("sicredi")) return "SICREDI"; if (n.Contains("sicoob")) return "SICOOB"; return value?.Trim() ?? string.Empty;
    }
    private static string MapRank(string value)
    {
        var n = Normalize(value); if (n.Contains("gen ex") || n.Contains("general de exercito")) return "GENERAL DE EXÉRCITO";
        if (n.Contains("gen div") || n.Contains("general de divisao")) return "GENERAL DE DIVISÃO"; if (n.Contains("gen bda") || n.Contains("general de brigada")) return "GENERAL DE BRIGADA";
        // Tenente-coronel precisa ser testado antes de coronel.
        if (n.Contains("ten cel") || n.Contains("tenente coronel")) return "TENENTE CORONEL";
        if (Word(n,"cel") || n.Equals("coronel", StringComparison.Ordinal) || n.StartsWith("coronel ", StringComparison.Ordinal)) return "CORONEL";
        if (Word(n,"maj") || n.Contains("major")) return "MAJOR"; if (Word(n,"cap") || n.Contains("capitao")) return "CAPITÃO";
        if ((n.Contains('1') && n.Contains("ten")) || n.Contains("primeiro tenente")) return "PRIMEIRO TENENTE";
        if ((n.Contains('2') && n.Contains("ten")) || n.Contains("segundo tenente")) return "SEGUNDO TENENTE"; if (n.Contains("asp")) return "ASPIRANTE A OFICIAL";
        if (n.Contains("sub") && n.Contains("ten")) return "SUB TENENTE";
        if ((n.Contains('1') && n.Contains("sgt")) || n.Contains("primeiro sargento")) return "PRIMEIRO SARGENTO";
        if ((n.Contains('2') && n.Contains("sgt")) || n.Contains("segundo sargento")) return "SEGUNDO SARGENTO";
        if ((n.Contains('3') && n.Contains("sgt")) || n.Contains("terceiro sargento")) return "TERCEIRO SARGENTO";
        var variable = ContainsAny(n,["vrv","variavel","nao engajado"]); var professional = ContainsAny(n,["profl","profissional","engajado","ef prof"]) && !variable;
        if (Word(n,"cb") || n.Contains("cabo")) return variable ? "CABO NAO ENGAJADO" : professional || n.Contains("ef") ? "CABO ENGAJADO" : "CABO";
        if (Word(n,"sd") || n.Contains("soldado")) return variable ? "SD NAO ENGAJADO" : "SD ENGAJADO"; return value?.Trim() ?? string.Empty;
    }
    private static bool Word(string text, string word) => System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + System.Text.RegularExpressions.Regex.Escape(word) + @"\b");
    private static string FirstCleanName(params string[] values) => CleanRankPrefix(values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty);
    private static string CleanRankPrefix(string value) => System.Text.RegularExpressions.Regex.Replace(value ?? string.Empty, @"^\s*(gen\s+ex|gen\s+div|gen\s+bda|cel|coronel|ten\s*cel|tenente\s+coronel|maj|major|cap|capitao|1[ºoªa]?\s*ten|2[ºoªa]?\s*ten|asp|sub\s*ten|1[ºoªa]?\s*sgt|2[ºoªa]?\s*sgt|3[ºoªa]?\s*sgt|cb|cabo|sd|soldado)(?:\s+ef\s+\w+)?\s+", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    private static string First(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static bool IsYesNo(string value) => Normalize(value) is "sim" or "nao" or "s" or "n";
    private static bool ContainsAny(string text, IEnumerable<string> values) => values.Any(v => text.Contains(Normalize(v), StringComparison.Ordinal));
    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
    }
}
