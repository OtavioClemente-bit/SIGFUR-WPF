using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class CpexExerciseAutomationService
{
    public const string LoginUrl = "https://cpex-intranet.eb.mil.br/asplogon_nova.asp?url=area_ua_cpex/index.asp";
    public const string TargetUrl = "https://cpex-intranet.eb.mil.br/Exerc_Anterior/sel_opcao.asp";
    public const string StatusReportUrl = "https://cpex-intranet.eb.mil.br/Exerc_Anterior/relatoriol_ea_UA.asp";
    private readonly AppPaths _paths;
    private readonly ExercisePreviousRepository _repository;
    private static readonly List<IWebDriver> OpenDrivers = [];
    private static readonly object CaptureLogSync = new();

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
            if (!File.Exists(path)) return CompleteOperatorDefaults(new CpexExerciseSettings());
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
            settings = CompleteOperatorDefaults(settings);
            if (!path.Equals(_paths.ExercisePreviousCpexSettingsFile, StringComparison.OrdinalIgnoreCase)) SaveSettings(settings);
            return settings;
        }
        catch { return CompleteOperatorDefaults(new CpexExerciseSettings()); }
    }

    private CpexExerciseSettings CompleteOperatorDefaults(CpexExerciseSettings settings)
    {
        try
        {
            if (File.Exists(_paths.UiConfigFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_paths.UiConfigFile));
                var root = document.RootElement;
                string Read(params string[] names)
                {
                    foreach (var name in names)
                        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                            return value.GetString()?.Trim() ?? string.Empty;
                    return string.Empty;
                }
                settings.OperatorName = First(settings.OperatorName, Read("operador", "operador_nome"));
                settings.OperatorEmail = First(settings.OperatorEmail, Read("operador_email_om", "email_om"));
                settings.OperatorPhone = First(settings.OperatorPhone, Read("operador_celular", "operador_telefone"));
            }
        }
        catch { }
        settings.OperatorCpf = First(settings.OperatorCpf, settings.LoginCpf);
        return settings;
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
            var extracted = ResolveCpexCode(code.Description);
            if (code.Type.Equals("Despesa", StringComparison.OrdinalIgnoreCase))
            {
                discounts += total.Original; correctedDiscounts += total.Corrected;
            }
            else
            {
                gross += total.Original; correctedGross += total.Corrected;
            }
            if (!string.IsNullOrWhiteSpace(extracted.Code)) discountByCode[extracted.Code] = (total.Original, total.Corrected);
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
        F("codom", CpexCodom(p.Codom)); F("sigla_om", p.OrganizationName);
        F("situacao", MapSituation(p.Situation)); F("indicativo", MapIndicative(p.EaIndicative));
        F("nome", p.FullName); F("posto_grad", MapRank(p.Rank));
        F("representante_nome", FirstCleanName(p.RepresentativeName, p.CompanyCommander, p.OdNameRank, p.FormerOdName));
        F("representante_cpf", ExercisePreviousRepository.Digits(string.IsNullOrWhiteSpace(p.RepresentativeCpf) ? p.FormerOdCpf : p.RepresentativeCpf));
        F("representante_idt", ExercisePreviousRepository.Digits(string.IsNullOrWhiteSpace(p.RepresentativeIdentity) ? p.FormerOdIdentity : p.RepresentativeIdentity));
        F("periodo_inicio", DateBr(p.PeriodStart)); F("periodo_fim", DateBr(p.PeriodEnd));
        F("qtd_meses", MonthsBetween(p.PeriodStart, p.PeriodEnd).ToString("0000", CultureInfo.InvariantCulture));
        var previousExerciseType = string.IsNullOrWhiteSpace(p.PreviousExerciseType) ? p.DebtType : p.PreviousExerciseType;
        // Comparação com o processo da Ten Pecene, já protocolado com sucesso:
        // auxílio-fardamento deve sair como código A56 e tipo de EA AEG. Um processo
        // antigo do Sub Paulo estava salvo apenas como "AUX FARDAMENTO" + AEA; o CPEx
        // recebeu o formulário, mas produziu somente a casca vazia do relatório.
        if (rows.Any(x => x.Code.Equals("A56", StringComparison.OrdinalIgnoreCase)))
            previousExerciseType = "AEG - Pagamento de Auxílio-Fardamento";
        F("tipo_exercicio_anterior", previousExerciseType);
        F("data_requerimento", DateBr(p.RequestDate)); F("averbacao_bi_adt", ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber));
        F("averbacao_data", DateBr(p.BulletinDate)); F("documento_materializou", p.RightMaterializationDocument);
        F("objeto_justificativa", First(p.NonPaymentExplanation, p.PaymentReason, p.Object));
        F("possui_pensao_judiciaria", "Não"); F("pesquisa_ficha_cadastro", "Sim"); F("pesquisa_ficha_financeira", "Sim"); F("pesquisa_levantamento_siafi", "Sim");
        F("documento_remessa", IsYesNo(p.RemittanceDocument) ? string.Empty : p.RemittanceDocument);
        F("banco", MapBank(p.Bank)); F("agencia", ExercisePreviousRepository.Digits(p.Agency)); F("conta", CpexAccount(p.Account));
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
        return await Task.Run(() => RunBrowser(process, payload, settings, debugFile, ct), ct);
    }

    public async Task<CpexExerciseStatusResult> QueryStatusAsync(
        ExercisePreviousProcess process,
        int year,
        CpexExerciseSettings settings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (process.Id <= 0) throw new InvalidOperationException("Salve o processo antes de consultar a situação no CPEx.");
        if (string.IsNullOrWhiteSpace(process.CpexProtocol)) throw new InvalidOperationException("Informe o protocolo CPEx antes da consulta.");
        if (string.IsNullOrWhiteSpace(process.FullName)) throw new InvalidOperationException("Informe o nome completo do militar antes da consulta.");
        if (year is < 2000 or > 2100) throw new InvalidOperationException("Informe um ano válido para o relatório do CPEx.");
        SaveSettings(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var worker = Task.Run(() => RunStatusQuery(process, year, settings, progress, timeout.Token), timeout.Token);
            var result = await worker.WaitAsync(TimeSpan.FromMinutes(3), ct);
            progress?.Report("Situação localizada. Salvando a consulta no processo...");
            await _repository.SaveCpexStatusCheckAsync(process.Id, result, ct);
            WriteStatusQueryLog($"Processo {process.Id:0000}, relatório {year}: consulta concluída com situação '{result.Situation}'.");
            return result;
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            WriteStatusQueryLog($"Processo {process.Id:0000}, relatório {year}: consulta encerrada pelo limite total de 3 minutos.");
            throw new InvalidOperationException("A consulta do relatório CPEx ultrapassou 3 minutos e foi encerrada. Confira a conexão com a intranet e tente novamente.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            WriteStatusQueryLog($"Processo {process.Id:0000}, relatório {year}: consulta cancelada pelo limite total de 3 minutos.");
            throw new InvalidOperationException("A consulta do relatório CPEx ultrapassou 3 minutos e foi encerrada. Confira a conexão com a intranet e tente novamente.");
        }
        catch (Exception ex)
        {
            WriteStatusQueryLog($"Processo {process.Id:0000}, relatório {year}: falha na consulta. {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public async Task<IReadOnlyList<CpexExerciseBatchStatusItem>> QueryStatusesAsync(
        IReadOnlyList<ExercisePreviousProcess> processes,
        CpexExerciseSettings settings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var candidates = (processes ?? [])
            .Where(x => x is not null && x.Id > 0 && !x.Paid)
            .DistinctBy(x => x.Id)
            .ToList();
        if (candidates.Count == 0) return [];

        SaveSettings(settings);
        var timeoutMinutes = Math.Clamp(3 + candidates.Count / 20, 3, 10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            var worker = Task.Run(() => RunStatusQueries(candidates, settings, progress, timeout.Token), timeout.Token);
            var results = await worker.WaitAsync(TimeSpan.FromMinutes(timeoutMinutes), ct);
            for (var index = 0; index < results.Count; index++)
            {
                var item = results[index];
                progress?.Report($"Salvando resultado {index + 1}/{results.Count}: {item.Result.MilitaryName}...");
                await _repository.SaveCpexStatusCheckAsync(item.ProcessId, item.Result, ct);
            }
            WriteStatusQueryLog($"Consulta em lote concluída: {results.Count(x => x.Found)} localizado(s), {results.Count(x => !x.Found)} não localizado(s), {results.Count} total.");
            return results;
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            WriteStatusQueryLog($"Consulta em lote encerrada pelo limite total de {timeoutMinutes} minutos.");
            throw new InvalidOperationException($"A consulta em lote ultrapassou {timeoutMinutes} minutos e foi encerrada. Confira a conexão com a intranet e tente novamente.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            WriteStatusQueryLog($"Consulta em lote cancelada pelo limite total de {timeoutMinutes} minutos.");
            throw new InvalidOperationException($"A consulta em lote ultrapassou {timeoutMinutes} minutos e foi encerrada. Confira a conexão com a intranet e tente novamente.");
        }
        catch (Exception ex)
        {
            WriteStatusQueryLog($"Falha na consulta em lote. {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private CpexExerciseStatusResult RunStatusQuery(
        ExercisePreviousProcess process,
        int year,
        CpexExerciseSettings settings,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        IWebDriver? driver = null;
        try
        {
            progress?.Report("Iniciando o navegador para consultar o CPEx...");
            driver = CreateDriver(settings, TimeSpan.FromSeconds(45), forceHeadless: true);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
            progress?.Report("Abrindo a Área da UA em segundo plano...");
            driver.Navigate().GoToUrl(LoginUrl);
            WaitReady(driver, 15);
            var loginLog = new List<string>();
            FillLogin(driver, settings.LoginCpf, settings.GetPassword(), loginLog);
            if (IsLoginPage(driver))
            {
                const int loginTimeout = 30;
                progress?.Report("Concluindo o login automático em segundo plano...");
                var loggedIn = WaitUntil(driver, d => !IsLoginPage(d), TimeSpan.FromSeconds(loginTimeout), ct);
                WaitForDomIdle(driver, TimeSpan.FromSeconds(3), ct);
                if (!loggedIn) ct.ThrowIfCancellationRequested();
                if (IsLoginPage(driver)) throw new InvalidOperationException("O login automático da Área da UA não foi concluído. Confira CPF e senha na configuração do CPEX.");
            }

            progress?.Report("Login concluído. Abrindo Exercícios Anteriores...");
            driver.Navigate().GoToUrl(TargetUrl);
            WaitReady(driver, 15);
            if (IsLoginPage(driver)) throw new InvalidOperationException("A sessão voltou para o logon. Confira usuário, senha, captcha e acesso à intranet.");

            progress?.Report($"Carregando o relatório do CPEx de {year}...");
            driver.Navigate().GoToUrl($"{StatusReportUrl}?ano={year:0000}");
            WaitReady(driver, 20);
            WaitUntil(driver, d => ReportHasRows(d) || IsLoginPage(d), TimeSpan.FromSeconds(15), ct);
            if (IsLoginPage(driver)) throw new InvalidOperationException("A sessão expirou ao abrir o relatório anual do CPEx.");

            progress?.Report($"Procurando protocolo {process.CpexProtocol} e o nome do militar...");
            var rows = ReadReportRows(driver, process.CpexProtocol, process.FullName);
            return FindStatusMatch(rows, process.CpexProtocol, process.FullName, year);
        }
        finally
        {
            if (driver is not null) { try { driver.Quit(); } catch { } driver.Dispose(); }
        }
    }

    private List<CpexExerciseBatchStatusItem> RunStatusQueries(
        IReadOnlyList<ExercisePreviousProcess> processes,
        CpexExerciseSettings settings,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new List<CpexExerciseBatchStatusItem>();
        var queryable = new List<ExercisePreviousProcess>();
        foreach (var process in processes)
        {
            var year = process.CpexStatusYear ?? process.ProcessYear ?? DateTime.Today.Year;
            if (string.IsNullOrWhiteSpace(process.CpexProtocol) || string.IsNullOrWhiteSpace(process.FullName))
            {
                results.Add(NotFoundBatchItem(process, year,
                    string.IsNullOrWhiteSpace(process.CpexProtocol)
                        ? "Processo sem protocolo CPEx informado."
                        : "Processo sem nome completo informado."));
            }
            else queryable.Add(process);
        }
        if (queryable.Count == 0) return results;

        IWebDriver? driver = null;
        try
        {
            progress?.Report("Iniciando consulta geral do CPEx em segundo plano...");
            driver = CreateDriver(settings, TimeSpan.FromSeconds(45), forceHeadless: true);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
            driver.Navigate().GoToUrl(LoginUrl);
            WaitReady(driver, 15);
            FillLogin(driver, settings.LoginCpf, settings.GetPassword(), []);
            if (IsLoginPage(driver))
            {
                var loggedIn = WaitUntil(driver, d => !IsLoginPage(d), TimeSpan.FromSeconds(30), ct);
                WaitForDomIdle(driver, TimeSpan.FromSeconds(3), ct);
                if (!loggedIn) ct.ThrowIfCancellationRequested();
                if (IsLoginPage(driver))
                    throw new InvalidOperationException("O login automático da Área da UA não foi concluído. Confira CPF e senha na configuração do CPEX.");
            }

            driver.Navigate().GoToUrl(TargetUrl);
            WaitReady(driver, 15);
            if (IsLoginPage(driver)) throw new InvalidOperationException("A sessão voltou para o logon. Confira CPF, senha e acesso à intranet.");

            var checkedCount = 0;
            foreach (var yearGroup in queryable.GroupBy(x => x.CpexStatusYear ?? x.ProcessYear ?? DateTime.Today.Year).OrderBy(x => x.Key))
            {
                ct.ThrowIfCancellationRequested();
                var year = yearGroup.Key;
                progress?.Report($"Carregando relatório do CPEx de {year} em segundo plano...");
                driver.Navigate().GoToUrl($"{StatusReportUrl}?ano={year:0000}");
                WaitReady(driver, 20);
                WaitUntil(driver, d => ReportHasRows(d) || IsLoginPage(d), TimeSpan.FromSeconds(20), ct);
                if (IsLoginPage(driver)) throw new InvalidOperationException("A sessão expirou ao abrir o relatório anual do CPEx.");

                foreach (var process in yearGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    checkedCount++;
                    progress?.Report($"Consultando {checkedCount}/{queryable.Count}: {process.FullName} — protocolo {process.CpexProtocol}...");
                    try
                    {
                        var rows = ReadReportRows(driver, process.CpexProtocol, process.FullName);
                        results.Add(new CpexExerciseBatchStatusItem
                        {
                            ProcessId = process.Id,
                            Found = true,
                            Result = FindStatusMatch(rows, process.CpexProtocol, process.FullName, year)
                        });
                    }
                    catch (InvalidOperationException ex)
                    {
                        results.Add(NotFoundBatchItem(process, year, ex.Message));
                    }
                }
            }
            return results.OrderByDescending(x => x.ProcessId).ToList();
        }
        finally
        {
            if (driver is not null) { try { driver.Quit(); } catch { } driver.Dispose(); }
        }
    }

    private static CpexExerciseBatchStatusItem NotFoundBatchItem(ExercisePreviousProcess process, int year, string detail)
        => new()
        {
            ProcessId = process.Id,
            Found = false,
            Result = new CpexExerciseStatusResult
            {
                Year = year,
                Protocol = process.CpexProtocol?.Trim() ?? string.Empty,
                MilitaryName = process.FullName?.Trim() ?? string.Empty,
                Situation = "NÃO ENCONTRADO",
                Explanation = string.IsNullOrWhiteSpace(detail) ? "Não encontrado no relatório anual do CPEx." : detail.Trim(),
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            }
        };

    private static bool ReportHasRows(IWebDriver driver)
    {
        try
        {
            driver.SwitchTo().DefaultContent();
            if (Convert.ToInt32(((IJavaScriptExecutor)driver).ExecuteScript("return document.querySelectorAll('tr').length;"), CultureInfo.InvariantCulture) > 0)
                return true;
            var frameCount = driver.FindElements(By.CssSelector("frame,iframe")).Count;
            for (var index = 0; index < frameCount; index++)
            {
                driver.SwitchTo().DefaultContent();
                var frames = driver.FindElements(By.CssSelector("frame,iframe"));
                if (index >= frames.Count) break;
                driver.SwitchTo().Frame(frames[index]);
                if (Convert.ToInt32(((IJavaScriptExecutor)driver).ExecuteScript("return document.querySelectorAll('tr').length;"), CultureInfo.InvariantCulture) > 0)
                    return true;
            }
            return false;
        }
        catch (WebDriverException) { return false; }
        finally { try { driver.SwitchTo().DefaultContent(); } catch { } }
    }

    private static List<IReadOnlyList<string>> ReadReportRows(IWebDriver driver, string protocol, string militaryName)
    {
        var rows = new List<IReadOnlyList<string>>();

        void ReadCurrentDocument()
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const wantedProtocol = String(arguments[0] || '').replace(/\D/g, '').replace(/^0+/, '');
                const normalize = value => String(value || '')
                    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
                    .toLowerCase().replace(/\s+/g, ' ').trim();
                const wantedName = normalize(arguments[1]);
                const identifierMatches = value => (String(value || '').match(/\d+/g) || [])
                    .some(part => part.replace(/^0+/, '') === wantedProtocol);
                const nameMatches = value => {
                    const candidate = normalize(value);
                    return candidate === wantedName ||
                        (candidate.length >= 8 && (candidate.includes(wantedName) || wantedName.includes(candidate)));
                };
                return Array.from(document.querySelectorAll('tr'))
                    .map(row => Array.from(row.querySelectorAll('th,td'))
                        .map(cell => String(cell.innerText || cell.textContent || '').replace(/\s+/g, ' ').trim())
                        .filter(Boolean))
                    .filter(cells => cells.length && cells.some(cell => identifierMatches(cell) || nameMatches(cell)));
                """, protocol, militaryName);
            if (raw is not IEnumerable<object> rawRows) return;
            foreach (var rawRow in rawRows)
            {
                if (rawRow is not IEnumerable<object> rawCells) continue;
                IReadOnlyList<string> cells = rawCells
                    .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                if (cells.Count > 0) rows.Add(cells);
            }
        }

        try
        {
            driver.SwitchTo().DefaultContent();
            ReadCurrentDocument();
            var frameCount = driver.FindElements(By.CssSelector("frame,iframe")).Count;
            for (var index = 0; index < frameCount; index++)
            {
                try
                {
                    driver.SwitchTo().DefaultContent();
                    var frames = driver.FindElements(By.CssSelector("frame,iframe"));
                    if (index >= frames.Count) break;
                    driver.SwitchTo().Frame(frames[index]);
                    ReadCurrentDocument();
                }
                catch (WebDriverException) { }
            }
        }
        finally { try { driver.SwitchTo().DefaultContent(); } catch { } }
        return rows;
    }

    internal static CpexExerciseStatusResult FindStatusMatch(
        IEnumerable<IReadOnlyList<string>> reportRows,
        string protocol,
        string militaryName,
        int year)
    {
        var wantedProtocol = ExercisePreviousRepository.Digits(protocol).TrimStart('0');
        var wantedName = NormalizeCompact(militaryName);
        if (string.IsNullOrWhiteSpace(wantedProtocol) || string.IsNullOrWhiteSpace(wantedName))
            throw new InvalidOperationException("Protocolo e nome completo são obrigatórios para conferir o relatório.");

        var rows = reportRows.Where(x => x.Count > 0).ToList();
        var protocolRows = rows.Where(x => x.Any(cell => IdentifierMatches(cell, wantedProtocol))).ToList();
        var nameRows = rows.Where(x => x.Any(cell => NameMatches(cell, wantedName))).ToList();
        var matches = protocolRows.Where(x => x.Any(cell => NameMatches(cell, wantedName))).ToList();
        if (matches.Count == 0)
        {
            if (protocolRows.Count > 0)
                throw new InvalidOperationException($"O protocolo {protocol} aparece no relatório de {year}, mas não está associado ao nome “{militaryName}”. Nada foi alterado.");
            if (nameRows.Count > 0)
                throw new InvalidOperationException($"O nome “{militaryName}” aparece no relatório de {year}, mas o protocolo {protocol} não confere. Nada foi alterado.");
            throw new InvalidOperationException($"Não encontrei o protocolo {protocol} com o nome “{militaryName}” no relatório de {year}. O processo permanece em aberto.");
        }

        var cells = matches
            .OrderByDescending(x => x.Count(cell => IdentifierMatches(cell, wantedProtocol) || NameMatches(cell, wantedName)))
            .First();
        var nameIndex = cells.Select((cell, index) => (cell, index)).First(x => NameMatches(x.cell, wantedName)).index;
        var details = cells.Skip(nameIndex + 1)
            .Select(CleanCell)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !IsReportNumber(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (details.Count == 0)
            throw new InvalidOperationException($"Localizei protocolo e militar no relatório de {year}, mas a situação ainda não foi publicada na linha.");

        var decision = details.FirstOrDefault(x => NormalizeCompact(x).StartsWith("aprovado", StringComparison.Ordinal))
            ?? details.FirstOrDefault(x => ContainsDecision(x, "rejeitad", "restituid"))
            ?? details.FirstOrDefault(x => ContainsDecision(x, "pendencia", "autorizad", "aguardando", "analisad"))
            ?? details[0];
        var explanationParts = details.Where(x => !ReferenceEquals(x, decision) && !x.Equals(decision, StringComparison.OrdinalIgnoreCase)).ToList();
        var explanation = explanationParts.Count == 0 ? decision : string.Join(" | ", explanationParts);
        var reportedName = cells.FirstOrDefault(x => NameMatches(x, wantedName)) ?? militaryName;

        return new CpexExerciseStatusResult
        {
            Year = year,
            Protocol = protocol.Trim(),
            MilitaryName = reportedName,
            Situation = decision,
            Explanation = explanation,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            RawRow = string.Join(" | ", cells.Select(CleanCell))
        };
    }

    private static bool IdentifierMatches(string value, string wanted)
    {
        var digits = ExercisePreviousRepository.Digits(value).TrimStart('0');
        if (digits.Equals(wanted, StringComparison.Ordinal)) return true;
        return System.Text.RegularExpressions.Regex.Matches(value ?? string.Empty, @"\d+")
            .Select(x => x.Value.TrimStart('0'))
            .Any(x => x.Equals(wanted, StringComparison.Ordinal));
    }

    private static bool NameMatches(string value, string wanted)
    {
        var candidate = NormalizeCompact(value);
        return candidate.Equals(wanted, StringComparison.Ordinal)
            || (candidate.Length >= 8 && (candidate.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(candidate, StringComparison.Ordinal)));
    }

    private static bool ContainsDecision(string value, params string[] terms)
    {
        var normalized = NormalizeCompact(value);
        return terms.Any(normalized.Contains);
    }

    private static string NormalizeCompact(string? value)
        => System.Text.RegularExpressions.Regex.Replace(Normalize(value), @"\s+", " ").Trim();

    private static string CleanCell(string? value)
        => System.Text.RegularExpressions.Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ");

    private static bool IsReportNumber(string value)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.IsMatch(compact, @"^(?:R\$)?[\d.]+,\d{2}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(compact, @"^\d+$");
    }

    private string RunBrowser(ExercisePreviousProcess process, CpexExercisePayload payload, CpexExerciseSettings settings, string debugFile, CancellationToken ct)
    {
        var log = new List<string> { "Payload salvo: " + debugFile };
        IWebDriver? driver = null;
        try
        {
            Directory.CreateDirectory(_paths.ExercisePreviousCpexDownloadsDirectory);
            driver = CreateDriver(settings);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(90);
            driver.Navigate().GoToUrl(LoginUrl);
            WaitReady(driver, 30);
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
            WaitReady(driver, 30);
            if (IsLoginPage(driver)) throw new InvalidOperationException("A sessão voltou para o logon. Confira usuário, senha, captcha e acesso à intranet.");

            FillFirstPage(driver, payload, log);
            WaitReady(driver, 30);
            FillFullForm(driver, payload, log);
            AcceptAlertIfPresent(driver, log, "Formulário");
            log.Add("Nenhum envio/protocolo foi confirmado. Confira tudo na tela e envie manualmente.");
            // Após um preenchimento bem-sucedido, a conferência humana é obrigatória.
            // Portanto, o navegador permanece aberto independentemente da preferência
            // usada para conservar a janela em cenários de erro.
            var monitoredDriver = driver;
            lock (OpenDrivers) OpenDrivers.Add(monitoredDriver);
            driver = null;
            _ = MonitorSuccessPageAndArchiveAsync(monitoredDriver, process);
            log.Add("Navegador mantido aberto para conferência e protocolo manual. Captura automática do PDF ativada.");
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

    private async Task MonitorSuccessPageAndArchiveAsync(IWebDriver driver, ExercisePreviousProcess process)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddHours(1);
            while (DateTime.UtcNow < deadline)
            {
                string url;
                string source;
                try
                {
                    url = driver.Url ?? string.Empty;
                    source = driver.PageSource ?? string.Empty;
                }
                catch (WebDriverException)
                {
                    return; // navegador fechado pelo operador
                }

                var success = url.Contains("/Exerc_Anterior/sucesso_ea.asp", StringComparison.OrdinalIgnoreCase)
                              || Normalize(source).Contains("processo recebido", StringComparison.Ordinal)
                              || Normalize(source).Contains("protocolo de envio", StringComparison.Ordinal);
                if (!success)
                {
                    await Task.Delay(1000);
                    continue;
                }

                await Task.Delay(1200); // aguarda estilos e imagens da página de impressão
                Directory.CreateDirectory(_paths.ExercisePreviousCpexDownloadsDirectory);
                var temporaryPdf = Path.Combine(
                    _paths.ExercisePreviousCpexDownloadsDirectory,
                    $"cpex_captura_{process.Id:0000}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf");

                SaveCurrentPageAsPdf(driver, temporaryPdf);
                var protocolService = new ExercisePreviousProtocolService(_paths, _repository);
                if (!await protocolService.IsPdfReadyAsync(temporaryPdf))
                    throw new IOException("O navegador gerou um PDF incompleto da página do CPEx.");

                var data = protocolService.ExtractProtocolDataFromPdf(temporaryPdf);
                var archived = protocolService.ArchivePdf(temporaryPdf, process, data.Protocol);
                try { File.Delete(temporaryPdf); } catch { }

                await _repository.SaveAutomaticCpexCaptureAsync(process.Id, archived, data.Protocol, data.ProtocolledAt);
                var status = string.IsNullOrWhiteSpace(data.Protocol) ? "PDF SALVO - CONFERIR PROTOCOLO" : "OK";

                void ApplyToCurrentProcess()
                {
                    process.CpexPrintPage = archived;
                    if (!string.IsNullOrWhiteSpace(data.Protocol)) process.CpexProtocol = data.Protocol;
                    if (!string.IsNullOrWhiteSpace(data.ProtocolledAt)) process.CpexProtocolledAt = data.ProtocolledAt;
                    process.CpexStatus = status;
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null && !dispatcher.CheckAccess())
                    await dispatcher.InvokeAsync(ApplyToCurrentProcess);
                else
                    ApplyToCurrentProcess();

                WriteCaptureLog($"Processo {process.Id:0000}: PDF do CPEx salvo automaticamente em {archived}. Protocolo: {data.Protocol}.");
                return;
            }

            WriteCaptureLog($"Processo {process.Id:0000}: monitor de PDF encerrado após uma hora sem localizar a página de sucesso.");
        }
        catch (Exception ex)
        {
            WriteCaptureLog($"Processo {process.Id:0000}: falha na captura automática do PDF. {ex}");
        }
    }

    private static void SaveCurrentPageAsPdf(IWebDriver driver, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        if (driver is ChromiumDriver chromium)
        {
            var result = chromium.ExecuteCdpCommand("Page.printToPDF", new Dictionary<string, object?>
            {
                ["printBackground"] = true,
                ["landscape"] = false,
                ["preferCSSPageSize"] = true,
                ["scale"] = 1.0
            });
            var json = JsonSerializer.Serialize(result);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out var dataElement)
                && !string.IsNullOrWhiteSpace(dataElement.GetString()))
            {
                File.WriteAllBytes(output, Convert.FromBase64String(dataElement.GetString()!));
                return;
            }
        }

        if (driver is ISupportsPrint printable)
        {
            printable.Print(new PrintOptions()).SaveAsFile(output);
            return;
        }

        throw new InvalidOperationException("O navegador aberto não oferece a captura PDF da página.");
    }

    private void WriteCaptureLog(string message)
    {
        try
        {
            Directory.CreateDirectory(_paths.ExercisePreviousLogsDirectory);
            var path = Path.Combine(_paths.ExercisePreviousLogsDirectory, "cpex_captura_automatica.log");
            lock (CaptureLogSync)
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void WriteStatusQueryLog(string message)
    {
        try
        {
            Directory.CreateDirectory(_paths.ExercisePreviousLogsDirectory);
            var path = Path.Combine(_paths.ExercisePreviousLogsDirectory, "cpex_consulta_situacao.log");
            lock (CaptureLogSync)
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private IWebDriver CreateDriver(CpexExerciseSettings settings, TimeSpan? commandTimeout = null, bool forceHeadless = false)
    {
        var browser = (settings.Browser ?? "edge").Trim().ToLowerInvariant();
        var driverTimeout = commandTimeout ?? TimeSpan.FromSeconds(120);
        if (browser.Contains("chrome"))
        {
            // O formulário é HTML tradicional. "Eager" libera o preenchimento assim que
            // o DOM está pronto, sem esperar imagens e recursos que não afetam os campos.
            var options = new ChromeOptions { PageLoadStrategy = PageLoadStrategy.Eager };
            options.AddArgument("--start-maximized"); options.AddArgument("--disable-notifications"); options.AddArgument("--disable-popup-blocking");
            options.AddUserProfilePreference("download.default_directory", Path.GetFullPath(_paths.ExercisePreviousCpexDownloadsDirectory));
            options.AddUserProfilePreference("download.prompt_for_download", false);
            options.AddUserProfilePreference("plugins.always_open_pdf_externally", true);
            if (settings.Headless || forceHeadless) { options.AddArgument("--headless=new"); options.AddArgument("--window-size=1600,1000"); }
            options.LeaveBrowserRunning = true;
            var service = string.IsNullOrWhiteSpace(settings.DriverDirectory)
                ? ChromeDriverService.CreateDefaultService()
                : ChromeDriverService.CreateDefaultService(settings.DriverDirectory);
            service.HideCommandPromptWindow = true;
            service.SuppressInitialDiagnosticInformation = true;
            return new ChromeDriver(service, options, driverTimeout);
        }
        else
        {
            var options = new EdgeOptions { PageLoadStrategy = PageLoadStrategy.Eager };
            options.AddArgument("--start-maximized"); options.AddArgument("--disable-notifications"); options.AddArgument("--disable-popup-blocking");
            options.AddUserProfilePreference("download.default_directory", Path.GetFullPath(_paths.ExercisePreviousCpexDownloadsDirectory));
            options.AddUserProfilePreference("download.prompt_for_download", false);
            options.AddUserProfilePreference("plugins.always_open_pdf_externally", true);
            if (settings.Headless || forceHeadless) { options.AddArgument("--headless=new"); options.AddArgument("--window-size=1600,1000"); }
            options.LeaveBrowserRunning = true;
            var service = string.IsNullOrWhiteSpace(settings.DriverDirectory)
                ? EdgeDriverService.CreateDefaultService()
                : EdgeDriverService.CreateDefaultService(settings.DriverDirectory);
            service.HideCommandPromptWindow = true;
            service.SuppressInitialDiagnosticInformation = true;
            return new EdgeDriver(service, options, driverTimeout);
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
        // O formulário atual do CPEx possui um contrato POST estável, apesar do HTML
        // visual ser montado com várias tabelas aninhadas. Preencher pelos nomes reais
        // evita acertar a caixa exibida e, ao mesmo tempo, deixar vazio o campo enviado
        // ao inseri_anterior.asp (causa do relatório final sem dados).
        if (TryFillCurrentCpexFormByContract(driver, p, log))
        {
            AcceptAlertIfPresent(driver, log, "Conclusão do formulário");
            return;
        }

        var f = p.Fields;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Row(string[] labels, string? value, string message)
        {
            var ok = FillLabelRowControls(driver, labels, [value], used);
            log.Add(ok ? message : $"Aviso: {message.Replace(": OK", string.Empty)} não localizado; confira manualmente.");
            return ok;
        }

        bool RowStrict(string[] labels, IReadOnlyList<string?> values, string message)
        {
            var ok = FillSameRowOnly(driver, labels, values, used);
            if (ok) log.Add(message);
            else log.Add($"Aviso: {message.Replace(": OK", string.Empty)} não localizado na mesma linha; confira manualmente.");
            return ok;
        }

        bool SelectRow(string[] labels, string? value, string message, bool allowOptionFallback = true)
        {
            var ok = FillSameRowOnly(driver, labels, [value], used);
            if (!ok && allowOptionFallback) ok = FillSelectByOptionAnywhere(driver, value, used);
            log.Add(ok ? message : $"Aviso: {message.Replace(": OK", string.Empty)} não localizado; confira manualmente.");
            return ok;
        }

        SelectRow(["indicativo"], f.GetValueOrDefault("indicativo"), "Indicativo: OK");
        SelectRow(["posto graduacao", "posto/graduação", "posto grad", "graduação", "graduacao"], f.GetValueOrDefault("posto_grad"), "Posto/graduação: OK");

        Row(["nome do representante legal", "representante legal ou preposto"], f.GetValueOrDefault("representante_nome"), "Representante legal — nome: OK");
        Row(["cpf do representante legal", "cpf do representante"], f.GetValueOrDefault("representante_cpf"), "Representante legal — CPF: OK");
        Row(["identidade do representante legal", "identidade do representante"], f.GetValueOrDefault("representante_idt"), "Representante legal — identidade: OK");

        Row(["data inicio do periodo da divida", "data início do período da dívida", "data inicio"], f.GetValueOrDefault("periodo_inicio"), "Período — início: OK");
        Row(["data final do periodo da divida", "data final do período da dívida", "data final"], f.GetValueOrDefault("periodo_fim"), "Período — final: OK");
        Row(["quantidade de meses", "meses que se refere"], f.GetValueOrDefault("qtd_meses"), "Período — quantidade de meses: OK");
        AcceptAlertIfPresent(driver, log, "Identificação e período");

        var codeCount = FillCodeRowsSequential(driver, p.Codes, used);
        log.Add($"Tabela de códigos: {codeCount} linha(s) preenchida(s).");
        SelectRow(["tipo de exercicio anterior", "tipo de exercício anterior"], f.GetValueOrDefault("tipo_exercicio_anterior"), "Tipo de Exercício Anterior: OK");

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
            var ok = FillSameRowOnly(driver, row.Labels, [HasMoney(original) ? original : string.Empty, HasMoney(corrected) ? corrected : string.Empty], used);
            log.Add($"Valores — {row.Key}: {(ok ? "OK" : "confira manualmente")}.");
        }
        AcceptAlertIfPresent(driver, log, "Valores");

        var formLogs = FillFormularioSectionByOrder(driver, p, used);
        if (formLogs.Count > 0) log.AddRange(formLogs);
        else
        {
            Row(["data do requerimento", "data requerimento"], f.GetValueOrDefault("data_requerimento"), "Data do requerimento: OK");
            RowStrict(["averbacao bi", "averbação bi", "bi/adt", "adt"], [f.GetValueOrDefault("averbacao_bi_adt"), f.GetValueOrDefault("averbacao_data")], "Averbação BI/ADT: OK");
            Row(["documento que materializou o direito", "materializou o direito", "materializou"], f.GetValueOrDefault("documento_materializou"), "Documento que materializou o direito: OK");
            Row(["objeto da divida", "objeto da dívida", "justificativa", "motivo da divida", "motivo da dívida"], f.GetValueOrDefault("objeto_justificativa"), "Objeto/justificativa: OK");
        }

        if (FillRadioNearText(driver, f.GetValueOrDefault("possui_pensao_judiciaria", "Não"), ["possui pensao judiciaria", "pensão judiciária", "zed"]))
            log.Add("Pensão Judiciária ZED: NÃO");
        else
            Row(["possui pensao judiciaria", "pensão judiciária", "zed"], f.GetValueOrDefault("possui_pensao_judiciaria", "Não"), "Pensão Judiciária ZED: NÃO");

        Row(["ficha cadastro"], f.GetValueOrDefault("pesquisa_ficha_cadastro", "Sim"), "Ficha cadastro: SIM");
        Row(["ficha financeira"], f.GetValueOrDefault("pesquisa_ficha_financeira", "Sim"), "Ficha financeira: SIM");
        Row(["levantamento siafi", "siafi"], f.GetValueOrDefault("pesquisa_levantamento_siafi", "Sim"), "Levantamento SIAFI: SIM");

        var remittance = f.GetValueOrDefault("documento_remessa");
        if (!string.IsNullOrWhiteSpace(remittance)) Row(["documento de remessa", "remessa do processo"], remittance, "Documento de remessa: OK");

        SelectRow(["banco", "instituicao bancaria", "instituição bancária", "domicilio bancario", "domicílio bancário", "banco para credito", "banco para crédito"], f.GetValueOrDefault("banco"), "Banco: OK");
        Row(["agencia sem digito", "agência sem dígito", "agencia"], f.GetValueOrDefault("agencia"), "Agência: OK");
        Row(["conta corrente", "conta corrente com dv", "conta"], f.GetValueOrDefault("conta"), "Conta: OK");

        if (FillControlsAfterHeading(driver, ["informacoes do operador", "informações do operador"],
                [f.GetValueOrDefault("operador_nome"), f.GetValueOrDefault("operador_cpf"), f.GetValueOrDefault("operador_email_om"), f.GetValueOrDefault("operador_celular")], used))
            log.Add("Informações do operador: OK");

        // O formulário legado do CPEx repete nomes e estruturas de tabela. Uma última
        // passagem direta pelo DOM garante os campos críticos sem depender da geometria
        // calculada pelo WebDriver (que varia com zoom, DevTools e resolução da tela).
        var criticalResult = FillCriticalFormFieldsByDom(driver, p);
        log.Add("Campos críticos do formulário: " + criticalResult + ".");
        AcceptAlertIfPresent(driver, log, "Conclusão do formulário");
    }

    private static bool TryFillCurrentCpexFormByContract(IWebDriver driver, CpexExercisePayload payload, List<string> log)
    {
        var js = (IJavaScriptExecutor)driver;
        bool hasContract;
        try
        {
            hasContract = Convert.ToBoolean(js.ExecuteScript("""
                const f = document.forms.namedItem('form') || document.querySelector('form[name="form"]');
                return !!(f && f.elements.namedItem('nome_beneficiado') && f.elements.namedItem('vlr_bruto')
                    && f.elements.namedItem('NR_AVERB') && f.elements.namedItem('bt_gravar'));
                """), CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }

        if (!hasContract) return false;

        var fields = payload.Fields;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["codom"] = fields.GetValueOrDefault("codom"),
            ["sigla_om"] = fields.GetValueOrDefault("sigla_om"),
            ["nome_beneficiado"] = fields.GetValueOrDefault("nome"),
            ["cpf"] = fields.GetValueOrDefault("cpf"),
            ["prec_cp"] = fields.GetValueOrDefault("prec_cp"),
            ["nome_requerente"] = fields.GetValueOrDefault("representante_nome"),
            ["cpf_requerente"] = fields.GetValueOrDefault("representante_cpf"),
            ["idt_requerente"] = fields.GetValueOrDefault("representante_idt"),
            ["data_inicio"] = fields.GetValueOrDefault("periodo_inicio"),
            ["data_fim"] = fields.GetValueOrDefault("periodo_fim"),
            ["qt_parcelas"] = fields.GetValueOrDefault("qtd_meses"),
            ["vlr_bruto"] = payload.Values.GetValueOrDefault("valor_bruto_devido"),
            ["vlr_bruto_cor"] = payload.Values.GetValueOrDefault("valor_bruto_devido_corrigido"),
            ["desc_pm_zeh"] = payload.Values.GetValueOrDefault("desconto_pensao_3_zeh"),
            ["desc_pm_zeh_cor"] = payload.Values.GetValueOrDefault("desconto_pensao_3_zeh_corrigido"),
            ["desc_pm_z12"] = payload.Values.GetValueOrDefault("desconto_pensao_zeb"),
            ["desc_pm_z12_cor"] = payload.Values.GetValueOrDefault("desconto_pensao_zeb_corrigido"),
            ["desc_pm_z05"] = payload.Values.GetValueOrDefault("desconto_pensao_15_zec"),
            ["desc_pm_z05_cor"] = payload.Values.GetValueOrDefault("desconto_pensao_15_zec_corrigido"),
            ["desc_fusex"] = payload.Values.GetValueOrDefault("desconto_fusex_zea"),
            ["desc_fusex_cor"] = payload.Values.GetValueOrDefault("desconto_fusex_zea_corrigido"),
            ["desc_gea"] = payload.Values.GetValueOrDefault("da_ex_ant_ded_gea"),
            ["desc_gea_cor"] = payload.Values.GetValueOrDefault("da_ex_ant_ded_gea_corrigido"),
            ["desc_geb"] = payload.Values.GetValueOrDefault("da_ea_aj_con_n_d_geb"),
            ["desc_geb_cor"] = payload.Values.GetValueOrDefault("da_ea_aj_con_n_d_geb_corrigido"),
            ["desc_pnr_z13"] = payload.Values.GetValueOrDefault("desconto_pnr_z13"),
            ["desc_pnr_z13_cor"] = payload.Values.GetValueOrDefault("desconto_pnr_z13_corrigido"),
            ["desc_pnr_z14"] = payload.Values.GetValueOrDefault("desconto_pnr_z14"),
            ["desc_pnr_z14_cor"] = payload.Values.GetValueOrDefault("desconto_pnr_z14_corrigido"),
            ["DESC_FUSEX_DEP"] = payload.Values.GetValueOrDefault("fusex_dep_zef"),
            ["DESC_FUSEX_DEP_COR"] = payload.Values.GetValueOrDefault("fusex_dep_zef_corrigido"),
            ["vlr_liq"] = payload.Values.GetValueOrDefault("valor_liquido_devido"),
            ["vlr_liq_cor"] = payload.Values.GetValueOrDefault("valor_liquido_devido_corrigido"),
            ["data_req"] = fields.GetValueOrDefault("data_requerimento"),
            ["NR_AVERB"] = fields.GetValueOrDefault("averbacao_bi_adt"),
            ["data_averbacao"] = fields.GetValueOrDefault("averbacao_data"),
            ["doc_direito"] = fields.GetValueOrDefault("documento_materializou"),
            ["justificativa"] = fields.GetValueOrDefault("objeto_justificativa"),
            ["doc_remessa"] = fields.GetValueOrDefault("documento_remessa"),
            ["cod_agencia"] = fields.GetValueOrDefault("agencia"),
            ["conta_corrente"] = fields.GetValueOrDefault("conta"),
            ["nome_operador"] = fields.GetValueOrDefault("operador_nome"),
            ["cpf_operador"] = fields.GetValueOrDefault("operador_cpf"),
            ["E_MAIL_OP"] = fields.GetValueOrDefault("operador_email_om"),
            ["TEL_CONTATO"] = fields.GetValueOrDefault("operador_celular")
        };

        for (var index = 0; index < Math.Min(10, payload.Codes.Count); index++)
        {
            var row = payload.Codes[index];
            var number = index + 1;
            values[$"cod{number}"] = row.Code;
            values[$"desc_cod{number}"] = row.Description;
            values[$"vlr_cod{number}"] = row.Original;
        }

        var selects = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["indicativo_mil"] = fields.GetValueOrDefault("indicativo"),
            ["posto_grad"] = fields.GetValueOrDefault("posto_grad"),
            ["tipo_ea"] = fields.GetValueOrDefault("tipo_exercicio_anterior"),
            ["ficha_cadastro"] = YesNoSelectValue(fields.GetValueOrDefault("pesquisa_ficha_cadastro")),
            ["ficha_financeira"] = YesNoSelectValue(fields.GetValueOrDefault("pesquisa_ficha_financeira")),
            ["siafi"] = YesNoSelectValue(fields.GetValueOrDefault("pesquisa_levantamento_siafi")),
            ["cod_banco"] = BankCode(fields.GetValueOrDefault("banco"))
        };

        var data = JsonSerializer.Serialize(new
        {
            values,
            selects,
            pension = Normalize(fields.GetValueOrDefault("possui_pensao_judiciaria")) is "sim" or "s" ? "S" : "N",
            required = new[]
            {
                "codom", "sigla_om", "indicativo_mil", "nome_beneficiado", "cpf", "prec_cp", "posto_grad",
                "nome_requerente", "cpf_requerente", "idt_requerente", "data_inicio", "data_fim", "qt_parcelas",
                "cod1", "desc_cod1", "vlr_cod1", "tipo_ea", "vlr_bruto", "vlr_bruto_cor", "vlr_liq", "vlr_liq_cor",
                "data_req", "NR_AVERB", "data_averbacao", "doc_direito", "justificativa", "ficha_cadastro",
                "ficha_financeira", "siafi", "cod_banco", "cod_agencia", "conta_corrente", "nome_operador",
                "cpf_operador", "E_MAIL_OP", "TEL_CONTATO"
            }
        });

        var auditJson = Convert.ToString(js.ExecuteScript("""
            const data = JSON.parse(arguments[0]);
            const form = document.forms.namedItem('form') || document.querySelector('form[name="form"]');
            const audit = { set: 0, missing: [], empty: [], selections: [] };

            function control(name) {
              const item = form.elements.namedItem(name);
              if (!item) return null;
              if (typeof item.length === 'number' && !item.tagName) return item[0] || null;
              return item;
            }
            function setValue(name, value) {
              const el = control(name);
              if (!el) { audit.missing.push(name); return; }
              const text = String(value ?? '');
              const proto = String(el.tagName).toLowerCase() === 'textarea'
                ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
              if (setter) setter.call(el, text); else el.value = text;
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              audit.set++;
            }
            function norm(value) {
              return String(value || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '')
                .toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
            }
            function setSelect(name, wanted) {
              const el = control(name);
              if (!el) { audit.missing.push(name); return; }
              const target = norm(wanted);
              let best = -1;
              for (let i = 0; i < el.options.length; i++) {
                const optionText = norm(el.options[i].text);
                const optionValue = norm(el.options[i].value);
                if ((target && optionValue === target) || (target && optionText === target)
                    || (target.length > 1 && optionText.includes(target))
                    || (optionText.length > 1 && target.includes(optionText))) {
                  best = i; break;
                }
              }
              if (best < 0) { audit.selections.push(name + '=' + wanted); return; }
              el.selectedIndex = best;
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              audit.set++;
            }

            for (const [name, value] of Object.entries(data.values)) setValue(name, value);
            for (const [name, value] of Object.entries(data.selects)) setSelect(name, value);

            const radioGroup = form.elements.namedItem('DESP_PJ');
            const radios = radioGroup && typeof radioGroup.length === 'number' ? Array.from(radioGroup) : (radioGroup ? [radioGroup] : []);
            const pension = radios.find(r => String(r.value).toUpperCase() === data.pension);
            if (pension) {
              pension.checked = true;
              pension.dispatchEvent(new Event('input', { bubbles: true }));
              pension.dispatchEvent(new Event('change', { bubbles: true }));
              if (typeof ShowHideDiv === 'function') ShowHideDiv(data.pension);
              audit.set++;
            } else audit.missing.push('DESP_PJ');

            const visualCodom = document.getElementById('v_codom');
            const visualSigla = document.getElementById('v_sigla_om');
            if (visualCodom) visualCodom.value = String(data.values.codom || '');
            if (visualSigla) visualSigla.value = String(data.values.sigla_om || '');

            for (const name of data.required) {
              const el = control(name);
              if (!el || String(el.value || '').trim() === '' || String(el.value) === '00' || String(el.value) === '0')
                audit.empty.push(name);
            }
            return JSON.stringify(audit);
            """, data), CultureInfo.InvariantCulture) ?? string.Empty;

        using var auditDocument = JsonDocument.Parse(auditJson);
        var root = auditDocument.RootElement;
        var missing = root.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var empty = root.GetProperty("empty").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var selections = root.GetProperty("selections").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (missing.Count > 0 || empty.Count > 0 || selections.Count > 0)
        {
            var details = string.Join("; ", new[]
            {
                missing.Count == 0 ? string.Empty : "não encontrados: " + string.Join(", ", missing),
                empty.Count == 0 ? string.Empty : "vazios: " + string.Join(", ", empty),
                selections.Count == 0 ? string.Empty : "opções incompatíveis: " + string.Join(", ", selections)
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            throw new InvalidOperationException("O SIGFUR bloqueou a liberação do formulário porque o POST do CPEx não está completo (" + details + "). Corrija esses dados no processo e tente novamente; não gere o relatório enquanto este aviso existir.");
        }

        log.Add($"Contrato POST do CPEx auditado: {root.GetProperty("set").GetInt32()} campo(s) escritos e todos os obrigatórios conferidos.");
        return true;
    }

    private static string YesNoSelectValue(string? value) => Normalize(value) is "nao" or "n" ? "2" : "1";

    private static string BankCode(string? value)
    {
        var digits = ExercisePreviousRepository.Digits(value ?? string.Empty);
        if (digits.Length >= 3) return digits[..3];
        var normalized = Normalize(value);
        if (normalized.Contains("brasil")) return "001";
        if (normalized.Contains("santander")) return "033";
        if (normalized.Contains("banrisul")) return "041";
        if (normalized.Contains("brb")) return "070";
        if (normalized.Contains("caixa") || normalized.Contains("cef")) return "104";
        if (normalized.Contains("bradesco")) return "237";
        if (normalized.Contains("itau")) return "341";
        if (normalized.Contains("hsbc")) return "399";
        if (normalized.Contains("citibank")) return "745";
        if (normalized.Contains("sicoob")) return "756";
        return string.Empty;
    }

    private static string FillCriticalFormFieldsByDom(IWebDriver driver, CpexExercisePayload payload)
    {
        try
        {
            var fields = payload.Fields;
            var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const requestDate = arguments[0] || '';
                const bulletinNumber = arguments[1] || '';
                const bulletinDate = arguments[2] || '';
                const operatorValues = [arguments[3] || '', arguments[4] || '', arguments[5] || '', arguments[6] || ''];

                function norm(s){
                  return String(s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '')
                    .toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
                }
                function visible(el){
                  if(!el || el.disabled) return false;
                  const st = getComputedStyle(el);
                  const r = el.getBoundingClientRect();
                  return st.display !== 'none' && st.visibility !== 'hidden' && r.width > 0 && r.height > 0;
                }
                function textInput(el){
                  if(!visible(el) || String(el.tagName).toLowerCase() !== 'input') return false;
                  return !['hidden','button','submit','radio','checkbox','image','reset','password'].includes(String(el.type || 'text').toLowerCase());
                }
                function setValue(el, value){
                  if(!el || !value) return false;
                  try {
                    el.focus();
                    const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                    const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
                    if(descriptor && descriptor.set) descriptor.set.call(el, String(value)); else el.value = String(value);
                    for(const type of ['input','keyup','change']) el.dispatchEvent(new Event(type, {bubbles:true}));
                    el.blur();
                    return String(el.value || '').trim() === String(value).trim();
                  } catch { return false; }
                }
                function rowInputs(row){
                  // A ordem do HTML é estável: requerimento, número do BI e data do BI.
                  // Ordenar por coordenada pode inverter os dois últimos com zoom/reflow.
                  return Array.from(row.querySelectorAll('input')).filter(textInput);
                }

                let header = [];
                const rows = Array.from(document.querySelectorAll('tr')).filter(visible);
                const headerRow = rows.find(row => {
                  const text = norm(row.innerText || row.textContent || '');
                  return text.includes('data do requerimento') && text.includes('averbacao') && text.includes('bi');
                });
                if(headerRow) header = rowInputs(headerRow);

                if(header.length < 3){
                  const labels = Array.from(document.querySelectorAll('td,th,label,span,font,b,strong')).filter(visible);
                  const requestLabel = labels.find(el => norm(el.innerText || el.textContent || '').includes('data do requerimento'));
                  const bulletinLabel = labels.find(el => {
                    const text = norm(el.innerText || el.textContent || '');
                    return text.includes('averbacao') && text.includes('bi');
                  });
                  const anchors = [requestLabel, bulletinLabel].filter(Boolean).map(el => el.getBoundingClientRect());
                  if(anchors.length){
                    const top = Math.min(...anchors.map(r => r.top)) - 8;
                    const bottom = Math.max(...anchors.map(r => r.bottom)) + 18;
                    header = Array.from(document.querySelectorAll('input')).filter(textInput)
                      .filter(el => { const r=el.getBoundingClientRect(); const cy=(r.top+r.bottom)/2; return cy >= top && cy <= bottom; })
                      .sort((a,b) => a.getBoundingClientRect().left - b.getBoundingClientRect().left);
                  }
                }

                const headerValues = [requestDate, bulletinNumber, bulletinDate];
                let headerFilled = 0;
                for(let i=0; i<Math.min(3, header.length); i++) if(setValue(header[i], headerValues[i])) headerFilled++;

                const allInputs = Array.from(document.querySelectorAll('input')).filter(textInput);
                function byKey(tokens){
                  const normalized = tokens.map(norm);
                  return allInputs.find(el => {
                    const key = norm(`${el.id || ''} ${el.name || ''}`);
                    return normalized.some(token => key === token || key.includes(token));
                  });
                }
                const operatorControls = [
                  byKey(['nome_operador','operador_nome']),
                  byKey(['cpf_operador','operador_cpf']),
                  byKey(['email_operador','email_om','operador_email']),
                  byKey(['cel_operador','celular_operador','telefone_operador','operador_celular'])
                ];

                const operatorHeading = Array.from(document.querySelectorAll('tr,td,div,b,strong')).filter(visible)
                  .find(el => norm(el.innerText || el.textContent || '') === 'informacoes do operador');
                if(operatorHeading){
                  const y0 = operatorHeading.getBoundingClientRect().bottom;
                  const positional = allInputs.filter(el => {
                    const r=el.getBoundingClientRect(); return r.top >= y0 - 2 && r.top <= y0 + 180;
                  }).sort((a,b) => (a.getBoundingClientRect().top-b.getBoundingClientRect().top) || (a.getBoundingClientRect().left-b.getBoundingClientRect().left));
                  for(let i=0; i<4; i++) if(!operatorControls[i]) operatorControls[i] = positional[i];
                }

                let operatorFilled = 0;
                for(let i=0; i<4; i++) if(setValue(operatorControls[i], operatorValues[i])) operatorFilled++;
                return `cabeçalho ${headerFilled}/3; operador ${operatorFilled}/4`;
                """,
                fields.GetValueOrDefault("data_requerimento"),
                fields.GetValueOrDefault("averbacao_bi_adt"),
                fields.GetValueOrDefault("averbacao_data"),
                fields.GetValueOrDefault("operador_nome"),
                fields.GetValueOrDefault("operador_cpf"),
                fields.GetValueOrDefault("operador_email_om"),
                fields.GetValueOrDefault("operador_celular"));
            return Convert.ToString(result, CultureInfo.InvariantCulture) ?? "sem retorno";
        }
        catch (Exception ex)
        {
            return "falha: " + ex.Message;
        }
    }

    private readonly record struct ControlGeometry(double Y, double X, double W, double H, IWebElement Element);

    private static int FillCodeRowsSequential(IWebDriver driver, IReadOnlyList<CpexExerciseCodeRow> rows, HashSet<string> used)
    {
        if (rows.Count == 0) return 0;
        var controls = VisibleTextControlsByGeometry(driver)
            .Where(x => !used.Contains(ElementKey(x.Element)))
            .ToList();
        var groups = GroupByVisualLine(controls, 7).Where(x => x.Count >= 3).ToList();
        var tableGroups = new List<List<ControlGeometry>>();
        foreach (var group in groups)
        {
            var first = ValueOf(group[0].Element).Trim().ToUpperInvariant();
            var widths = group.Take(3).Select(x => x.W).ToArray();
            var looksLikeCodeRow =
                (string.IsNullOrWhiteSpace(first) || System.Text.RegularExpressions.Regex.IsMatch(first, "^[A-Z]\\d{2}$") || System.Text.RegularExpressions.Regex.IsMatch(first, "^Z[A-Z0-9]{2}$"))
                && widths[0] <= Math.Max(90, widths[1] * 0.45)
                && widths[1] >= widths[0]
                && group[0].X < group[1].X && group[1].X < group[2].X;
            if (looksLikeCodeRow) tableGroups.Add(group.Take(3).ToList());
        }
        if (tableGroups.Count == 0) tableGroups = groups.Take(rows.Count).Select(x => x.Take(3).ToList()).ToList();

        var filled = 0;
        foreach (var (row, controls3) in rows.Zip(tableGroups))
        {
            if (!HasMoney(row.Original)) continue;
            var values = new[] { row.Code, row.Description, row.Original };
            for (var i = 0; i < Math.Min(values.Length, controls3.Count); i++)
            {
                Fill(controls3[i].Element, values[i]);
                used.Add(ElementKey(controls3[i].Element));
            }
            filled++;
        }
        return filled;
    }

    private static bool FillSameRowOnly(IWebDriver driver, IEnumerable<string> labels, IReadOnlyList<string?> values, HashSet<string> used)
    {
        var controls = ControlsSameRowByLabel(driver, labels, Math.Max(6, values.Count + 2))
            .Where(x => !used.Contains(ElementKey(x)))
            .ToList();
        if (controls.Count == 0) return false;
        return FillControlsInOrder(controls, values, used);
    }

    private static bool FillLabelRowControls(IWebDriver driver, IEnumerable<string> labels, IReadOnlyList<string?> values, HashSet<string> used)
    {
        if (FillSameRowOnly(driver, labels, values, used)) return true;

        var controls = ControlsNearLabel(driver, labels, Math.Max(4, values.Count + 1))
            .Where(x => !used.Contains(ElementKey(x)))
            .ToList();
        if (controls.Count > 0 && FillControlsInOrder(controls, values, used)) return true;

        var terms = labels.Select(Normalize).ToArray();
        foreach (var selector in new[] { "tr", "td", "div" })
        {
            foreach (var container in driver.FindElements(By.CssSelector(selector)))
            {
                if (!IsVisible(container)) continue;
                var text = Normalize(container.Text);
                if (!ContainsAny(text, terms)) continue;
                if (selector == "div" && text.Length > 350) continue;
                var fallback = container.FindElements(By.CssSelector("input:not([type=hidden]),textarea,select"))
                    .Where(IsVisible)
                    .Where(IsFillableControl)
                    .Where(x => !used.Contains(ElementKey(x)))
                    .ToList();
                if (fallback.Count == 0 || fallback.Count > 8) continue;
                if (FillControlsInOrder(fallback, values, used)) return true;
            }
        }
        return false;
    }

    private static List<string> FillFormularioSectionByOrder(IWebDriver driver, CpexExercisePayload payload, HashSet<string> used)
    {
        var logs = new List<string>();
        var y0 = HeadingY(driver, ["informacoes do formulario"]) ?? HeadingY(driver, ["informações do formulário"]);
        if (y0 is null) return logs;
        var next = new[]
        {
            HeadingY(driver, ["declaracao de realizacao de pesquisa"]),
            HeadingY(driver, ["declaração de realização de pesquisa"]),
            HeadingY(driver, ["domicilio bancario"]),
            HeadingY(driver, ["domicílio bancário"])
        }.Where(x => x.HasValue && x.Value > y0.Value).Select(x => x!.Value).DefaultIfEmpty(double.MaxValue).Min();

        // Não filtra por "used" aqui. A linha possui três campos muito próximos e um
        // localizador genérico anterior pode tê-los marcado por engano. Esta seção é
        // semanticamente fechada e deve sempre sobrescrever os três na ordem oficial.
        var section = VisibleTextControlsByGeometry(driver)
            .Where(x => x.Y > y0.Value + 4 && x.Y < next - 4)
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();
        if (section.Count == 0) return logs;

        var inputs = section.Where(x => x.Element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase)).ToList();
        if (inputs.Count > 0)
        {
            var firstLine = inputs.Where(x => Math.Abs(x.Y - inputs[0].Y) <= 12).OrderBy(x => x.X).Take(3).ToList();
            var vals = new (string Message, string Value)[]
            {
                ("Data do requerimento: OK", payload.Fields.GetValueOrDefault("data_requerimento") ?? string.Empty),
                ("Averbação BI/ADT — número: OK", payload.Fields.GetValueOrDefault("averbacao_bi_adt") ?? string.Empty),
                ("Averbação BI/ADT — data: OK", payload.Fields.GetValueOrDefault("averbacao_data") ?? string.Empty)
            };
            foreach (var (item, data) in firstLine.Zip(vals))
            {
                if (string.IsNullOrWhiteSpace(data.Value)) continue;
                if (Fill(item.Element, data.Value))
                {
                    used.Add(ElementKey(item.Element));
                    logs.Add(data.Message);
                }
            }

            if (firstLine.Count < vals.Length)
            {
                FillLabelRowControls(driver, ["data do requerimento", "data requerimento"],
                    [payload.Fields.GetValueOrDefault("data_requerimento")], used);
                FillSameRowOnly(driver, ["averbacao bi", "averbação bi", "bi/adt", "adt"],
                    [payload.Fields.GetValueOrDefault("averbacao_bi_adt"), payload.Fields.GetValueOrDefault("averbacao_data")], used);
            }
        }

        var textareas = section.Where(x => x.Element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Y).ThenBy(x => x.X).Take(2).ToList();
        var textareaValues = new (string Message, string Value)[]
        {
            ("Documento que materializou o direito: OK", payload.Fields.GetValueOrDefault("documento_materializou") ?? string.Empty),
            ("Objeto/justificativa: OK", payload.Fields.GetValueOrDefault("objeto_justificativa") ?? string.Empty)
        };
        foreach (var (item, data) in textareas.Zip(textareaValues))
        {
            if (string.IsNullOrWhiteSpace(data.Value)) continue;
            if (Fill(item.Element, data.Value))
            {
                used.Add(ElementKey(item.Element));
                logs.Add(data.Message);
            }
        }

        return logs;
    }

    private static bool FillControlsAfterHeading(IWebDriver driver, IEnumerable<string> headings, IReadOnlyList<string?> values, HashSet<string> used)
    {
        var y0 = HeadingY(driver, headings);
        if (y0 is null) return false;
        var controls = VisibleTextControlsByGeometry(driver)
            // Seção final e exclusiva do operador. Não reaproveita o filtro global,
            // pois um campo bancário com nome/id repetido não pode bloquear esta área.
            .Where(x => x.Y > y0.Value + 2 && x.Y < y0.Value + 180)
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .Take(values.Count)
            .Select(x => x.Element)
            .ToList();
        var required = values.Count(x => !string.IsNullOrWhiteSpace(x));
        if (required == 0 || controls.Count < values.Count) return false;
        var filled = 0;
        foreach (var (element, value) in controls.Zip(values))
        {
            if (string.IsNullOrWhiteSpace(value) || !Fill(element, value)) continue;
            used.Add(ElementKey(element));
            filled++;
        }
        return filled == required;
    }

    private static bool FillSelectByOptionAnywhere(IWebDriver driver, string? value, HashSet<string> used)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidates = driver.FindElements(By.CssSelector("select"))
            .Where(IsVisible)
            .Where(x => !used.Contains(ElementKey(x)))
            .Select(x =>
            {
                var position = GetElementPosition(x);
                return (Score: SelectMatchScore(x, value), Element: x, position.Y, position.X);
            })
            .Where(x => x.Score >= 700)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();
        foreach (var candidate in candidates)
        {
            if (!Fill(candidate.Element, value)) continue;
            used.Add(ElementKey(candidate.Element));
            return true;
        }
        return false;
    }

    private static bool FillRadioNearText(IWebDriver driver, string? wanted, IEnumerable<string> labels)
    {
        var wantedNorm = Normalize(wanted);
        if (string.IsNullOrWhiteSpace(wantedNorm)) return false;
        var terms = labels.Select(Normalize).ToArray();
        var candidates = new List<(int Score, IWebElement Element)>();
        foreach (var element in driver.FindElements(By.CssSelector("input[type=radio],input[type=checkbox]")))
        {
            if (!IsVisible(element)) continue;
            var contextScore = terms.Sum(t => Normalize(Context(driver, element)).Contains(t) ? 20 : 0);
            if (contextScore <= 0) continue;
            var option = Normalize(OptionText(driver, element));
            var value = Normalize(element.GetAttribute("value"));
            var optionScore = 0;
            if (option == wantedNorm || value == wantedNorm) optionScore = 100;
            else if (option.Contains(wantedNorm) || value.Contains(wantedNorm)) optionScore = 80;
            else if (wantedNorm.StartsWith("nao", StringComparison.Ordinal) && (option.Contains("nao") || value is "n" or "nao" or "no")) optionScore = 90;
            else if (wantedNorm.StartsWith("sim", StringComparison.Ordinal) && (option.Contains("sim") || value is "s" or "sim" or "yes")) optionScore = 90;
            if (optionScore > 0) candidates.Add((contextScore + optionScore, element));
        }
        var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault();
        if (best.Element is null) return false;
        try { if (!best.Element.Selected) best.Element.Click(); return true; }
        catch { return false; }
    }

    private static bool FillControlsInOrder(IReadOnlyList<IWebElement> controls, IReadOnlyList<string?> values, HashSet<string> used)
    {
        var ok = false;
        foreach (var (element, value) in controls.Zip(values))
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!Fill(element, value)) continue;
            used.Add(ElementKey(element));
            ok = true;
        }
        return ok;
    }

    private static List<List<ControlGeometry>> GroupByVisualLine(IEnumerable<ControlGeometry> controls, double tolerance)
    {
        var groups = new List<List<ControlGeometry>>();
        var current = new List<ControlGeometry>();
        double? lastY = null;
        foreach (var item in controls.OrderBy(x => x.Y).ThenBy(x => x.X))
        {
            if (lastY is null || Math.Abs(item.Y - lastY.Value) <= tolerance)
            {
                current.Add(item);
                lastY = lastY is null ? item.Y : (lastY.Value + item.Y) / 2;
                continue;
            }
            if (current.Count > 0) groups.Add(current.OrderBy(x => x.X).ToList());
            current = [item];
            lastY = item.Y;
        }
        if (current.Count > 0) groups.Add(current.OrderBy(x => x.X).ToList());
        return groups;
    }

    private static List<ControlGeometry> VisibleTextControlsByGeometry(IWebDriver driver)
    {
        var result = new List<ControlGeometry>();
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                function visible(el){
                  if(!el || el.disabled) return false;
                  const st=getComputedStyle(el);
                  if(st.display==='none'||st.visibility==='hidden'||st.opacity==='0') return false;
                  const r=el.getBoundingClientRect(); return r.width>0&&r.height>0;
                }
                function usable(el){
                  const tag=(el.tagName||'').toLowerCase();
                  const typ=String(el.type||'text').toLowerCase();
                  if(tag==='select'||tag==='textarea') return true;
                  return tag==='input'&&!['hidden','button','submit','radio','checkbox','image','reset','password'].includes(typ);
                }
                return Array.from(document.querySelectorAll('input:not([type=hidden]),textarea,select'))
                  .filter(el=>visible(el)&&usable(el))
                  .map(el=>{const r=el.getBoundingClientRect();return {el,y:r.top+scrollY,x:r.left+scrollX,w:r.width,h:r.height};})
                  .sort((a,b)=>(a.y-b.y)||(a.x-b.x));
                """);
            if (raw is not System.Collections.IEnumerable rows) return result;
            foreach (var item in rows)
            {
                if (item is not System.Collections.IDictionary map || map["el"] is not IWebElement element) continue;
                result.Add(new ControlGeometry(
                    Convert.ToDouble(map["y"], CultureInfo.InvariantCulture),
                    Convert.ToDouble(map["x"], CultureInfo.InvariantCulture),
                    Convert.ToDouble(map["w"], CultureInfo.InvariantCulture),
                    Convert.ToDouble(map["h"], CultureInfo.InvariantCulture),
                    element));
            }
        }
        catch { }
        return result;
    }

    private static List<IWebElement> ControlsSameRowByLabel(IWebDriver driver, IEnumerable<string> labels, int maxControls)
        => ScriptElements(driver, """
            const terms = arguments[0] || [];
            const maxControls = Number(arguments[1] || 6);
            function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();}
            function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
            function visible(el){if(!el)return false;const st=getComputedStyle(el);if(st.display==='none'||st.visibility==='hidden'||st.opacity==='0')return false;const r=el.getBoundingClientRect();return r.width>0&&r.height>0;}
            function usable(c){if(!visible(c)||c.disabled)return false;const tag=(c.tagName||'').toLowerCase();const typ=String(c.type||'text').toLowerCase();if(tag==='select'||tag==='textarea')return true;if(tag!=='input')return false;return !['hidden','button','submit','radio','checkbox','image','reset','password'].includes(typ);}
            function termScore(txt){const n=norm(txt);if(!n)return 0;let score=0;for(const t of terms){if(!t)continue;if(n===t)score=Math.max(score,2000+t.length);else if(n.includes(t))score=Math.max(score,1500+t.length);else if(t.includes(n)&&n.length>4)score=Math.max(score,900+n.length);}return score;}
            function rowControls(labelEl, labRect){
              const tr=labelEl.closest('tr'); let controls=[];
              if(tr) controls=Array.from(tr.querySelectorAll('input:not([type="hidden"]),textarea,select')).filter(usable);
              if(!controls.length){
                const all=Array.from(document.querySelectorAll('input:not([type="hidden"]),textarea,select')).filter(usable);
                controls=all.filter(c=>{const r=c.getBoundingClientRect();const cy=(r.top+r.bottom)/2;const lcy=(labRect.top+labRect.bottom)/2;return Math.abs(cy-lcy)<=18||(r.bottom>=labRect.top-3&&r.top<=labRect.bottom+3);});
              }
              return controls.map(c=>{const r=c.getBoundingClientRect();return {el:c,left:r.left,top:r.top,right:r.right,cx:(r.left+r.right)/2};}).filter(c=>c.left>=labRect.right-12||c.cx>=labRect.right-4);
            }
            const labels=Array.from(document.querySelectorAll('td,th,label,span,font,b,strong,p,div'))
              .filter(visible)
              .map(el=>{const txt=clean(el.innerText||el.textContent||'');const sc=txt.length<=220?termScore(txt):0;const r=el.getBoundingClientRect();return {el,txt,sc,r,left:r.left,top:r.top};})
              .filter(x=>x.sc>0)
              .sort((a,b)=>(b.sc-a.sc)||(a.top-b.top)||(a.left-b.left));
            for(const lab of labels){
              const controls=rowControls(lab.el, lab.r).sort((a,b)=>(a.left-b.left)||(a.top-b.top));
              const out=[]; for(const c of controls){ if(!out.includes(c.el)) out.push(c.el); if(out.length>=maxControls) break; }
              if(out.length) return out;
            }
            return [];
            """, labels.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(), maxControls);

    private static List<IWebElement> ControlsNearLabel(IWebDriver driver, IEnumerable<string> labels, int maxControls)
        => ScriptElements(driver, """
            const terms = arguments[0] || [];
            const maxControls = Number(arguments[1] || 6);
            function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();}
            function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
            function visible(el){if(!el)return false;const st=getComputedStyle(el);if(st.display==='none'||st.visibility==='hidden'||st.opacity==='0')return false;const r=el.getBoundingClientRect();return r.width>0&&r.height>0;}
            function usable(c){if(!visible(c)||c.disabled)return false;const tag=(c.tagName||'').toLowerCase();const typ=String(c.type||'text').toLowerCase();if(tag==='select'||tag==='textarea')return true;if(tag!=='input')return false;return !['hidden','button','submit','radio','checkbox','image','reset','password'].includes(typ);}
            const controls=Array.from(document.querySelectorAll('input:not([type="hidden"]),textarea,select')).filter(usable).map(c=>{const r=c.getBoundingClientRect();return {el:c,left:r.left,right:r.right,top:r.top,bottom:r.bottom,cx:(r.left+r.right)/2,cy:(r.top+r.bottom)/2};});
            function termScore(txt){const n=norm(txt);if(!n)return 0;let score=0;for(const t of terms){if(!t)continue;if(n===t)score=Math.max(score,1200+t.length);else if(n.includes(t))score=Math.max(score,900+t.length);else if(t.includes(n)&&n.length>4)score=Math.max(score,500+n.length);}return score;}
            const labels=Array.from(document.querySelectorAll('td,th,label,span,font,b,strong,p,div')).filter(visible).map(el=>{const txt=clean(el.innerText||el.textContent||'');const sc=txt.length<=240?termScore(txt):0;const r=el.getBoundingClientRect();return {el,sc,left:r.left,right:r.right,top:r.top,bottom:r.bottom,cy:(r.top+r.bottom)/2};}).filter(x=>x.sc>0);
            const candidates=[];
            for(const lab of labels) for(const c of controls){
              const sameLine=Math.abs(c.cy-lab.cy)<=28||(c.bottom>=lab.top-6&&c.top<=lab.bottom+22);
              const rightSide=c.left>=lab.left-8;
              const nearBelow=c.top>=lab.bottom-4&&c.top<=lab.bottom+55&&Math.abs(c.left-lab.left)<420;
              if(!((sameLine&&rightSide)||nearBelow)) continue;
              let score=lab.sc-Math.abs(c.cy-lab.cy)*3;
              if(c.left>=lab.right-10) score+=180-Math.min(180,Math.abs(c.left-lab.right)); else score-=Math.min(200,Math.abs(c.left-lab.left));
              score-=Math.max(0,c.left-lab.right-700)/2;
              candidates.push({score,el:c.el,x:c.left,y:c.top});
            }
            candidates.sort((a,b)=>(b.score-a.score)||(a.y-b.y)||(a.x-b.x));
            const out=[]; for(const c of candidates){ if(!out.includes(c.el)) out.push(c.el); if(out.length>=maxControls) break; }
            return out;
            """, labels.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(), maxControls);

    private static List<IWebElement> ScriptElements(IWebDriver driver, string script, params object[] args)
    {
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript(script, args);
            if (raw is IWebElement one) return [one];
            if (raw is System.Collections.IEnumerable many)
            {
                var result = new List<IWebElement>();
                foreach (var item in many) if (item is IWebElement element) result.Add(element);
                return result;
            }
        }
        catch { }
        return [];
    }

    private static double? HeadingY(IWebDriver driver, IEnumerable<string> headings)
    {
        var terms = headings.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (terms.Length == 0) return null;
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const terms=arguments[0]||[];
                function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();}
                function visible(el){const st=getComputedStyle(el);if(st.display==='none'||st.visibility==='hidden'||st.opacity==='0')return false;const r=el.getBoundingClientRect();return r.width>0&&r.height>0;}
                let best=null;
                for(const el of document.querySelectorAll('tr,td,div,h1,h2,h3,h4,b,strong')){
                  if(!visible(el)) continue;
                  const text=norm(el.innerText||el.textContent||'');
                  if(!text||text.length>260||!terms.every(t=>text.includes(t))) continue;
                  const y=el.getBoundingClientRect().top+scrollY;
                  if(best===null||y<best) best=y;
                }
                return best;
                """, terms);
            return raw is null ? null : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static string OptionText(IWebDriver driver, IWebElement element)
    {
        try
        {
            return Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const el=arguments[0];
                function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
                const out=[]; if(el.value) out.push(el.value);
                if(el.id){const lab=document.querySelector('label[for="'+CSS.escape(el.id)+'"]'); if(lab) out.push(clean(lab.innerText));}
                let n=el.nextSibling, guard=0;
                while(n&&guard<5){
                  if(n.nodeType===Node.TEXT_NODE){const t=clean(n.textContent); if(t) out.push(t);}
                  else if(n.nodeType===Node.ELEMENT_NODE){const t=clean(n.innerText||n.value||n.alt||n.title); if(t) out.push(t); if((n.tagName||'').toLowerCase()==='input') break;}
                  n=n.nextSibling; guard++;
                }
                return out.join(' | ');
                """, element), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string ElementKey(IWebElement element)
    {
        try
        {
            var id = element.GetAttribute("id");
            var name = element.GetAttribute("name");
            if (!string.IsNullOrWhiteSpace(id)) return "id:" + id;
            if (!string.IsNullOrWhiteSpace(name)) return $"name:{name}:{element.TagName}:{element.GetAttribute("type")}";
            var p = element.Location;
            var s = element.Size;
            return $"xy:{p.X}:{p.Y}:{s.Width}:{s.Height}:{element.TagName}";
        }
        catch { return element.GetHashCode().ToString(CultureInfo.InvariantCulture); }
    }

    private static string ValueOf(IWebElement element)
    {
        try { return element.GetAttribute("value") ?? string.Empty; }
        catch { return string.Empty; }
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
        // Os localizadores já devolvem controles visíveis/habilitados. Reconsultar
        // Displayed e Enabled aqui gerava duas viagens extras ao WebDriver por campo.
        if (element is null) return false;
        var text = value ?? string.Empty;
        try
        {
            if (element.TagName.Equals("select", StringComparison.OrdinalIgnoreCase)) return FillSelectIfContains(element, text);
            if (string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase) || string.Equals(element.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase))
            {
                var yes = Normalize(text) is "sim" or "s" or "yes";
                if (element.Selected != yes) element.Click(); return true;
            }
            var driver = ((IWrapsDriver)element).WrappedDriver;
            return Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
                const el = arguments[0];
                const value = String(arguments[1] || '');
                el.value = value;
                return String(el.value || '') === value;
                """, element, text));
        }
        catch
        {
            try
            {
                var driver = ((IWrapsDriver)element).WrappedDriver;
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].value=arguments[1];", element, text);
                return true;
            }
            catch { return false; }
        }
    }

    private static bool FillSelectIfContains(IWebElement element, string? value)
    {
        try
        {
            var driver = ((IWrapsDriver)element).WrappedDriver;
            return Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
                const el = arguments[0];
                const raw = String(arguments[1] || '');
                function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();}
                function aliases(rawValue){
                  const n=norm(rawValue); const out=new Set(); if(n) out.add(n);
                  function add(v){const x=norm(v); if(x) out.add(x);}
                  if(n.includes('primeiro sargento')||/\b1\b.*\b(sgt|sargento)\b/.test(n)){add('1 sgt');add('1 sargento');add('primeiro sargento');}
                  if(n.includes('segundo sargento')||/\b2\b.*\b(sgt|sargento)\b/.test(n)){add('2 sgt');add('2 sargento');add('segundo sargento');}
                  if(n.includes('terceiro sargento')||/\b3\b.*\b(sgt|sargento)\b/.test(n)){add('3 sgt');add('3 sargento');add('terceiro sargento');}
                  if(n.includes('primeiro tenente')||/\b1\b.*\bten/.test(n)){add('1 ten');add('1 tenente');add('primeiro tenente');}
                  if(n.includes('segundo tenente')||/\b2\b.*\bten/.test(n)){add('2 ten');add('2 tenente');add('segundo tenente');}
                  if(n.includes('sub tenente')||n.includes('subtenente')){add('sub ten');add('subtenente');add('sub tenente');}
                  if(n.includes('cabo engajado')){add('cb engajado');add('cabo engajado');}
                  if(n.includes('cabo nao engajado')){add('cb nao engajado');add('cabo nao engajado');}
                  if(n.includes('sd engajado')||n.includes('soldado engajado')){add('sd engajado');add('soldado engajado');}
                  if(n.includes('sd nao engajado')||n.includes('soldado nao engajado')){add('sd nao engajado');add('soldado nao engajado');}
                  if(n.includes('bradesco')||/\b237\b/.test(n)){add('bradesco');add('banco bradesco');add('237');}
                  if(n.includes('banco do brasil')||/\b001\b/.test(n)){add('banco do brasil');add('brasil');add('001');}
                  if(n.includes('caixa')||/\b104\b/.test(n)){add('caixa');add('caixa economica federal');add('104');}
                  if(n.includes('itau')||/\b341\b/.test(n)){add('itau');add('341');}
                  if(n.includes('santander')||/\b033\b/.test(n)){add('santander');add('033');}
                  return Array.from(out);
                }
                const targets=aliases(raw); let best=-1,bestScore=-1;
                for(let i=0;i<el.options.length;i++){
                  const opt=el.options[i]; const text=norm(opt.text||''); const val=norm(opt.value||'');
                  let score=-1;
                  for(const target of targets){
                    if(!target) continue;
                    if(target===text) score=Math.max(score,1000+target.length);
                    else if(target===val) score=Math.max(score,960+target.length);
                    else if(text.includes(target)) score=Math.max(score,850+target.length);
                    else if(target.includes(text)&&text.length>2) score=Math.max(score,760+text.length);
                    else if(val.includes(target)) score=Math.max(score,700+target.length);
                  }
                  if(score>bestScore){bestScore=score;best=i;}
                }
                if(best>=0&&bestScore>=0){el.selectedIndex=best; return true;}
                return false;
                """, element, value ?? string.Empty));
        }
        catch { return false; }
    }

    private static int SelectMatchScore(IWebElement element, string? value)
    {
        try
        {
            var driver = ((IWrapsDriver)element).WrappedDriver;
            return Convert.ToInt32(((IJavaScriptExecutor)driver).ExecuteScript("""
                const el = arguments[0];
                const raw = String(arguments[1] || '');
                function norm(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();}
                const n=norm(raw); const targets=[n];
                if(n.includes('banco do brasil')||/\b001\b/.test(n)) targets.push('banco do brasil','brasil','001');
                if(n.includes('bradesco')||/\b237\b/.test(n)) targets.push('bradesco','237');
                if(n.includes('caixa')||/\b104\b/.test(n)) targets.push('caixa','caixa economica federal','104');
                if(n.includes('itau')||/\b341\b/.test(n)) targets.push('itau','341');
                if(n.includes('santander')||/\b033\b/.test(n)) targets.push('santander','033');
                let best=-1;
                for(let i=0;i<el.options.length;i++){
                  const opt=el.options[i]; const text=norm(opt.text||''); const val=norm(opt.value||'');
                  for(const target of targets){
                    if(!target) continue;
                    let score=-1;
                    if(target===text) score=1000+target.length;
                    else if(target===val) score=960+target.length;
                    else if(text.includes(target)) score=850+target.length;
                    else if(target.includes(text)&&text.length>2) score=760+text.length;
                    else if(val.includes(target)) score=700+target.length;
                    if(score>best) best=score;
                  }
                }
                return best;
                """, element, value ?? string.Empty), CultureInfo.InvariantCulture);
        }
        catch { return -1; }
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
    private static bool IsFillableControl(IWebElement e)
    {
        if (e.TagName.Equals("select", StringComparison.OrdinalIgnoreCase) || e.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase)) return true;
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

    private static (string Code, string Description) ResolveCpexCode(string text)
    {
        var extracted = ExtractCode(text);
        if (!string.IsNullOrWhiteSpace(extracted.Code)) return extracted;

        var description = Normalize(extracted.Description);
        if (description.Contains("aux") && description.Contains("fard"))
            return ("A56", string.IsNullOrWhiteSpace(extracted.Description) ? "AUXILIO FARDAMENTO" : extracted.Description);

        return extracted;
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
    private static bool HasMoney(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out var number) && Math.Abs(number) >= 0.005m;
    private static string CpexCodom(string? value)
    {
        var digits = ExercisePreviousRepository.Digits(value ?? string.Empty);
        // O CPEx trabalha com CODOM de seis posições. Cadastros antigos do SIGFUR
        // podem trazer um zero extra à esquerda (ex.: 0037515 em vez de 037515).
        if (digits.Length > 6) digits = digits[^6..];
        return digits.Length is > 0 and < 6 ? digits.PadLeft(6, '0') : digits;
    }

    private static string CpexAccount(string? value)
    {
        var normalized = ExercisePreviousRepository.NormalizeCpexAccount(value);
        if (normalized.Length > 12)
        {
            throw new InvalidOperationException(
                $"A conta bancária '{value}' possui {normalized.Length} dígitos significativos, " +
                "mas o CPEx aceita no máximo 12. Corrija a conta no cadastro antes de abrir o CPEx. " +
                "O envio foi interrompido para evitar a geração de outro relatório vazio.");
        }

        return normalized;
    }
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
