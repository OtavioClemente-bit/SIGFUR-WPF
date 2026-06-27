using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Automação nativa do CPEX/SIPPES para contracheques e fichas financeiras.
/// A Ficha Financeira usa fluxo próprio direto no CPEx, preenchendo CPF/ano e salvando PDF.
/// </summary>
public sealed class CpexPaystubAutomationService
{
    public const string LoginUrl = "https://cpex-intranet.eb.mil.br/asplogon_nova.asp?url=area_ua_cpex/index.asp";
    public const string QueryUrl = "https://cpex-intranet.eb.mil.br/cc_sippes/consulta.asp";
    public const string FinancialStatementUrl = "https://cpex-intranet.eb.mil.br/ff_sippes/consulta.asp";
    public const string SippesLoginUrl = "https://sippes.eb.mil.br/jsp/login/formLogin.jsp";
    public const string SippesBaseUrl = "https://sippes.eb.mil.br/consultarContracheque.do?metodo=exibirTelaConsultar&limparSessao=true";
    public const string SippesSelectUrl = "https://sippes.eb.mil.br/consultarContracheque.do?metodo=exibirTelaSelecionarFavorecidoCC&paginaDestino=consultarRelatorio&acaoPai=consultarContracheque&formularioPai=formularioConsultarContracheque&camposDestino=identificacaoFavorecido-cpfFavorecido-nomeFavorecido-precCpFavorecido&abrangenciaOm=true";

    private static readonly string[] Months =
    [
        "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho",
        "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro"
    ];

    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private IWebDriver? _preparedDriver;
    private CpexPaystubSettings? _preparedSettings;
    private DateTime _preparedAt = DateTime.MinValue;
    private int _serviceProcessId;
    private readonly HashSet<int> _driverProcessIds = [];
    private readonly HashSet<int> _browserProcessIds = [];
    private string _preparedBrowser = "Edge";

    public bool HasPreparedSession => _preparedDriver is not null;
    public DateTime? PreparedAt => _preparedDriver is null ? null : _preparedAt;

