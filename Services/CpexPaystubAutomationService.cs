using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using UglyToad.PdfPig;
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
public sealed class CpexPaystubAutomationService : IDisposable
{
    private const string FinancialStatementSessionSystem = "FICHA";
    private const int ModernFinancialStatementStartYear = 2021;
    public const string LoginUrl = "https://cpex-intranet.eb.mil.br/asplogon_nova.asp?url=area_ua_cpex/index.asp";
    public const string QueryUrl = "https://cpex-intranet.eb.mil.br/cc_sippes/consulta.asp";
    public const string FinancialStatementUrl = "https://cpex-intranet.eb.mil.br/ff_sippes/consulta.asp";
    public const string LegacyFinancialStatementUrl = "https://cpex-intranet.eb.mil.br/ficha_financeira.asp";
    public const string SippesLoginUrl = "https://sippes.eb.mil.br/jsp/login/formLogin.jsp";
    public const string SippesBaseUrl = "https://sippes.eb.mil.br/consultarContracheque.do?metodo=exibirTelaConsultar&limparSessao=true";
    public const string SippesSelectUrl = "https://sippes.eb.mil.br/consultarContracheque.do?metodo=exibirTelaSelecionarFavorecidoCC&paginaDestino=consultarRelatorio&acaoPai=consultarContracheque&formularioPai=formularioConsultarContracheque&camposDestino=identificacaoFavorecido-cpfFavorecido-nomeFavorecido-precCpFavorecido&abrangenciaOm=true";
    public const string SippesActiveDataUrl = "https://sippes.eb.mil.br/gerarRelatorioDadosMA.do?metodo=exibirTelaConsultar&limparSessao=true";
    public const string SippesActiveQueryUrl = "https://sippes.eb.mil.br/gerarRelatorioDadosMA.do?metodo=consultarMilitares&consultaPaginada=true";
    public const string SippesMirrorBaseUrl = "https://sippes.eb.mil.br/relatorioEspelhoContracheque.do?metodo=exibirTelaConsulta&limparSessao=true";
    public const string SippesMirrorFolhasUrl = "https://sippes.eb.mil.br/relatorioEspelhoContracheque.do?metodo=exibirTelaConsultarFolhasProcessadas&paginaDestino=consultar&acaoPai=relatorioEspelhoContracheque&formularioPai=formularioRelatorioEspelho&camposDestino=codigoFolhaPagamentoFiltro-descricaoFolhaPagamentoFiltro";


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
        settings.OutputDirectory = PersonDocumentStorageService.EnsureWritableRoot(_paths, settings.OutputDirectory);
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
        settings.OutputDirectory = PersonDocumentStorageService.EnsureWritableRoot(_paths, settings.OutputDirectory);
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

    public async Task<List<SippesPersonnelRow>> ReadSippesActivePersonnelAsync(
        IProgress<CpexPaystubProgress>? progress = null,
        bool downloadReports = false,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync();
        settings.System = "SIPPES";
        settings.Browser = NormalizeBrowser(settings.Browser);
        // O SIPPES antigo falha no headless real. O SIGFUR usa janela normal e a mantém escondida/minimizada por Win32.
        settings.Headless = false;
        // Este caminho é temporário desta conferência. Não pode passar pelo SaveSettingsAsync,
        // porque as configurações de contracheque normal sempre normalizam OutputDirectory
        // para a pasta oficial de contracheques. Se passasse, os PDFs Dados MA cairiam na
        // pasta errada e o módulo pareceria "não salvar".
        var dadosMaDownloadDirectory = GetSippesDadosMaDownloadDirectory();
        settings.OutputDirectory = dadosMaDownloadDirectory;
        if (string.IsNullOrWhiteSpace(settings.SheetCode))
            settings.SheetCode = CalculateSheetCode(settings.Year, settings.Month).ToString(CultureInfo.InvariantCulture);

        var password = ReadSavedPassword(settings);
        if (string.IsNullOrWhiteSpace(settings.Login))
            throw new InvalidOperationException("Informe e salve o usuário do SIPPES antes de conferir o efetivo.");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Informe e salve a senha do SIPPES antes de conferir o efetivo.");

        await SaveSettingsAsync(settings);
        // SaveSettingsAsync preserva as credenciais e normaliza a pasta de contracheques.
        // Para Dados MA, usamos novamente a pasta específica do perfil ativo.
        settings.OutputDirectory = dadosMaDownloadDirectory;
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var driver = EnsurePreparedSessionUnsafe(settings, password, progress, cancellationToken);
                HideAutomationWindows(driver);

                progress?.Report(new CpexPaystubProgress { Message = "1/5 Abrindo Dados de Militar da Ativa no SIPPES em segundo plano..." });
                NavigateSippes(driver, SippesActiveDataUrl, cancellationToken);
                HideAutomationWindows(driver);
                if (IsSippesLoginPage(driver))
                {
                    progress?.Report(new CpexPaystubProgress { Message = "Sessão voltou ao login. Reautenticando SIPPES..." });
                    PrepareSippesSession(driver, settings.Login, password, cancellationToken, progress);
                    HideAutomationWindows(driver);
                    NavigateSippes(driver, SippesActiveDataUrl, cancellationToken);
                    HideAutomationWindows(driver);
                }

                // O loading visual do SIPPES às vezes fica infinito mesmo com a tabela pronta.
                // Por isso a regra agora é: espera a página/tabela ficar legível, não o spinner sumir para sempre.
                WaitUntil(driver, d => PageContains(d, "CONSULTAR RELATÓRIO DE DADOS DE MILITAR DA ATIVA") || HasSippesActiveSearchButton(d) || HasSippesActiveRows(d), TimeSpan.FromMinutes(3), cancellationToken);

                progress?.Report(new CpexPaystubProgress { Message = "2/5 Abrindo a consulta paginada direta, sem alterar Exibidos..." });
                NavigateSippes(driver, SippesActiveQueryUrl, cancellationToken);
                HideAutomationWindows(driver);
                if (!WaitForSippesActiveRowsReady(driver, TimeSpan.FromMinutes(3), cancellationToken, progress, "Aguardando a primeira página da tabela"))
                {
                    var diagnosticPath = SaveSippesActiveDiagnostics(driver, 1);
                    throw new InvalidOperationException(
                        "O SIPPES abriu a consulta, mas a tabela de militares não ficou legível para automação dentro do tempo limite. " +
                        (string.IsNullOrWhiteSpace(diagnosticPath) ? string.Empty : $"Diagnóstico salvo em: {diagnosticPath}"));
                }

                var total = ReadSippesActiveTotal(driver);
                var visiblePages = ReadSippesActivePageCount(driver);
                var firstRows = ExtractSippesActiveRows(driver);
                if (firstRows.Count == 0)
                {
                    var diagnosticPath = SaveSippesActiveDiagnostics(driver, 1);
                    throw new InvalidOperationException(
                        "A tabela apareceu, mas o SIGFUR não conseguiu transformar a primeira página em nomes. " +
                        (string.IsNullOrWhiteSpace(diagnosticPath) ? string.Empty : $"Diagnóstico salvo em: {diagnosticPath}"));
                }

                var pages = visiblePages > 0
                    ? visiblePages
                    : total > 0 && firstRows.Count > 0
                        ? Math.Max(1, (int)Math.Ceiling(total / (double)firstRows.Count))
                        : 1;
                pages = Math.Clamp(pages, 1, 120);

                var all = new Dictionary<string, SippesPersonnelRow>(StringComparer.OrdinalIgnoreCase);
                AddSippesRows(all, firstRows);
                progress?.Report(new CpexPaystubProgress { Current = 1, Total = pages, Message = $"3/5 Página 1/{pages} lida ({all.Count} registro(s))." });

                for (var pageIndex = 1; pageIndex < pages; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new CpexPaystubProgress { Current = pageIndex + 1, Total = pages, Message = $"Lendo página {pageIndex + 1}/{pages} do SIPPES..." });
                    NavigateSippes(driver, SippesActiveQueryPageUrl(pageIndex), cancellationToken);
                    HideAutomationWindows(driver);
                    if (!WaitForSippesActiveRowsReady(driver, TimeSpan.FromMinutes(3), cancellationToken, progress, $"Aguardando página {pageIndex + 1}/{pages}"))
                    {
                        var diagnosticPath = SaveSippesActiveDiagnostics(driver, pageIndex + 1);
                        throw new InvalidOperationException($"A página {pageIndex + 1} do SIPPES não ficou legível. " +
                                                            (string.IsNullOrWhiteSpace(diagnosticPath) ? string.Empty : $"Diagnóstico salvo em: {diagnosticPath}"));
                    }

                    var rows = ExtractSippesActiveRows(driver);
                    if (rows.Count == 0)
                    {
                        var diagnosticPath = SaveSippesActiveDiagnostics(driver, pageIndex + 1);
                        throw new InvalidOperationException($"A página {pageIndex + 1} apareceu, mas nenhuma linha foi lida. " +
                                                            (string.IsNullOrWhiteSpace(diagnosticPath) ? string.Empty : $"Diagnóstico salvo em: {diagnosticPath}"));
                    }
                    AddSippesRows(all, rows);
                    HideAutomationWindows(driver);
                }

                var result = all.Values
                    .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
                    .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (downloadReports && result.Count > 0)
                {
                    progress?.Report(new CpexPaystubProgress { Message = "4/5 Baixando PDFs de Dados de Militar da Ativa e lendo situação de pagamento..." });
                    DownloadSippesActiveDataReports(driver, result, settings.OutputDirectory, progress, cancellationToken);
                    HideAutomationWindows(driver);
                }

