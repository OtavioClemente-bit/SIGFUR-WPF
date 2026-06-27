using System.Text;
using System.Windows;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views;

namespace SIGFUR.Wpf;

public partial class App : Application
{
    public static AppPaths Paths { get; private set; } = null!;
    public static JsonFileService Json { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static BackupService Backup { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static ThemeService Theme { get; private set; } = null!;
    public static DatabaseSafetyService Database { get; private set; } = null!;
    public static DashboardService Dashboard { get; private set; } = null!;
    public static MilitaryRepository MilitaryRepository { get; private set; } = null!;
    public static MilitaryPreferenceService MilitaryPreferences { get; private set; } = null!;
    public static PaystubService Paystubs { get; private set; } = null!;
    public static FinancialStatementCaptureService FinancialStatements { get; private set; } = null!;
    public static UiStatePersistenceService UiState { get; private set; } = null!;
    public static SisbolAutomationService Sisbol { get; private set; } = null!;
    public static CertificateOcrService CertificateOcr { get; private set; } = null!;
    public static LicensedTransferredRepository LicensedTransferred { get; private set; } = null!;
    public static LicensedTransferredSpreadsheetService LicensedSpreadsheets { get; private set; } = null!;
    public static CpexPaystubAutomationService CpexPaystubs { get; private set; } = null!;
    public static ReminderService Reminders { get; private set; } = null!;
    public static IntelligentBulletinService IntelligentBulletins { get; private set; } = null!;
    public static SalaryService Salaries { get; private set; } = null!;
    public static GratificationService Gratifications { get; private set; } = null!;
    public static VacationPlanService Vacations { get; private set; } = null!;
    public static PdfTextService PdfText { get; private set; } = null!;
    public static SisbolPersonIndexImportService SisbolPersonIndex { get; private set; } = null!;
    public static ExternalBulletinService ExternalBulletins { get; private set; } = null!;
    public static PaymentConferenceService PaymentConference { get; private set; } = null!;
    public static MeasuresTakenService MeasuresTaken { get; private set; } = null!;
    public static PlanCallService PlanCall { get; private set; } = null!;
    public static JudicialPensionService JudicialPension { get; private set; } = null!;
    public static BankInconsistencyService BankInconsistencies { get; private set; } = null!;
    public static DutyRosterService DutyRoster { get; private set; } = null!;
    public static AbsenceService Absences { get; private set; } = null!;
    public static LegislationService Legislation { get; private set; } = null!;
    public static PhpmTemplateService Phpm { get; private set; } = null!;
    public static AssistantCredentialService AssistantCredentials { get; private set; } = null!;
    public static AssistantStorageService AssistantStorage { get; private set; } = null!;
    public static AssistantAttachmentService AssistantAttachments { get; private set; } = null!;
    public static AssistantDataService AssistantData { get; private set; } = null!;
    public static OpenAiAssistantService Assistant { get; private set; } = null!;
    public static SigfurAssistantService AssistantLocal { get; private set; } = null!;
    public static AssistantDocumentService AssistantDocuments { get; private set; } = null!;
    public static BulletinKnowledgeService BulletinKnowledge { get; private set; } = null!;
    public static SecurityService Security { get; private set; } = null!;
    public static ProfileService Profiles { get; private set; } = null!;
    public static SigfurBackupService SyncBackup { get; private set; } = null!;
    public static SigfurRestoreService SyncRestore { get; private set; } = null!;
    public static SigfurProfileSession StartupSession { get; private set; } = new();

    private SplashWindow? _splash;
    private int _fatalDialogActive;

    protected override void OnStartup(StartupEventArgs e)
    {
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = ptBr;
        CultureInfo.DefaultThreadCurrentUICulture = ptBr;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        base.OnStartup(e);
        GlobalContextMenuService.Install();
        PerformanceTuningService.Install();

        // Durante o carregamento a Splash não deve virar, por acidente, a janela que
        // controla o encerramento da aplicação. A política normal é restaurada depois
        // que a MainWindow for exibida com sucesso.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        RegisterExceptionHandlers();

        // Não usamos "async void" no ponto de entrada. Toda exceção do carregamento
        // fica contida em StartAsync e gera log, em vez de encerrar silenciosamente.
        _ = StartAsync(e);
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        try
        {
            if (e.Args.Any(x => x.Equals("--diagnose-db", StringComparison.OrdinalIgnoreCase)))
            {
                StartupSession = new SigfurProfileSession { IsProfileMode = false, StatusMessage = "Diagnóstico em modo local." };
                InitializeServices();
                var diagnosticProfile = await Settings.LoadProfileAsync();
                await RunDatabaseDiagnosticAsync(diagnosticProfile);
                return;
            }

            var startupJson = new JsonFileService();
            var startupSecurity = new SecurityService();
            var startupProfiles = new ProfileService(startupJson, startupSecurity);
            var startupBackup = new SigfurBackupService();
            var startupRestore = new SigfurRestoreService(startupBackup);
            var startupPaths = new AppPaths();
            var startupLog = new LogService(startupPaths);
            var startupUiState = new UiStatePersistenceService(startupPaths, startupLog);
            var startupWindow = new StartupLoginWindow(startupProfiles, startupBackup, startupRestore, startupUiState);
            var startupResult = startupWindow.ShowDialog();
            if (startupResult != true || startupWindow.Session is null)
            {
                Shutdown();
                return;
            }

            StartupSession = startupWindow.Session;
            InitializeServices(StartupSession.IsProfileMode ? StartupSession.LocalDataPath : null);

            var startupWarnings = new List<string>();
            if (PerformanceTuningService.SlowStorageDetected)
                startupWarnings.Add("O SIGFUR está sendo executado de unidade removível ou rede. Para melhor desempenho, copie a pasta publicada para o disco local do computador.");

            try
            {
                await Theme.InitializeAsync();
            }
            catch (Exception ex)
            {
                startupWarnings.Add("Tema salvo inválido; foi aplicado o tema institucional padrão.");
                await SafeLogAsync("Falha ao restaurar o tema. Usando tema padrão.", ex);
                try { Theme.Apply("institutional-blue", raiseEvent: false); } catch { }
            }

            UiProfile profile;
            try
            {
                profile = await Settings.LoadProfileAsync();
            }
            catch (Exception ex)
            {
                profile = new UiProfile();
                startupWarnings.Add("As configurações de perfil estavam inválidas e foram ignoradas nesta abertura.");
                await SafeLogAsync("Falha ao carregar o perfil da interface.", ex);
            }

            var splashStarted = System.Diagnostics.Stopwatch.StartNew();
            _splash = new SplashWindow("v6.1.12");
            _splash.Show();

            WindowStateData windowState;
            try
            {
                windowState = await Settings.LoadWindowStateAsync();
            }
            catch (Exception ex)
            {
                windowState = new WindowStateData { Width = 1180, Height = 820, UiScale = 1.0 };
                startupWarnings.Add("O posicionamento antigo da janela estava inválido e foi restaurado com segurança.");
                await SafeLogAsync("Falha ao carregar o estado da janela. Usando valores seguros.", ex);
            }

            Dictionary<string, string> hotkeys;
            try
            {
                hotkeys = await Settings.LoadHotkeysAsync();
            }
            catch (Exception ex)
            {
                hotkeys = SettingsService.DefaultHotkeys();
                startupWarnings.Add("Os atalhos personalizados estavam inválidos e os padrões foram restaurados.");
                await SafeLogAsync("Falha ao carregar atalhos. Usando atalhos padrão.", ex);
            }

            try
            {
                _splash.SetStage("Preparando o SIGFUR...", $"Aplicando o tema {Theme.Current.DisplayName}", 12, string.Empty);
                await Task.Delay(90);

                _splash.SetStage("Localizando banco oficial...", "Verificando integridade, caminho e quantidade de militares", 32,
                    $"Banco oficial: {Paths.DatabaseFile}");
                var databaseReport = await Database.InitializeAsync(profile.LegacyProjectRoot);

                _splash.SetStage("Validando dados...", databaseReport.Message, 58,
                    string.IsNullOrWhiteSpace(databaseReport.SnapshotPath) ? string.Empty : $"Snapshot: {Path.GetFileName(databaseReport.SnapshotPath)}");

                _splash.SetStage("Criando backup...", "Gerando pacote de segurança", 76, string.Empty);
                await Backup.CreateAsync("sigfur_backup", 5);

                _splash.SetStage("Carregando painel...", "Militares, lembretes, boletins e indicadores", 92,
                    $"{Math.Max(0, databaseReport.Official.MilitaryCount)} militar(es)");
            }
            catch (Exception ex)
            {
                startupWarnings.Add(ex.Message);
                await SafeLogAsync("Inicialização WPF concluída com avisos.", ex);
                _splash.SetStage("Inicialização com avisos", "O painel será aberto", 94, ex.Message);
            }

            _splash.SetStage("SIGFUR pronto", "Abrindo o painel principal", 100, string.Empty);

            if (!string.IsNullOrWhiteSpace(StartupSession.StatusMessage))
                startupWarnings.Insert(0, StartupSession.StatusMessage);
            var warningText = string.Join(" | ", startupWarnings.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            var main = new MainWindow(profile, windowState, hotkeys, warningText);
            MainWindow = main;

            var minimumSplash = TimeSpan.FromSeconds(1.85);
            if (splashStarted.Elapsed < minimumSplash)
                await Task.Delay(minimumSplash - splashStarted.Elapsed);

            main.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            await _splash.CloseSmoothAsync();
            _splash = null;

            // Erros de atualização do dashboard não podem derrubar a janela já aberta.
            try
            {
                await main.InitializeAsync();
            }
            catch (Exception ex)
            {
                await SafeLogAsync("A janela abriu, mas a primeira atualização do dashboard falhou.", ex);
                SigfurDialog.Show(
                    main,
                    "O SIGFUR foi aberto, mas parte do Dashboard não pôde ser atualizada.\n\n" +
                    ex.Message + "\n\nO erro foi registrado no log.",
                    "SIGFUR — Aviso de inicialização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            await HandleFatalStartupExceptionAsync(ex);
        }
    }

    private static void InitializeServices(string? dataDirectory = null)
    {
        Paths = new AppPaths(dataDirectory);
        Json = new JsonFileService();
        Security = new SecurityService();
        Profiles = new ProfileService(Json, Security);
        SyncBackup = new SigfurBackupService();
        SyncRestore = new SigfurRestoreService(SyncBackup);
        Settings = new SettingsService(Paths, Json);
        Backup = new BackupService(Paths);
        Log = new LogService(Paths);
        Theme = new ThemeService(Paths, Json);
        Database = new DatabaseSafetyService(Paths, Log);
        Dashboard = new DashboardService(Database, Paths, Json);
        MilitaryRepository = new MilitaryRepository(Paths, Log);
        MilitaryPreferences = new MilitaryPreferenceService(Paths, Json);
        Paystubs = new PaystubService(Paths, Log);
        FinancialStatements = new FinancialStatementCaptureService(Paths, Log);
        UiState = new UiStatePersistenceService(Paths, Log);
        Sisbol = new SisbolAutomationService(Paths, Json, Log);
        CertificateOcr = new CertificateOcrService(Paths, Json, Log);
        LicensedTransferred = new LicensedTransferredRepository(Paths, MilitaryRepository, Log);
        LicensedSpreadsheets = new LicensedTransferredSpreadsheetService();
        CpexPaystubs = new CpexPaystubAutomationService(Paths, Json, Log);
        Reminders = new ReminderService(Paths, Json);
        IntelligentBulletins = new IntelligentBulletinService(Paths, Json, Settings, MilitaryRepository, LicensedTransferred, Log);
        Salaries = new SalaryService(Paths, Json, Log);
        Gratifications = new GratificationService(Paths, Json, MilitaryRepository, Log);
        Vacations = new VacationPlanService(Paths, Json, MilitaryRepository, Log);
        PdfText = new PdfTextService(Log);
        SisbolPersonIndex = new SisbolPersonIndexImportService(Paths, PdfText, MilitaryRepository, Log);
        ExternalBulletins = new ExternalBulletinService(Paths, Json, PdfText, Log);
        PaymentConference = new PaymentConferenceService(Paths, Json, PdfText, MilitaryRepository, Paystubs, Log);
        MeasuresTaken = new MeasuresTakenService(Paths, Json, MilitaryRepository, LicensedTransferred, PdfText, Log);
        PlanCall = new PlanCallService(Paths, Json, MilitaryRepository, Log);
        JudicialPension = new JudicialPensionService(Paths, Json, MilitaryRepository, Log);
        BankInconsistencies = new BankInconsistencyService(Paths, Json, Log);
        DutyRoster = new DutyRosterService(Paths, Json);
        Absences = new AbsenceService(Paths);
        Legislation = new LegislationService(Paths, Log);
        Phpm = new PhpmTemplateService(Paths, Json, Log);
        BulletinKnowledge = new BulletinKnowledgeService(Paths, Json, Log);
        AssistantCredentials = new AssistantCredentialService();
        AssistantStorage = new AssistantStorageService(Paths, Json);
        AssistantAttachments = new AssistantAttachmentService(PdfText);
        AssistantData = new AssistantDataService(Paths, MilitaryRepository, Vacations, IntelligentBulletins, Legislation, Paystubs, LicensedTransferred, Reminders, DutyRoster, Absences, BulletinKnowledge, PaymentConference, Log);
        Assistant = new OpenAiAssistantService(AssistantCredentials, AssistantStorage, AssistantData, Settings, BulletinKnowledge, Log);
        AssistantLocal = new SigfurAssistantService(Paths, MilitaryRepository, Paystubs, SisbolPersonIndex, IntelligentBulletins, Legislation, Settings, Log);
        AssistantDocuments = new AssistantDocumentService(Paths);
    }

    private async Task RunDatabaseDiagnosticAsync(UiProfile profile)
    {
        try
        {
            var diagnosticReport = await Database.InitializeAsync(profile.LegacyProjectRoot);
            SigfurDialog.Show(
                DatabaseDiagnosticFormatter.Format(diagnosticReport, Paths, Theme.Current.DisplayName),
                "SIGFUR — Diagnóstico do banco",
                MessageBoxButton.OK,
                diagnosticReport.Official.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            await SafeLogAsync("Falha no diagnóstico nativo do banco.", ex);
            SigfurDialog.Show(ex.Message, "SIGFUR — Diagnóstico do banco", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (UiState is not null) UiState.FlushOpenWindows();
            if (Sisbol is not null) Sisbol.Dispose();
            if (BankInconsistencies is not null) BankInconsistencies.Dispose();
        }
        catch { }
        base.OnExit(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var logPath = await WriteFatalLogAsync("Erro não tratado na interface WPF.", e.Exception);

        if (Interlocked.Exchange(ref _fatalDialogActive, 1) != 0) return;
        try
        {
            SigfurDialog.Show(
                MainWindow,
                "O SIGFUR encontrou um erro inesperado, mas impediu o fechamento silencioso.\n\n" +
                e.Exception.Message + $"\n\nLog: {logPath}",
                "SIGFUR — Erro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _fatalDialogActive, 0);
        }
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Erro fatal desconhecido.");
        WriteFallbackLog("Erro fatal fora da interface WPF.", exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        _ = SafeLogAsync("Erro de tarefa em segundo plano observado pelo SIGFUR.", e.Exception);
    }

    private async Task HandleFatalStartupExceptionAsync(Exception exception)
    {
        try
        {
            if (_splash is not null)
            {
                try { _splash.Close(); } catch { }
                _splash = null;
            }

            var logPath = await WriteFatalLogAsync("Falha fatal durante a inicialização do SIGFUR.", exception);
            SigfurDialog.Show(
                "O SIGFUR não conseguiu concluir a abertura. O programa não apagou nem substituiu seus cadastros.\n\n" +
                exception.Message + $"\n\nFoi criado um relatório em:\n{logPath}",
                "SIGFUR — Falha na abertura",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Último recurso: o erro original já foi tentado no arquivo de fallback.
        }
        finally
        {
            Shutdown(1);
        }
    }

    private static async Task SafeLogAsync(string message, Exception exception)
    {
        try
        {
            if (Log is not null)
            {
                await Log.WriteAsync(message, exception);
                return;
            }
        }
        catch { }

        WriteFallbackLog(message, exception);
    }

    private static async Task<string> WriteFatalLogAsync(string message, Exception exception)
    {
        await SafeLogAsync(message, exception);
        return GetFatalLogPath();
    }

    private static string GetFatalLogPath()
    {
        try
        {
            if (Paths is not null) return Paths.ApplicationLogFile;
        }
        catch { }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = AppContext.BaseDirectory;
        return Path.Combine(local, "SIGFUR", "logs", "wpf_fatal_startup.log");
    }

    private static void WriteFallbackLog(string message, Exception exception)
    {
        try
        {
            var path = GetFatalLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}")
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 100))
                .ToString();
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch { }
    }
}