    public CpexPaystubAutomationService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
    }

    public async Task<CpexPaystubSettings> LoadSettingsAsync()
    {
        var settings = await _json.LoadAsync<CpexPaystubSettings>(_paths.CpexPaystubSettingsFile) ?? new CpexPaystubSettings();
        settings.OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? PersonDocumentStorageService.DefaultRoot(_paths)
            : settings.OutputDirectory;
        settings.Month = Math.Clamp(settings.Month, 1, 12);
        settings.Year = settings.Year is < 2000 or > 2200 ? DateTime.Today.Year : settings.Year;
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.System = NormalizeSystem(settings.System);
        settings.SheetCode = string.IsNullOrWhiteSpace(settings.SheetCode)
            ? CalculateSheetCode(settings.Year, settings.Month).ToString(CultureInfo.InvariantCulture)
            : MilitaryFormatting.Digits(settings.SheetCode);
        return settings;
    }

    public Task SaveSettingsAsync(CpexPaystubSettings settings)
    {
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.System = NormalizeSystem(settings.System);
        settings.Month = Math.Clamp(settings.Month, 1, 12);
        settings.SheetCode = string.IsNullOrWhiteSpace(settings.SheetCode)
            ? CalculateSheetCode(settings.Year, settings.Month).ToString(CultureInfo.InvariantCulture)
            : MilitaryFormatting.Digits(settings.SheetCode);
        settings.OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? PersonDocumentStorageService.DefaultRoot(_paths)
            : settings.OutputDirectory;
        return _json.SaveAsync(_paths.CpexPaystubSettingsFile, settings);
    }

    public string ReadSavedPassword(CpexPaystubSettings settings)
        => settings.SavePassword ? WindowsSecretProtector.Unprotect(settings.ProtectedPassword) : string.Empty;

    public async Task SaveCredentialsAsync(string login, string password, bool savePassword = true)
    {
        var settings = await LoadSettingsAsync();
        settings.Login = (login ?? string.Empty).Trim();
        settings.SavePassword = savePassword;
        settings.ProtectedPassword = savePassword && !string.IsNullOrWhiteSpace(password)
            ? WindowsSecretProtector.Protect(password)
            : string.Empty;
        await SaveSettingsAsync(settings);
    }

    public async Task ClearCredentialsAsync()
    {
        var settings = await LoadSettingsAsync();
        settings.Login = string.Empty;
        settings.ProtectedPassword = string.Empty;
        settings.SavePassword = false;
        await SaveSettingsAsync(settings);
    }

    public async Task<PaystubDownloadResult> DownloadLatestPaystubForMilitaryAsync(
        MilitaryRecord military,
        bool openAfterDownload = true,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(military);
        var settings = await LoadSettingsAsync();
        var password = ReadSavedPassword(settings);
        if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Não há login/senha salvos para o SIPPES/CPEx. Abra a Central de Contracheques, salve as credenciais e prepare a sessão uma vez.");

        settings.System = NormalizeSystem(settings.System);
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.Processing = string.IsNullOrWhiteSpace(settings.Processing) ? "Definitivo" : settings.Processing;
        settings.PayrollType = string.IsNullOrWhiteSpace(settings.PayrollType) ? "Normal" : settings.PayrollType;
        settings.OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? PersonDocumentStorageService.DefaultRoot(_paths)
            : settings.OutputDirectory;
        settings.OpenAfterDownload = false;

        var person = ToPaystubPerson(military);
        var attempts = new List<string>();
        var cursor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        for (var offset = 0; offset < 12; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = cursor.AddMonths(-offset);
            var attemptSettings = CloneSettings(settings);
            attemptSettings.Year = target.Year;
            attemptSettings.Month = target.Month;
            attemptSettings.SheetCode = CalculateSheetCode(target.Year, target.Month).ToString(CultureInfo.InvariantCulture);

            progress?.Report(new CpexPaystubProgress
            {
                Current = offset + 1,
                Total = 12,
                Name = military.Name,
                Message = offset == 0
                    ? $"Tentando competência atual {target:MM/yyyy} no {attemptSettings.System}..."
                    : $"Competência {target:MM/yyyy} não baixou. Tentando mês anterior..."
            });

            CpexPaystubBatchResult result;
            try
            {
                result = await DownloadPreparedAsync([person], attemptSettings, password, progress, cancellationToken, writeFailureReport: false);
            }
            catch (Exception ex) when (ShouldTryPreviousCompetence(ex))
            {
                attempts.Add($"{target:MM/yyyy}: {ex.Message}");
                continue;
            }

            var path = result.DownloadedFiles.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
            if (!string.IsNullOrWhiteSpace(path))
            {
                var finalPath = BuildPaystubOutputPath(attemptSettings, person);
                if (!SamePath(path, finalPath) && File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? attemptSettings.OutputDirectory);
                    File.Move(path, finalPath, overwrite: true);
                    path = finalPath;
                }

                if (openAfterDownload) ShellService.OpenPath(path);
                return new PaystubDownloadResult
                {
                    Success = true,
                    FilePath = path,
                    Year = target.Year,
                    Month = target.Month,
                    Message = $"Contracheque {target:MM/yyyy} salvo e aberto."
                };
            }

            var failure = result.Failures.FirstOrDefault();
            attempts.Add($"{target:MM/yyyy}: {(string.IsNullOrWhiteSpace(failure) ? "sem PDF retornado" : failure)}");
            if (!ShouldTryPreviousCompetence(failure)) break;
        }

        var message = "Não consegui baixar o contracheque mais recente disponível. Nenhum arquivo antigo foi aberto.";
        var downloadResult = new PaystubDownloadResult { Success = false, Message = message };
        downloadResult.Attempts.AddRange(attempts);
        return downloadResult;
    }

    private static CpexPaystubPerson ToPaystubPerson(MilitaryRecord military)
        => new(military.Name, military.Cpf, military.ShortRank, military.Id, military.MilitaryId, military.PrecCp);

    private static bool ShouldTryPreviousCompetence(Exception ex)
        => ShouldTryPreviousCompetence(ex.Message);

    private static bool ShouldTryPreviousCompetence(string? message)
    {
        var text = Normalize(message);
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Contains("login") || text.Contains("senha") || text.Contains("credencial") || text.Contains("usuario")
            || text.Contains("captcha") || text.Contains("certificado") || text.Contains("id inválido") || text.Contains("cpf invalido")
            || text.Contains("idt nao informada") || text.Contains("idt não informada") || text.Contains("prec-cp nao informado")
            || text.Contains("prec-cp não informado") || text.Contains("sessao expirada") || text.Contains("sessão expirada")
            || text.Contains("invalid session") || text.Contains("no such window") || text.Contains("desconectado"))
            return false;
        return text.Contains("nao retornou") || text.Contains("não retornou") || text.Contains("nao encontrado") || text.Contains("não encontrado")
               || text.Contains("nenhum registro") || text.Contains("nao localiz") || text.Contains("não localiz")
               || text.Contains("pdf nao apareceu") || text.Contains("pdf não apareceu") || text.Contains("sem pdf")
               || text.Contains("timeout") || text.Contains("tempo limite") || text.Contains("inexistente");
    }

    public static string BuildPaystubFileName(int year, int month)
        => $"{PortugueseMonth(month)} - {year}.pdf";

    public static string GetMilitaryPaystubFolder(AppPaths paths, string root, MilitaryRecord military)
        => PersonDocumentStorageService.PrepareRegisteredFolder(paths, root, military.ShortRank, military.Name, military.Cpf, military.PrecCp);

    public async Task PrepareHiddenSessionAsync(
        CpexPaystubSettings settings,
        string password,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings = CloneSettings(settings);
        settings.System = NormalizeSystem(settings.System);
        settings.Browser = NormalizeBrowser(settings.Browser);
        // O SIPPES antigo não é confiável no headless real. A janela normal nasce fora da tela,
        // preservando a automação oculta sem pagar o custo de criar e descartar dois drivers.
        settings.Headless = settings.System != "SIPPES";
        ValidateSessionSettings(settings, password);
        await SaveSettingsAsync(settings);
        Directory.CreateDirectory(settings.OutputDirectory);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                EnsurePreparedSessionUnsafe(settings, password, progress, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<CpexPaystubBatchResult> DownloadPreparedAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        CpexPaystubSettings settings,
        string password,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool writeFailureReport = true)
    {
        settings = CloneSettings(settings);
        settings.System = NormalizeSystem(settings.System);
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.Headless = settings.System != "SIPPES";

        var result = new CpexPaystubBatchResult();
        var valid = ValidateDownloadRequest(people, settings, password);
        await SaveSettingsAsync(settings);
        Directory.CreateDirectory(settings.OutputDirectory);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                var driver = EnsurePreparedSessionUnsafe(settings, password, progress, cancellationToken);
                HideAutomationWindows(driver);
                var isSippes = settings.System == "SIPPES";

                for (var index = 0; index < valid.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var person = valid[index];
                    progress?.Report(new CpexPaystubProgress
                    {
                        Current = index + 1,
                        Total = valid.Count,
                        Name = person.Name,
                        Message = $"Baixando contracheque de {person.Name}..."
                    });

                    try
                    {
                        var path = isSippes
                            ? DownloadOneSippes(driver, person, settings, cancellationToken)
                            : DownloadOneSiappes(driver, person, settings, cancellationToken);
                        result.DownloadedFiles.Add(path);
                        HideAutomationWindows(driver);
                    }
                    catch (Exception ex) when (LooksLikeExpiredSession(ex))
                    {
                        progress?.Report(new CpexPaystubProgress
                        {
                            Current = index + 1,
                            Total = valid.Count,
                            Name = person.Name,
                            Message = "Sessão expirada ou navegador desconectado. Refazendo login oculto e continuando..."
                        });

                        try
                        {
                            driver = RecreatePreparedSessionUnsafe(settings, password, progress, cancellationToken);
                            var path = isSippes
                                ? DownloadOneSippes(driver, person, settings, cancellationToken)
                                : DownloadOneSiappes(driver, person, settings, cancellationToken);
                            result.DownloadedFiles.Add(path);
                            HideAutomationWindows(driver);
                        }
                        catch (Exception retryEx)
                        {
                            result.Failures.Add($"{person.Name} ({MilitaryFormatting.FormatCpf(person.Cpf)}): {retryEx.Message}");
                            RecoverNavigation(driver, isSippes);
                            HideAutomationWindows(driver);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add($"{person.Name} ({MilitaryFormatting.FormatCpf(person.Cpf)}): {ex.Message}");
                        RecoverNavigation(driver, isSippes);
                        HideAutomationWindows(driver);
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }

        if (writeFailureReport) await WriteFailureReportAsync(settings, result, cancellationToken);
        return result;
    }


    public async Task<CpexPaystubBatchResult> DownloadFinancialStatementsPreparedAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        CpexPaystubSettings settings,
        int statementYear,
        string password,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings = CloneSettings(settings);
        settings.System = "SIAPPES";
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.Headless = true;
        settings.OpenAfterDownload = false;

        var result = new CpexPaystubBatchResult();
        var valid = ValidateFinancialStatementRequest(people, settings, statementYear, password);
        Directory.CreateDirectory(settings.OutputDirectory);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                var driver = EnsurePreparedSessionUnsafe(settings, password, progress, cancellationToken);

                for (var index = 0; index < valid.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var person = valid[index];
                    progress?.Report(new CpexPaystubProgress
                    {
                        Current = index + 1,
                        Total = valid.Count,
                        Name = person.Name,
                        Message = $"Baixando ficha financeira de {person.Name} — {statementYear}..."
                    });

                    try
                    {
                        var path = DownloadOneFinancialStatement(driver, person, settings, statementYear, cancellationToken);
                        result.DownloadedFiles.Add(path);
                    }
                    catch (Exception ex) when (LooksLikeExpiredSession(ex))
                    {
                        progress?.Report(new CpexPaystubProgress
                        {
                            Current = index + 1,
                            Total = valid.Count,
                            Name = person.Name,
                            Message = "Sessão CPEx expirada. Refazendo login oculto e continuando..."
                        });

                        try
                        {
                            driver = RecreatePreparedSessionUnsafe(settings, password, progress, cancellationToken);
                            var path = DownloadOneFinancialStatement(driver, person, settings, statementYear, cancellationToken);
                            result.DownloadedFiles.Add(path);
                        }
                        catch (Exception retryEx)
                        {
                            result.Failures.Add($"{person.Name} ({MilitaryFormatting.FormatCpf(person.Cpf)}): {retryEx.Message}");
                            try { driver.Navigate().GoToUrl(FinancialStatementUrl); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add($"{person.Name} ({MilitaryFormatting.FormatCpf(person.Cpf)}): {ex.Message}");
                        try { driver.Navigate().GoToUrl(FinancialStatementUrl); } catch { }
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }

        await WriteFinancialStatementFailureReportAsync(settings, statementYear, result, cancellationToken);
        return result;
    }

    public void DisposePreparedSession()
    {
        if (!_sessionGate.Wait(TimeSpan.FromMilliseconds(250))) return;
        try
        {
            DisposePreparedDriverUnsafe();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<CpexPaystubBatchResult> DownloadAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        CpexPaystubSettings settings,
        string password,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new CpexPaystubBatchResult();
        settings.System = NormalizeSystem(settings.System);
        var isSippes = settings.System == "SIPPES";
        if (isSippes) settings.Headless = false;
        var valid = people
            .Where(x => MilitaryFormatting.Digits(x.Cpf).Length == 11)
            .Where(x => !isSippes || (!string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.MilitaryId)) && !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.PrecCp))))
            .GroupBy(x => MilitaryFormatting.Digits(x.Cpf))
            .Select(x => x.First())
            .ToList();

        if (valid.Count == 0)
            throw new InvalidOperationException(isSippes
                ? "Nenhum militar possui, ao mesmo tempo, CPF, IDT e PREC-CP válidos para o SIPPES. Confira o cadastro na carteira."
                : "Nenhum CPF válido foi informado para o SIAPPES / Área UA.");
        if (string.IsNullOrWhiteSpace(settings.Login)) throw new InvalidOperationException("Informe o usuário do sistema escolhido.");
        if (!isSippes && MilitaryFormatting.Digits(settings.Login).Length < 6) throw new InvalidOperationException("Informe o CPF/usuário usado no login da Área Exclusiva da UA.");
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Informe a senha do sistema escolhido.");
        if (settings.Month is < 1 or > 12) throw new InvalidOperationException("Selecione um mês válido.");
        if (settings.Year is < 2000 or > 2200) throw new InvalidOperationException("Informe um ano válido.");
        if (isSippes && string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(settings.SheetCode)))
            throw new InvalidOperationException("Informe o código da folha do SIPPES.");

        await SaveSettingsAsync(settings);
        Directory.CreateDirectory(settings.OutputDirectory);

        await Task.Run(() =>
        {
            IWebDriver? driver = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new CpexPaystubProgress { Current = 0, Total = valid.Count, Message = isSippes ? "Abrindo o SIPPES..." : "Abrindo o SIAPPES / Área UA..." });
                driver = CreateDriver(settings);
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(isSippes ? 10 : 35);
                if (isSippes) PrepareSippesSession(driver, settings.Login, password, cancellationToken, progress);
                else LoginSiappes(driver, settings.Login, password, cancellationToken);
                HideAutomationWindows(driver);

                for (var index = 0; index < valid.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var person = valid[index];
                    progress?.Report(new CpexPaystubProgress
                    {
                        Current = index + 1,
                        Total = valid.Count,
                        Name = person.Name,
                        Message = $"Baixando contracheque de {person.Name}..."
                    });
                    try
                    {
                        var path = isSippes
                            ? DownloadOneSippes(driver, person, settings, cancellationToken)
                            : DownloadOneSiappes(driver, person, settings, cancellationToken);
                        result.DownloadedFiles.Add(path);
                        HideAutomationWindows(driver);
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add($"{person.Name} ({MilitaryFormatting.FormatCpf(person.Cpf)}): {ex.Message}");
                        try { driver.Navigate().GoToUrl(isSippes ? SippesSelectUrl : QueryUrl); } catch { }
                        HideAutomationWindows(driver);
                    }
                }
            }
            finally
            {
                try { driver?.Quit(); } catch { }
                try { driver?.Dispose(); } catch { }
            }
        }, cancellationToken);

        if (result.Failures.Count > 0)
        {
            var report = Path.Combine(settings.OutputDirectory,
                $"falhas_contracheques_{settings.Year}_{settings.Month:00}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllLinesAsync(report,
            [
                $"FALHAS — DOWNLOAD DE CONTRACHEQUES {settings.System}",
                $"Referência: {settings.Month:00}/{settings.Year}",
                "",
                .. result.Failures
            ], Encoding.UTF8, cancellationToken);
        }
        return result;
    }

    private static CpexPaystubSettings CloneSettings(CpexPaystubSettings source) => new()
    {
        System = source.System,
        Login = source.Login,
        SavePassword = source.SavePassword,
        ProtectedPassword = source.ProtectedPassword,
        Browser = source.Browser,
        Headless = source.Headless,
        OpenAfterDownload = source.OpenAfterDownload,
        OutputDirectory = source.OutputDirectory,
        Year = source.Year,
        Month = source.Month,
        Processing = source.Processing,
        PayrollType = source.PayrollType,
        SheetCode = source.SheetCode
    };

    private static void ValidateSessionSettings(CpexPaystubSettings settings, string password)
    {
        if (string.IsNullOrWhiteSpace(settings.Login)) throw new InvalidOperationException("Informe o usuário do sistema escolhido.");
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Informe a senha do sistema escolhido.");
        if (settings.Month is < 1 or > 12) throw new InvalidOperationException("Selecione um mês válido.");
        if (settings.Year is < 2000 or > 2200) throw new InvalidOperationException("Informe um ano válido.");
        if (settings.System == "SIAPPES" && MilitaryFormatting.Digits(settings.Login).Length < 6)
            throw new InvalidOperationException("Informe o CPF/usuário usado no login da Área Exclusiva da UA.");
        if (settings.System == "SIPPES" && string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(settings.SheetCode)))
            throw new InvalidOperationException("Informe o código da folha do SIPPES.");
    }

    private static List<CpexPaystubPerson> ValidateDownloadRequest(IReadOnlyList<CpexPaystubPerson> people, CpexPaystubSettings settings, string password)
    {
        ValidateSessionSettings(settings, password);
        var isSippes = settings.System == "SIPPES";
        var valid = people
            .Where(x => MilitaryFormatting.Digits(x.Cpf).Length == 11)
            .Where(x => !isSippes || (!string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.MilitaryId)) && !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.PrecCp))))
            .GroupBy(x => MilitaryFormatting.Digits(x.Cpf))
            .Select(x => x.First())
            .ToList();

        if (valid.Count == 0)
            throw new InvalidOperationException(isSippes
                ? "Nenhum militar possui, ao mesmo tempo, CPF, IDT e PREC-CP válidos para o SIPPES. Confira o cadastro na carteira."
                : "Nenhum CPF válido foi informado para o SIAPPES / Área UA.");
        return valid;
    }


    private static List<CpexPaystubPerson> ValidateFinancialStatementRequest(
        IReadOnlyList<CpexPaystubPerson> people,
        CpexPaystubSettings settings,
        int statementYear,
        string password)
    {
        if (string.IsNullOrWhiteSpace(settings.Login)) throw new InvalidOperationException("Informe o CPF/usuário da Área Exclusiva do CPEx.");
        if (MilitaryFormatting.Digits(settings.Login).Length < 6) throw new InvalidOperationException("Para baixar Ficha Financeira, informe o CPF/usuário da Área Exclusiva do CPEx.");
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Informe a senha da Área Exclusiva do CPEx.");
        if (statementYear is < 2000 or > 2200) throw new InvalidOperationException("Informe um ano válido para a Ficha Financeira.");

        var valid = people
            .Where(x => MilitaryFormatting.Digits(x.Cpf).Length == 11)
            .GroupBy(x => MilitaryFormatting.Digits(x.Cpf))
            .Select(x => x.First())
            .ToList();
        if (valid.Count == 0) throw new InvalidOperationException("Nenhum militar possui CPF válido para baixar Ficha Financeira.");
        return valid;
    }

    private IWebDriver EnsurePreparedSessionUnsafe(CpexPaystubSettings settings, string password, IProgress<CpexPaystubProgress>? progress, CancellationToken ct)
    {
        if (_preparedDriver is not null && SessionMatches(_preparedSettings, settings) && IsDriverAlive(_preparedDriver))
        {
            if (settings.System == "SIPPES")
            {
                try
                {
                    if (IsSippesReady(_preparedDriver))
                    {
                        HideAutomationWindows(_preparedDriver);
                        progress?.Report(new CpexPaystubProgress { Message = "SIPPES pronto — baixando em segundo plano..." });
                        return _preparedDriver;
                    }

                    progress?.Report(new CpexPaystubProgress { Message = "Sessão oculta aberta. Conferindo login e tela do SIPPES..." });
                    PrepareSippesSession(_preparedDriver, settings.Login, password, ct, progress);
                    HideAutomationWindows(_preparedDriver);
                    _preparedAt = DateTime.Now;
                    progress?.Report(new CpexPaystubProgress { Message = $"Sessão oculta reaproveitada e pronta às {_preparedAt:HH:mm}." });
                    return _preparedDriver;
                }
                catch (WebDriverException)
                {
                    progress?.Report(new CpexPaystubProgress { Message = "O navegador da sessão anterior desconectou. Criando uma nova sessão..." });
                }
            }
            else
            {
                HideAutomationWindows(_preparedDriver);
                progress?.Report(new CpexPaystubProgress { Message = "Sessão oculta já preparada. Iniciando download..." });
                return _preparedDriver;
            }
        }
        return RecreatePreparedSessionUnsafe(settings, password, progress, ct);
    }

    private IWebDriver RecreatePreparedSessionUnsafe(CpexPaystubSettings settings, string password, IProgress<CpexPaystubProgress>? progress, CancellationToken ct)
    {
        DisposePreparedDriverUnsafe();
        IWebDriver? driver = null;
        try
        {
            progress?.Report(new CpexPaystubProgress { Message = settings.System == "SIPPES" ? "Preparando SIPPES oculto..." : "Preparando Área UA/SIAPPES oculta..." });
            driver = CreateDriver(settings);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(settings.System == "SIPPES" ? 10 : 35);
            if (settings.System == "SIPPES")
            {
                PrepareSippesSession(driver, settings.Login, password, ct, progress);
            }
            else
            {
                LoginSiappes(driver, settings.Login, password, ct);
            }
            HideAutomationWindows(driver);
            _preparedDriver = driver;
            _preparedSettings = CloneSettings(settings);
            _preparedAt = DateTime.Now;
            progress?.Report(new CpexPaystubProgress { Message = $"Sessão oculta preparada às {_preparedAt:HH:mm}." });
            return driver;
        }
        catch
        {
            try { driver?.Quit(); } catch { }
            try { driver?.Dispose(); } catch { }
            throw;
        }
    }

    private void DisposePreparedDriverUnsafe()
    {
        try { _preparedDriver?.Quit(); } catch { }
        try { _preparedDriver?.Dispose(); } catch { }
        _preparedDriver = null;
        _preparedSettings = null;
        _preparedAt = DateTime.MinValue;
        _serviceProcessId = 0;
        _driverProcessIds.Clear();
        _browserProcessIds.Clear();
        _preparedBrowser = "Edge";
    }

    private static bool SessionMatches(CpexPaystubSettings? prepared, CpexPaystubSettings current)
    {
        if (prepared is null) return false;
        return string.Equals(NormalizeSystem(prepared.System), NormalizeSystem(current.System), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeBrowser(prepared.Browser), NormalizeBrowser(current.Browser), StringComparison.OrdinalIgnoreCase)
               && string.Equals((prepared.Login ?? string.Empty).Trim(), (current.Login ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeDirectory(prepared.OutputDirectory), NormalizeDirectory(current.OutputDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string? path)
    {
        try { return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path ?? string.Empty; }
    }

    private static bool IsDriverAlive(IWebDriver driver)
    {
        try { return driver.WindowHandles.Count > 0; }
        catch { return false; }
    }

    private static bool LooksLikeExpiredSession(Exception ex)
    {
        var text = Normalize(string.Join(' ', ex.Message, ex.GetType().Name));
        return text.Contains("sessao") || text.Contains("session") || text.Contains("login") || text.Contains("senha")
               || text.Contains("no such window") || text.Contains("invalid session") || text.Contains("disconnected")
               || text.Contains("navegador") || text.Contains("conectado");
    }

    private static void RecoverNavigation(IWebDriver driver, bool isSippes)
    {
        try { driver.Navigate().GoToUrl(isSippes ? SippesSelectUrl : QueryUrl); } catch { }
    }

    private static async Task WriteFailureReportAsync(CpexPaystubSettings settings, CpexPaystubBatchResult result, CancellationToken cancellationToken)
    {
        if (result.Failures.Count == 0) return;
        var report = Path.Combine(settings.OutputDirectory,
            $"falhas_contracheques_{settings.Year}_{settings.Month:00}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllLinesAsync(report,
        [
            $"FALHAS — DOWNLOAD DE CONTRACHEQUES {settings.System}",
            $"Referência: {settings.Month:00}/{settings.Year}",
            "",
            .. result.Failures
        ], Encoding.UTF8, cancellationToken);
    }


    private static async Task WriteFinancialStatementFailureReportAsync(CpexPaystubSettings settings, int statementYear, CpexPaystubBatchResult result, CancellationToken cancellationToken)
    {
        if (result.Failures.Count == 0) return;
        var report = Path.Combine(settings.OutputDirectory,
            $"falhas_fichas_financeiras_{statementYear}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllLinesAsync(report,
        [
            $"FALHAS — DOWNLOAD DE FICHAS FINANCEIRAS {statementYear}",
            "",
            .. result.Failures
        ], Encoding.UTF8, cancellationToken);
    }

    private IWebDriver CreateDriver(CpexPaystubSettings settings)
    {
        var browser = NormalizeBrowser(settings.Browser);
        var browserProcessNames = BrowserProcessNames(browser);
        var driverProcessNames = DriverProcessNames(browser);
        var browserProcessesBefore = SnapshotProcessIds(browserProcessNames);
        var driverProcessesBefore = SnapshotProcessIds(driverProcessNames);

        IWebDriver driver;
        object service;
        // O Page.printToPDF do Chromium é o caminho mais confiável para salvar o PDF sem diálogo.
        if (browser == "Chrome")
        {
            var chromeService = ChromeDriverService.CreateDefaultService();
            chromeService.HideCommandPromptWindow = true;
            chromeService.SuppressInitialDiagnosticInformation = true;
            var options = new ChromeOptions();
            ConfigureChromium(options, settings, chrome: true);
            driver = new ChromeDriver(chromeService, options, TimeSpan.FromSeconds(90));
            service = chromeService;
        }
        else
        {
            var edgeService = EdgeDriverService.CreateDefaultService();
            edgeService.HideCommandPromptWindow = true;
            edgeService.SuppressInitialDiagnosticInformation = true;
            var options = new EdgeOptions();
            ConfigureChromium(options, settings, chrome: false);
            driver = new EdgeDriver(edgeService, options, TimeSpan.FromSeconds(90));
            service = edgeService;
        }

        TrackDriverLaunch(service, browser, browserProcessNames, driverProcessNames, browserProcessesBefore, driverProcessesBefore);
        HideDriverServiceWindows();
        return driver;
    }

    private static void ConfigureChromium(DriverOptions options, CpexPaystubSettings settings, bool chrome)
    {
        if (options is not ChromiumOptions chromium) return;
        chromium.PageLoadStrategy = PageLoadStrategy.Eager;
        if (settings.Headless)
        {
            chromium.AddArgument("--headless=new");
        }
        else
        {
            // Login/captcha manual precisa de janela normal. Depois que a sessão fica pronta,
            // HideAutomationWindows usa Win32 para remover da tela, barra de tarefas e Alt+Tab.
        }
        chromium.AddArgument("--ignore-certificate-errors");
        chromium.AddArgument("--disable-gpu");
        chromium.AddArgument("--disable-popup-blocking");
        chromium.AddArgument("--disable-notifications");
        chromium.AddArgument("--disable-backgrounding-occluded-windows");
        chromium.AddArgument("--disable-renderer-backgrounding");
        chromium.AddArgument("--window-size=1440,1000");
        chromium.AddArgument(chrome ? "--incognito" : "--inprivate");
        chromium.AddUserProfilePreference("credentials_enable_service", false);
        chromium.AddUserProfilePreference("profile.password_manager_enabled", false);
        chromium.AddUserProfilePreference("autofill.profile_enabled", false);
        chromium.AddUserProfilePreference("autofill.credit_card_enabled", false);
        chromium.AddUserProfilePreference("download.default_directory", Path.GetFullPath(settings.OutputDirectory));
        chromium.AddUserProfilePreference("download.prompt_for_download", false);
        chromium.AddUserProfilePreference("download.directory_upgrade", true);
        chromium.AddUserProfilePreference("plugins.always_open_pdf_externally", true);
    }

    private void TrackDriverLaunch(
        object service,
        string browser,
        IReadOnlyCollection<string> browserProcessNames,
        IReadOnlyCollection<string> driverProcessNames,
        HashSet<int> browserProcessesBefore,
        HashSet<int> driverProcessesBefore)
    {
        _preparedBrowser = browser;
        _serviceProcessId = ReadServiceProcessId(service);

        var driverProcessIds = SnapshotProcessIds(driverProcessNames);
        driverProcessIds.ExceptWith(driverProcessesBefore);
        if (_serviceProcessId > 0) driverProcessIds.Add(_serviceProcessId);
        _driverProcessIds.Clear();
        _driverProcessIds.UnionWith(driverProcessIds);

        var browserProcessIds = SnapshotProcessIds(browserProcessNames);
        browserProcessIds.ExceptWith(browserProcessesBefore);
        if (_serviceProcessId > 0)
        {
            foreach (var processId in DescendantProcessIds(_serviceProcessId))
                if (ProcessNameMatches(processId, browserProcessNames)) browserProcessIds.Add(processId);
        }
        _browserProcessIds.Clear();
        _browserProcessIds.UnionWith(browserProcessIds);
    }

    private void HideAutomationWindows(IWebDriver? driver = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { driver?.Manage().Window.Minimize(); } catch { }
            return;
        }

        HideDriverServiceWindows();
        var handles = FindBrowserWindows(visibleOnly: false);
        foreach (var handle in handles)
        {
            try
            {
                ShowWindow(handle, SwHide);
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpHideWindow);
            }
            catch { }
        }
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
        pids.Remove(_serviceProcessId);
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
        foreach (var root in roots) candidates.UnionWith(DescendantProcessIds(root));
        var names = BrowserProcessNames(_preparedBrowser);
        candidates.RemoveWhere(processId => !ProcessNameMatches(processId, names));
        _browserProcessIds.UnionWith(candidates);
        return candidates;
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
        }
        catch { }
        return 0;
    }

    private static HashSet<int> SnapshotProcessIds(IEnumerable<string> processNames)
    {
        var allowed = processNames.Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (allowed.Contains(process.ProcessName.ToLowerInvariant())) result.Add(process.Id);
            }
            catch { }
            finally { try { process.Dispose(); } catch { } }
        }
        return result;
    }

    private static IReadOnlyCollection<string> BrowserProcessNames(string browser)
        => string.Equals(browser, "Chrome", StringComparison.OrdinalIgnoreCase) ? ["chrome"] : ["msedge"];

    private static IReadOnlyCollection<string> DriverProcessNames(string browser)
        => string.Equals(browser, "Chrome", StringComparison.OrdinalIgnoreCase) ? ["chromedriver"] : ["msedgedriver"];

    private static bool ProcessNameMatches(int processId, IReadOnlyCollection<string> names)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return names.Any(name => process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static HashSet<int> DescendantProcessIds(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        if (!OperatingSystem.IsWindows() || rootPid <= 0) return result;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            var pairs = new List<(int Pid, int Parent)>();
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    pairs.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                }
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
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static void LoginSiappes(IWebDriver driver, string login, string password, CancellationToken ct)
    {
        driver.Navigate().GoToUrl(LoginUrl);
        WaitReady(driver, ct);
        var inputs = driver.FindElements(By.CssSelector("input"))
            .Where(IsDisplayed)
            .ToList();
        var user = inputs.FirstOrDefault(x => !EqualsIgnoreCase(x.GetAttribute("type"), "password") &&
            ContainsAny(Signature(x), ["cpf", "usuario", "usuário", "login"]))
            ?? inputs.FirstOrDefault(x => !EqualsIgnoreCase(x.GetAttribute("type"), "password") && !IsButton(x));
        var pass = inputs.FirstOrDefault(x => EqualsIgnoreCase(x.GetAttribute("type"), "password"));
        if (user is null || pass is null) throw new InvalidOperationException("Não encontrei os campos de usuário e senha da Área Exclusiva da UA.");
        SetValue(driver, user, MilitaryFormatting.Digits(login));
        SetValue(driver, pass, password);
        var enter = driver.FindElements(By.CssSelector("button,input[type=submit],input[type=button],a"))
            .FirstOrDefault(x => IsDisplayed(x) && ContainsAny(Context(x), ["entrar", "acessar", "login"]));
        var loginUrl = SafeUrl(driver);
        try { (enter ?? pass).Click(); } catch { try { pass.SendKeys(Keys.Enter); } catch { } }
        WaitUntil(driver, d => !string.Equals(SafeUrl(d), loginUrl, StringComparison.OrdinalIgnoreCase)
                               || !d.FindElements(By.CssSelector("input[type=password]")).Any(IsDisplayed),
            TimeSpan.FromSeconds(18), ct);
        AcceptAlert(driver);
        ct.ThrowIfCancellationRequested();
    }

    private static string DownloadOneSiappes(IWebDriver driver, CpexPaystubPerson person, CpexPaystubSettings settings, CancellationToken ct)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        driver.Navigate().GoToUrl(QueryUrl);
        WaitReady(driver, ct);
        DisableAutofill(driver);
        FillCpf(driver, cpf);

        var selects = driver.FindElements(By.CssSelector("select")).Where(IsDisplayed).ToList();
        if (selects.Count == 0) throw new InvalidOperationException("A tela de consulta abriu, mas os campos de seleção não foram encontrados.");

        SelectBest(selects, settings.Processing, settings.Processing);
        SelectBest(selects, Months[settings.Month - 1], settings.Month.ToString(CultureInfo.InvariantCulture), settings.Month.ToString("00"));
        SelectBest(selects, settings.Year.ToString(CultureInfo.InvariantCulture));
        SelectBest(selects, settings.PayrollType, settings.PayrollType);

        // O site costuma reaplicar o favorecido anterior no último change do formulário.
        FillCpf(driver, cpf);
        WaitForDomIdle(driver, TimeSpan.FromSeconds(2), ct);
        var before = driver.WindowHandles.ToList();
        var bodyBefore = BodySignature(driver);
        var consult = driver.FindElements(By.CssSelector("button,input[type=submit],input[type=button],a"))
            .FirstOrDefault(x => IsDisplayed(x) && ContainsAny(Context(x), ["consultar", "pesquisar"]));
        if (consult is null) throw new InvalidOperationException("Não encontrei o botão CONSULTAR.");
        consult.Click();
        WaitUntil(driver, d => d.WindowHandles.Any(x => !before.Contains(x))
                               || !string.Equals(BodySignature(d), bodyBefore, StringComparison.Ordinal),
            TimeSpan.FromSeconds(25), ct);
        AcceptAlert(driver);
        var newHandle = driver.WindowHandles.FirstOrDefault(x => !before.Contains(x));
        if (!string.IsNullOrWhiteSpace(newHandle)) driver.SwitchTo().Window(newHandle);
        WaitReady(driver, ct);

        var currentText = Normalize(driver.FindElement(By.TagName("body")).Text);
        if (currentText.Contains("nenhum registro") || currentText.Contains("nao encontrado") || currentText.Contains("não encontrado"))
            throw new InvalidOperationException("O SIAPPES / Área UA não retornou contracheque para os parâmetros escolhidos.");

        var output = BuildPaystubOutputPath(settings, person);
        PrintCurrentPage(driver, output);
        CleanupEquivalentPaystubFiles(settings, person, output);

        if (!string.IsNullOrWhiteSpace(newHandle))
        {
            try { driver.Close(); } catch { }
            try { driver.SwitchTo().Window(before.Last()); } catch { }
        }
        return output;
    }


    private static string DownloadOneFinancialStatement(IWebDriver driver, CpexPaystubPerson person, CpexPaystubSettings settings, int statementYear, CancellationToken ct)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        if (cpf.Length != 11) throw new InvalidOperationException("CPF inválido para consultar Ficha Financeira.");

        driver.Navigate().GoToUrl(FinancialStatementUrl);
        WaitReady(driver, ct);
        DisableAutofill(driver);

        if (IsFinancialStatementPage(driver) && PageContainsDigits(driver, cpf))
        {
            // Já está na ficha correta, normalmente por reaproveitamento de aba.
        }
        else
        {
            var handlesBefore = driver.WindowHandles.ToList();
            if (!FillFinancialStatementForm(driver, cpf, statementYear))
            {
                var text = BodySignature(driver);
                if (LooksLikeFinancialStatementLoginOrBlock(text))
                    throw new InvalidOperationException("A página da Ficha Financeira pediu login/certificado/captcha ou a sessão expirou.");
                throw new InvalidOperationException("Não encontrei ou não consegui preencher automaticamente CPF/ano da Ficha Financeira.");
            }

            Thread.Sleep(800);
            var newHandle = driver.WindowHandles.FirstOrDefault(x => !handlesBefore.Contains(x));
            if (!string.IsNullOrWhiteSpace(newHandle)) driver.SwitchTo().Window(newHandle);
        }

        var status = WaitForFinancialStatementResult(driver, cpf, TimeSpan.FromSeconds(55), ct);
        if (status == "nao_encontrado") throw new InvalidOperationException("Ficha Financeira não encontrada para este CPF/ano.");
        if (status != "ok")
        {
            var text = BodySignature(driver);
            if (LooksLikeFinancialStatementLoginOrBlock(text))
                throw new InvalidOperationException("A página da Ficha Financeira pediu login/certificado/captcha ou a sessão expirou.");
            throw new TimeoutException("A Ficha Financeira não carregou dentro do tempo esperado.");
        }

        var output = BuildFinancialStatementOutputPath(settings, person, statementYear);
        PrintFinancialStatementPage(driver, output);
        return output;
    }

    private static bool FillFinancialStatementForm(IWebDriver driver, string cpf, int statementYear)
    {
        try
        {
            var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const cpf = arguments[0] || '';
                const ano = String(arguments[1] || '');
                function norm(s){return String(s||'').toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g,'');}
                function visible(el){
                    try {
                        const st = window.getComputedStyle(el);
                        const r = el.getBoundingClientRect();
                        return st.display !== 'none' && st.visibility !== 'hidden' && r.width > 1 && r.height > 1 && !el.disabled;
                    } catch(e) { return false; }
                }
                function setValue(el, value){
                    if (!el) return false;
                    try { el.removeAttribute('readonly'); el.disabled = false; el.focus(); } catch(e) {}
                    try { el.value = ''; } catch(e) {}
                    try { el.value = value; } catch(e) { return false; }
                    ['input','change','blur'].forEach(name=>{ try { el.dispatchEvent(new Event(name, {bubbles:true})); } catch(e) {} });
                    return true;
                }
                const all = Array.from(document.querySelectorAll('input, textarea, select')).filter(visible);
                const textInputs = all.filter(el => ['text','tel','search','number',''].includes(String(el.type||'').toLowerCase()) || el.tagName === 'TEXTAREA');

                let cpfEl = null;
                for (const el of textInputs) {
                    const blob = norm([el.name, el.id, el.placeholder, el.title, el.getAttribute('aria-label'), el.parentElement && el.parentElement.innerText].join(' '));
                    if (blob.includes('cpf')) { cpfEl = el; break; }
                }
                if (!cpfEl) cpfEl = textInputs[0] || null;
                if (!cpfEl || !setValue(cpfEl, cpf)) return false;

                for (const el of textInputs) {
                    if (el === cpfEl) continue;
                    const blob = norm([el.name, el.id, el.placeholder, el.title, el.getAttribute('aria-label'), el.parentElement && el.parentElement.innerText].join(' '));
                    if (blob.includes('prec') || blob.includes('preccp') || blob.includes('prec-cp')) { setValue(el, ''); break; }
                }

                let anoEl = null;
                for (const el of all) {
                    const blob = norm([el.name, el.id, el.placeholder, el.title, el.getAttribute('aria-label'), el.parentElement && el.parentElement.innerText].join(' '));
                    if (blob.includes('ano') || blob.includes('exercicio') || blob.includes('exerc')) { anoEl = el; break; }
                }
                if (!anoEl) anoEl = Array.from(document.querySelectorAll('select')).find(visible) || null;
                if (anoEl) {
                    if (anoEl.tagName === 'SELECT') {
                        const opts = Array.from(anoEl.options || []);
                        let chosen = -1;
                        for (let i=0; i<opts.length; i++) {
                            const txt = norm(opts[i].textContent || '');
                            const val = norm(opts[i].value || '');
                            if (txt === norm(ano) || val === norm(ano) || txt.includes(norm(ano)) || val.includes(norm(ano))) { chosen = i; break; }
                        }
                        if (chosen >= 0) {
                            anoEl.selectedIndex = chosen;
                            anoEl.value = anoEl.options[chosen].value;
                            ['input','change','blur'].forEach(name=>{ try { anoEl.dispatchEvent(new Event(name, {bubbles:true})); } catch(e) {} });
                        } else setValue(anoEl, ano);
                    } else setValue(anoEl, ano);
                }

                const btns = Array.from(document.querySelectorAll('button, input[type=submit], input[type=button], input[type=image], a')).filter(visible);
                let btn = null;
                for (const el of btns) {
                    const blob = norm([el.innerText, el.value, el.alt, el.title, el.id, el.name, el.getAttribute('aria-label')].join(' '));
                    if (blob.includes('consult') || blob.includes('visualiz') || blob.includes('pesquis') || blob.includes('buscar')) { btn = el; break; }
                }
                if (btn) { btn.click(); return true; }
                if (cpfEl.form) { cpfEl.form.submit(); return true; }
                return false;
                """, cpf, statementYear.ToString(CultureInfo.InvariantCulture));
            return Convert.ToBoolean(result, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    private static string WaitForFinancialStatementResult(IWebDriver driver, string cpf, TimeSpan timeout, CancellationToken ct)
    {
        var limit = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            try { WaitForDomIdle(driver, TimeSpan.FromSeconds(2), ct); } catch { }
            if (IsFinancialStatementPage(driver) && PageContainsDigits(driver, cpf)) return "ok";
            var text = BodySignature(driver);
            if (IndicatesNotFound(text)) return "nao_encontrado";
            if (ct.WaitHandle.WaitOne(600)) throw new OperationCanceledException(ct);
        }
        return "timeout";
    }

    private static bool IsFinancialStatementPage(IWebDriver driver)
    {
        var text = Normalize(string.Join(' ', SafeUrl(driver), SafeTitle(driver), BodySignature(driver)));
        return text.Contains("ficha") && (text.Contains("financeira") || text.Contains("financeiro"));
    }

    private static bool PageContainsDigits(IWebDriver driver, string digits)
    {
        try
        {
            var text = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return (document.body && document.body.innerText || '').slice(0, 120000);"), CultureInfo.InvariantCulture) ?? string.Empty;
            return MilitaryFormatting.Digits(text).Contains(MilitaryFormatting.Digits(digits), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static bool LooksLikeFinancialStatementLoginOrBlock(string? text)
    {
        var value = Normalize(text);
        return value.Contains("login") || value.Contains("autentic") || value.Contains("senha") || value.Contains("usuario")
               || value.Contains("certificado") || value.Contains("captcha") || value.Contains("area exclusiva")
               || value.Contains("acesso") || value.Contains("sessao expirada") || value.Contains("expirou");
    }

    private static bool IndicatesNotFound(string? text)
    {
        var value = Normalize(text);
        return value.Contains("nenhum registro") || value.Contains("nao encontrado") || value.Contains("não encontrado")
               || value.Contains("nao localiz") || value.Contains("não localiz") || value.Contains("inexistente");
    }

    private readonly record struct SippesLoginResult(
        bool Submitted,
        bool Authenticated,
        bool UserFound,
        bool PasswordFound,
        string Method);

    private void PrepareSippesSession(IWebDriver driver, string login, string password, CancellationToken ct, IProgress<CpexPaystubProgress>? progress = null)
    {
        // Mesmo fluxo do SIGFUR Python: login -> ponte curta -> seleção, sempre no mesmo driver.
        SwitchToSippesWindow(driver);
        if (IsSippesReady(driver))
        {
            progress?.Report(new CpexPaystubProgress { Message = "SIPPES já preparado. Reutilizando a sessão atual." });
            return;
        }

        var lastStage = "início";
        SippesLoginResult? lastLogin = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lastStage = "login";
            progress?.Report(new CpexPaystubProgress { Message = $"1/6 Abrindo login do SIPPES{(attempt > 1 ? " (2ª tentativa)" : string.Empty)}..." });
            NavigateSippes(driver, SippesLoginUrl, ct);

            if (IsSippesReady(driver))
            {
                progress?.Report(new CpexPaystubProgress { Message = "SIPPES já autenticado e pronto." });
                return;
            }

            if (IsSippesLoginPage(driver))
            {
                WaitUntil(driver, HasSippesLoginForm, TimeSpan.FromSeconds(3), ct);
                progress?.Report(new CpexPaystubProgress { Message = "2/6 Preenchendo usuário/CPF e senha salvos..." });
                progress?.Report(new CpexPaystubProgress { Message = "3/6 Enviando o login..." });
                lastLogin = TrySippesLogin(driver, login, password, ct);
                LogSippesDiagnostic(lastStage, driver, lastLogin);

                if (!lastLogin.Value.Authenticated)
                {
                    if (attempt < 2)
                        progress?.Report(new CpexPaystubProgress { Message = "O login não avançou. Fazendo a última tentativa..." });
                    continue;
                }
            }
            else
            {
                lastLogin = new SippesLoginResult(false, true, false, false, "sessão existente");
                LogSippesDiagnostic("login já autenticado", driver, lastLogin);
            }

            lastStage = "ponte";
            progress?.Report(new CpexPaystubProgress { Message = "4/6 Abrindo a ponte do contracheque..." });
            NavigateSippes(driver, SippesBaseUrl, ct);
            if (IsSippesLoginPage(driver))
            {
                LogSippesDiagnostic("ponte retornou ao login", driver, lastLogin);
                if (attempt < 2)
                    progress?.Report(new CpexPaystubProgress { Message = "A ponte voltou ao login. Fazendo a última tentativa..." });
                continue;
            }

            lastStage = "seleção de favorecido";
            progress?.Report(new CpexPaystubProgress { Message = "5/6 Abrindo a seleção de favorecido..." });
            NavigateSippes(driver, SippesSelectUrl, ct);
            if (IsSippesLoginPage(driver))
            {
                LogSippesDiagnostic("seleção retornou ao login", driver, lastLogin);
                if (attempt < 2)
                    progress?.Report(new CpexPaystubProgress { Message = "A seleção voltou ao login. Fazendo a última tentativa..." });
                continue;
            }

            lastStage = "confirmação selecionarFavorecido";
            progress?.Report(new CpexPaystubProgress { Message = "6/6 Confirmando selecionarFavorecido(...)..." });
            if (WaitUntil(driver, IsSippesReady, TimeSpan.FromSeconds(10), ct))
            {
                LogSippesDiagnostic("sessão pronta", driver, lastLogin);
                progress?.Report(new CpexPaystubProgress { Message = "SIPPES pronto para receber os downloads." });
                return;
            }

            LogSippesDiagnostic(lastStage, driver, lastLogin);
        }

        var url = SafeUrl(driver);
        var title = SafeTitle(driver);
        var loginDetails = lastLogin is { } diagnostic
            ? $"Usuário localizado: {(diagnostic.UserFound ? "sim" : "não")} | senha localizada: {(diagnostic.PasswordFound ? "sim" : "não")} | envio: {diagnostic.Method}."
            : "A tela de login não forneceu diagnóstico de preenchimento.";
        throw new InvalidOperationException(
            "Não foi possível deixar o SIPPES pronto após duas tentativas.\n\n" +
            $"Etapa: {lastStage}\nURL atual: {url}\nTítulo: {title}\n{loginDetails}\n" +
            "Confira as credenciais salvas e tente novamente.");
    }

    private static void NavigateSippes(IWebDriver driver, string url, CancellationToken ct)
    {
        try { driver.SwitchTo().DefaultContent(); } catch { }
        try
        {
            driver.Navigate().GoToUrl(url);
        }
        catch (WebDriverTimeoutException)
        {
            // Mesmo quando o Chromium acusa timeout, o SIPPES frequentemente já carregou a parte útil da tela.
        }
        WaitSippesUsable(driver, ct);
        AcceptAlert(driver);
        SwitchToSippesWindow(driver);
    }

    private static void WaitSippesUsable(IWebDriver driver, CancellationToken ct)
    {
        // A ponte pode manter requisições abertas. Basta o DOM existir; as etapas seguintes
        // verificam login e selecionarFavorecido diretamente.
        WaitUntil(driver, d =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Convert.ToBoolean(((IJavaScriptExecutor)d).ExecuteScript("return !!document.body;"));
            }
            catch { }
            try { if (IsSippesLoginPage(d) || HasJsFunction(d, "selecionarFavorecido")) return true; } catch { }
            return false;
        }, TimeSpan.FromSeconds(3), ct);
    }

    private static bool IsSippesReady(IWebDriver driver)
    {
        try
        {
            SwitchToSippesWindow(driver);
            return !IsSippesLoginPage(driver) && HasJsFunction(driver, "selecionarFavorecido");
        }
        catch { return false; }
    }

    private static bool IsSippesLoginPage(IWebDriver driver)
    {
        try
        {
            SwitchToSippesWindow(driver);
            var url = SafeUrl(driver);
            if (url.Contains("formLogin.jsp", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.Contains("sippes.eb.mil.br/index.jsp", StringComparison.OrdinalIgnoreCase)) return false;
            return RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
                const txt=(document.body?.innerText||'').toLowerCase();
                const visible=el=>{ try{ const r=el.getBoundingClientRect(); const st=window.getComputedStyle(el); return r.width>0 && r.height>0 && st.visibility!=='hidden' && st.display!=='none'; }catch(e){ return true; } };
                const hasPass=Array.from(document.querySelectorAll('input[type="password"]')).some(visible);
                const hasButton=Array.from(document.querySelectorAll('input[name="botaoLogin"],input[value="OK"],button[name="botaoLogin"]')).some(visible);
                return !!(hasPass || hasButton || ((txt.includes('senha') && txt.includes('sippes')) && (hasPass || hasButton)));
                """)));
        }
        catch { return false; }
    }

    private static bool HasSippesLoginForm(IWebDriver driver)
    {
        try
        {
            SwitchToSippesWindow(driver);
            return RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
                return !!document.querySelector('input[type="password"]') || typeof logar === 'function';
                """)));
        }
        catch { return false; }
    }

    private static SippesLoginResult TrySippesLogin(IWebDriver driver, string login, string password, CancellationToken ct)
    {
        var report = string.Empty;
        try
        {
            SwitchToSippesWindow(driver);
            var executed = RunInSippesContexts(driver, () =>
            {
                report = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const usuario=arguments[0]||'', senha=arguments[1]||'';
                const norm=s=>(s||'').toString().toLowerCase();
                const visible=el=>{ try{ const r=el.getBoundingClientRect(); const st=window.getComputedStyle(el); return r.width>0 && r.height>0 && st.visibility!=='hidden' && st.display!=='none'; }catch(e){ return true; } };
                const fire=el=>{ try{el.removeAttribute('readonly');el.disabled=false;}catch(e){} try{el.dispatchEvent(new Event('input',{bubbles:true}));}catch(e){} try{el.dispatchEvent(new Event('change',{bubbles:true}));}catch(e){} try{el.dispatchEvent(new Event('blur',{bubbles:true}));}catch(e){} };
                const inputs=Array.from(document.querySelectorAll('input'));
                const pass=inputs.find(el=>norm(el.type)==='password' && !el.disabled);
                if(pass){ pass.focus(); pass.value=senha; fire(pass); }
                const users=inputs.filter(el=>{ const t=norm(el.type||'text'); if(el.disabled || t==='password' || t==='hidden' || t==='button' || t==='submit') return false; if(!['text','search','tel','number','email',''].includes(t)) return false; if(!visible(el)) return false; return true; });
                let user=null;
                for(const el of users){ const k=norm([el.name,el.id,el.className,el.placeholder,el.title,el.getAttribute?.('aria-label')].join(' ')); if(k.includes('cpf')||k.includes('usuario')||k.includes('login')||k.includes('user')||k.includes('nome')||k.includes('idt')||k.includes('ident')||k.includes('codusuario')){ user=el; break; } }
                if(!user && users.length===1) user=users[0];
                if(user && usuario){ user.focus(); user.value=usuario; fire(user); }
                const result=method=>(user?'1':'0')+'|'+(pass?'1':'0')+'|'+method;
                if(!pass) return result('nenhum');
                try{ if(typeof logar==='function'){ logar(); try{ if(typeof loading==='function') loading(); }catch(e){} return result('logar()'); } }catch(e){}
                const btn=document.querySelector('input[name="botaoLogin"],input[title*="login" i],input[value="OK"],input#botao,button[name="botaoLogin"],button[type="submit"],input[type="submit"]');
                if(btn){ try{btn.removeAttribute('disabled');}catch(e){} try{btn.focus();}catch(e){} try{btn.click(); return result('botão');}catch(e){} }
                return result('Enter');
                """, login, password), CultureInfo.InvariantCulture) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(report);
            });

            var parts = report.Split('|', 3);
            var userFound = parts.ElementAtOrDefault(0) == "1";
            var passwordFound = parts.ElementAtOrDefault(1) == "1";
            var method = parts.ElementAtOrDefault(2) ?? "nenhum";
            var submitted = executed && passwordFound && !method.Equals("nenhum", StringComparison.OrdinalIgnoreCase);

            if (submitted && method.Equals("Enter", StringComparison.OrdinalIgnoreCase))
            {
                try { driver.FindElements(By.CssSelector("input[type=password]")).FirstOrDefault()?.SendKeys(Keys.Enter); }
                catch { submitted = false; }
            }

            if (!submitted)
                return new SippesLoginResult(false, false, userFound, passwordFound, method);

            var authenticated = WaitUntil(driver,
                d => !SafeUrl(d).Contains("formLogin.jsp", StringComparison.OrdinalIgnoreCase) || !IsSippesLoginPage(d),
                TimeSpan.FromSeconds(9), ct);
            AcceptAlert(driver);
            return new SippesLoginResult(true, authenticated, userFound, passwordFound, method);
        }
        catch
        {
            // O envio do formulário pode interromper o ExecuteScript enquanto a página troca.
            // Se a tela de login já desapareceu, o envio foi aceito mesmo sem o retorno do script.
            var authenticated = !SafeUrl(driver).Contains("formLogin.jsp", StringComparison.OrdinalIgnoreCase)
                                || !IsSippesLoginPage(driver);
            return new SippesLoginResult(
                authenticated,
                authenticated,
                report.StartsWith("1|", StringComparison.Ordinal),
                report.Split('|').ElementAtOrDefault(1) == "1",
                authenticated ? "navegação durante envio" : (string.IsNullOrWhiteSpace(report) ? "erro" : report));
        }
    }

    private void LogSippesDiagnostic(string stage, IWebDriver driver, SippesLoginResult? login)
    {
        var loginPage = IsSippesLoginPage(driver);
        var functionPresent = HasJsFunction(driver, "selecionarFavorecido");
        var detail = login is { } value
            ? $"userField={value.UserFound}; passwordField={value.PasswordFound}; submit={value.Method}; authenticated={value.Authenticated}"
            : "login=not-run";
        _log.WriteAsync(
            $"SIPPES preparo: stage={stage}; url={SafeUrl(driver)}; title={SafeTitle(driver)}; loginPage={loginPage}; selecionarFavorecido={functionPresent}; bridgeReturnedToLogin={stage.Contains("ponte", StringComparison.OrdinalIgnoreCase) && loginPage}; {detail}")
            .GetAwaiter().GetResult();
    }

    private static string DownloadOneSippes(IWebDriver driver, CpexPaystubPerson person, CpexPaystubSettings settings, CancellationToken ct)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        var idt = MilitaryFormatting.Digits(person.MilitaryId);
        var prec = MilitaryFormatting.Digits(person.PrecCp);
        var code = MilitaryFormatting.Digits(settings.SheetCode);
        if (cpf.Length != 11) throw new InvalidOperationException("CPF inválido.");
        if (string.IsNullOrWhiteSpace(idt)) throw new InvalidOperationException("IDT não informada no cadastro do militar.");
        if (string.IsNullOrWhiteSpace(prec)) throw new InvalidOperationException("PREC-CP não informado no cadastro do militar.");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Código da folha do SIPPES não informado.");

        NavigateSippes(driver, SippesSelectUrl, ct);
        if (IsSippesLoginPage(driver))
            throw new InvalidOperationException("Sessão do SIPPES expirada durante o lote. O SIGFUR vai refazer o login oculto e tentar novamente.");
        if (!WaitUntil(driver, d => IsSippesReady(d), TimeSpan.FromSeconds(18), ct))
            throw new InvalidOperationException("A função selecionarFavorecido não apareceu na tela do SIPPES.");

        if (!SelectSippesBeneficiary(driver, idt, cpf, person.Name, prec))
            throw new InvalidOperationException("O SIPPES não aceitou a seleção automática do favorecido.");

        WaitUntil(driver, d =>
        {
            SwitchToSippesWindow(d);
            return HasJsFunction(d, "pesquisarContracheque") || HasJsFunction(d, "visualizarContracheque") || PageContains(d, "contracheque");
        }, TimeSpan.FromSeconds(18), ct);
        SwitchToSippesWindow(driver);
        TryPesquisarSippes(driver);
        WaitUntil(driver, d => HasJsFunction(d, "visualizarContracheque") || PageContains(d, "contracheque"), TimeSpan.FromSeconds(18), ct);

        Directory.CreateDirectory(settings.OutputDirectory);
        var baseline = EnumeratePdfFiles(settings.OutputDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var started = DateTime.UtcNow;

        if (!ExecuteVisualizarSippes(driver, idt, prec, code))
            throw new InvalidOperationException("A função visualizarContracheque ainda não ficou disponível no SIPPES.");

        var downloaded = WaitForDownloadedPdf(settings.OutputDirectory, baseline, started, TimeSpan.FromSeconds(95), ct);
        if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            throw new TimeoutException("O PDF não apareceu na pasta de downloads dentro do tempo limite.");

        var output = BuildPaystubOutputPath(settings, person);
        File.Move(downloaded, output, true);
        CleanupEquivalentPaystubFiles(settings, person, output);
        return output;
    }

    private static bool SelectSippesBeneficiary(IWebDriver driver, string idt, string cpf, string? name, string prec)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const idt=arguments[0]||'', cpf=arguments[1]||'', nome=arguments[2]||'', prec=arguments[3]||'';
            if(typeof selecionarFavorecido==='function'){
                selecionarFavorecido(idt,cpf,nome,prec,'1');
                return true;
            }
            const norm=s=>(s||'').toString().toLowerCase();
            const fire=el=>{ try{el.dispatchEvent(new Event('input',{bubbles:true}));}catch(e){} try{el.dispatchEvent(new Event('change',{bubbles:true}));}catch(e){} try{el.dispatchEvent(new Event('blur',{bubbles:true}));}catch(e){} };
            const inputs=Array.from(document.querySelectorAll('input')).filter(el=>{ const t=norm(el.type); return !t||['text','search','tel','number','hidden'].includes(t); });
            let touched=false;
            for(const el of inputs){
                const key=norm([el.name,el.id,el.className,el.placeholder,el.title].join(' '));
                if(key.includes('cpf')){el.value=cpf;fire(el);touched=true;}
                else if(key.includes('prec')){el.value=prec;fire(el);touched=true;}
                else if(key.includes('idt')||key.includes('cadastro')||key.includes('identificacao')){el.value=idt;fire(el);touched=true;}
                else if(key.includes('nome')){el.value=nome;fire(el);touched=true;}
            }
            if(!touched && inputs.length){ inputs[0].value = idt || cpf; fire(inputs[0]); touched = true; }
            return touched;
            """, idt, cpf, StripAccentsUpper(name), prec)));

    private static bool TryPesquisarSippes(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            try{ if(typeof pesquisarContracheque==='function'){ pesquisarContracheque(); return true; } }catch(e){}
            try{ if(window.parent && typeof window.parent.pesquisarContracheque==='function'){ window.parent.pesquisarContracheque(); return true; } }catch(e){}
            try{ if(window.top && typeof window.top.pesquisarContracheque==='function'){ window.top.pesquisarContracheque(); return true; } }catch(e){}
            const itens=Array.from(document.querySelectorAll('button,input[type="button"],input[type="submit"],a,*[onclick]'));
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const btn=itens.find(el=>{ const t=norm([el.id,el.name,el.value,el.innerText,el.title,el.getAttribute?.('onclick')].join(' ')); return t.includes('pesquisar')&&!t.includes('limpar'); });
            if(btn){ try{btn.removeAttribute('disabled');}catch(e){} btn.click(); return true; }
            return false;
            """)));

    private static bool ExecuteVisualizarSippes(IWebDriver driver, string idt, string prec, string code)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const idt=arguments[0], prec=arguments[1], codigo=arguments[2];
            if(typeof visualizarContracheque!=='function') return false;
            visualizarContracheque(idt,prec,codigo,'');
            return true;
            """, idt, prec, code)));

    private static bool HasJsFunction(IWebDriver driver, string name)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver)
            .ExecuteScript("return typeof window[arguments[0]] === 'function';", name)));

    private static bool RunInSippesContexts(IWebDriver driver, Func<bool> action, int maxDepth = 5)
    {
        try { driver.SwitchTo().DefaultContent(); } catch { }
        return Visit(0);

        bool Visit(int depth)
        {
            try { if (action()) return true; } catch { }
            if (depth >= maxDepth) return false;

            int frameCount;
            try { frameCount = driver.FindElements(By.CssSelector("iframe,frame")).Count; }
            catch { return false; }

            for (var index = 0; index < frameCount; index++)
            {
                try
                {
                    var frames = driver.FindElements(By.CssSelector("iframe,frame"));
                    if (index >= frames.Count) continue;
                    driver.SwitchTo().Frame(frames[index]);
                    if (Visit(depth + 1)) return true;
                }
                catch { }

                try { driver.SwitchTo().ParentFrame(); }
                catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
            }
            return false;
        }
    }

    private static bool WaitUntil(IWebDriver driver, Func<IWebDriver, bool> predicate, TimeSpan timeout, CancellationToken ct)
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
                ct.ThrowIfCancellationRequested();
                return predicate(d);
            });
        }
        catch (WebDriverTimeoutException) { return false; }
    }

    private static void SwitchToSippesWindow(IWebDriver driver)
    {
        foreach (var handle in driver.WindowHandles.Reverse())
        {
            try
            {
                driver.SwitchTo().Window(handle);
                if ((driver.Url ?? string.Empty).Contains("sippes", StringComparison.OrdinalIgnoreCase)) return;
            }
            catch { }
        }
    }

    private static IEnumerable<string> EnumeratePdfFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).ToList(); }
        catch { return []; }
    }

    private static string? WaitForDownloadedPdf(string root, HashSet<string> baseline, DateTime started, TimeSpan timeout, CancellationToken ct)
    {
        var limit = DateTime.UtcNow + timeout;
        var stable = new Dictionary<string, (long Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var path in EnumeratePdfFiles(root)
                         .Where(x => !baseline.Contains(x))
                         .OrderByDescending(SafeLastWriteUtc))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length < 2048 || info.LastWriteTimeUtc < started.AddSeconds(-2)) continue;
                    var previous = stable.GetValueOrDefault(path);
                    stable[path] = previous.Size == info.Length ? (info.Length, previous.Count + 1) : (info.Length, 1);
                    if (stable[path].Count >= 3) return path;
                }
                catch { }
            }
            if (ct.WaitHandle.WaitOne(450)) throw new OperationCanceledException(ct);
        }
        return null;
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string BuildPersonFolder(CpexPaystubSettings settings, CpexPaystubPerson person)
    {
        var root = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? "." : settings.OutputDirectory;
        var isExternal = person.SourceId <= 0 && (person.Rank.Contains("Pessoa", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(person.MilitaryId) && string.IsNullOrWhiteSpace(person.PrecCp));
        return PersonDocumentStorageService.BuildFolder(
            root, person.Rank, person.Name, person.Cpf, person.PrecCp, isExternal);
    }


    public static string BuildPaystubOutputPath(CpexPaystubSettings settings, CpexPaystubPerson person)
    {
        var folder = BuildPersonFolder(settings, person);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, BuildLegacyPaystubFileName(person, settings));
    }

    private static string BuildLegacyPaystubFileName(CpexPaystubPerson person, CpexPaystubSettings settings)
    {
        // Mesmo padrão do contracheque_manager.py antigo:
        // dentro da pasta do militar, o arquivo fica somente como "JANEIRO - 2026.pdf".
        // Isso permite baixar de novo e salvar exatamente por cima do PDF já existente.
        return BuildPaystubFileName(settings.Year, settings.Month);
    }

    private static string BuildNameBasedPaystubFileName(CpexPaystubPerson person, CpexPaystubSettings settings)
    {
        var name = CleanLegacyPersonName(person.Name);
        return $"{name} - Contracheque - {settings.Month:00}-{settings.Year}.pdf";
    }

    private static string PortugueseMonth(int month) => month switch
    {
        1 => "JANEIRO",
        2 => "FEVEREIRO",
        3 => "MARÇO",
        4 => "ABRIL",
        5 => "MAIO",
        6 => "JUNHO",
        7 => "JULHO",
        8 => "AGOSTO",
        9 => "SETEMBRO",
        10 => "OUTUBRO",
        11 => "NOVEMBRO",
        12 => "DEZEMBRO",
        _ => Math.Clamp(month, 1, 12).ToString("00")
    };

    private static void CleanupEquivalentPaystubFiles(CpexPaystubSettings settings, CpexPaystubPerson person, string keepPath)
    {
        try
        {
            var folder = BuildPersonFolder(settings, person);
            if (!Directory.Exists(folder)) return;

            var cpf = MilitaryFormatting.Digits(person.Cpf);
            var wrongNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                BuildNameBasedPaystubFileName(person, settings),
                $"Contracheque_{settings.Year}_{settings.Month:00}_{cpf}_{FolderToken(person.Name, upper: true)}.pdf",
                $"Contracheque - {CleanLegacyPersonName(person.Name)} - CPF {cpf} - {settings.Year}-{settings.Month:00}.pdf"
            };

            foreach (var candidate in wrongNames.Select(name => Path.Combine(folder, name)))
            {
                if (!File.Exists(candidate) || SamePath(candidate, keepPath)) continue;
                try { File.Delete(candidate); } catch { }
            }
        }
        catch { }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string BuildFinancialStatementOutputPath(CpexPaystubSettings settings, CpexPaystubPerson person, int statementYear)
    {
        var folder = BuildFinancialStatementFolder(settings, person);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, BuildFinancialStatementFileName(person, statementYear));
    }

    public static string BuildFinancialStatementFolder(CpexPaystubSettings settings, CpexPaystubPerson person)
    {
        var folder = BuildPersonFolder(settings, person);
        return Path.Combine(folder, "Ficha Financeira");
    }

    public static string BuildFinancialStatementFileName(CpexPaystubPerson person, int statementYear)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        var name = CleanLegacyPersonName(person.Name);
        var prefix = string.IsNullOrWhiteSpace(cpf) ? name : $"{cpf} - {name}";
        return $"{prefix} - Ficha Financeira - {statementYear}.pdf";
    }

    private static string CleanLegacyPersonName(string? value)
    {
        var name = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Militar";

        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(clean) ? "Militar" : clean;
    }

    private static string FolderToken(string? value, bool upper)
    {
        var text = (value ?? string.Empty)
            .Replace('º', ' ')
            .Replace('°', ' ')
            .Replace('ª', ' ')
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        var cleaned = Regex.Replace(builder.ToString(), "_+", "_").Trim('_');
        if (upper) return cleaned.ToUpperInvariant();
        var parts = cleaned.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('_', parts.Select(part => part.Length <= 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string StripAccentsUpper(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant()
            .Trim();
    }

    private static void PrintCurrentPage(IWebDriver driver, string output)
    {
        if (driver is not ChromiumDriver chromium)
            throw new InvalidOperationException("A geração automática do PDF exige Edge ou Chrome.");
        var result = chromium.ExecuteCdpCommand("Page.printToPDF", new Dictionary<string, object?>
        {
            ["printBackground"] = true,
            ["landscape"] = false,
            ["preferCSSPageSize"] = true,
            ["scale"] = 1.0
        });

        // Selenium 4.44 retorna o resultado do CDP como object. Serializar para JSON
        // evita depender do tipo concreto usado internamente pelo driver.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var dataElement))
            throw new InvalidOperationException("O contracheque abriu, mas o navegador não retornou o PDF.");

        var base64 = dataElement.GetString();
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("O contracheque abriu, mas o navegador retornou um PDF vazio.");

        File.WriteAllBytes(output, Convert.FromBase64String(base64));
    }


    private static void PrintFinancialStatementPage(IWebDriver driver, string output)
    {
        if (driver is not ChromiumDriver chromium)
            throw new InvalidOperationException("A geração automática da Ficha Financeira exige Edge ou Chrome.");

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                (function(){
                    const id = 'sigfur-ficha-financeira-print-css';
                    let st = document.getElementById(id);
                    if (!st) { st = document.createElement('style'); st.id = id; document.head.appendChild(st); }
                    st.textContent = `
                        @media print {
                            html, body { background: #fff !important; overflow: visible !important; }
                            * { -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; }
                            button, input[type="button"], input[type="submit"], input[type="reset"], .no-print, .noprint, .navbar, .menu, .toolbar, .sidebar { display: none !important; }
                            table { border-collapse: collapse !important; page-break-inside: avoid !important; width: 100% !important; }
                            th, td { font-size: 9px !important; padding: 2px 3px !important; }
                            body { font-family: Arial, sans-serif !important; font-size: 9px !important; }
                        }`;
                })();
                """);
        }
        catch { }

        var result = chromium.ExecuteCdpCommand("Page.printToPDF", new Dictionary<string, object?>
        {
            ["printBackground"] = true,
            ["landscape"] = true,
            ["preferCSSPageSize"] = false,
            ["paperWidth"] = 11.69,
            ["paperHeight"] = 8.27,
            ["marginTop"] = 0.18,
            ["marginBottom"] = 0.18,
            ["marginLeft"] = 0.18,
            ["marginRight"] = 0.18,
            ["scale"] = 0.85
        });

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var dataElement))
            throw new InvalidOperationException("A Ficha Financeira abriu, mas o navegador não retornou o PDF.");

        var base64 = dataElement.GetString();
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("A Ficha Financeira abriu, mas o navegador retornou um PDF vazio.");

        File.WriteAllBytes(output, Convert.FromBase64String(base64));
    }

    private static void FillCpf(IWebDriver driver, string cpf)
    {
        DisableAutofill(driver);
        var fields = driver.FindElements(By.CssSelector("input"));
        var candidates = fields.Where(x =>
        {
            var type = (x.GetAttribute("type") ?? "text").ToLowerInvariant();
            return (type is "text" or "tel" or "number" or "search" or "hidden" or "") &&
                   ContainsAny(Signature(x), ["cpf", "identificacao favorecido", "identificação favorecido"]);
        }).ToList();
        if (candidates.Count == 0)
            candidates = fields.Where(x => IsDisplayed(x) && !IsButton(x) && !EqualsIgnoreCase(x.GetAttribute("type"), "password")).Take(1).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("Não encontrei o campo CPF da consulta do contracheque.");

        foreach (var field in fields)
        {
            if (candidates.Contains(field)) continue;
            var signature = Signature(field);
            if (ContainsAny(signature, ["nome", "prec", "favorecido"])) SetValue(driver, field, string.Empty);
        }
        foreach (var field in candidates) SetValue(driver, field, cpf);
    }

    private static void SelectBest(IReadOnlyList<IWebElement> selects, params string[] targets)
    {
        var targetNorm = targets.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).ToList();
        foreach (var element in selects)
        {
            try
            {
                var select = new SelectElement(element);
                for (var index = 0; index < select.Options.Count; index++)
                {
                    var option = select.Options[index];
                    var text = Normalize(option.Text);
                    var value = Normalize(option.GetAttribute("value"));
                    if (targetNorm.Any(t => t == text || t == value || text.Contains(t) || (t.Length > 1 && value.Contains(t))))
                    {
                        select.SelectByIndex(index);
                        return;
                    }
                }
            }
            catch { }
        }
    }

    private static void WaitReady(IWebDriver driver, CancellationToken ct)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(35));
        wait.Until(d =>
        {
            ct.ThrowIfCancellationRequested();
            try { return string.Equals(((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString(), "complete", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        WaitForDomIdle(driver, TimeSpan.FromSeconds(2), ct);
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

    private static string SafeUrl(IWebDriver driver)
    {
        try { return driver.Url ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeTitle(IWebDriver driver)
    {
        try { return driver.Title ?? string.Empty; }
        catch { return string.Empty; }
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

    private static bool PageContains(IWebDriver driver, string text)
    {
        try { return Normalize(driver.FindElement(By.TagName("body")).Text).Contains(Normalize(text), StringComparison.Ordinal); }
        catch { return false; }
    }

    private static void SetValue(IWebDriver driver, IWebElement element, string value)
    {
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                const el=arguments[0], v=arguments[1];
                try{el.removeAttribute('readonly');el.disabled=false;el.setAttribute('autocomplete','off');}catch(e){}
                try{el.focus();el.value='';el.dispatchEvent(new Event('input',{bubbles:true}));
                    el.value=v;el.dispatchEvent(new Event('input',{bubbles:true}));
                    el.dispatchEvent(new Event('change',{bubbles:true}));el.blur();}catch(e){}
                """, element, value);
        }
        catch
        {
            try { element.Clear(); element.SendKeys(value); } catch { }
        }
    }

    private static void DisableAutofill(IWebDriver driver)
    {
        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                document.querySelectorAll('form,input,textarea,select').forEach(el=>{
                  try{el.setAttribute('autocomplete','off');el.setAttribute('data-lpignore','true');el.setAttribute('spellcheck','false');}catch(e){}
                });
                """);
        }
        catch { }
    }

    private static bool IsDisplayed(IWebElement x) { try { return x.Displayed; } catch { return false; } }
    private static bool IsButton(IWebElement x)
    {
        var type = (x.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
        return type is "submit" or "button" or "image" or "reset";
    }
    private static string Signature(IWebElement x)
        => Normalize(string.Join(' ', new[] { x.GetAttribute("id"), x.GetAttribute("name"), x.GetAttribute("title"), x.GetAttribute("placeholder"), x.GetAttribute("aria-label"), x.GetAttribute("class") }));
    private static string Context(IWebElement x)
    {
        try { return Normalize(string.Join(' ', x.Text, x.GetAttribute("value"), x.GetAttribute("title"), x.GetAttribute("aria-label"))); }
        catch { return string.Empty; }
    }
    private static void AcceptAlert(IWebDriver driver) { try { driver.SwitchTo().Alert().Accept(); } catch { } }
    private static bool EqualsIgnoreCase(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool ContainsAny(string text, IEnumerable<string> values) => values.Any(x => text.Contains(Normalize(x), StringComparison.Ordinal));
    private static string NormalizeBrowser(string? value) => string.Equals(value, "Chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome" : "Edge";
    private static string NormalizeSystem(string? value)
        => (value ?? string.Empty).Contains("SIAPPES", StringComparison.OrdinalIgnoreCase) ? "SIAPPES" : "SIPPES";
    private static int CalculateSheetCode(int year, int month)
    {
        var deltaMonths = (year - 2026) * 12 + (month - 4);
        return 4178 + deltaMonths * 20;
    }
    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant().Replace('º', ' ').Replace('°', ' ');
    }
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.Join('_', clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('_');
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    private const int SwHide = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpHideWindow = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