                progress?.Report(new CpexPaystubProgress
                {
                    Message = downloadReports
                        ? $"SIPPES lido: {result.Count} registro(s). PDFs analisados: {result.Count(x => !string.IsNullOrWhiteSpace(x.ReportPdfPath))}."
                        : $"SIPPES lido: {result.Count} registro(s) localizado(s)."
                });
                HideAutomationWindows(driver);
                return result;
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }


    public async Task<SippesOmPaystubMirrorResult> GenerateSippesOmPaystubMirrorAsync(
        int year,
        int month,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        year = year is < 2000 or > 2200 ? DateTime.Today.Year : year;
        month = Math.Clamp(month, 1, 12);

        var settings = await LoadSettingsAsync();
        settings.System = "SIPPES";
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.Headless = false;
        var mirrorDirectory = GetSippesMirrorDownloadDirectory(year, month);
        settings.OutputDirectory = mirrorDirectory;

        var password = ReadSavedPassword(settings);
        if (string.IsNullOrWhiteSpace(settings.Login))
            throw new InvalidOperationException("Informe e salve o usuário do SIPPES antes de gerar o Espelho de Contracheque da OM.");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Informe e salve a senha do SIPPES antes de gerar o Espelho de Contracheque da OM.");

        await SaveSettingsAsync(settings);
        settings.OutputDirectory = mirrorDirectory;

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var driver = EnsurePreparedSessionUnsafe(settings, password, progress, cancellationToken);
                ShowAutomationWindows(driver);
                progress?.Report(new CpexPaystubProgress { Message = "Navegador SIPPES visível. Acompanhe a tela enquanto o site carrega; o SIGFUR não vai esconder essa automação." });

                progress?.Report(new CpexPaystubProgress { Message = "1/7 Abrindo Espelho de Contracheque da OM no SIPPES..." });
                NavigateSippes(driver, SippesMirrorBaseUrl, cancellationToken);
                ShowAutomationWindows(driver);
                if (IsSippesLoginPage(driver))
                {
                    progress?.Report(new CpexPaystubProgress { Message = "Sessão voltou ao login. Reautenticando SIPPES..." });
                    PrepareSippesSession(driver, settings.Login, password, cancellationToken, progress);
                    NavigateSippes(driver, SippesMirrorBaseUrl, cancellationToken);
                    ShowAutomationWindows(driver);
                }

                progress?.Report(new CpexPaystubProgress { Message = "2/7 Entrando primeiro no menu Espelho da OM e depois na tela exata de Pesquisar Folha..." });
                NavigateSippes(driver, SippesMirrorBaseUrl, cancellationToken);
                ShowAutomationWindows(driver);
                WaitUntil(driver, d => !IsSippesLoginPage(d) && (PageContains(d, "ESPELHO DE CONTRACHEQUE") || SafeUrl(d).Contains("relatorioEspelhoContracheque", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromMinutes(2), cancellationToken);

                NavigateSippes(driver, SippesMirrorFolhasUrl, cancellationToken);
                ShowAutomationWindows(driver);
                if (!WaitUntil(driver, d => PageContains(d, "PESQUISAR FOLHA") || HasSippesMirrorSearchControls(d), TimeSpan.FromMinutes(2), cancellationToken))
                    throw new InvalidOperationException("O SIPPES abriu, mas não mostrou a tela PESQUISAR FOLHA com Ano/Mês.");

                progress?.Report(new CpexPaystubProgress { Message = $"3/7 Preenchendo Ano={year} e Mês={PortugueseMonth(month)} diretamente nos campos da tela PESQUISAR FOLHA..." });
                if (!FillSippesMirrorFolhaSearch(driver, year, month))
                {
                    var diagnostic = SaveSippesMirrorDiagnostics(driver, year, month, "nao_preencheu_ano_mes");
                    throw new InvalidOperationException("Não consegui preencher Ano/Mês na tela de folhas processadas do SIPPES. " +
                                                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"Diagnóstico salvo em: {diagnostic}"));
                }
                progress?.Report(new CpexPaystubProgress { Message = $"Ano/Mês preenchidos. Agora clicando no primeiro Pesquisar da tela PESQUISAR FOLHA..." });
                if (!ClickSippesPesquisar(driver) && !SubmitSippesFolhaPagamentoWithReference(driver, year, month))
                    throw new InvalidOperationException("Não consegui clicar/enviar o primeiro Pesquisar da tela de folhas processadas.");
                if (!WaitSippesMirrorFolhaResult(driver, year, month, TimeSpan.FromMinutes(5), cancellationToken, progress))
                {
                    var diagnostic = SaveSippesMirrorDiagnostics(driver, year, month, "folha_processada_nao_apareceu");
                    throw new InvalidOperationException("Cliquei em Pesquisar, mas a folha do mês/ano informado não apareceu na lista do SIPPES. " +
                                                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"Diagnóstico salvo em: {diagnostic}"));
                }

                progress?.Report(new CpexPaystubProgress { Message = $"4/7 Clicando no ano {year} da folha Normal {PortugueseMonth(month)}/{year}..." });
                if (!SelectSippesMirrorFolhaResult(driver, year, month))
                    throw new InvalidOperationException("A folha do mês/ano informado apareceu, mas não consegui clicar no ano para selecioná-la.");

                progress?.Report(new CpexPaystubProgress { Message = "Aguardando o SIPPES sair da tela PESQUISAR FOLHA e abrir a tela CONSULTAR ESPELHO..." });
                if (!WaitUntil(driver, IsSippesMirrorDetailFilterPage, TimeSpan.FromMinutes(3), cancellationToken))
                {
                    var diagnostic = SaveSippesMirrorDiagnostics(driver, year, month, "nao_abriu_tela_consultar_espelho_apos_selecionar_folha");
                    throw new InvalidOperationException("Cliquei na folha, mas o SIPPES não abriu a tela CONSULTAR ESPELHO DE CONTRACHEQUE DA OM. " +
                                                        "Parei aqui de propósito para não clicar em Pesquisar de novo na tela errada e resetar a consulta. " +
                                                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"Diagnóstico salvo em: {diagnostic}"));
                }

                progress?.Report(new CpexPaystubProgress { Message = "5/7 Tela CONSULTAR ESPELHO aberta. Agora vou acionar o botão Pesquisar de baixo, sem tocar na lupa da Folha de Pagamento..." });
                if (!IsSippesMirrorDetailFilterPage(driver))
                    throw new InvalidOperationException("O SIGFUR ainda não está na tela CONSULTAR ESPELHO DE CONTRACHEQUE DA OM. Não vou clicar em Pesquisar para não voltar para a seleção de mês/ano.");

                // O SIPPES pode começar a carregar imediatamente após o clique e derrubar o contexto do Selenium.
                // Se isso acontecer, não exibimos erro prematuro: consideramos que o carregamento começou e passamos a aguardar a lista.
                var secondSearchTriggered = ClickSippesMirrorDetailPesquisar(driver);
                if (!secondSearchTriggered)
                {
                    if (IsSippesPageBusyOrNavigating(driver) || !IsSippesMirrorDetailFilterPage(driver))
                    {
                        progress?.Report(new CpexPaystubProgress { Message = "O Pesquisar de baixo parece ter iniciado o carregamento do SIPPES. Vou aguardar a lista em vez de abrir erro agora." });
                    }
                    else if (!SubmitSippesCurrentForm(driver, "consultarContracheque"))
                    {
                        throw new InvalidOperationException("Não consegui clicar/enviar o Pesquisar de baixo na tela de filtro do Espelho da OM.");
                    }
                }

                var mirrorListLoaded = WaitForSippesMirrorSelectionRowsReady(driver, TimeSpan.FromMinutes(1), cancellationToken, progress, "Aguardando a lista do espelho carregar após o segundo Pesquisar");
                if (!mirrorListLoaded && IsSippesMirrorDetailOrSelectionPage(driver))
                {
                    progress?.Report(new CpexPaystubProgress { Message = "A consulta do SIPPES passou de 1 minuto sem estabilizar. Vou dar F5 automático e, depois do F5, clicar novamente no Pesquisar de baixo para o SIPPES trazer a lista completa com o seletor de Exibidos." });
                    if (!RefreshSippesMirrorAndPesquisarAgain(driver, cancellationToken, progress, "Primeiro F5 automático"))
                    {
                        progress?.Report(new CpexPaystubProgress { Message = "O F5 automático foi aplicado, mas não consegui confirmar o novo Pesquisar. Mesmo assim vou aguardar se a lista estabiliza." });
                    }
                    mirrorListLoaded = WaitForSippesMirrorSelectionRowsReady(driver, TimeSpan.FromMinutes(6), cancellationToken, progress, "Após F5 + novo Pesquisar: aguardando a lista completa do espelho carregar");
                }

                if (!mirrorListLoaded && IsSippesMirrorDetailOrSelectionPage(driver))
                {
                    if (IsSippesPageBusyOrNavigating(driver))
                    {
                        progress?.Report(new CpexPaystubProgress { Message = "O SIPPES ainda parece preso no carregamento. Vou interromper esse carregamento, aplicar outro F5 e clicar novamente no Pesquisar de baixo." });
                        TryStopSippesLoading(driver);
                    }

                    progress?.Report(new CpexPaystubProgress { Message = "Segunda tentativa: F5 + Pesquisar de baixo novamente, sem usar a lupa e sem voltar para mês/ano." });
                    RefreshSippesMirrorAndPesquisarAgain(driver, cancellationToken, progress, "Segundo F5 automático");
                    mirrorListLoaded = WaitForSippesMirrorSelectionRowsReady(driver, TimeSpan.FromMinutes(6), cancellationToken, progress, "Após segundo F5 + novo Pesquisar: aguardando a lista completa do espelho carregar");
                }

                if (!mirrorListLoaded)
                {
                    var diagnostic = SaveSippesMirrorDiagnostics(driver, year, month, "lista_espelho_nao_carregou");
                    throw new InvalidOperationException("Cliquei em Pesquisar na tela do Espelho da OM, mas a lista de militares não carregou no tempo esperado. " +
                                                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"Diagnóstico salvo em: {diagnostic}"));
                }

                progress?.Report(new CpexPaystubProgress { Message = "6/7 Colocando o máximo de registros por página para evitar passar página por página..." });
                TryMaximizeSippesMirrorPageSize(driver);
                WaitForSippesMirrorSelectionRowsReady(driver, TimeSpan.FromMinutes(8), cancellationToken, progress, "Aguardando a lista em página máxima estabilizar");

                var visibleCount = CountSippesMirrorSelectionRows(driver);
                if (visibleCount <= 0)
                    throw new InvalidOperationException("O SIPPES carregou a consulta, mas nenhum militar ficou selecionável para gerar relatório.");

                progress?.Report(new CpexPaystubProgress { Message = $"7/7 Marcando {visibleCount} registro(s) e gerando o relatório completo..." });
                if (!MarkAllSippesMirrorRows(driver))
                    throw new InvalidOperationException("Não consegui marcar todos os militares na tela do Espelho da OM.");
                if (!ClickSippesMirrorGenerateReport(driver))
                    throw new InvalidOperationException("Não consegui clicar em Gerar/Consultar relatório no Espelho da OM.");

                if (!WaitForSippesMirrorReportReady(driver, TimeSpan.FromMinutes(8), cancellationToken, progress))
                {
                    var diagnostic = SaveSippesMirrorDiagnostics(driver, year, month, "relatorio_nao_carregou");
                    throw new InvalidOperationException("O SIPPES iniciou o relatório, mas a página detalhada não ficou legível no tempo limite. " +
                                                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"Diagnóstico salvo em: {diagnostic}"));
                }

                Directory.CreateDirectory(mirrorDirectory);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var htmlPath = Path.Combine(mirrorDirectory, $"Espelho_Contracheque_OM_{year}_{month:00}_{stamp}.html");
                var textPath = Path.ChangeExtension(htmlPath, ".txt");
                var html = ReadCurrentHtml(driver);
                var text = ReadCurrentBodyText(driver);
                File.WriteAllText(htmlPath, html, Encoding.UTF8);
                File.WriteAllText(textPath, text, Encoding.UTF8);

                var people = ExtractSippesMirrorPeopleFromHtmlText(html, text);
                if (people.Count == 0)
                {
                    var diagnostic = SaveSippesMirrorDiagnostics(driver, year, month, "sem_pessoas_parseadas");
                    throw new InvalidOperationException("O relatório foi gerado, mas o SIGFUR não conseguiu separar os nomes/CPFs do espelho. " +
                                                        $"HTML salvo em: {htmlPath}. " +
                                                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"Diagnóstico extra: {diagnostic}"));
                }

                progress?.Report(new CpexPaystubProgress { Message = $"Espelho OM gerado: {people.Count} pessoa(s). Arquivo salvo em {htmlPath}" });
                ShowAutomationWindows(driver);
                progress?.Report(new CpexPaystubProgress { Message = "Concluído. Mantive o navegador aberto/visível para conferência manual da tela do SIPPES." });
                return new SippesOmPaystubMirrorResult
                {
                    Year = year,
                    Month = month,
                    GeneratedAt = DateTime.Now,
                    HtmlPath = htmlPath,
                    TextPath = textPath,
                    People = people
                };
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public Task<SippesOmPaystubMirrorResult> ReadCurrentVisibleSippesOmPaystubMirrorAsync(
        int year,
        int month,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        year = year is < 2000 or > 2200 ? DateTime.Today.Year : year;
        month = Math.Clamp(month, 1, 12);
        return Task.Run(() =>
        {
            var driver = _preparedDriver ?? throw new InvalidOperationException("Não há navegador SIPPES preparado para ler a tela aberta.");
            ShowAutomationWindows(driver);
            progress?.Report(new CpexPaystubProgress { Message = "Lendo o relatório de Espelho da OM que já está aberto no navegador..." });
            if (!WaitForSippesMirrorReportReady(driver, TimeSpan.FromSeconds(20), cancellationToken, progress))
                throw new InvalidOperationException("A tela aberta não parece ser o relatório detalhado do Espelho de Contracheque da OM.");

            var mirrorDirectory = GetSippesMirrorDownloadDirectory(year, month);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var htmlPath = Path.Combine(mirrorDirectory, $"Espelho_Contracheque_OM_{year}_{month:00}_{stamp}_lido_aberto.html");
            var textPath = Path.ChangeExtension(htmlPath, ".txt");
            var html = ReadCurrentHtml(driver);
            var text = ReadCurrentBodyText(driver);
            Directory.CreateDirectory(mirrorDirectory);
            File.WriteAllText(htmlPath, html, Encoding.UTF8);
            File.WriteAllText(textPath, text, Encoding.UTF8);
            var people = ExtractSippesMirrorPeopleFromHtmlText(html, text);
            if (people.Count == 0)
                throw new InvalidOperationException("A tela aberta foi lida, mas nenhum favorecido foi identificado no Espelho da OM.");
            return new SippesOmPaystubMirrorResult
            {
                Year = year,
                Month = month,
                GeneratedAt = DateTime.Now,
                HtmlPath = htmlPath,
                TextPath = textPath,
                People = people
            };
        }, cancellationToken);
    }


    public async Task<SippesOmPaystubMirrorResult> ReadSavedSippesOmPaystubMirrorFileAsync(
        string htmlPath,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath))
            throw new FileNotFoundException("Arquivo HTML salvo do Espelho da OM não localizado.", htmlPath);

        year = year is < 2000 or > 2200 ? DateTime.Today.Year : year;
        month = Math.Clamp(month, 1, 12);
        var html = await File.ReadAllTextAsync(htmlPath, Encoding.UTF8, cancellationToken);
        var textPath = Path.ChangeExtension(htmlPath, ".txt");
        var text = File.Exists(textPath)
            ? await File.ReadAllTextAsync(textPath, Encoding.UTF8, cancellationToken)
            : HtmlToLooseText(html);
        var people = ExtractSippesMirrorPeopleFromHtmlText(html, text);
        if (people.Count == 0)
            throw new InvalidOperationException("O arquivo salvo do Espelho da OM foi lido, mas nenhum favorecido foi identificado.");

        return new SippesOmPaystubMirrorResult
        {
            Year = year,
            Month = month,
            GeneratedAt = File.GetLastWriteTime(htmlPath),
            HtmlPath = htmlPath,
            TextPath = textPath,
            People = people
        };
    }

    public async Task<List<SippesPersonnelRow>> ReadCurrentVisibleSippesActivePersonnelAsync(
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var driver = _preparedDriver ?? throw new InvalidOperationException("Não há navegador SIPPES preparado. Clique em 'Preparar sessão oculta' ou execute a conferência primeiro.");
                ShowAutomationWindows(driver);
                progress?.Report(new CpexPaystubProgress { Message = "Lendo a tabela que já está aberta no navegador visível..." });
                WaitSippesLoadingToFinish(driver, TimeSpan.FromMinutes(3), cancellationToken, progress, "Aguardando o loading da tela aberta terminar");
                if (!WaitUntil(driver, HasSippesActiveRows, TimeSpan.FromSeconds(30), cancellationToken))
                    throw new InvalidOperationException("A tela aberta não parece estar na tabela de Dados de Militar da Ativa do SIPPES.");

                var rows = ExtractSippesActiveRows(driver);
                if (rows.Count == 0)
                {
                    var diagnosticPath = SaveSippesActiveDiagnostics(driver, 1);
                    throw new InvalidOperationException("A tela aberta tem tabela, mas o SIGFUR não conseguiu transformar as linhas em nomes. " +
                                                        (string.IsNullOrWhiteSpace(diagnosticPath) ? string.Empty : $"Diagnóstico salvo em: {diagnosticPath}"));
                }

                return rows
                    .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
                    .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<List<SippesPersonnelRow>> ReadSavedSippesActiveDataReportsAsync(
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var files = EnumerateSavedSippesDadosMaPdfs()
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();

            return ReadSippesDataReportsFromPdfFiles(files, progress, cancellationToken);
        }, cancellationToken);
    }

    public async Task<List<SippesPersonnelRow>> ReadSippesActiveDataReportsFromFilesAsync(
        IReadOnlyList<string> pdfFiles,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var files = (pdfFiles ?? [])
                .Where(File.Exists)
                .Where(x => string.Equals(Path.GetExtension(x), ".pdf", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();

            return ReadSippesDataReportsFromPdfFiles(files, progress, cancellationToken);
        }, cancellationToken);
    }

    private static List<SippesPersonnelRow> ReadSippesDataReportsFromPdfFiles(
        IReadOnlyList<string> files,
        IProgress<CpexPaystubProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<SippesPersonnelRow>();
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CpexPaystubProgress
            {
                Current = i + 1,
                Total = files.Count,
                Message = $"Lendo PDF Dados MA {i + 1}/{files.Count}: {Path.GetFileName(files[i])}"
            });

            var row = TryReadSippesDataReportFromPdf(files[i]);
            if (row is not null) rows.Add(row);
        }

        return NormalizeSippesRows(rows)
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> EnumerateSavedSippesDadosMaPdfs()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string Path, SearchOption Option)>();

        void AddRoot(string? path, SearchOption option)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                if (Directory.Exists(full) && roots.All(x => !string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase)))
                    roots.Add((full, option));
            }
            catch { }
        }

        var appSippes = _paths.SippesDirectory;
        AddRoot(GetSippesDadosMaRootDirectory(), SearchOption.AllDirectories);
        AddRoot(appSippes, SearchOption.AllDirectories);
        // Versões anteriores podiam cair em contracheques por causa da normalização do OutputDirectory.
        // Mantemos a busca aqui para recuperar esses PDFs e reaproveitar na conferência.
        AddRoot(_paths.PaystubsDirectory, SearchOption.AllDirectories);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddRoot(System.IO.Path.Combine(userProfile, "Downloads"), SearchOption.TopDirectoryOnly);
        AddRoot(System.IO.Path.Combine(userProfile, "Downloads", "SIPPES"), SearchOption.AllDirectories);
        AddRoot(System.IO.Path.Combine(userProfile, "Downloads", "SIGFUR"), SearchOption.AllDirectories);
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SearchOption.TopDirectoryOnly);
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SearchOption.TopDirectoryOnly);

        foreach (var (root, option) in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.pdf", option).ToList(); }
            catch { continue; }

            foreach (var file in files)
            {
                if (IsLikelySippesDadosMaPdfName(file)) candidates.Add(file);
            }
        }

        return candidates;
    }

    private static bool IsLikelySippesDadosMaPdfName(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        var normalized = Normalize(file);
        var compact = Regex.Replace(normalized, @"[^a-z0-9]", string.Empty, RegexOptions.CultureInvariant);

        return compact.StartsWith("dadosma", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("dadosma", StringComparison.OrdinalIgnoreCase)
               || (compact.Contains("dados", StringComparison.OrdinalIgnoreCase) && compact.Contains("militar", StringComparison.OrdinalIgnoreCase) && compact.Contains("ativa", StringComparison.OrdinalIgnoreCase))
               || (compact.Contains("dados", StringComparison.OrdinalIgnoreCase) && compact.Contains("sippes", StringComparison.OrdinalIgnoreCase))
               || (compact.Contains("relatorio", StringComparison.OrdinalIgnoreCase) && compact.Contains("dados", StringComparison.OrdinalIgnoreCase) && compact.Contains("militar", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddSippesRows(Dictionary<string, SippesPersonnelRow> target, IEnumerable<SippesPersonnelRow> rows)
    {
        foreach (var row in rows)
        {
            var key = BuildSippesPersonnelKey(row);
            if (!string.IsNullOrWhiteSpace(key) && !target.ContainsKey(key)) target[key] = row;
        }
    }

    public async Task<PaystubDownloadResult> DownloadPaystubForMilitaryAsync(
        MilitaryRecord military,
        int year,
        int month,
        bool openAfterDownload = true,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(military);
        if (year is < 2000 or > 2200) throw new InvalidOperationException("Informe um ano válido para o contracheque.");
        if (month is < 1 or > 12) throw new InvalidOperationException("Selecione um mês válido para o contracheque.");

        var settings = await LoadSettingsAsync();
        var password = ReadSavedPassword(settings);
        if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Não há login/senha salvos para o SIPPES. Salve as credenciais uma vez na Central de Contracheques.");

        settings.Year = year;
        settings.Month = month;
        settings.SheetCode = CalculateSheetCode(year, month).ToString(CultureInfo.InvariantCulture);
        settings.System = "SIPPES";
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.Processing = string.IsNullOrWhiteSpace(settings.Processing) ? "Definitivo" : settings.Processing;
        settings.PayrollType = string.IsNullOrWhiteSpace(settings.PayrollType) ? "Normal" : settings.PayrollType;
        settings.OutputDirectory = PersonDocumentStorageService.DefaultRoot(_paths);
        settings.Headless = true;
        settings.OpenAfterDownload = false;

        progress?.Report(new CpexPaystubProgress
        {
            Current = 1,
            Total = 1,
            Name = military.Name,
            Message = $"Baixando contracheque de {military.Name} — {month:00}/{year}..."
        });

        var person = ToPaystubPerson(military);
        var result = await DownloadPreparedAsync([person], settings, password, progress, cancellationToken, writeFailureReport: false);
        var path = result.DownloadedFiles.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (!string.IsNullOrWhiteSpace(path))
        {
            var finalPath = BuildPaystubOutputPath(settings, person);
            if (!SamePath(path, finalPath) && File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? settings.OutputDirectory);
                File.Move(path, finalPath, overwrite: true);
                path = finalPath;
            }

            if (openAfterDownload) ShellService.OpenPath(path);
            return new PaystubDownloadResult
            {
                Success = true,
                FilePath = path,
                Year = year,
                Month = month,
                Message = openAfterDownload
                    ? $"Contracheque {month:00}/{year} salvo e aberto."
                    : $"Contracheque {month:00}/{year} salvo."
            };
        }

        var message = result.Failures.FirstOrDefault();
        return new PaystubDownloadResult
        {
            Success = false,
            Year = year,
            Month = month,
            Message = string.IsNullOrWhiteSpace(message)
                ? $"Não consegui baixar o contracheque de {month:00}/{year}."
                : message
        };
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
        settings.OutputDirectory = PersonDocumentStorageService.DefaultRoot(_paths);
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

    public async Task<PaystubDownloadResult> DownloadLatestFinancialStatementForMilitaryAsync(
        MilitaryRecord military,
        int statementYear,
        bool openAfterDownload = true,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(military);
        var settings = await LoadSettingsAsync();
        var password = ReadSavedPassword(settings);
        if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Nao ha login/senha salvos para a Area Exclusiva do CPEx. Abra a Central de Contracheques e salve as credenciais.");

        settings.OutputDirectory = PersonDocumentStorageService.DefaultRoot(_paths);
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.System = FinancialStatementSessionSystem;
        settings.Headless = true;
        settings.OpenAfterDownload = false;

        progress?.Report(new CpexPaystubProgress
        {
            Current = 1,
            Total = 1,
            Name = military.Name,
            Message = $"Baixando ficha financeira de {military.Name} - {statementYear}..."
        });

        var person = ToPaystubPerson(military);
        var result = await DownloadFinancialStatementsPreparedAsync([person], settings, statementYear, password, progress, cancellationToken, writeFailureReport: false);
        var path = result.DownloadedFiles.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (openAfterDownload) ShellService.OpenPath(path);
            return new PaystubDownloadResult
            {
                Success = true,
                FilePath = path,
                Year = statementYear,
                Message = $"Ficha financeira {statementYear} salva e aberta."
            };
        }

        var message = result.Failures.FirstOrDefault();
        return new PaystubDownloadResult
        {
            Success = false,
            Year = statementYear,
            Message = string.IsNullOrWhiteSpace(message)
                ? $"Nao consegui baixar a ficha financeira de {statementYear}."
                : message
        };
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

    public void BringPreparedSessionToFront()
    {
        if (_preparedDriver is null) return;
        ShowAutomationWindows(_preparedDriver);
    }

    public void HidePreparedSessionWindows()
    {
        HideAutomationWindows(_preparedDriver);
    }

    public async Task OpenSippesOmPaystubMirrorManualAsync(
        int year,
        int month,
        IProgress<CpexPaystubProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        year = year is < 2000 or > 2200 ? DateTime.Today.Year : year;
        month = Math.Clamp(month, 1, 12);

        var settings = await LoadSettingsAsync();
        settings.System = "SIPPES";
        settings.Browser = NormalizeBrowser(settings.Browser);
        settings.Headless = false;
        settings.OutputDirectory = GetSippesMirrorDownloadDirectory(year, month);

        var password = ReadSavedPassword(settings);
        if (string.IsNullOrWhiteSpace(settings.Login))
            throw new InvalidOperationException("Informe e salve o usuário do SIPPES antes de abrir o Espelho de Contracheque da OM.");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Informe e salve a senha do SIPPES antes de abrir o Espelho de Contracheque da OM.");

        await SaveSettingsAsync(settings);
        Directory.CreateDirectory(settings.OutputDirectory);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                var driver = EnsurePreparedSessionUnsafe(settings, password, progress, cancellationToken);
                ShowAutomationWindows(driver);
                progress?.Report(new CpexPaystubProgress { Message = "SIPPES aberto em modo manual assistido. O navegador ficará visível e você fará os cliques manualmente." });

                NavigateSippes(driver, SippesMirrorBaseUrl, cancellationToken);
                ShowAutomationWindows(driver);
                if (IsSippesLoginPage(driver))
                {
                    progress?.Report(new CpexPaystubProgress { Message = "Sessão voltou ao login. Reautenticando SIPPES..." });
                    PrepareSippesSession(driver, settings.Login, password, cancellationToken, progress);
                    NavigateSippes(driver, SippesMirrorBaseUrl, cancellationToken);
                    ShowAutomationWindows(driver);
                }

                // Deixa o usuário já próximo da primeira tela útil, mas sem automatizar os cliques frágeis do SIPPES.
                NavigateSippes(driver, SippesMirrorFolhasUrl, cancellationToken);
                ShowAutomationWindows(driver);
                WaitUntil(driver, d => PageContains(d, "PESQUISAR FOLHA") || SafeUrl(d).Contains("relatorioEspelhoContracheque", StringComparison.OrdinalIgnoreCase), TimeSpan.FromMinutes(2), cancellationToken);
                progress?.Report(new CpexPaystubProgress { Message = $"Modo manual pronto. No SIPPES, selecione {PortugueseMonth(month)}/{year}, pesquise, abra a folha, gere o relatório completo e depois clique OK no SIGFUR." });
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

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
        CancellationToken cancellationToken = default,
        bool writeFailureReport = true)
    {
        settings = CloneSettings(settings);
        settings.System = FinancialStatementSessionSystem;
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

                    Exception? lastError = null;
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (attempt > 1)
                            {
                                progress?.Report(new CpexPaystubProgress
                                {
                                    Current = index + 1,
                                    Total = valid.Count,
                                    Name = person.Name,
                                    Message = $"Nova tentativa da ficha financeira de {person.Name} ({attempt}/3)..."
                                });
                                try { NavigateFinancialStatement(driver, statementYear, cancellationToken); } catch { }
                            }

                            var path = DownloadOneFinancialStatement(driver, person, settings, statementYear, cancellationToken);
                            result.DownloadedFiles.Add(path);
                            lastError = null;
                            break;
                        }
                        catch (Exception ex) when (LooksLikeExpiredSession(ex))
                        {
                            lastError = ex;
                            progress?.Report(new CpexPaystubProgress
                            {
                                Current = index + 1,
                                Total = valid.Count,
                                Name = person.Name,
                                Message = "Sessão CPEx expirada. Refazendo login oculto e continuando..."
                            });
                            try { driver = RecreatePreparedSessionUnsafe(settings, password, progress, cancellationToken); }
                            catch (Exception retryLoginEx) { lastError = retryLoginEx; break; }
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            try { NavigateFinancialStatement(driver, statementYear, cancellationToken); } catch { }
                            if (attempt < 3 && !IndicatesNotFound(ex.Message)) continue;
                            break;
                        }
                    }

                    if (lastError is not null)
                        result.Failures.Add($"{person.Name} ({MilitaryFormatting.FormatCpf(person.Cpf)}): {lastError.Message}");
                }
            }, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }

        if (writeFailureReport) await WriteFinancialStatementFailureReportAsync(settings, statementYear, result, cancellationToken);
        return result;
    }

    public void DisposePreparedSession()
        => DisposePreparedSession(TimeSpan.FromMilliseconds(500));

    private void DisposePreparedSession(TimeSpan wait)
    {
        if (!_sessionGate.Wait(wait)) return;
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

        var usesLegacyPortal = statementYear < ModernFinancialStatementStartYear;
        var valid = people
            .Where(x => MilitaryFormatting.Digits(x.Cpf).Length == 11
                        || (usesLegacyPortal && MilitaryFormatting.Digits(x.PrecCp).Length > 0))
            .GroupBy(x => FinancialStatementPersonKey(x, usesLegacyPortal))
            .Select(x => x.First())
            .ToList();
        if (valid.Count == 0) throw new InvalidOperationException(usesLegacyPortal
            ? "Nenhum militar possui CPF ou PREC-CP válido para baixar a Ficha Financeira antiga."
            : "Nenhum militar possui CPF válido para baixar Ficha Financeira.");
        return valid;
    }

    private static string FinancialStatementPersonKey(CpexPaystubPerson person, bool usesLegacyPortal)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        if (cpf.Length == 11) return "CPF:" + cpf;
        return usesLegacyPortal ? "PREC:" + MilitaryFormatting.Digits(person.PrecCp) : "INVALIDO";
    }

    private IWebDriver EnsurePreparedSessionUnsafe(CpexPaystubSettings settings, string password, IProgress<CpexPaystubProgress>? progress, CancellationToken ct)
    {
        if (_preparedDriver is not null && SessionMatches(_preparedSettings, settings) && IsDriverAlive(_preparedDriver))
        {
            if (IsSippesSession(settings))
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
                progress?.Report(new CpexPaystubProgress
                {
                    Message = IsFinancialStatementSession(settings)
                        ? "Sessao da Ficha Financeira ja preparada. Iniciando download..."
                        : "Sessao oculta ja preparada. Iniciando download..."
                });
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
            var isSippes = IsSippesSession(settings);
            var isFinancialStatement = IsFinancialStatementSession(settings);
            progress?.Report(new CpexPaystubProgress
            {
                Message = isSippes
                    ? "Preparando SIPPES oculto..."
                    : isFinancialStatement
                        ? "Entrando na Area UA para Ficha Financeira..."
                        : "Preparando Area UA/SIAPPES oculta..."
            });
            driver = CreateDriver(settings);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(isSippes ? 10 : isFinancialStatement ? 18 : 35);
            if (isSippes)
            {
                PrepareSippesSession(driver, settings.Login, password, ct, progress);
            }
            else if (isFinancialStatement)
            {
                PrepareFinancialStatementSession(driver, settings.Login, password, ct);
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
        var driver = _preparedDriver;
        var browser = _preparedBrowser;
        var processRoots = CaptureTrackedProcessRootsUnsafe();
        _preparedDriver = null;
        _preparedSettings = null;
        _preparedAt = DateTime.MinValue;
        _serviceProcessId = 0;
        _driverProcessIds.Clear();
        _browserProcessIds.Clear();
        _preparedBrowser = "Edge";

        if (driver is not null)
        {
            try
            {
                var quit = Task.Run(() =>
                {
                    try { driver.Quit(); } catch { }
                });
                if (!quit.Wait(TimeSpan.FromSeconds(2))) KillTrackedProcessTrees(processRoots, browser);
            }
            catch
            {
                KillTrackedProcessTrees(processRoots, browser);
            }
            try { driver.Dispose(); } catch { }
        }

        KillTrackedProcessTrees(processRoots, browser);
    }

    public void Dispose()
    {
        DisposePreparedSession(TimeSpan.FromSeconds(2));
        GC.SuppressFinalize(this);
    }

    private static bool SessionMatches(CpexPaystubSettings? prepared, CpexPaystubSettings current)
    {
        if (prepared is null) return false;
        return string.Equals(NormalizeSessionSystem(prepared.System), NormalizeSessionSystem(current.System), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeBrowser(prepared.Browser), NormalizeBrowser(current.Browser), StringComparison.OrdinalIgnoreCase)
               && string.Equals((prepared.Login ?? string.Empty).Trim(), (current.Login ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeDirectory(prepared.OutputDirectory), NormalizeDirectory(current.OutputDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSippesSession(CpexPaystubSettings settings)
        => string.Equals(NormalizeSessionSystem(settings.System), "SIPPES", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinancialStatementSession(CpexPaystubSettings settings)
        => string.Equals(NormalizeSessionSystem(settings.System), FinancialStatementSessionSystem, StringComparison.OrdinalIgnoreCase);

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
            // A posição inicial fora da área visível impede a janela branca/preta de
            // aparecer na frente do SIGFUR enquanto o Selenium ainda está iniciando.
            chromium.AddArgument("--window-position=-32000,-32000");
        }
        chromium.AddArgument("--ignore-certificate-errors");
        chromium.AddArgument("--disable-gpu");
        chromium.AddArgument("--disable-features=CalculateNativeWinOcclusion");
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

    private static void TrySetChromiumDownloadDirectory(IWebDriver driver, string downloadDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(downloadDirectory)) return;
            Directory.CreateDirectory(downloadDirectory);
            if (driver is ChromiumDriver chromium)
            {
                chromium.ExecuteCdpCommand("Page.setDownloadBehavior", new Dictionary<string, object>
                {
                    ["behavior"] = "allow",
                    ["downloadPath"] = Path.GetFullPath(downloadDirectory)
                });
            }
        }
        catch
        {
            // Se o navegador não aceitar CDP, o caminho principal por fetch ainda salva direto no arquivo final.
        }
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

    private void ShowAutomationWindows(IWebDriver? driver = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { driver?.Manage().Window.Maximize(); } catch { }
            return;
        }

        var handles = FindBrowserWindows(visibleOnly: false);
        foreach (var handle in handles)
        {
            try
            {
                ShowWindow(handle, SwRestore);
                SetWindowPos(handle, IntPtr.Zero, 40, 40, 0, 0, SwpNoSize | SwpNoZOrder | SwpShowWindow);
            }
            catch { }
        }

        try { driver?.Manage().Window.Maximize(); } catch { }
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

    private HashSet<int> CaptureTrackedProcessRootsUnsafe()
    {
        try { ResolveBrowserProcessIds(); } catch { }
        var processRoots = new HashSet<int>(_driverProcessIds);
        processRoots.UnionWith(_browserProcessIds);
        if (_serviceProcessId > 0) processRoots.Add(_serviceProcessId);
        processRoots.Remove(Environment.ProcessId);
        processRoots.RemoveWhere(processId => processId <= 0);
        return processRoots;
    }

    private static void KillTrackedProcessTrees(IEnumerable<int> rootPids, string browser)
    {
        var allowedNames = BrowserProcessNames(browser)
            .Concat(DriverProcessNames(browser))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPid in rootPids.Distinct())
            KillProcessTree(rootPid, allowedNames);
    }

    private static void KillProcessTree(int rootPid, IReadOnlyCollection<string> allowedNames)
    {
        if (rootPid <= 0 || rootPid == Environment.ProcessId) return;
        foreach (var pid in DescendantProcessIds(rootPid).OrderByDescending(x => x))
        {
            if (pid <= 0 || pid == Environment.ProcessId) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!allowedNames.Contains(process.ProcessName)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
            catch { }
        }
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

    private static void LoginSiappes(IWebDriver driver, string login, string password, CancellationToken ct, bool fastAreaUaOnly = false)
    {
        driver.Navigate().GoToUrl(LoginUrl);
        if (fastAreaUaOnly) WaitAreaUaLoginUsable(driver, ct);
        else WaitReady(driver, ct);
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

    private static void PrepareFinancialStatementSession(IWebDriver driver, string login, string password, CancellationToken ct)
    {
        LoginSiappes(driver, login, password, ct, fastAreaUaOnly: true);
        NavigateFinancialStatement(driver, DateTime.Today.Year, ct);
    }

    private static void WaitAreaUaLoginUsable(IWebDriver driver, CancellationToken ct)
    {
        WaitUntil(driver, d =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Convert.ToBoolean(((IJavaScriptExecutor)d).ExecuteScript("""
                    const body = document.body;
                    if (!body) return false;
                    const hasPassword = !!document.querySelector('input[type="password"]');
                    const hasInput = !!document.querySelector('input, button, select, textarea');
                    const text = (body.innerText || '').toLowerCase();
                    return hasPassword || hasInput || text.includes('area') || text.includes('login');
                    """));
            }
            catch { return false; }
        }, TimeSpan.FromSeconds(10), ct);

        try { WaitForDomIdle(driver, TimeSpan.FromMilliseconds(700), ct); } catch { }
        AcceptAlert(driver);
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
        var precCp = MilitaryFormatting.Digits(person.PrecCp);
        var usesLegacyPortal = statementYear < ModernFinancialStatementStartYear;
        var searchDigits = cpf.Length == 11 ? cpf : usesLegacyPortal ? precCp : string.Empty;
        if (string.IsNullOrWhiteSpace(searchDigits))
            throw new InvalidOperationException(usesLegacyPortal
                ? "CPF e PREC-CP inválidos para consultar a Ficha Financeira antiga."
                : "CPF inválido para consultar Ficha Financeira.");

        NavigateFinancialStatement(driver, statementYear, ct);
        DisableAutofill(driver);

        if (IsFinancialStatementPage(driver) && PageContainsDigits(driver, searchDigits))
        {
            // Já está na ficha correta, normalmente por reaproveitamento de aba.
        }
        else
        {
            var handlesBefore = driver.WindowHandles.ToList();
            if (!FillFinancialStatementForm(driver, cpf, precCp, statementYear, usesLegacyPortal))
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

        var status = WaitForFinancialStatementResult(driver, searchDigits, TimeSpan.FromSeconds(30), ct);
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

    private static bool FillFinancialStatementForm(IWebDriver driver, string cpf, string precCp, int statementYear, bool usesLegacyPortal)
    {
        try
        {
            var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const cpf = arguments[0] || '';
                const prec = arguments[1] || '';
                const ano = String(arguments[2] || '');
                const legado = !!arguments[3];
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
                let precEl = null;
                for (const el of textInputs) {
                    const blob = norm([el.name, el.id, el.placeholder, el.title, el.getAttribute('aria-label'), el.parentElement && el.parentElement.innerText].join(' '));
                    if (!cpfEl && blob.includes('cpf')) cpfEl = el;
                    if (!precEl && (blob.includes('prec') || blob.includes('preccp') || blob.includes('prec-cp'))) precEl = el;
                }
                if (!cpfEl && !legado) cpfEl = textInputs[0] || null;

                let searchEl = null;
                if (cpf && cpf.length === 11 && cpfEl) {
                    if (!setValue(cpfEl, cpf)) return false;
                    if (precEl) setValue(precEl, '');
                    searchEl = cpfEl;
                } else if (legado && prec && precEl) {
                    if (!setValue(precEl, prec)) return false;
                    if (cpfEl) setValue(cpfEl, '');
                    searchEl = precEl;
                } else {
                    return false;
                }

                let anoEls = [];
                for (const el of all) {
                    const blob = norm([el.name, el.id, el.placeholder, el.title, el.getAttribute('aria-label'), el.parentElement && el.parentElement.innerText].join(' '));
                    if (blob.includes('ano') || blob.includes('exercicio') || blob.includes('exerc')) anoEls.push(el);
                }
                if (!anoEls.length) anoEls = Array.from(document.querySelectorAll('select')).filter(visible);
                for (const anoEl of anoEls) {
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
                if (searchEl.form) { searchEl.form.submit(); return true; }
                return false;
                """, cpf, precCp, statementYear.ToString(CultureInfo.InvariantCulture), usesLegacyPortal);
            return Convert.ToBoolean(result, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    private static void NavigateFinancialStatement(IWebDriver driver, int statementYear, CancellationToken ct)
    {
        try
        {
            driver.SwitchTo().DefaultContent();
        }
        catch { }

        try
        {
            driver.Navigate().GoToUrl(statementYear >= ModernFinancialStatementStartYear
                ? FinancialStatementUrl
                : LegacyFinancialStatementUrl);
        }
        catch (WebDriverTimeoutException)
        {
            // PageLoadStrategy.Eager can time out on old ASP pages even when the useful DOM is ready.
        }

        WaitUntil(driver, d =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Convert.ToBoolean(((IJavaScriptExecutor)d).ExecuteScript("""
                    const body = document.body;
                    if (!body) return false;
                    const txt = (body.innerText || '').toLowerCase();
                    const hasInput = !!document.querySelector('input, select, textarea, button');
                    return hasInput || txt.includes('ficha') || txt.includes('financeira') || txt.includes('cpf');
                    """));
            }
            catch { return false; }
        }, TimeSpan.FromSeconds(12), ct);

        try { WaitForDomIdle(driver, TimeSpan.FromMilliseconds(800), ct); } catch { }
        AcceptAlert(driver);
    }

    private static string WaitForFinancialStatementResult(IWebDriver driver, string cpf, TimeSpan timeout, CancellationToken ct)
    {
        var limit = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            try { WaitForDomIdle(driver, TimeSpan.FromSeconds(1), ct); } catch { }
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

    private static bool HasSippesActiveSearchButton(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const visible=el=>{
                if(!el) return false;
                const st=window.getComputedStyle(el);
                const r=el.getBoundingClientRect();
                return st.display!=='none' && st.visibility!=='hidden' && r.width>0 && r.height>0;
            };
            return Array.from(document.querySelectorAll('input,button,a')).some(el=>{
                const value=((el.value||el.innerText||el.title||el.name||el.id||el.getAttribute('onclick')||'')+'').toLowerCase();
                return visible(el) && (value.includes('pesquisar') || el.name === 'btPesquisar' || el.id === 'btPesquisar');
            }) || typeof pesquisar === 'function';
            """)));

    private static bool ClickSippesActiveSearch(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            try{
                if(typeof pesquisar === 'function'){
                    pesquisar();
                    return true;
                }
            }catch(e){}
            const visible=el=>{
                if(!el) return false;
                const st=window.getComputedStyle(el);
                const r=el.getBoundingClientRect();
                return st.display!=='none' && st.visibility!=='hidden' && r.width>0 && r.height>0;
            };
            const buttons=Array.from(document.querySelectorAll('input,button,a'));
            const btn=buttons.find(el=>{
                const value=((el.value||el.innerText||el.title||el.name||el.id||el.getAttribute('onclick')||'')+'').toLowerCase();
                return visible(el) && (value.includes('pesquisar') || el.name === 'btPesquisar' || el.id === 'btPesquisar');
            });
            if(btn){ try{ btn.removeAttribute('disabled'); }catch(e){} try{ btn.click(); return true; }catch(e){} }
            return false;
            """)));

    private static bool WaitSippesLoadingToFinish(
        IWebDriver driver,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<CpexPaystubProgress>? progress = null,
        string? message = null)
    {
        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        var lastReport = DateTime.MinValue;
        var sawLoading = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            bool loading;
            try { loading = IsSippesLoadingVisible(driver); }
            catch { loading = false; }

            if (loading)
            {
                sawLoading = true;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
                {
                    lastReport = DateTime.UtcNow;
                    var elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                    progress?.Report(new CpexPaystubProgress
                    {
                        Message = string.IsNullOrWhiteSpace(message)
                            ? $"Aguardando o loading do SIPPES terminar... {elapsed}s"
                            : $"{message}... {elapsed}s"
                    });
                }
            }
            else
            {
                // Pequena janela de estabilidade. O SIPPES costuma piscar o overlay enquanto troca página.
                if (!sawLoading)
                {
                    if (ct.WaitHandle.WaitOne(450)) throw new OperationCanceledException(ct);
                    try { if (IsSippesLoadingVisible(driver)) { sawLoading = true; continue; } } catch { }
                }
                return true;
            }

            if (ct.WaitHandle.WaitOne(750)) throw new OperationCanceledException(ct);
        }

        progress?.Report(new CpexPaystubProgress { Message = "O loading do SIPPES passou do tempo limite. Mantive o navegador visível para conferência manual." });
        return !IsSippesLoadingVisible(driver);
    }

    private static bool IsSippesLoadingVisible(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const visible = el => {
                if (!el) return false;
                const tag = (el.tagName || '').toUpperCase();
                if (['SCRIPT','STYLE','OPTION','META','LINK','HEAD','TITLE'].includes(tag)) return false;
                const st = window.getComputedStyle(el);
                if (!st || st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity || '1') === 0) return false;
                const r = el.getBoundingClientRect();
                if (r.width < 12 || r.height < 12) return false;
                return r.bottom >= 0 && r.right >= 0 && r.top <= (window.innerHeight || document.documentElement.clientHeight) && r.left <= (window.innerWidth || document.documentElement.clientWidth);
            };
            const norm = s => (s || '').toString().normalize ? (s || '').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase() : (s || '').toString().toLowerCase();
            const candidates = Array.from(document.querySelectorAll('div,span,img,table,tr,td,body,*[id],*[class],*[style]'));
            return candidates.some(el => {
                if (!visible(el)) return false;
                const key = norm([el.id, el.className, el.getAttribute('src'), el.getAttribute('alt'), el.getAttribute('title'), el.getAttribute('style')].join(' '));
                const text = norm((el.innerText || el.textContent || '').trim());
                const hasLoadingWord = /(loading|load|aguarde|carregando|processando|spinner|wait|modal|bloqueio|overlay)/.test(key + ' ' + text);
                if (!hasLoadingWord) return false;
                const r = el.getBoundingClientRect();
                // Overlay grande ou spinner central; evita confundir menu/rodapé antigo com loading.
                return (r.width > 30 && r.height > 30) || /loading|carregando|aguarde|processando|spinner/.test(key + ' ' + text);
            });
            """)));

    private string GetSippesDadosMaRootDirectory()
    {
        var dir = _paths.SippesDadosMaDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string GetSippesDadosMaDownloadDirectory()
    {
        var day = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dir = Path.Combine(GetSippesDadosMaRootDirectory(), day);
        Directory.CreateDirectory(dir);
        return dir;
    }


    private string GetSippesMirrorRootDirectory()
    {
        var dir = _paths.SippesEspelhoContrachequeOmDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string GetSippesMirrorDownloadDirectory(int year, int month)
    {
        var dir = Path.Combine(GetSippesMirrorRootDirectory(), $"{year}_{month:00}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool HasSippesMirrorSearchControls(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const txt=(document.body?.innerText||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const hasYear=Array.from(document.querySelectorAll('input,select')).some(el=>/ano|exercicio|referencia/.test(((el.name||'')+' '+(el.id||'')+' '+(el.title||'')).toLowerCase()));
            const hasMonth=Array.from(document.querySelectorAll('select,input')).some(el=>/mes/.test(((el.name||'')+' '+(el.id||'')+' '+(el.title||'')).toLowerCase())) || txt.includes('mes');
            return txt.includes('pesquisar folha') || (hasYear && hasMonth && txt.includes('folha'));
            """)));

    private static bool FillSippesMirrorFolhaSearch(IWebDriver driver, int year, int month)
    {
        var filledByJavascript = RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const ano=String(arguments[0]), mes=Number(arguments[1]);
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().trim();
            const monthNames=['janeiro','fevereiro','marco','abril','maio','junho','julho','agosto','setembro','outubro','novembro','dezembro'];
            const wantedMonthName=monthNames[mes-1];
            const wantedMes=String(mes);
            const wantedMes2=wantedMes.padStart(2,'0');

            function visible(el){
                if(!el) return false;
                try{
                    if(el.type==='hidden') return false;
                    const r=el.getBoundingClientRect();
                    const st=window.getComputedStyle(el);
                    return r.width>0 && r.height>0 && st.display!=='none' && st.visibility!=='hidden';
                }catch(e){ return true; }
            }
            function fire(el){
                if(!el) return;
                try{ el.removeAttribute('readonly'); el.removeAttribute('disabled'); el.disabled=false; }catch(e){}
                for(const ev of ['keydown','keypress','input','keyup','change','blur']){
                    try{ el.dispatchEvent(new Event(ev,{bubbles:true,cancelable:true})); }catch(e){}
                }
            }
            function setNativeValue(el,value){
                if(!el) return false;
                try{ el.scrollIntoView({block:'center', inline:'center'}); }catch(e){}
                try{ el.focus(); }catch(e){}
                try{
                    const proto = el.tagName==='SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
                    const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                    if(desc && desc.set) desc.set.call(el, value);
                    else el.value=value;
                }catch(e){ try{ el.value=value; }catch(_){} }
                try{ el.setAttribute('value', value); }catch(e){}
                fire(el);
                return (el.value||'').toString().trim()===value;
            }
            function setText(el,value){
                if(!el) return false;
                try{ el.value=''; }catch(e){}
                return setNativeValue(el,value) || ((el.value||'').toString().trim()===value);
            }
            function setSelect(el){
                if(!el || el.tagName!=='SELECT') return false;
                try{ el.removeAttribute('disabled'); el.disabled=false; }catch(e){}
                const options=Array.from(el.options||[]);
                let opt=options.find(o=>norm(o.value)===wantedMes || norm(o.value)===wantedMes2)
                    || options.find(o=>norm(o.textContent)===wantedMonthName || norm(o.textContent).includes(wantedMonthName));
                if(!opt) return false;
                try{ opt.selected=true; }catch(e){}
                try{ el.selectedIndex=options.indexOf(opt); }catch(e){}
                setNativeValue(el,opt.value);
                try{ el.value=opt.value; }catch(e){}
                fire(el);
                const chosen=el.options ? el.options[el.selectedIndex] : null;
                return Number(el.value)===mes || norm(el.value)===wantedMes2 || (chosen && norm(chosen.textContent).includes(wantedMonthName));
            }
            function keyOf(el){ return norm([el.name,el.id,el.className,el.title,el.placeholder,el.getAttribute && el.getAttribute('aria-label')].join(' ')); }
            function findFirst(selectors){
                for(const sel of selectors){ try{ const el=document.querySelector(sel); if(el) return el; }catch(e){} }
                return null;
            }

            const yearSelectors=[
                'input[name="formularioConsultarFolha.ano"]',
                'input[id="formularioConsultarFolha.ano"]',
                'input[name$=".ano"]',
                'input[id$=".ano"]',
                'input[name="ano"]',
                'input[id="ano"]'
            ];
            const monthSelectors=[
                'select[name="formularioConsultarFolha.mes"]',
                'select[id="comboMes"]',
                'select[name$=".mes"]',
                'select[id$=".mes"]',
                'select[name="mes"]',
                'select[id="mes"]'
            ];

            let yearEl=findFirst(yearSelectors);
            let monthEl=findFirst(monthSelectors);
            const all=Array.from(document.querySelectorAll('input,select'));
            if(!yearEl){
                yearEl=all.find(el=>visible(el) && el.tagName!=='SELECT' && !/button|submit|reset|checkbox|radio|hidden/.test(norm(el.type)) && (/ano/.test(keyOf(el)) || /exercicio|referencia/.test(keyOf(el))));
            }
            if(!monthEl){
                monthEl=all.find(el=>visible(el) && el.tagName==='SELECT' && (/mes/.test(keyOf(el)) || Array.from(el.options||[]).some(o=>norm(o.textContent).includes(wantedMonthName))));
            }

            const yearOk=setText(yearEl, ano);
            const monthOk=setSelect(monthEl);

            // SIPPES antigo às vezes mantém hidden com o mesmo nome. Força também dentro do formulário.
            const form=document.forms['formularioRelatoriosEspelho'] || document.querySelector('form[action*="relatorioEspelhoContracheque"],form');
            if(form){
                function ensure(name,value){
                    let el=form.querySelector('[name="'+name.replace(/"/g,'\\"')+'"]') || document.querySelector('[name="'+name.replace(/"/g,'\\"')+'"]');
                    if(!el){ el=document.createElement('input'); el.type='hidden'; el.name=name; form.appendChild(el); }
                    try{ el.value=String(value); el.setAttribute('value', String(value)); fire(el); }catch(e){}
                }
                ensure('formularioConsultarFolha.ano', ano);
                ensure('formularioConsultarFolha.mes', wantedMes);
            }

            const finalYear=(yearEl && (yearEl.value||'').toString().trim()===ano) || yearOk;
            let finalMonth=monthOk;
            if(monthEl){
                const chosen=monthEl.options ? monthEl.options[monthEl.selectedIndex] : null;
                finalMonth = finalMonth || Number(monthEl.value)===mes || norm(monthEl.value)===wantedMes2 || (chosen && norm(chosen.textContent).includes(wantedMonthName));
            }
            return !!(finalYear && finalMonth);
            """, year, month)));

        if (filledByJavascript) return true;

        return RunInSippesContexts(driver, () =>
        {
            try
            {
                IWebElement? yearElement = null;
                foreach (var by in new[]
                         {
                             By.Name("formularioConsultarFolha.ano"),
                             By.Id("formularioConsultarFolha.ano"),
                             By.CssSelector("input[name$='.ano']"),
                             By.CssSelector("input[id$='.ano']"),
                             By.Name("ano"),
                             By.Id("ano")
                         })
                {
                    yearElement = driver.FindElements(by).FirstOrDefault(e => e.Displayed && e.Enabled);
                    if (yearElement is not null) break;
                }

                IWebElement? monthElement = null;
                foreach (var by in new[]
                         {
                             By.Name("formularioConsultarFolha.mes"),
                             By.Id("comboMes"),
                             By.CssSelector("select[name$='.mes']"),
                             By.CssSelector("select[id$='.mes']"),
                             By.Name("mes"),
                             By.Id("mes")
                         })
                {
                    monthElement = driver.FindElements(by).FirstOrDefault(e => e.Displayed && e.Enabled);
                    if (monthElement is not null) break;
                }

                if (yearElement is null || monthElement is null) return false;
                yearElement.Clear();
                yearElement.SendKeys(year.ToString(CultureInfo.InvariantCulture));

                var select = new SelectElement(monthElement);
                var monthText = PortugueseMonth(month);
                var selected = false;
                foreach (var option in select.Options)
                {
                    var value = (option.GetAttribute("value") ?? string.Empty).Trim();
                    var text = MilitaryRankService.Normalize(option.Text).Trim();
                    if (value.Equals(month.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                        || value.Equals(month.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                        || text.Contains(MilitaryRankService.Normalize(monthText), StringComparison.OrdinalIgnoreCase))
                    {
                        select.SelectByValue(value);
                        selected = true;
                        break;
                    }
                }

                return selected && yearElement.GetAttribute("value")?.Trim() == year.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool SubmitSippesFolhaPagamentoWithReference(IWebDriver driver, int year, int month)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const ano=String(arguments[0]), mes=String(arguments[1]);
            const form=document.forms['formularioRelatoriosEspelho'] || document.querySelector('form[action*="relatorioEspelhoContracheque"],form');
            if(!form) return false;
            function ensure(name,value){
                let el=form.querySelector('[name="'+name.replace(/"/g,'\\"')+'"]') || document.querySelector('[name="'+name.replace(/"/g,'\\"')+'"]');
                if(!el){ el=document.createElement('input'); el.type='hidden'; el.name=name; form.appendChild(el); }
                try{ el.removeAttribute('disabled'); el.disabled=false; }catch(e){}
                el.value=value;
                el.setAttribute('value', value);
            }
            ensure('formularioConsultarFolha.ano', ano);
            ensure('formularioConsultarFolha.mes', mes);
            try{ form.method='post'; }catch(e){}
            try{ form.action='/relatorioEspelhoContracheque.do?metodo=consultarFolhaPagamento&consultaPaginada=true'; }catch(e){}
            try{ if(typeof pesquisar==='function'){ pesquisar(); return true; } }catch(e){}
            try{ form.submit(); return true; }catch(e){}
            try{ HTMLFormElement.prototype.submit.call(form); return true; }catch(e){}
            return false;
            """, year, month)));

    private static bool ClickSippesPesquisar(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const safeAttr=(el,n)=>{ try{return el.getAttribute(n)||'';}catch(e){return '';} };
            const fire=el=>{
                try{el.removeAttribute('disabled'); el.disabled=false;}catch(e){}
                try{el.scrollIntoView({block:'center', inline:'center'});}catch(e){}
                try{el.focus();}catch(e){}
            };
            const clickElement=el=>{
                if(!el) return false;
                fire(el);
                try{ el.click(); return true; }catch(e){}
                try{ el.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,view:window})); return true; }catch(e){}
                const js=[safeAttr(el,'onclick'), safeAttr(el,'href')].find(x=>norm(x).includes('pesquisar'));
                if(js){
                    try{ (0,eval)(js.replace(/^javascript:/i,'')); return true; }catch(e){}
                }
                return false;
            };

            const candidates=Array.from(document.querySelectorAll('input,button,a,*[onclick]'));
            const btn=candidates.find(el=>{
                const txt=norm([el.value,el.innerText,el.textContent,el.title,el.name,el.id,safeAttr(el,'onclick'),safeAttr(el,'href')].join(' '));
                return txt.includes('pesquisar') && !txt.includes('limpar') && !txt.includes('imprimir') && !txt.includes('ajuda');
            });
            if(clickElement(btn)) return true;

            try{ if(typeof pesquisar==='function'){ pesquisar(); return true; } }catch(e){}
            try{ if(window.parent && typeof window.parent.pesquisar==='function'){ window.parent.pesquisar(); return true; } }catch(e){}
            try{ if(window.top && typeof window.top.pesquisar==='function'){ window.top.pesquisar(); return true; } }catch(e){}

            const form=document.forms['formularioRelatoriosEspelho'] || document.querySelector('form[action*="relatorioEspelhoContracheque"],form');
            if(form){
                try{ form.submit(); return true; }catch(e){}
                try{ HTMLFormElement.prototype.submit.call(form); return true; }catch(e){}
            }
            return false;
            """)));

    private static bool SubmitSippesCurrentForm(IWebDriver driver, string expectedMethod)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const expected=(arguments[0]||'').toString();
            const forms=Array.from(document.forms||[]);
            let form=forms.find(f=>(f.name||'').toLowerCase().includes('formulariorelatoriosespelho'))
                || forms.find(f=>(f.action||'').includes('relatorioEspelhoContracheque'))
                || forms[0];
            if(!form) return false;
            try{
                if(expected && !(form.action||'').includes(expected)){
                    const base=location.origin + '/relatorioEspelhoContracheque.do?metodo='+expected+'&consultaPaginada=true';
                    form.action = base;
                    form.setAttribute('action', base);
                }
            }catch(e){}
            try{ HTMLFormElement.prototype.submit.call(form); return true; }catch(e){}
            try{ form.submit(); return true; }catch(e){}
            return false;
            """, expectedMethod)));

    private static bool ClickSippesMirrorDetailPesquisar(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const txt=norm(document.body?.innerText||'');
            const url=norm(location.href||'');
            const isDetail = txt.includes('consultar espelho de contracheque da om') || url.includes('relatorioespelhocontracheque.do?metodo=voltar');
            if(!isDetail) return false;

            const forms=Array.from(document.forms||[]);
            const form=forms.find(f=>norm(f.name).includes('formulariorelatoriosespelho'))
                || forms.find(f=>(f.action||'').includes('relatorioEspelhoContracheque'))
                || forms[0];
            if(!form) return false;

            // Nesta tela há uma lupa do campo Folha de pagamento e há o Pesquisar correto, embaixo.
            // A lupa volta para a seleção de mês/ano. Portanto, só aceitamos o botão btnPesquisar
            // ou o botão visual com title/onclick de "Pesquisar espelhos de contracheque".
            const candidates=Array.from(document.querySelectorAll('input[type="button"],input[type="submit"],button'));
            const bottomButton=candidates.find(el=>{
                const t=norm([el.value,el.innerText,el.textContent,el.title,el.name,el.id,el.getAttribute?.('onclick')].join(' '));
                if(!t.includes('pesquisar')) return false;
                if(t.includes('folha de pagamento')) return false;
                if(t.includes('limpar') || t.includes('imprimir') || t.includes('ajuda')) return false;
                return norm(el.name)==='btnpesquisar' || t.includes('espelhos de contracheque') || t.includes('espelho de contracheque') || (norm(el.id)==='botao' && norm(el.value)==='pesquisar');
            });
            if(!bottomButton && typeof pesquisar !== 'function') return false;

            try{ window.__sigfurEspelhoOmSegundoPesquisar = new Date().toISOString(); }catch(e){}

            // MUITO IMPORTANTE: agendar o clique e retornar antes da navegação.
            // Se chamarmos pesquisar()/submit() diretamente, o Selenium às vezes perde o contexto
            // enquanto o SIPPES abre o carregamento e o SIGFUR acha, erradamente, que não clicou.
            setTimeout(function(){
                try{
                    if(bottomButton){
                        try{ bottomButton.scrollIntoView({block:'center', inline:'center'}); }catch(e){}
                        try{ bottomButton.removeAttribute('disabled'); bottomButton.disabled=false; }catch(e){}
                        bottomButton.click();
                        return;
                    }
                }catch(e){}
                try{ if(typeof pesquisar==='function'){ pesquisar(); return; } }catch(e){}
                try{ if(window.parent && typeof window.parent.pesquisar==='function'){ window.parent.pesquisar(); return; } }catch(e){}
                try{ if(window.top && typeof window.top.pesquisar==='function'){ window.top.pesquisar(); return; } }catch(e){}
            }, 120);
            return true;
            """)));


    private static bool ClickSippesMirrorVisiblePesquisarImmediate(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const visible=el=>{
                if(!el) return false;
                const st=getComputedStyle(el);
                if(st.display==='none' || st.visibility==='hidden' || Number(st.opacity||'1')===0) return false;
                const r=el.getBoundingClientRect();
                return r.width>2 && r.height>2 && r.bottom>=0 && r.right>=0 && r.top <= (innerHeight||document.documentElement.clientHeight) && r.left <= (innerWidth||document.documentElement.clientWidth);
            };
            const fields=Array.from(document.querySelectorAll('input,select,textarea')).map(el=>norm([el.name,el.id,el.value,el.title].join(' '))).join(' ');
            const isMirrorFilter = fields.includes('codom') || fields.includes('codug') || fields.includes('codigo') || norm(document.body?.innerText||'').includes('consultar espelho de contracheque da om');
            if(!isMirrorFilter) return false;

            const buttons=Array.from(document.querySelectorAll('input[type="button"],input[type="submit"],button'));
            const candidates=buttons.filter(el=>{
                const t=norm([el.value,el.innerText,el.textContent,el.title,el.name,el.id,el.getAttribute?.('onclick')].join(' '));
                if(!visible(el)) return false;
                if(!t.includes('pesquisar')) return false;
                if(t.includes('folha de pagamento')) return false;
                if(t.includes('limpar') || t.includes('imprimir') || t.includes('ajuda')) return false;
                return norm(el.name)==='btnpesquisar'
                    || t.includes('espelhos de contracheque')
                    || t.includes('espelho de contracheque')
                    || (norm(el.id)==='botao' && norm(el.value)==='pesquisar')
                    || (norm(el.value)==='pesquisar' && t.includes('pesquisar()'));
            });
            const target=candidates.find(el=>norm(el.name)==='btnpesquisar') || candidates[0];
            try{ window.__sigfurEspelhoOmSegundoPesquisarDireto = new Date().toISOString(); }catch(e){}
            if(target){
                try{ target.scrollIntoView({block:'center', inline:'center'}); }catch(e){}
                try{ target.removeAttribute('disabled'); target.disabled=false; }catch(e){}
                try{ target.focus(); }catch(e){}
                try{ target.click(); return true; }catch(e){}
                try{ target.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,view:window})); return true; }catch(e){}
            }
            try{ if(typeof pesquisar==='function'){ pesquisar(); return true; } }catch(e){}
            try{ if(window.parent && typeof window.parent.pesquisar==='function'){ window.parent.pesquisar(); return true; } }catch(e){}
            try{ if(window.top && typeof window.top.pesquisar==='function'){ window.top.pesquisar(); return true; } }catch(e){}
            return false;
            """)));

    private static bool IsSippesPageBusyOrNavigating(IWebDriver driver)
    {
        try
        {
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
                const txt=norm(document.body?.innerText||'');
                const hasOverlay=!!document.querySelector('.blockUI,.ui-widget-overlay,.modal-backdrop,#aguarde,#loading,.loading,.spinner,.carregando');
                return document.readyState !== 'complete'
                    || hasOverlay
                    || txt.includes('aguarde')
                    || txt.includes('carregando')
                    || txt.includes('processando');
                """);
            return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }
        catch (WebDriverException)
        {
            // Durante navegação/carregamento o EdgeDriver pode recusar comandos.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryStopSippesLoading(IWebDriver driver)
    {
        try { ((IJavaScriptExecutor)driver).ExecuteScript("try{ window.stop(); }catch(e){} return true;"); }
        catch { }
    }

    private static void TryRefreshSippesPage(IWebDriver driver)
    {
        try
        {
            driver.SwitchTo().DefaultContent();
            driver.Navigate().Refresh();
            return;
        }
        catch { }

        try { ((IJavaScriptExecutor)driver).ExecuteScript("try{ location.reload(); }catch(e){} return true;"); }
        catch { }
    }

    private static bool RefreshSippesMirrorAndPesquisarAgain(
        IWebDriver driver,
        CancellationToken cancellationToken,
        IProgress<CpexPaystubProgress>? progress,
        string label)
    {
        try
        {
            progress?.Report(new CpexPaystubProgress { Message = $"{label}: atualizando a tela do SIPPES..." });
            TryRefreshSippesPage(driver);

            // O SIPPES às vezes retorna com alguns registros, mas sem o seletor de Exibidos.
            // Por isso, depois do F5, aguardamos a tela voltar e enviamos o Pesquisar novamente.
            WaitUntil(driver, d =>
            {
                try
                {
                    return IsSippesMirrorDetailOrSelectionPage(d) || !IsSippesPageBusyOrNavigating(d);
                }
                catch { return false; }
            }, TimeSpan.FromSeconds(45), cancellationToken);

            if (IsSippesPageBusyOrNavigating(driver))
            {
                progress?.Report(new CpexPaystubProgress { Message = $"{label}: o carregamento continuou preso após o F5. Vou parar a página e clicar novamente no Pesquisar de baixo." });
                TryStopSippesLoading(driver);
            }

            // Dá um pequeno tempo para o DOM antigo sair da frente. O SIPPES às vezes mostra alguns registros,
            // mas sem o seletor de Exibidos; nessa situação o clique precisa ser exatamente no botão Pesquisar de baixo.
            if (cancellationToken.WaitHandle.WaitOne(1500)) throw new OperationCanceledException(cancellationToken);

            progress?.Report(new CpexPaystubProgress { Message = $"{label}: F5 concluído. Agora vou clicar diretamente no botão Pesquisar de baixo que está visível nessa tela." });
            if (ClickSippesMirrorVisiblePesquisarImmediate(driver)) return true;

            progress?.Report(new CpexPaystubProgress { Message = $"{label}: clique direto não confirmou. Vou tentar o acionamento reserva do Pesquisar de baixo." });
            return ClickSippesMirrorDetailPesquisar(driver) || SubmitSippesCurrentForm(driver, "consultarContracheque");
        }
        catch
        {
            try { return SubmitSippesCurrentForm(driver, "consultarContracheque"); }
            catch { return false; }
        }
    }

    private static bool IsSippesMirrorDetailOrSelectionPage(IWebDriver driver)
    {
        try
        {
            if (IsSippesMirrorDetailFilterPage(driver)) return true;
            if (CountSippesMirrorSelectionRows(driver) > 0) return true;
            var raw = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
                const txt=norm(document.body?.innerText||'');
                const url=norm(location.href||'');
                return url.includes('relatorioespelhocontracheque')
                    || txt.includes('consultar espelho de contracheque da om')
                    || txt.includes('resultados encontrados')
                    || txt.includes('total de registros');
                """);
            return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }

    private static bool WaitSippesMirrorFolhaResult(
        IWebDriver driver,
        int year,
        int month,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<CpexPaystubProgress>? progress)
    {
        var limit = DateTime.UtcNow + timeout;
        var lastReport = DateTime.UtcNow.AddSeconds(-10);
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (HasSippesMirrorFolhaResult(driver, year, month)) return true;
            }
            catch { }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new CpexPaystubProgress { Message = $"Aguardando resultado da folha {PortugueseMonth(month)}/{year}... {(int)(DateTime.UtcNow - started).TotalSeconds}s. Veja a tela do SIPPES aberta." });
            }
            if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException(ct);
        }
        return false;
    }

    private static bool HasSippesMirrorFolhaResult(IWebDriver driver, int year, int month)
    {
        var monthName = PortugueseMonth(month);
        return RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const ano=String(arguments[0]);
            const mes=(arguments[1]||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const txt=norm(document.body?.innerText||'');
            if(!txt.includes('resultado') && !txt.includes('registros') && !txt.includes('tipo de folha')) return false;
            if(!txt.includes(ano) || !txt.includes(mes)) return false;
            return Array.from(document.querySelectorAll('a,*[onclick]')).some(el=>norm([el.innerText,el.textContent,el.getAttribute?.('onclick')].join(' ')).includes(ano));
            """, year, monthName)));
    }

    private static bool SelectSippesMirrorFolhaResult(IWebDriver driver, int year, int month)
    {
        var monthName = PortugueseMonth(month);
        return RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const ano=String(arguments[0]);
            const mes=(arguments[1]||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const safeAttr=(el,n)=>{ try{return el.getAttribute(n)||'';}catch(e){return '';} };
            const all=Array.from(document.querySelectorAll('a,*[onclick]'));
            const candidates=all.filter(el=>{
                const row=el.closest('tr');
                const txt=norm([(row ? row.innerText : ''), el.innerText, el.textContent, safeAttr(el,'onclick'), safeAttr(el,'href')].join(' '));
                return txt.includes(ano) && txt.includes(mes) && (txt.includes('normal') || txt.includes('selecionar'));
            });
            const target=candidates[0] || all.find(el=>norm([(el.closest('tr') ? el.closest('tr').innerText : ''), el.innerText, safeAttr(el,'onclick'), safeAttr(el,'href')].join(' ')).includes(ano));

            function extractSelect(raw){
                if(!raw) return null;
                const js=raw.replace(/^javascript:/i,'');
                const m=js.match(/selecionar\s*\(\s*['"]?([^,'")]+)['"]?\s*,\s*['"]([^'"]+)['"]/i);
                return m ? { codigo:m[1], descricao:m[2], js } : null;
            }
            function ensure(form,name,value){
                let el=form.querySelector('[name="'+name.replace(/"/g,'\\"')+'"]') || document.querySelector('[name="'+name.replace(/"/g,'\\"')+'"]');
                if(!el){ el=document.createElement('input'); el.type='hidden'; el.name=name; form.appendChild(el); }
                try{ el.removeAttribute('disabled'); el.disabled=false; }catch(e){}
                el.value=String(value); el.setAttribute('value', String(value));
                try{ el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); }catch(e){}
            }
            function submitVoltar(info){
                if(!info || !info.codigo) return false;
                const form=document.forms['formularioRelatoriosEspelho'] || document.querySelector('form[action*="relatorioEspelhoContracheque"],form');
                if(!form) return false;
                ensure(form,'codigoFolhaPagamentoFiltro',info.codigo);
                ensure(form,'descricaoFolhaPagamentoFiltro',info.descricao||('Normal '+arguments[1]+'/'+ano));
                try{ form.method='post'; }catch(e){}
                try{ form.action='/relatorioEspelhoContracheque.do?metodo=voltar'; }catch(e){}
                try{ HTMLFormElement.prototype.submit.call(form); return true; }catch(e){}
                try{ form.submit(); return true; }catch(e){}
                return false;
            }
            function executeSelectText(raw){
                const info=extractSelect(raw);
                if(!info) return false;
                try{ if(typeof selecionar==='function'){ selecionar(info.codigo, info.descricao); return true; } }catch(e){}
                try{ (0,eval)(info.js); return true; }catch(e){}
                return submitVoltar(info);
            }
            if(target){
                try{ target.scrollIntoView({block:'center', inline:'center'}); }catch(e){}
                const href=safeAttr(target,'href');
                const onclick=safeAttr(target,'onclick');
                if(executeSelectText(href) || executeSelectText(onclick)) return true;
                try{ target.click(); return true; }catch(e){}
                try{ target.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,view:window})); return true; }catch(e){}
                const info=extractSelect(href) || extractSelect(onclick);
                if(submitVoltar(info)) return true;
            }
            for(const el of candidates){
                const href=safeAttr(el,'href');
                const onclick=safeAttr(el,'onclick');
                if(executeSelectText(href) || executeSelectText(onclick)) return true;
                const info=extractSelect(href) || extractSelect(onclick);
                if(submitVoltar(info)) return true;
            }
            return false;
            """, year, monthName)));
    }

    private static bool HasSippesMirrorDetailSearchButton(IWebDriver driver)
        => IsSippesMirrorDetailFilterPage(driver);

    private static bool IsSippesMirrorDetailFilterPage(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const txt=norm(document.body?.innerText||'');
            const url=norm(location.href||'');
            const titleOk=txt.includes('consultar espelho de contracheque da om') || url.includes('metodo=voltar') || url.includes('metodo=consultarcontracheque');
            const hasPesquisar=Array.from(document.querySelectorAll('input,button,a,*[onclick]')).some(el=>{
                const t=norm([el.value,el.innerText,el.textContent,el.title,el.name,el.id,el.getAttribute?.('onclick')].join(' '));
                return t.includes('pesquisar') && !t.includes('folha de pagamento');
            });
            const stillChoosingFolha=txt.includes('pesquisar folha') && !txt.includes('consultar espelho de contracheque da om');
            return !!(titleOk && hasPesquisar && !stillChoosingFolha);
            """)));

    private static bool WaitForSippesMirrorSelectionRowsReady(
        IWebDriver driver,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<CpexPaystubProgress>? progress,
        string message)
    {
        var limit = DateTime.UtcNow + timeout;
        var lastReport = DateTime.UtcNow.AddSeconds(-10);
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var count = CountSippesMirrorSelectionRows(driver);
                if (count > 0)
                {
                    // Se o SIPPES mostrou só alguns registros após F5, mas não trouxe o seletor de Exibidos,
                    // ainda não consideramos a lista pronta. Esse é o caso que acabava gerando relatório parcial.
                    if (!HasCompleteSippesMirrorSelectionPage(driver))
                    {
                        if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
                        {
                            lastReport = DateTime.UtcNow;
                            progress?.Report(new CpexPaystubProgress { Message = $"{message}... encontrei {count} registro(s), mas ainda sem o seletor de Exibidos. Vou continuar aguardando para não gerar relatório parcial." });
                        }
                    }
                    else
                    {
                        if (ct.WaitHandle.WaitOne(1200)) throw new OperationCanceledException(ct);
                        if (CountSippesMirrorSelectionRows(driver) > 0 && HasCompleteSippesMirrorSelectionPage(driver)) return true;
                    }
                }
            }
            catch { }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new CpexPaystubProgress { Message = $"{message}... {(int)(DateTime.UtcNow - started).TotalSeconds}s. O SIPPES pode demorar vários minutos; a tela está visível para acompanhar." });
            }
            if (ct.WaitHandle.WaitOne(1200)) throw new OperationCanceledException(ct);
        }
        return false;
    }

    private static int CountSippesMirrorSelectionRows(IWebDriver driver)
        => RunInSippesIntContexts(driver, () => Convert.ToInt32(((IJavaScriptExecutor)driver).ExecuteScript("""
            const boxes=Array.from(document.querySelectorAll('input[type="checkbox"][name="codigo"]'))
                .filter(x => (x.value||'').toLowerCase() !== 'checkbox' && (x.value||'').trim() !== '');
            if(boxes.length) return boxes.length;
            const txt=(document.body?.innerText||'').toLowerCase();
            if(!txt.includes('resultados encontrados') && !txt.includes('total de registros')) return 0;
            return Array.from(document.querySelectorAll('tbody tr,tr')).filter(tr=>/processada|definitivamente|normal/i.test(tr.innerText||'')).length;
            """)));

    private static bool HasCompleteSippesMirrorSelectionPage(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const txt=norm(document.body?.innerText||'');
            const boxes=Array.from(document.querySelectorAll('input[type="checkbox"][name="codigo"]'))
                .filter(x => (x.value||'').toLowerCase() !== 'checkbox' && (x.value||'').trim() !== '');
            if(!boxes.length) return false;

            const hasExibidosSelect=Array.from(document.querySelectorAll('select')).some(s=>{
                const info=norm([s.name,s.id,s.title,s.getAttribute?.('onchange'),s.parentElement?.innerText].join(' '));
                const hasBigOption=Array.from(s.options||[]).some(o=>Number((o.value||o.textContent||'').replace(/\D/g,'')) >= 50);
                return info.includes('registro') || info.includes('exibid') || info.includes('pagina') || hasBigOption;
            });
            if(hasExibidosSelect) return true;

            const m=txt.match(/total\s+de\s+registros\s*[:\-]?\s*(\d+)/i) || txt.match(/total\s*[:\-]?\s*(\d+)/i);
            if(m){
                const total=Number(m[1]||'0')||0;
                if(total > boxes.length && total > 20) return false;
            }

            // Se não há indicação de paginação/total maior, aceita a lista como completa.
            return true;
            """)));

    private static bool TryMaximizeSippesMirrorPageSize(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().toLowerCase();
            const fire=el=>{ try{el.dispatchEvent(new Event('input',{bubbles:true}));}catch(e){} try{el.dispatchEvent(new Event('change',{bubbles:true}));}catch(e){} };
            const selects=Array.from(document.querySelectorAll('select'));
            let target=selects.find(s=>norm(s.name).includes('registros') || norm(s.id).includes('registros') || Array.from(s.options).some(o=>Number(o.value)>=100));
            if(!target) return false;
            const options=Array.from(target.options)
                .map(o=>({value:o.value, text:o.textContent||'', n:Number((o.value||o.textContent||'').replace(/\D/g,''))||0}))
                .sort((a,b)=>b.n-a.n);
            const best=options.find(o=>o.n>0);
            if(!best) return false;
            if(target.value!==best.value){ target.value=best.value; fire(target); }
            try{ if(typeof carregarPagina==='function'){ carregarPagina(); return true; } }catch(e){}
            return true;
            """)));

    private static bool MarkAllSippesMirrorRows(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const boxes=Array.from(document.querySelectorAll('input[type="checkbox"][name="codigo"]'))
                .filter(x => (x.value||'').toLowerCase() !== 'checkbox' && (x.value||'').trim() !== '');
            if(!boxes.length) return false;
            boxes.forEach(cb=>{ try{cb.disabled=false; cb.checked=true; cb.setAttribute('checked','checked'); cb.dispatchEvent(new Event('change',{bubbles:true}));}catch(e){} });
            try{ window.codigoSelecionados = boxes.map(cb=>cb.value); }catch(e){}
            try{ if(window.parent) window.parent.codigoSelecionados = boxes.map(cb=>cb.value); }catch(e){}
            const header=Array.from(document.querySelectorAll('input[type="checkbox"]')).find(cb=>(cb.value||'').toLowerCase()==='checkbox' || /todos|selecionar/i.test([cb.name,cb.id,cb.title,cb.getAttribute?.('onclick')].join(' ')));
            if(header){ try{ header.checked=true; }catch(e){} }
            return boxes.every(cb=>cb.checked);
            """)));

    private static bool ClickSippesMirrorGenerateReport(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            try{ if(typeof gerarRelatorio==='function'){ gerarRelatorio(); return true; } }catch(e){}
            try{ if(window.parent && typeof window.parent.gerarRelatorio==='function'){ window.parent.gerarRelatorio(); return true; } }catch(e){}
            const candidates=Array.from(document.querySelectorAll('input[type="button"],input[type="submit"],button,a,*[onclick]'));
            const btn=candidates.find(el=>{
                const txt=norm([el.value,el.innerText,el.textContent,el.title,el.name,el.id,el.getAttribute?.('onclick')].join(' '));
                return (txt.includes('gerar relatorio') || txt.includes('consultar relatorio') || txt.includes('relatorio')) && !txt.includes('voltar');
            });
            if(btn){ try{btn.removeAttribute('disabled');}catch(e){} try{btn.click(); return true;}catch(e){} }
            return false;
            """)));

    private static bool WaitForSippesMirrorReportReady(
        IWebDriver driver,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<CpexPaystubProgress>? progress)
    {
        var limit = DateTime.UtcNow + timeout;
        var lastReport = DateTime.UtcNow.AddSeconds(-10);
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (IsSippesMirrorReportReady(driver))
                {
                    if (ct.WaitHandle.WaitOne(1200)) throw new OperationCanceledException(ct);
                    if (IsSippesMirrorReportReady(driver)) return true;
                }
            }
            catch { }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new CpexPaystubProgress { Message = $"Aguardando relatório detalhado do Espelho da OM... {(int)(DateTime.UtcNow - started).TotalSeconds}s. Veja se a página ainda está processando no SIPPES." });
            }
            if (ct.WaitHandle.WaitOne(1200)) throw new OperationCanceledException(ct);
        }
        return false;
    }

    private static bool IsSippesMirrorReportReady(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const norm=s=>(s||'').toString().normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase();
            const txt=norm(document.body?.innerText||'');
            return (txt.includes('detalhar espelho de contracheque') || txt.includes('resultado da consulta'))
                   && txt.includes('favorecido')
                   && (txt.includes('lancamentos receitas') || txt.includes('lançamentos receitas') || txt.includes('total liquido') || txt.includes('total líquido'));
            """)));

    private static string ReadCurrentHtml(IWebDriver driver)
    {
        try
        {
            return RunInSippesStringContexts(driver, () => Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return document.documentElement ? document.documentElement.outerHTML : '';"), CultureInfo.InvariantCulture) ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    private static string ReadCurrentBodyText(IWebDriver driver)
    {
        try
        {
            return RunInSippesStringContexts(driver, () => Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return document.body ? document.body.innerText : '';"), CultureInfo.InvariantCulture) ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    private static int RunInSippesIntContexts(IWebDriver driver, Func<int> action, int maxDepth = 5)
    {
        try { driver.SwitchTo().DefaultContent(); } catch { }
        var best = 0;
        Visit(0);
        return best;

        void Visit(int depth)
        {
            try { best = Math.Max(best, action()); } catch { }
            if (depth >= maxDepth) return;
            int frameCount;
            try { frameCount = driver.FindElements(By.CssSelector("iframe,frame")).Count; }
            catch { return; }
            for (var index = 0; index < frameCount; index++)
            {
                try
                {
                    var frames = driver.FindElements(By.CssSelector("iframe,frame"));
                    if (index >= frames.Count) continue;
                    driver.SwitchTo().Frame(frames[index]);
                    Visit(depth + 1);
                }
                catch { }
                try { driver.SwitchTo().ParentFrame(); }
                catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
            }
        }
    }

    private static string RunInSippesStringContexts(IWebDriver driver, Func<string> action, int maxDepth = 5)
    {
        try { driver.SwitchTo().DefaultContent(); } catch { }
        var best = string.Empty;
        Visit(0);
        return best;

        void Visit(int depth)
        {
            try
            {
                var value = action();
                if (!string.IsNullOrWhiteSpace(value) && value.Length > best.Length) best = value;
            }
            catch { }
            if (depth >= maxDepth) return;
            int frameCount;
            try { frameCount = driver.FindElements(By.CssSelector("iframe,frame")).Count; }
            catch { return; }
            for (var index = 0; index < frameCount; index++)
            {
                try
                {
                    var frames = driver.FindElements(By.CssSelector("iframe,frame"));
                    if (index >= frames.Count) continue;
                    driver.SwitchTo().Frame(frames[index]);
                    Visit(depth + 1);
                }
                catch { }
                try { driver.SwitchTo().ParentFrame(); }
                catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
            }
        }
    }

    private static List<SippesOmPaystubMirrorPerson> ExtractSippesMirrorPeopleFromHtmlText(string html, string text)
    {
        var bodyText = string.IsNullOrWhiteSpace(text) ? HtmlToLooseText(html) : text;
        bodyText = bodyText.Replace('\u00a0', ' ');
        bodyText = Regex.Replace(bodyText, @"[ \t]+", " ", RegexOptions.CultureInvariant);
        bodyText = Regex.Replace(bodyText, @"\r\n|\r", "\n", RegexOptions.CultureInvariant);
        var sheet = Regex.Match(bodyText, @"Folha\s+de\s+Pagamento\s*:?\s*(?<v>[^\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["v"].Value.Trim();
        var globalStatus = Regex.Match(bodyText, @"Situa[cç][aã]o\s*:?\s*(?<v>[^\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["v"].Value.Trim();
        var sections = Regex.Split(bodyText, @"(?=\bFavorecido\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Where(x => Regex.IsMatch(x, @"\bNome\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        var people = new List<SippesOmPaystubMirrorPerson>();
        foreach (var section in sections)
        {
            var clean = Regex.Replace(section, @"[ \t]+", " ", RegexOptions.CultureInvariant).Trim();
            var name = MatchValue(clean, @"Nome\s*:\s*(?<v>.*?)(?:\s+CPF\s*:|\n)");
            var cpf = MilitaryFormatting.Digits(MatchValue(clean, @"CPF\s*:\s*(?<v>[0-9\.\-]+)"));
            var idt = MilitaryFormatting.Digits(MatchValue(clean, @"Nr\s*de\s*Id\s*/\s*Cadastro\s*:\s*(?<v>[0-9\.\-]+)"));
            var prec = MilitaryFormatting.Digits(MatchValue(clean, @"Prec\s*/\s*CP\s*:\s*(?<v>[0-9\.\-]+)"));
            var rank = MatchValue(clean, @"Posto\s*/\s*Grad\s*:\s*(?<v>.*?)(?:\s+Situa[cç][aã]o|\s+Banco|\n)");
            var om = MatchValue(clean, @"OM\s+de\s+vincula[cç][aã]o\s*:\s*(?<v>[^\n]+)");
            var status = MatchValue(clean, @"Situa[cç][aã]o\s+da\s+folha\s*:\s*(?<v>[^\n]+)");
            if (string.IsNullOrWhiteSpace(status)) status = globalStatus;
            var netValue = ParseBrazilianDecimal(MatchValue(clean, @"Total\s+l[ií]quido\s*(?<v>[0-9\.]+,[0-9]{2})"));

            if (string.IsNullOrWhiteSpace(name) || (string.IsNullOrWhiteSpace(cpf) && string.IsNullOrWhiteSpace(idt))) continue;
            people.Add(new SippesOmPaystubMirrorPerson
            {
                MilitaryId = idt,
                Cpf = cpf,
                Name = CleanSippesText(name),
                Rank = CleanSippesText(rank),
                PrecCp = prec,
                Om = CleanSippesText(om),
                PaymentSheet = CleanSippesText(sheet),
                PaymentStatus = CleanSippesText(status),
                NetValue = netValue,
                SourceText = clean,
                Rubrics = ExtractSippesMirrorRubricsFromSection(clean)
            });
        }

        return people
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }


    private static List<SippesOmPaystubRubricValue> ExtractSippesMirrorRubricsFromSection(string section)
    {
        var result = new List<SippesOmPaystubRubricValue>();
        if (string.IsNullOrWhiteSpace(section)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in Regex.Split(section.Replace('\u00a0', ' '), @"\r\n|\n|\r"))
        {
            var line = Regex.Replace(rawLine, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            if (line.Length == 0 || line.Contains("Total receitas", StringComparison.OrdinalIgnoreCase) || line.Contains("Total despesas", StringComparison.OrdinalIgnoreCase) || line.Contains("Total líquido", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Match match in Regex.Matches(line,
                         @"\b(?<code>[A-Z]{1,3}\d{3,5})\b\s+(?<description>.*?)(?<value>\d{1,3}(?:\.\d{3})*,\d{2})(?=\s+\b[A-Z]{1,3}\d{3,5}\b|\s*$)",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                AddSippesMirrorRubricIfValid(result, seen, match.Groups["code"].Value, match.Groups["description"].Value, match.Groups["value"].Value);
        }

        if (result.Count > 0) return result;

        var normalized = Regex.Replace(section.Replace('\u00a0', ' '), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        foreach (Match match in Regex.Matches(normalized,
                     @"\b(?<code>[A-Z]{1,3}\d{3,5})\b\s*(?<description>.*?)(?<value>\d{1,3}(?:\.\d{3})*,\d{2})(?=\s+\b[A-Z]{1,3}\d{3,5}\b|\s+Total\s+(?:receitas|despesas|l[ií]quido)\b|$)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline))
            AddSippesMirrorRubricIfValid(result, seen, match.Groups["code"].Value, match.Groups["description"].Value, match.Groups["value"].Value);

        return result;
    }

    private static void AddSippesMirrorRubricIfValid(List<SippesOmPaystubRubricValue> result, HashSet<string> seen, string rawCode, string rawDescription, string rawValue)
    {
        var code = NormalizeSippesMirrorRubricCode(rawCode);
        var description = CleanSippesText(rawDescription);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(description)) return;
        if (description.Contains("Total receitas", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Total despesas", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Total líquido", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Código", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Descrição", StringComparison.OrdinalIgnoreCase))
            return;
        var amount = ParseBrazilianDecimal(rawValue);
        if (!amount.HasValue || amount.Value <= 0) return;
        var key = $"{code}|{amount.Value:0.00}|{description}";
        if (!seen.Add(key)) return;
        result.Add(new SippesOmPaystubRubricValue
        {
            Code = code,
            Description = description,
            Value = amount.Value
        });
    }

    private static string NormalizeSippesMirrorRubricCode(string? rawCode)
    {
        var code = Regex.Replace(rawCode ?? string.Empty, @"[^A-Za-z0-9]", string.Empty, RegexOptions.CultureInvariant).ToUpperInvariant();
        var match = Regex.Match(code, @"^(?<prefix>[A-Z]{1,3})(?<digits>\d{1,5})$", RegexOptions.CultureInvariant);
        if (!match.Success) return code;
        var digits = match.Groups["digits"].Value;
        if (digits.Length < 4) digits = digits.PadLeft(4, '0');
        return match.Groups["prefix"].Value + digits;
    }

    private static string HtmlToLooseText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        decoded = Regex.Replace(decoded, @"<(br|/tr|/div|/p|/table|/fieldset|/h\d)\b[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        decoded = Regex.Replace(decoded, @"<[^>]+>", " ", RegexOptions.CultureInvariant);
        return System.Net.WebUtility.HtmlDecode(decoded);
    }

    private static string MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return match.Success ? Regex.Replace(match.Groups["v"].Value, @"\s+", " ", RegexOptions.CultureInvariant).Trim() : string.Empty;
    }

    private static decimal? ParseBrazilianDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim().Replace(".", string.Empty).Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private string SaveSippesMirrorDiagnostics(IWebDriver driver, int year, int month, string reason)
    {
        try
        {
            var dir = GetSippesMirrorDownloadDirectory(year, month);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var basePath = Path.Combine(dir, $"diagnostico_espelho_om_{reason}_{stamp}");
            File.WriteAllText(basePath + ".html", ReadCurrentHtml(driver), Encoding.UTF8);
            File.WriteAllText(basePath + ".txt", ReadCurrentBodyText(driver), Encoding.UTF8);
            return basePath + ".html";
        }
        catch { return string.Empty; }
    }

    private static string SippesActiveQueryPageUrl(int pageIndex)
        => $"{SippesActiveQueryUrl}&paginaCorrente={Math.Max(0, pageIndex).ToString(CultureInfo.InvariantCulture)}";

    private static bool WaitForSippesActiveRowsReady(
        IWebDriver driver,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<CpexPaystubProgress>? progress,
        string message)
    {
        var limit = DateTime.UtcNow + timeout;
        var lastReport = DateTime.UtcNow.AddSeconds(-10);
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow < limit)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (HasSippesActiveRows(driver))
                {
                    // Pequena estabilidade porque o SIPPES redesenha a tabela e mantém spinner visual por alguns segundos.
                    if (ct.WaitHandle.WaitOne(900)) throw new OperationCanceledException(ct);
                    if (HasSippesActiveRows(driver)) return true;
                }
            }
            catch { }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(6))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new CpexPaystubProgress { Message = $"{message}... {(int)(DateTime.UtcNow - started).TotalSeconds}s" });
            }
            if (ct.WaitHandle.WaitOne(850)) throw new OperationCanceledException(ct);
        }
        return false;
    }

    private void DownloadSippesActiveDataReports(
        IWebDriver driver,
        IReadOnlyList<SippesPersonnelRow> rows,
        string downloadDirectory,
        IProgress<CpexPaystubProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(downloadDirectory);
        TrySetChromiumDownloadDirectory(driver, downloadDirectory);
        var rootDirectory = GetSippesDadosMaRootDirectory();
        Directory.CreateDirectory(rootDirectory);

        var valid = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.ReportScript))
            .GroupBy(BuildSippesPersonnelKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (valid.Count == 0)
        {
            foreach (var row in rows)
            {
                row.ReportDownloadStatus = "Sem script do PDF na tabela";
                row.PaymentStatus = "Não baixado";
            }
            return;
        }

        for (var index = 0; index < valid.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var row = valid[index];
            progress?.Report(new CpexPaystubProgress
            {
                Current = index + 1,
                Total = valid.Count,
                Name = row.Name,
                Message = $"PDF Dados MA {index + 1}/{valid.Count}: {row.Name} — baixando novamente"
            });

            try
            {
                // Conferência online precisa sempre baixar de novo. O reaproveitamento de PDFs antigos
                // fica somente nos botões "Ler PDFs salvos" e "Selecionar PDFs…". Isso evita carregar
                // uma situação antiga/errada de uma conferência anterior quando o SIPPES travar em alguém.
                var pdfPath = DownloadOneSippesActiveDataReport(driver, row, downloadDirectory, ct);
                row.ReportPdfPath = pdfPath;
                row.PaymentStatus = ReadDadosMaPaymentStatus(pdfPath);
                row.ReportDownloadStatus = "PDF baixado novamente e lido";
            }
            catch (OperationCanceledException)
            {
                row.ReportDownloadStatus = "Cancelado pelo usuário";
                row.PaymentStatus = "Não lido";
                throw;
            }
            catch (Exception ex)
            {
                row.ReportDownloadStatus = "Falha: " + ex.Message;
                row.PaymentStatus = "Não lido";
                _log.WriteAsync($"Falha ao baixar/analisar Dados MA SIPPES de {row.Name}.", ex).GetAwaiter().GetResult();
            }
            finally
            {
                HideAutomationWindows(driver);
            }
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string DownloadOneSippesActiveDataReport(IWebDriver driver, SippesPersonnelRow row, string downloadDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(downloadDirectory);
        var output = Path.Combine(downloadDirectory, BuildDadosMaFileName(row));

        // Garante que a conferência online não leia arquivo velho com o mesmo nome.
        // Se o download falhar, a linha fica como falha/não lido, em vez de herdar situação antiga.
        TryDeleteFile(output);
        TryDeleteFile(Path.ChangeExtension(output, ".resposta_sippes.html"));

        // Caminho principal: baixa o PDF pelo próprio navegador com fetch autenticado.
        // Isso evita depender do visualizador do Edge, pasta de Downloads ou troca de aba.
        if (TryFetchSippesDataReportPdf(driver, row, output, ct, out var fetchError))
            return output;

        // Caminho de segurança: se o SIPPES bloquear fetch por alguma regra antiga, tenta acionar
        // o onclick original e capturar o arquivo baixado.
        var baseline = EnumeratePdfFiles(downloadDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var started = DateTime.UtcNow;

        if (!ExecuteSippesDataReportScript(driver, row.ReportScript))
            throw new InvalidOperationException("Não consegui acionar o ícone PDF/gerarRelatorio desta linha. " + fetchError);

        var downloaded = WaitForDownloadedPdf(downloadDirectory, baseline, started, TimeSpan.FromSeconds(75), ct);
        if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            throw new TimeoutException("O PDF de Dados MA não apareceu na pasta de download dentro do tempo limite seguro. Pulei esta pessoa para a conferência continuar. " + fetchError);

        if (!SamePath(downloaded, output))
        {
            if (File.Exists(output)) File.Delete(output);
            File.Move(downloaded, output, true);
        }
        return output;
    }

    private static bool TryFetchSippesDataReportPdf(IWebDriver driver, SippesPersonnelRow row, string output, CancellationToken ct, out string error)
    {
        error = string.Empty;
        if (!TryParseSippesDataReportArguments(row.ReportScript, out var identificacao, out var precCp))
        {
            error = "A linha não trouxe os parâmetros do gerarRelatorio(identificação, Prec-CP).";
            return false;
        }

        try
        {
            var previousAsyncTimeout = driver.Manage().Timeouts().AsynchronousJavaScript;
            driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(75);
            try
            {
                string? json = null;
                var executed = RunInSippesContexts(driver, () =>
                {
                    var raw = ((IJavaScriptExecutor)driver).ExecuteAsyncScript("""
                        const identificacao = (arguments[0] || '').toString();
                        const precCp = (arguments[1] || '').toString();
                        const callback = arguments[arguments.length - 1];

                        (async function(){
                            const findForm = () => document.forms['formularioGerarRelatorioDadosMA'] || document.forms[0] || null;
                            const ensureField = (form, name, value) => {
                                let el = form ? form.elements[name] : null;
                                if (!el && form) {
                                    el = document.createElement('input');
                                    el.type = 'hidden';
                                    el.name = name;
                                    form.appendChild(el);
                                }
                                if (el) el.value = value || '';
                            };

                            const form = findForm();
                            let body;
                            if (form) {
                                ensureField(form, 'identificacaoFavorecido', identificacao);
                                ensureField(form, 'precCpFavorecido', precCp);
                                ensureField(form, 'codomFiltro', (form.elements['codomFiltro'] && form.elements['codomFiltro'].value) || '037515');
                                ensureField(form, 'codugAbrangente', (form.elements['codugAbrangente'] && form.elements['codugAbrangente'].value) || 'false');
                                ensureField(form, 'abrangenciaCpex', (form.elements['abrangenciaCpex'] && form.elements['abrangenciaCpex'].value) || 'false');
                                ensureField(form, 'w3c', '1');
                                body = new FormData(form);
                                body.set('identificacaoFavorecido', identificacao);
                                body.set('precCpFavorecido', precCp);
                            } else {
                                body = new URLSearchParams();
                                body.set('identificacaoFavorecido', identificacao);
                                body.set('precCpFavorecido', precCp);
                                body.set('codomFiltro', '037515');
                                body.set('codugAbrangente', 'false');
                                body.set('abrangenciaCpex', 'false');
                                body.set('w3c', '1');
                            }

                            const response = await fetch('/gerarRelatorioDadosMA.do?metodo=gerarRelatorioDadosMA', {
                                method: 'POST',
                                body: body,
                                credentials: 'include',
                                cache: 'no-store'
                            });
                            const contentType = response.headers.get('content-type') || '';
                            const buffer = await response.arrayBuffer();
                            const bytes = new Uint8Array(buffer);
                            let binary = '';
                            const chunk = 0x8000;
                            for (let i = 0; i < bytes.length; i += chunk) {
                                binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
                            }
                            let prefix = '';
                            for (let i = 0; i < Math.min(bytes.length, 80); i++) prefix += String.fromCharCode(bytes[i]);
                            callback(JSON.stringify({
                                ok: response.ok,
                                status: response.status,
                                contentType: contentType,
                                url: response.url || '',
                                length: bytes.length,
                                prefix: prefix,
                                base64: btoa(binary)
                            }));
                        })().catch(function(e){
                            callback(JSON.stringify({ ok: false, error: (e && (e.stack || e.message)) || String(e) }));
                        });
                        """, identificacao, precCp);
                    json = Convert.ToString(raw, CultureInfo.InvariantCulture);
                    return !string.IsNullOrWhiteSpace(json);
                });

                if (!executed || string.IsNullOrWhiteSpace(json))
                {
                    error = "Não consegui executar o fetch autenticado no contexto do SIPPES.";
                    return false;
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
                var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetRawText() : string.Empty;
                var contentType = root.TryGetProperty("contentType", out var contentTypeElement) ? contentTypeElement.GetString() ?? string.Empty : string.Empty;
                var prefix = root.TryGetProperty("prefix", out var prefixElement) ? prefixElement.GetString() ?? string.Empty : string.Empty;
                if (!ok)
                {
                    var jsError = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
                    error = string.IsNullOrWhiteSpace(jsError)
                        ? $"Fetch retornou HTTP {status} ({contentType})."
                        : $"Fetch falhou: {jsError}";
                    return false;
                }

                if (!root.TryGetProperty("base64", out var base64Element))
                {
                    error = $"Fetch não retornou base64. HTTP {status} ({contentType}).";
                    return false;
                }

                var base64 = base64Element.GetString();
                if (string.IsNullOrWhiteSpace(base64))
                {
                    error = $"Fetch retornou resposta vazia. HTTP {status} ({contentType}).";
                    return false;
                }

                var bytes = Convert.FromBase64String(base64);
                var looksLikePdf = bytes.Length > 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F';
                if (!looksLikePdf)
                {
                    var diagnostic = Path.ChangeExtension(output, ".resposta_sippes.html");
                    try { File.WriteAllBytes(diagnostic, bytes); } catch { }
                    error = $"SIPPES respondeu, mas não foi PDF. HTTP {status}, tipo {contentType}, início: {prefix}. Diagnóstico: {diagnostic}";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
                File.WriteAllBytes(output, bytes);
                return true;
            }
            finally
            {
                try { driver.Manage().Timeouts().AsynchronousJavaScript = previousAsyncTimeout; } catch { }
            }
        }
        catch (Exception ex) when (ex is WebDriverException or TimeoutException or JsonException or FormatException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseSippesDataReportArguments(string? script, out string identificacao, out string precCp)
    {
        identificacao = string.Empty;
        precCp = string.Empty;
        var text = script ?? string.Empty;
        var match = Regex.Match(text, @"gerarRelatorio\s*\(\s*['""]?(?<idt>\d{5,})['""]?\s*,\s*['""]?(?<prec>\d{5,})['""]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        identificacao = match.Groups["idt"].Value;
        precCp = match.Groups["prec"].Value;
        return !string.IsNullOrWhiteSpace(identificacao) && !string.IsNullOrWhiteSpace(precCp);
    }

    private static bool ExecuteSippesDataReportScript(IWebDriver driver, string script)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const raw = (arguments[0] || '').toString();
            const clean = s => (s || '').toString().replace(/\s+/g, ' ').trim();
            const candidates = Array.from(document.querySelectorAll('[onclick], a, img, input, button'));
            const byExact = candidates.find(el => clean(el.getAttribute('onclick') || '') === clean(raw));
            if (byExact) { try { byExact.click(); return true; } catch(e){} }

            const m = raw.match(/gerarRelatorio\s*\((.*)\)/i);
            if (m && typeof gerarRelatorio === 'function') {
                const args = [];
                const re = /'([^']*)'|"([^"]*)"|([^,\s\)]+)/g;
                let item;
                while ((item = re.exec(m[1])) !== null) args.push(item[1] || item[2] || item[3] || '');
                try { gerarRelatorio.apply(window, args); return true; } catch(e) {}
            }

            if (/gerarRelatorio/i.test(raw)) {
                try { (0, eval)(raw); return true; } catch(e) {}
            }

            const byLoose = candidates.find(el => /gerarRelatorio|pdf|relatorio/i.test(clean((el.getAttribute('onclick') || '') + ' ' + (el.title || '') + ' ' + (el.alt || '') + ' ' + (el.src || ''))));
            if (byLoose) { try { byLoose.click(); return true; } catch(e){} }
            return false;
            """, script)));

    private static string BuildDadosMaFileName(SippesPersonnelRow row)
    {
        var cpf = MilitaryFormatting.Digits(row.Cpf);
        var idt = MilitaryFormatting.Digits(row.MilitaryId);
        var key = !string.IsNullOrWhiteSpace(cpf) ? MilitaryFormatting.FormatCpf(cpf) : idt;
        var name = CleanLegacyPersonName(row.Name);
        var rank = CleanLegacyPersonName(MilitaryRankService.ShortName(row.Rank));
        var prefix = string.Join(" - ", new[] { key, rank, name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return $"{prefix} - Dados MA SIPPES.pdf";
    }

    private static string ReadDadosMaPaymentStatus(string pdfPath)
    {
        try
        {
            var text = ReadPdfText(pdfPath);
            return ExtractDadosMaPaymentStatusFromText(text);
        }
        catch
        {
            return "Não lido";
        }
    }

    private static string ExtractDadosMaPaymentStatusFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Situação não localizada";

        // Primeiro separa situações impeditivas. Elas são mais importantes que qualquer
        // ocorrência solta de "normal" que possa aparecer no PDF.
        var global = NormalizeDadosMaPaymentValue(text, allowGenericNormal: false);
        if (!string.IsNullOrWhiteSpace(global) && !string.Equals(global, "Pagamento Normal", StringComparison.OrdinalIgnoreCase)) return global;

        var labelWindow = ExtractDadosMaSituationWindow(text);
        if (!string.IsNullOrWhiteSpace(labelWindow))
        {
            var fromWindow = FindDadosMaPaymentStatusInTextWindow(labelWindow);
            if (!string.IsNullOrWhiteSpace(fromWindow)) return fromWindow;
        }

        // Alguns PDFs do SIPPES vêm com o valor em linha isolada ou com quebra entre
        // "Pagamento" e "Normal". Aqui aceitamos "Normal" só quando há o rótulo do campo 11
        // no próprio PDF, evitando marcar qualquer PDF aleatório como normal.
        var normalized = Normalize(text);
        var compact = Regex.Replace(normalized, @"\s+", string.Empty, RegexOptions.CultureInvariant);
        var hasSituationLabel = normalized.Contains("situacao do militar na om") || normalized.Contains("situacao do militar") || normalized.Contains("11 situacao");
        if (hasSituationLabel)
        {
            if (compact.Contains("pagamentonormal", StringComparison.OrdinalIgnoreCase)) return "Pagamento Normal";
            if (Regex.IsMatch(normalized, @"(^|\s)normal(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Normal";
            if (Regex.IsMatch(normalized, @"(^|\s)(regular|em pagamento|ativo na om)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Normal";

            // No Dados MA o campo 11 sempre representa a situação do militar na OM. Alguns PDFs com
            // situação normal vêm com a palavra "Normal" quebrada/fora da ordem no texto extraído pelo
            // PdfPig. Como as situações impeditivas já foram procuradas no PDF inteiro acima, se existe
            // o campo 11 e nenhuma situação suspensa/bloqueada/transferida/cancelada foi encontrada,
            // o SIGFUR classifica como Pagamento Normal em vez de deixar "situação não localizada".
            return "Pagamento Normal";
        }

        if (!string.IsNullOrWhiteSpace(global)) return global;
        return "Situação não localizada";
    }

    private static string ExtractDadosMaSituationWindow(string text)
    {
        var cleanText = text.Replace('\u00a0', ' ');
        var normalized = Normalize(cleanText);
        var compactNormalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant);

        var labels = new[]
        {
            "situacao do militar na om",
            "situacao do militar",
            "11 situacao"
        };

        var normalizedIndex = labels.Select(label => compactNormalized.IndexOf(label, StringComparison.OrdinalIgnoreCase))
            .Where(x => x >= 0)
            .DefaultIfEmpty(-1)
            .Min();

        var lines = cleanText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanSippesText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var lineIndex = lines.FindIndex(line =>
        {
            var n = Normalize(line);
            return n.Contains("situacao do militar na om") || n.Contains("situacao do militar") || n.Contains("11 situacao");
        });

        if (lineIndex >= 0)
        {
            var windowLines = lines.Skip(lineIndex).Take(45).ToList();
            var end = windowLines.FindIndex(1, line =>
            {
                var n = Normalize(line);
                return n.Contains("endereco residencial") || n.Contains("dados academicos") || n.Contains("dados para fins de pagamento");
            });
            if (end > 0) windowLines = windowLines.Take(end).ToList();
            return string.Join('\n', windowLines);
        }

        if (normalizedIndex >= 0)
        {
            // Fallback quando o PdfPig colapsa o texto em bloco: usa a região normalizada.
            var tail = compactNormalized.Substring(normalizedIndex, Math.Min(compactNormalized.Length - normalizedIndex, 1800));
            return tail;
        }

        return string.Empty;
    }

    private static string FindDadosMaPaymentStatusInTextWindow(string text)
    {
        var lines = text.Replace('\u00a0', ' ')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanSippesText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(45)
            .ToList();

        foreach (var line in lines)
        {
            var value = NormalizeDadosMaPaymentValue(line, allowGenericNormal: true);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        var joined = string.Join(' ', lines);
        return NormalizeDadosMaPaymentValue(joined, allowGenericNormal: true);
    }

    private static string NormalizeDadosMaPaymentValue(string? value, bool allowGenericNormal = true)
    {
        var raw = CleanSippesText(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = Normalize(raw);
        var compact = Regex.Replace(text, @"\s+", string.Empty, RegexOptions.CultureInvariant);

        if (compact.Contains("pagamentosuspenso", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"(^|\s)suspens[oa](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Suspenso";
        if (compact.Contains("pagamentobloqueado", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"(^|\s)bloquead[oa](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Bloqueado";
        if (compact.Contains("pagamentotransferido", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"(^|\s)transferid[oa](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Transferido";
        if (compact.Contains("pagamentocancelado", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"(^|\s)cancelad[oa](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Cancelado";
        if (compact.Contains("pagamentoparalisado", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"(^|\s)paralisad[oa](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Paralisado";
        if (compact.Contains("pagamentoinativo", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"(^|\s)inativ[oa](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Inativo";

        if (compact.Contains("pagamentonormal", StringComparison.OrdinalIgnoreCase)) return "Pagamento Normal";
        if (!allowGenericNormal) return string.Empty;

        if (Regex.IsMatch(text, @"(^|\s)normal(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Normal";
        if (Regex.IsMatch(text, @"(^|\s)(regular|em pagamento|ativo na om)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Pagamento Normal";
        return string.Empty;
    }

    private static string ReadPdfText(string pdfPath)
    {
        var builder = new StringBuilder();
        using var pdf = PdfDocument.Open(pdfPath);
        foreach (var page in pdf.GetPages()) builder.AppendLine(page.Text ?? string.Empty);
        return builder.ToString();
    }

    private static bool TryAttachExistingSippesDataReport(SippesPersonnelRow row, string downloadDirectory, string rootDirectory)
    {
        var candidates = new List<string>();
        var todayExpected = Path.Combine(downloadDirectory, BuildDadosMaFileName(row));
        if (File.Exists(todayExpected)) candidates.Add(todayExpected);

        var cpf = MilitaryFormatting.Digits(row.Cpf);
        var idt = MilitaryFormatting.Digits(row.MilitaryId);
        var nameKey = Normalize(row.Name);
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(rootDirectory, "*.pdf", SearchOption.AllDirectories)
                .Where(path => IsLikelyDadosMaPdfForRow(path, row, cpf, idt, nameKey))
                .OrderByDescending(File.GetLastWriteTime)
                .Take(8));
        }
        catch { }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 1024) continue;
            row.ReportPdfPath = path;
            row.PaymentStatus = ReadDadosMaPaymentStatus(path);
            row.ReportDownloadStatus = "PDF salvo reaproveitado";
            return true;
        }

        return false;
    }

    private static bool IsLikelyDadosMaPdfForRow(string path, SippesPersonnelRow row, string cpf, string idt, string nameKey)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        var fileDigits = MilitaryFormatting.Digits(file);
        if (!string.IsNullOrWhiteSpace(cpf) && cpf.Length >= 9 && fileDigits.Contains(cpf, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(idt) && idt.Length >= 5 && fileDigits.Contains(idt, StringComparison.OrdinalIgnoreCase)) return true;
        var fileName = Normalize(file);
        return !string.IsNullOrWhiteSpace(nameKey) && nameKey.Length >= 10 && fileName.Contains(nameKey, StringComparison.OrdinalIgnoreCase);
    }

    private static SippesPersonnelRow? TryReadSippesDataReportFromPdf(string pdfPath)
    {
        try
        {
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length < 1024) return null;
            var text = ReadPdfText(pdfPath);
            var normalizedText = Normalize(text);
            if (!normalizedText.Contains("relatorio de dados de militar da ativa")
                && !normalizedText.Contains("dados de militar da ativa")
                && !normalizedText.Contains("situacao do militar na om"))
            {
                return null;
            }

            var row = new SippesPersonnelRow
            {
                ReportPdfPath = pdfPath,
                ReportDownloadStatus = "PDF salvo lido",
                PaymentStatus = ExtractDadosMaPaymentStatusFromText(text)
            };

            FillSippesRowFromDadosMaFileName(row, pdfPath);
            FillSippesRowFromDadosMaText(row, text);

            if (string.IsNullOrWhiteSpace(row.Name) && string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(row.Cpf))) return null;
            return row;
        }
        catch
        {
            return null;
        }
    }

    private static void FillSippesRowFromDadosMaFileName(SippesPersonnelRow row, string pdfPath)
    {
        var file = Path.GetFileNameWithoutExtension(pdfPath);
        var match = Regex.Match(file, @"^(?<key>[\d\.\-]+)\s+-\s+(?<rank>.*?)\s+-\s+(?<name>.*?)\s+-\s+Dados\s+MA\s+SIPPES$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return;

        var keyDigits = MilitaryFormatting.Digits(match.Groups["key"].Value);
        if (keyDigits.Length >= 10) row.Cpf = keyDigits;
        else if (keyDigits.Length >= 5) row.MilitaryId = keyDigits;
        row.Rank = CleanSippesText(match.Groups["rank"].Value);
        row.Name = CleanSippesText(match.Groups["name"].Value);
    }

    private static void FillSippesRowFromDadosMaText(SippesPersonnelRow row, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (string.IsNullOrWhiteSpace(row.Cpf))
        {
            var cpf = Regex.Match(text, @"\b\d{11}\b", RegexOptions.CultureInvariant);
            if (cpf.Success) row.Cpf = cpf.Value;
        }

        if (string.IsNullOrWhiteSpace(row.Name))
        {
            var name = Regex.Match(text, @"Nome\s+Completo\s*:\s*(?<name>[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-ZÁÉÍÓÚÂÊÔÃÕÇ ]{5,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            row.Name = name.Success ? CleanSippesText(name.Groups["name"].Value) : ExtractLikelyDadosMaName(text);
        }

        if (string.IsNullOrWhiteSpace(row.Rank))
        {
            var rank = Regex.Match(text, @"Posto/Gradua[cç][aã]o\s*:\s*(?<rank>[^\r\n]{2,60})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (rank.Success) row.Rank = CleanSippesText(rank.Groups["rank"].Value);
        }

        if (string.IsNullOrWhiteSpace(row.MilitaryId))
        {
            var idt = Regex.Match(text, @"Identidade\s*:\s*(?<idt>\d{6,12})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (idt.Success) row.MilitaryId = idt.Groups["idt"].Value;
        }

        if (string.IsNullOrWhiteSpace(row.Om))
        {
            var om = Regex.Match(text, @"Organiza[cç][aã]o\s+Militar\s*:\s*(?<om>[^\r\n]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (om.Success) row.Om = CleanSippesText(om.Groups["om"].Value);
        }
    }

    private static string ExtractLikelyDadosMaName(string text)
    {
        var lines = text.Replace('\u00a0', ' ')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanSippesText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var nomeIndex = lines.FindIndex(x => Normalize(x).Contains("nome completo"));
        var start = nomeIndex >= 0 ? nomeIndex + 1 : Math.Max(0, lines.FindIndex(x => Normalize(x).Contains("dados cadastrais")) + 1);
        var endMarker = lines.FindIndex(start, x => Normalize(x).Contains("endereco residencial") || Normalize(x).Contains("dados academicos"));
        if (endMarker < 0) endMarker = Math.Min(lines.Count, start + 35);

        foreach (var line in lines.Skip(start).Take(Math.Max(0, endMarker - start)))
        {
            var candidate = CleanSippesText(line);
            var normalized = Normalize(candidate);
            if (candidate.Length < 7 || candidate.Length > 90) continue;
            if (Regex.IsMatch(candidate, @"\d")) continue;
            if (!Regex.IsMatch(candidate, @"[A-Za-zÀ-ÿ]+\s+[A-Za-zÀ-ÿ]+")) continue;
            if (normalized.Contains("posto graduacao") || normalized.Contains("arma quadro") || normalized.Contains("subcategoria") || normalized.Contains("identidade") || normalized.Contains("cadastro") || normalized.Contains("prec cp") || normalized.Contains("situacao") || normalized.Contains("pagamento") || normalized.Contains("temporario")) continue;
            if (normalized.Contains("ministerio") || normalized.Contains("exercito") || normalized.Contains("secretaria") || normalized.Contains("sistema de pagamento") || normalized.Contains("relatorio")) continue;
            return candidate.ToUpperInvariant();
        }

        return string.Empty;
    }

    private static int ReadSippesVisibleRowsCount(IWebDriver driver)
    {
        var count = 0;
        RunInSippesContexts(driver, () =>
        {
            var value = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const clean = s => (s || '').toString().replace(/\u00a0/g, ' ').replace(/[\u00ad\ufffd]/g, '').replace(/\s+/g, ' ').trim();
                const digits = s => clean(s).replace(/\D/g, '');
                const trs = Array.from(document.querySelectorAll('#table-grid tr, table tr'));
                let n = 0;
                for (const tr of trs) {
                    const cells = Array.from(tr.querySelectorAll('td')).map(td => clean(td.innerText || td.textContent)).filter(Boolean);
                    if (cells.length < 3) continue;
                    const joined = cells.join(' ');
                    if (/Nr\s+de\s+Idt|CPF|Posto|Gradua|Resultados\s+encontrados|Total\s+de\s+registros|Página\s+\d+\s+de/i.test(joined)) continue;
                    if (digits(cells[0]).length >= 5 || digits(cells[1] || '').length >= 7) n++;
                }
                return String(n);
                """), CultureInfo.InvariantCulture) ?? string.Empty;
            _ = int.TryParse(MilitaryFormatting.Digits(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
            return count > 0;
        });
        return count;
    }

    private static int SelectMaximumSippesRowsPerPage(IWebDriver driver, CancellationToken ct)
    {
        var selected = 0;
        RunInSippesContexts(driver, () =>
        {
            var value = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const clean = s => (s || '').toString().replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                const toNumber = s => {
                    const n = parseInt(clean(s).replace(/\D/g, ''), 10);
                    return Number.isFinite(n) ? n : 0;
                };
                let best = null;
                const preferred = document.querySelector('select#registrosPorPagina, select[name="registrosPorPagina"]');
                const selects = preferred ? [preferred] : Array.from(document.querySelectorAll('select'));
                for (const sel of selects) {
                    const hint = clean((sel.name || '') + ' ' + (sel.id || '') + ' ' + (sel.getAttribute('onchange') || '') + ' ' + (sel.parentElement ? sel.parentElement.innerText : '')).toLowerCase();
                    const likely = preferred === sel || hint.includes('registro') || hint.includes('exibid') || hint.includes('pagina') || hint.includes('página') || hint.includes('carregarpagina');
                    if (!likely) continue;
                    const options = Array.from(sel.options || []);
                    for (let i = 0; i < options.length; i++) {
                        const opt = options[i];
                        const n = Math.max(toNumber(opt.value), toNumber(opt.textContent));
                        if (n < 20) continue;
                        if (!best || n > best.n) best = { n, sel, opt, index: i };
                    }
                }
                if (!best) return '';
                try { best.sel.selectedIndex = best.index; } catch(e) {}
                try { best.sel.value = best.opt.value; } catch(e) {}
                try { best.opt.selected = true; } catch(e) {}
                try { best.sel.dispatchEvent(new Event('input', { bubbles: true })); } catch(e) {}
                try { best.sel.dispatchEvent(new Event('change', { bubbles: true })); } catch(e) {}
                try { if (typeof best.sel.onchange === 'function') best.sel.onchange.call(best.sel); } catch(e) {}
                try { if (typeof carregarPagina === 'function') carregarPagina(0); } catch(e) {}
                return String(best.n);
                """), CultureInfo.InvariantCulture) ?? string.Empty;
            _ = int.TryParse(MilitaryFormatting.Digits(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out selected);
            return selected > 0;
        });
        if (ct.WaitHandle.WaitOne(5000)) throw new OperationCanceledException(ct);
        return selected;
    }

    private static int ReadSippesActiveTotal(IWebDriver driver)
    {
        var total = 0;
        RunInSippesContexts(driver, () =>
        {
            var value = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const text=(document.body && document.body.innerText || '').replace(/\u00a0/g,' ').replace(/\s+/g,' ');
                const m=text.match(/Total\s+de\s+registros\s*[:\-]?\s*(\d+)/i);
                if(m) return m[1];
                const m2=text.match(/registros\s*[:\-]?\s*(\d+)/i);
                return m2 ? m2[1] : '';
                """), CultureInfo.InvariantCulture) ?? string.Empty;
            _ = int.TryParse(MilitaryFormatting.Digits(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
            return total > 0;
        });
        return total;
    }


    private static int ReadSippesActivePageCount(IWebDriver driver)
    {
        var pages = 0;
        RunInSippesContexts(driver, () =>
        {
            var value = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const text=(document.body && document.body.innerText || '').replace(/\u00a0/g,' ').replace(/\s+/g,' ');
                const m=text.match(/Página\s+\d+\s+de\s+(\d+)/i);
                if(m) return m[1];
                const nums=Array.from(document.querySelectorAll('a,input,button'))
                    .map(el=>((el.innerText||el.value||'')+'').trim())
                    .filter(x=>/^\d+$/.test(x))
                    .map(x=>parseInt(x,10));
                return nums.length ? String(Math.max(...nums)) : '';
                """), CultureInfo.InvariantCulture) ?? string.Empty;
            _ = int.TryParse(MilitaryFormatting.Digits(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out pages);
            return pages > 0;
        });
        return pages;
    }

    private static bool HasSippesActiveRows(IWebDriver driver)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const clean = s => (s || '').toString().replace(/\u00a0/g, ' ').replace(/[\u00ad\ufffd]/g, '').replace(/\s+/g, ' ').trim();
            const digits = s => clean(s).replace(/\D/g, '');
            const isHeader = s => /Nr\s+de\s+Idt|Ident|Cadastro|CPF|Posto|Gradua|Resultados\s+encontrados|Total\s+de\s+registros|Página\s+\d+\s+de|LEGISLAÇÃO|CONSULTAR/i.test(clean(s));
            const looksLikeData = cells => {
                cells = (cells || []).map(clean).filter(Boolean);
                if (cells.length < 3) return false;
                const joined = cells.join(' ');
                if (isHeader(joined)) return false;
                const id = digits(cells[0]);
                const cpf = digits(cells[1] || '');
                const text = joined.toUpperCase();
                const hasName = /[A-ZÀ-Ý]{3,}\s+[A-ZÀ-Ý]{2,}/.test(text);
                const hasRankOrOm = /(SOLDADO|CABO|SARGENTO|TENENTE|CAPIT|MAJOR|ASP|SGT|CB|SD|CIA|\d{3,}\s*-)/i.test(joined);
                return (id.length >= 5 || cpf.length >= 7) && hasName && hasRankOrOm;
            };
            const trs = Array.from(document.querySelectorAll('#table-grid tr, table tr'));
            if (trs.some(tr => looksLikeData(Array.from(tr.querySelectorAll('td')).map(td => td.innerText || td.textContent)))) return true;
            const text = clean(document.body && document.body.innerText || '');
            return /\b\d{5,}[-.\d]*\s+[\d.*-]{7,}\s+[A-ZÀ-Ý]{3,}\s+[A-ZÀ-Ý]{2,}/i.test(text);
            """)));

    private static void GoToSippesActivePage(IWebDriver driver, int pageIndex)
        => RunInSippesContexts(driver, () => Convert.ToBoolean(((IJavaScriptExecutor)driver).ExecuteScript("""
            const page=Number(arguments[0]||0);
            try{
                if(typeof carregarPagina === 'function'){
                    carregarPagina(page);
                    return true;
                }
            }catch(e){}
            const label=String(page+1);
            const link=Array.from(document.querySelectorAll('a,input,button')).find(el=>((el.innerText||el.value||'').trim())===label);
            if(link){ link.click(); return true; }
            return page===0;
            """, pageIndex)));

    private static List<SippesPersonnelRow> ExtractSippesActiveRows(IWebDriver driver)
    {
        var json = string.Empty;
        var bestCount = 0;
        RunInSippesContexts(driver, () =>
        {
            var candidate = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("""
                const clean = s => (s || '').toString()
                    .replace(/\u00a0/g, ' ')
                    .replace(/[\u00ad\ufffd]/g, '')
                    .replace(/[ \t]+/g, ' ')
                    .replace(/\s*\n\s*/g, '\n')
                    .trim();
                const flat = s => clean(s).replace(/\s+/g, ' ').trim();
                const digits = s => flat(s).replace(/\D/g, '');
                const norm = s => flat(s).normalize ? flat(s).normalize('NFD').replace(/[\u0300-\u036f]/g, '').toUpperCase() : flat(s).toUpperCase();
                const isHeader = s => /Nr\s+de\s+Idt|Ident|Cadastro|CPF|Posto|Gradua|Resultados\s+encontrados|Total\s+de\s+registros|Página\s+\d+\s+de|Exibidos|LEGISLAÇÃO|CONSULTAR|Cadastrar|Pesquisar|Limpar/i.test(flat(s));
                const rankRe = /(General\s+de\s+Ex[eé]rcito|General\s+de\s+Divis[aã]o|General\s+de\s+Brigada|Coronel|Tenente\s+Coronel|Major|Capit[aã]o|Primeiro\s+Tenente|Segundo\s+Tenente|Aspirante(?:\s+a\s+Oficial)?|Subtenente|Sargento|Cabo|Soldado(?:\s+Efetivo\s+Profissional|\s+Efetivo\s+Vari[aá]vel|\s+Engajado|\s*-\s*Recruta|\s+Recruta)?|Gen\s+Ex|Gen\s+Div|Gen\s+Bda|Ten\s+Cel|1[º°]?\s*Ten|2[º°]?\s*Ten|Asp\s*Of|S\s*Ten|1[º°]?\s*Sgt|2[º°]?\s*Sgt|3[º°]?\s*Sgt|Cb|Sd)/i;
                const omRe = /(\b\d{3,}\s*-\s*[^\s].*|\b\d+ª?\s+Cia\b.*|\bCia\b.*|\bOM\b.*)/i;
                const nameStopRe = /\b(Soldado|Cabo|Sargento|Tenente|Aspirante|Subtenente|Capit[aã]o|Major|Coronel|General|Gen|Ten\s*Cel|Asp\s*Of|S\s*Ten|Sgt|Cb|Sd)\b/i;
                const rows = [];
                const pushRow = (idt, cpf, name, rank, om, script) => {
                    idt = flat(idt); cpf = flat(cpf); name = flat(name); rank = flat(rank); om = flat(om); script = flat(script);
                    if (!idt && !cpf) return;
                    if (digits(idt).length < 5 && digits(cpf).length < 7) return;
                    if (!name || isHeader(name)) return;
                    if (!/[A-Za-zÀ-ÿ]{3,}/.test(name)) return;
                    // Evita pegar assinatura, rodapé, paginação ou textos de menu como militar.
                    if (/Documento\s+N[ºo]|Assinado\s+com\s+senha|Autenticado|Página\s+\d+\s+de|Total\s+de\s+registros/i.test(name)) return;
                    rows.push({ MilitaryId: idt, Cpf: cpf, Name: name, Rank: rank, Om: om, ReportScript: script });
                };

                // 1) Leitura normal da tabela do SIPPES: #table-grid.
                const trs = Array.from(document.querySelectorAll('#table-grid tr, table tr'));
                for (const tr of trs) {
                    const tds = Array.from(tr.querySelectorAll('td'));
                    if (tds.length < 3) continue;
                    const cells = tds.map(td => flat(td.innerText || td.textContent)).filter(Boolean);
                    if (cells.length < 3) continue;
                    const joined = cells.join(' ');
                    if (isHeader(joined)) continue;
                    const onclicks = Array.from(tr.querySelectorAll('[onclick]')).map(el => el.getAttribute('onclick') || '').filter(Boolean);
                    const script = onclicks.find(x => /gerarRelatorio|relatorio|pdf|dados/i.test(x)) || '';

                    // Layout mais comum: Idt/Cadastro | CPF | Nome | Posto/Graduação | OM | PDF.
                    const firstDigits = digits(cells[0]);
                    const secondDigits = digits(cells[1] || '');
                    if ((firstDigits.length >= 5 || secondDigits.length >= 7) && cells.length >= 4) {
                        let idt = cells[0] || '';
                        let cpf = cells[1] || '';
                        let name = cells[2] || '';
                        let rank = cells[3] || '';
                        let om = cells.length >= 5 ? cells.slice(4).join(' ') : '';

                        // Alguns layouts quebram nome em duas colunas ou omitem coluna visualmente.
                        if (!rankRe.test(rank) && cells.length >= 5) {
                            const rankIndex = cells.findIndex((c, i) => i >= 3 && rankRe.test(c));
                            if (rankIndex > 2) {
                                name = cells.slice(2, rankIndex).join(' ');
                                rank = cells[rankIndex];
                                om = cells.slice(rankIndex + 1).join(' ');
                            }
                        }
                        pushRow(idt, cpf, name, rank, om, script);
                        continue;
                    }

                    // Fallback por padrões, sem depender da posição exata das colunas.
                    const idIndex = cells.findIndex(c => digits(c).length >= 5 && digits(c).length <= 12);
                    const cpfIndex = cells.findIndex((c, i) => i !== idIndex && digits(c).length >= 7 && digits(c).length <= 11);
                    const rankIndex = cells.findIndex(c => rankRe.test(c));
                    let nameIndex = cells.findIndex((c, i) => i !== idIndex && i !== cpfIndex && i !== rankIndex && /[A-Za-zÀ-ÿ]{3,}\s+[A-Za-zÀ-ÿ]{2,}/.test(c) && !isHeader(c));
                    const omIndex = cells.findIndex(c => omRe.test(c));
                    if (nameIndex < 0 && idIndex >= 0 && rankIndex > idIndex) nameIndex = idIndex + 2;
                    pushRow(idIndex >= 0 ? cells[idIndex] : '', cpfIndex >= 0 ? cells[cpfIndex] : '', nameIndex >= 0 ? cells[nameIndex] : '', rankIndex >= 0 ? cells[rankIndex] : '', omIndex >= 0 ? cells[omIndex] : '', script);
                }

                if (rows.length > 0) return JSON.stringify(rows);

                // 2) Fallback bruto pelo texto visível. Serve quando o SIPPES renderiza tabela antiga e o WebDriver não enxerga os TDs corretamente.
                const body = document.body && document.body.innerText ? document.body.innerText : '';
                const lines = clean(body).split(/\n+/).map(flat).filter(Boolean);
                const startRow = s => /^\d{5,}[-.\d]*\s+[\d.*-]{7,}\s+/.test(s) || /^\d{5,}[-.\d]*$/.test(s);
                const groups = [];
                let current = [];
                for (const line of lines) {
                    if (isHeader(line)) continue;
                    if (startRow(line)) {
                        if (current.length) groups.push(current.join(' '));
                        current = [line];
                    } else if (current.length) {
                        current.push(line);
                    }
                }
                if (current.length) groups.push(current.join(' '));

                for (const g0 of groups) {
                    let g = flat(g0);
                    const m = g.match(/^(\d{5,}[-.\d]*)\s+([\d.*-]{7,})\s+(.+)$/);
                    if (!m) continue;
                    let rest = flat(m[3]);
                    const rankMatch = rest.match(rankRe);
                    if (!rankMatch) continue;
                    let name = flat(rest.slice(0, rankMatch.index));
                    let afterRank = flat(rest.slice((rankMatch.index || 0) + rankMatch[0].length));
                    let om = '';
                    const omMatch = afterRank.match(omRe);
                    if (omMatch) om = flat(afterRank.slice(omMatch.index || 0));
                    pushRow(m[1], m[2], name, rankMatch[0], om, '');
                }

                return JSON.stringify(rows);
                """), CultureInfo.InvariantCulture) ?? string.Empty;

            var count = string.IsNullOrWhiteSpace(candidate) ? 0 : Regex.Matches(candidate, "\"MilitaryId\"", RegexOptions.CultureInvariant).Count;
            if (count > bestCount)
            {
                bestCount = count;
                json = candidate;
            }
            return count > 0;
        });

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var rows = JsonSerializer.Deserialize<List<SippesPersonnelRow>>(json) ?? [];
                var parsed = NormalizeSippesRows(rows);
                if (parsed.Count > 0) return parsed;
            }
            catch
            {
                // Segue para o fallback por HTML/texto visível.
            }
        }

        var snapshot = ReadBestSippesActiveSnapshot(driver);
        return ExtractSippesActiveRowsFromHtmlText(snapshot.Html, snapshot.Text);
    }

    private static List<SippesPersonnelRow> NormalizeSippesRows(IEnumerable<SippesPersonnelRow> rows)
        => rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.MilitaryId)) || !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.Cpf)))
            .GroupBy(BuildSippesPersonnelKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g => g.First())
            .ToList();

    private static (string Html, string Text) ReadBestSippesActiveSnapshot(IWebDriver driver)
    {
        var bestHtml = string.Empty;
        var bestText = string.Empty;
        RunInSippesContexts(driver, () =>
        {
            try
            {
                var text = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return document.body ? (document.body.innerText || document.body.textContent || '') : '';"), CultureInfo.InvariantCulture) ?? string.Empty;
                var html = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return document.documentElement ? document.documentElement.outerHTML : '';"), CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.Length > bestText.Length)
                {
                    bestText = text;
                    bestHtml = html;
                }
            }
            catch { }
            return false;
        });
        return (bestHtml, bestText);
    }

    private static List<SippesPersonnelRow> ExtractSippesActiveRowsFromHtmlText(string html, string text)
    {
        var rows = new List<SippesPersonnelRow>();

        if (!string.IsNullOrWhiteSpace(html))
        {
            foreach (Match tr in Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
            {
                var trHtml = tr.Groups[1].Value;
                var scriptMatch = Regex.Match(trHtml, "onclick\\s*=\\s*\"(?<script>[^\"]*gerarRelatorio[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
                var script = scriptMatch.Success ? System.Net.WebUtility.HtmlDecode(scriptMatch.Groups["script"].Value) : string.Empty;
                var cells = Regex.Matches(trHtml, @"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
                    .Cast<Match>()
                    .Select(m => CleanSippesHtmlCell(m.Groups[1].Value))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                TryAddSippesRowFromCells(rows, cells, script);
            }
        }

        if (rows.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            foreach (var group in BuildSippesTextRowGroups(text))
                TryAddSippesRowFromText(rows, group);
        }

        return NormalizeSippesRows(rows);
    }

    private static IEnumerable<string> BuildSippesTextRowGroups(string text)
    {
        var lines = Regex.Split(CleanSippesText(text), @"\r?\n+")
            .Select(CleanSippesText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (IsSippesHeaderOrChrome(line)) continue;
            var startsRow = Regex.IsMatch(line, @"^\d{5,}[-.\d]*\s+\d{2,3}[.\d*-]+", RegexOptions.CultureInvariant)
                            || Regex.IsMatch(line, @"^\d{5,}[-.\d]*$", RegexOptions.CultureInvariant);
            if (startsRow)
            {
                if (current.Count > 0) yield return string.Join(' ', current);
                current.Clear();
            }
            if (startsRow || current.Count > 0) current.Add(line);
        }
        if (current.Count > 0) yield return string.Join(' ', current);
    }

    private static void TryAddSippesRowFromText(List<SippesPersonnelRow> rows, string text)
    {
        var value = CleanSippesText(text);
        var match = Regex.Match(value, @"^(?<idt>\d{5,}[-.\d]*)\s+(?<cpf>[\d.*-]{7,})\s+(?<rest>.+)$", RegexOptions.CultureInvariant);
        if (!match.Success) return;
        var rest = CleanSippesText(match.Groups["rest"].Value);
        var rankMatch = SippesRankMatch(rest);
        if (!rankMatch.Success) return;
        var name = CleanSippesText(rest[..rankMatch.Index]);
        var afterRank = CleanSippesText(rest[(rankMatch.Index + rankMatch.Length)..]);
        var omMatch = Regex.Match(afterRank, @"(\b\d{3,}\s*-\s*.+|\b\d+ª?\s+Cia\b.*|\bCia\b.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var om = omMatch.Success ? CleanSippesText(omMatch.Value) : string.Empty;
        AddSippesRow(rows, match.Groups["idt"].Value, match.Groups["cpf"].Value, name, rankMatch.Value, om, string.Empty);
    }

    private static void TryAddSippesRowFromCells(List<SippesPersonnelRow> rows, IReadOnlyList<string> cells, string script = "")
    {
        if (cells.Count < 3) return;
        var joined = CleanSippesText(string.Join(' ', cells));
        if (IsSippesHeaderOrChrome(joined)) return;

        var idIndex = cells.Select((Value, Index) => new { Value, Index })
            .FirstOrDefault(x => MilitaryFormatting.Digits(x.Value).Length is >= 5 and <= 12)?.Index ?? -1;
        if (idIndex < 0) return;

        var cpfIndex = cells.Select((Value, Index) => new { Value, Index })
            .FirstOrDefault(x => x.Index != idIndex && MilitaryFormatting.Digits(x.Value).Length is >= 7 and <= 11)?.Index ?? -1;
        var rankIndex = cells.Select((Value, Index) => new { Value, Index })
            .FirstOrDefault(x => x.Index > Math.Max(idIndex, cpfIndex) && LooksLikeSippesRank(x.Value))?.Index ?? -1;

        if (cpfIndex < 0 || rankIndex < 0) return;
        var nameStart = Math.Min(cpfIndex + 1, cells.Count - 1);
        var name = CleanSippesText(string.Join(' ', cells.Skip(nameStart).Take(Math.Max(0, rankIndex - nameStart))));
        var rank = cells[rankIndex];
        var om = CleanSippesText(string.Join(' ', cells.Skip(rankIndex + 1)))
            .Replace("Gerar PDF", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        AddSippesRow(rows, cells[idIndex], cells[cpfIndex], name, rank, om, script);
    }

    private static void AddSippesRow(List<SippesPersonnelRow> rows, string idt, string cpf, string name, string rank, string om, string script)
    {
        idt = CleanSippesText(idt);
        cpf = CleanSippesText(cpf);
        name = CleanSippesText(name);
        rank = CleanSippesText(rank);
        om = CleanSippesText(om);
        if (MilitaryFormatting.Digits(idt).Length < 5 && MilitaryFormatting.Digits(cpf).Length < 7) return;
        if (string.IsNullOrWhiteSpace(name) || IsSippesHeaderOrChrome(name)) return;
        if (!Regex.IsMatch(name, @"[A-Za-zÀ-ÿ]{3,}", RegexOptions.CultureInvariant)) return;
        rows.Add(new SippesPersonnelRow { MilitaryId = idt, Cpf = cpf, Name = name, Rank = rank, Om = om, ReportScript = script });
    }

    private static string CleanSippesHtmlCell(string html)
    {
        var value = Regex.Replace(html ?? string.Empty, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"<[^>]+>", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return CleanSippesText(System.Net.WebUtility.HtmlDecode(value));
    }

    private static string CleanSippesText(string? value)
        => Regex.Replace((value ?? string.Empty).Replace(' ', ' ').Replace('­', ' ').Replace('�', ' '), @"[ 	]+", " ", RegexOptions.CultureInvariant).Trim();

    private static bool IsSippesHeaderOrChrome(string? value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text)) return true;
        return text.Contains("nr de idt") || text.Contains("cadastro") && text.Contains("cpf") && text.Contains("nome")
               || text.Contains("resultado encontrado") || text.Contains("resultados encontrados") || text.Contains("total de registros")
               || text.Contains("pagina") && text.Contains("exibido") || text.Contains("consultar relatorio")
               || text.Contains("legislacao") || text.Contains("sistema de pagamento de pessoal") || text.Contains("copyright");
    }

    private static bool LooksLikeSippesRank(string? value) => SippesRankMatch(value ?? string.Empty).Success;

    private static Match SippesRankMatch(string value)
        => Regex.Match(value ?? string.Empty,
            @"(General\s+de\s+Ex[eé]rcito|General\s+de\s+Divis[aã]o|General\s+de\s+Brigada|Coronel|Tenente\s+Coronel|Major|Capit[aã]o|Primeiro\s*-?\s*Tenente|Segundo\s*-?\s*Tenente|Aspirante(?:\s+a\s+Oficial)?|Subtenente|Sargento|Cabo|Soldado(?:\s+Efetivo\s+Profissional|\s+Efetivo\s+Vari[aá]vel|\s+Engajado|\s*-\s*Recruta|\s+Recruta)?|Gen\s+Ex|Gen\s+Div|Gen\s+Bda|Ten\s+Cel|1[º°]?\s*Ten|2[º°]?\s*Ten|Asp\s*Of|S\s*Ten|1[º°]?\s*Sgt|2[º°]?\s*Sgt|3[º°]?\s*Sgt|Cb|Sd)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string SaveSippesActiveDiagnostics(IWebDriver driver, int page)
    {
        try
        {
            var dir = Path.Combine(_paths.DataDirectory, "Logs", "SIPPES");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var basePath = Path.Combine(dir, $"conferencia_efetivo_pagina_{page}_{stamp}");

            try
            {
                var html = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return document.documentElement ? document.documentElement.outerHTML : '';"), CultureInfo.InvariantCulture) ?? string.Empty;
                File.WriteAllText(basePath + ".html", html, Encoding.UTF8);
            }
            catch { }

            try
            {
                var text = Convert.ToString(((IJavaScriptExecutor)driver).ExecuteScript("return document.body ? document.body.innerText : '';"), CultureInfo.InvariantCulture) ?? string.Empty;
                File.WriteAllText(basePath + ".txt", text, Encoding.UTF8);
            }
            catch { }

            try
            {
                if (driver is ITakesScreenshot screenshotDriver)
                {
                    var screenshot = screenshotDriver.GetScreenshot();
                    screenshot.SaveAsFile(basePath + ".png");
                }
            }
            catch { }

            return basePath + ".html";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildSippesPersonnelKey(SippesPersonnelRow row)
    {
        var cpf = MilitaryFormatting.Digits(row.Cpf);
        if (cpf.Length >= 9) return "CPF:" + cpf;
        var idt = MilitaryFormatting.Digits(row.MilitaryId);
        if (idt.Length >= 5) return "IDT:" + idt;
        var name = Normalize(row.Name);
        return string.IsNullOrWhiteSpace(name) ? string.Empty : "NOME:" + name;
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
    private static string NormalizeSessionSystem(string? value)
    {
        if (string.Equals(value, FinancialStatementSessionSystem, StringComparison.OrdinalIgnoreCase)
            || (value ?? string.Empty).Contains("FICHA", StringComparison.OrdinalIgnoreCase)
            || (value ?? string.Empty).Contains("FINANC", StringComparison.OrdinalIgnoreCase))
            return FinancialStatementSessionSystem;
        return NormalizeSystem(value);
    }
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
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
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
