namespace SIGFUR.Wpf.Services;

public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        // O WPF possui um único local oficial. Não herdamos ESCALA_DATA_DIR/MILITARES_DB
        // de processos externos, pois uma variável antiga poderia apontar silenciosamente
        // para outro militares.db vazio e dar a impressão de que os cadastros sumiram.
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? GetDefaultDataDirectory()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectory.Trim()));

        Directory.CreateDirectory(DataDirectory);

        // Variáveis exportadas apenas para módulos legados abertos explicitamente.
        // A aplicação C# sempre usa as propriedades desta classe como fonte de verdade.
        Environment.SetEnvironmentVariable("ESCALA_DATA_DIR", DataDirectory);
        Environment.SetEnvironmentVariable("MILITARES_DB", DatabaseFile);
    }

    public static string GetDefaultDataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIGFUR");

    public string DataDirectory { get; }
    public string DatabaseFile => Path.Combine(DataDirectory, "militares.db");
    public string TransportRoutesDatabaseFile => Path.Combine(DataDirectory, "aux_transporte_rotas.sqlite3");
    public string MilitaryDocumentsDirectory => Path.Combine(DataDirectory, "documentos_militares");
    public string PaystubsDirectory => Path.Combine(DataDirectory, "contracheques");
    public string GeneratedDocumentsDirectory => Path.Combine(DataDirectory, "documentos_gerados");
    public string DocumentTemplatesDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SIGFUR", "templates", "docs");
    public string SippesSettingsFile => Path.Combine(DataDirectory, "sippes_wpf.json");
    public string AdjustmentAccountsSettingsFile => Path.Combine(DataDirectory, "adjustment_accounts_wpf.json");
    public string SalaryHiddenFile => Path.Combine(DataDirectory, "soldos_hidden.json");
    public string SalarySettingsFile => Path.Combine(DataDirectory, "soldos_settings_wpf.json");
    public string GratificationSettingsFile => Path.Combine(DataDirectory, "gratificacao_representacao_wpf.json");
    public string LegacyGratificationSettingsFile => Path.Combine(DataDirectory, "grat2_prefs.json");
    public string GratificationTemplatesDirectory => Path.Combine(DataDirectory, "gratificacao_representacao", "templates");
    public string ExercisePreviousDirectory => Path.Combine(DataDirectory, "EA");
    public string ExercisePreviousTemplatesDirectory => Path.Combine(ExercisePreviousDirectory, "templates");
    public string ExercisePreviousOutputDirectory => Path.Combine(ExercisePreviousDirectory, "processos");
    public string ExercisePreviousLogsDirectory => Path.Combine(ExercisePreviousDirectory, "logs");
    public string ExercisePreviousProtocolsDirectory => Path.Combine(ExercisePreviousDirectory, "protocolos_cpex");
    public string ExercisePreviousCpexDownloadsDirectory => Path.Combine(ExercisePreviousProtocolsDirectory, "_downloads");
    public string ExercisePreviousCpexSettingsFile => Path.Combine(ExercisePreviousDirectory, "config", "cpex_online_config.json");
    public string ListColumnsFile => Path.Combine(DataDirectory, "military_columns_wpf.json");
    public string DocumentFormCacheFile => Path.Combine(DataDirectory, "document_generation_cache_wpf.json");
    public string DocumentProfilesFile => Path.Combine(DataDirectory, "document_generation_profiles_wpf.json");
    public string DocumentTemplateSelectionFile => Path.Combine(DataDirectory, "document_template_selection_wpf.json");
    public string CopyFormatFile => Path.Combine(DataDirectory, "copy_format_wpf.json");
    public string ExportPreferencesFile => Path.Combine(DataDirectory, "military_export_preferences_wpf.json");
    public string PostalOmDatabaseFile => Path.Combine(DataDirectory, "correios_oms.sqlite3");
    public string NamedMilitaryListsFile => Path.Combine(DataDirectory, "military_saved_lists_wpf.json");
    public string PaystubCenterSettingsFile => Path.Combine(DataDirectory, "paystub_center_wpf.json");
    public string LicensedTransferredSettingsFile => Path.Combine(DataDirectory, "lt_settings_wpf.json");
    public string CpexPaystubSettingsFile => Path.Combine(DataDirectory, "lt_contracheque_settings_wpf.json");
    public string ExternalPaystubPeopleFile => Path.Combine(DataDirectory, "paystub_external_people_wpf.json");
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    public string AppSettingsFile => Path.Combine(DataDirectory, "app_settings.json");
    public string UiConfigFile => Path.Combine(DataDirectory, "ui_config.json");
    public string RemindersFile => Path.Combine(DataDirectory, "lembretes.json");
    public string ReminderArchiveFile => Path.Combine(DataDirectory, "lembretes_historico.json");
    public string ReminderSettingsFile => Path.Combine(DataDirectory, "lembretes_cfg_wpf.json");
    public string LegacyReminderSettingsFile => Path.Combine(DataDirectory, "lembretes_cfg.json");
    public string PaymentRemindersFile => Path.Combine(DataDirectory, "pagamento_lembretes_dashboard.json");
    public string CalendarEventsFile => Path.Combine(DataDirectory, "dashboard_calendar_events.json");
    public string BirthdayConferenceFile => Path.Combine(DataDirectory, "aniversariantes_conferencia.json");
    public string BirthdayBulletinTemplateFile => Path.Combine(DataDirectory, "aniversariantes_boletim_modelo.txt");
    public string BulletinIndexFile => Path.Combine(DataDirectory, "boletins_salvos_index.json");
    public string IntelligentBulletinReviewFile => Path.Combine(DataDirectory, "boletins_conferencia_ok.json");
    public string IntelligentBulletinSettingsFile => Path.Combine(DataDirectory, "boletim_inteligente_wpf.json");
    public string IntelligentBulletinLibraryDirectory => Path.Combine(DataDirectory, "boletins_salvos");
    public string IntelligentBulletinTextDirectory => Path.Combine(DataDirectory, "boletins_salvos_textos");
    public string IntelligentBulletinParseDirectory => Path.Combine(DataDirectory, "boletins_salvos_parse");
    public string IntelligentBulletinTempDirectory => Path.Combine(DataDirectory, "boletins_salvos_tmp");
    public string SisbolPersonIndexDirectory => Path.Combine(DataDirectory, "boletins", "indices", "por_pessoa");
    public string FurrielIndexFile => Path.Combine(DataDirectory, "boletim_furriel", "indice_furriel.json");
    public string ExternalBulletinDirectory => Path.Combine(DataDirectory, "boletins_externos");
    public string ExternalBulletinRegionDirectory => Path.Combine(ExternalBulletinDirectory, "Regiao");
    public string ExternalBulletinCmlDirectory => Path.Combine(ExternalBulletinDirectory, "CML");
    public string ExternalBulletinIndexFile => Path.Combine(ExternalBulletinDirectory, "indice_boletins_externos.json");
    public string ExternalBulletinSettingsFile => Path.Combine(ExternalBulletinDirectory, "config_boletins_externos.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string ApplicationLogFile => Path.Combine(LogDirectory, "wpf_app.log");
    public string DatabaseSafetyLogFile => Path.Combine(LogDirectory, "wpf_database_safety.log");
    public string DatabaseSafetyDirectory => Path.Combine(DataDirectory, "database_safety");
    public string DatabaseLocationFile => Path.Combine(DataDirectory, "database_location_wpf.json");
    public string ThemeSettingsFile => Path.Combine(DataDirectory, "wpf_theme.json");
    public string UiStateFile => Path.Combine(DataDirectory, "wpf_ui_state.json");
    public string PersonnelRelationPreferencesFile => Path.Combine(DataDirectory, "relacao_pessoal_wpf.json");
    public string VacationPreferencesFile => Path.Combine(DataDirectory, "prefs_plano_ferias.json");
    public string VacationBulletinsFile => Path.Combine(DataDirectory, "boletins_ferias_wpf.json");
    public string VacationLegacyModelsFile => Path.Combine(DataDirectory, "boletins_ferias.json");
    public string VacationLegacyGeneratedFile => Path.Combine(DataDirectory, "boletins_ferias_gerados.json");
    public string VacationFinancialFile => Path.Combine(DataDirectory, "ferias_financeiro_individual.json");
    public string VacationOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "Plano_de_Ferias");
    public string BulletinTemplatesFile => Path.Combine(DataDirectory, "boletins.json");
    public string BulletinPreferencesFile => Path.Combine(DataDirectory, "prefs_boletim_wpf.json");
    public string BulletinLegacyPreferencesFile => Path.Combine(DataDirectory, "prefs_boletim.json");
    public string BulletinLegacyFormPreferencesFile => Path.Combine(DataDirectory, "form_prefs_boletim.json");
    public string BulletinDefaultTemplatesFile => Path.Combine(AppContext.BaseDirectory, "Resources", "Boletim", "boletins_padrao.json");
    public string BulletinCodomCatalogFile => Path.Combine(AppContext.BaseDirectory, "Resources", "Boletim", "codom_catalogo.json");
    public string BulletinKnowledgeFile => Path.Combine(AppContext.BaseDirectory, "Resources", "Boletim", "conhecimento_boletins.json");
    public string BulletinManualKeysFile => Path.Combine(DataDirectory, "boletim_chaves_manuais_wpf.json");
    public string CertificateBulletinKeysFile => Path.Combine(DataDirectory, "certidao_chaves_boletim.json");
    public IReadOnlyList<string> BulletinGlobalKeyFiles =>
    [
        // Chaves manuais são carregadas primeiro. Arquivos automáticos mais
        // específicos (certidão, pensão e BI/ADT) prevalecem quando há conflito.
        BulletinManualKeysFile,
        CertificateBulletinKeysFile,
        Path.Combine(DataDirectory, "pensao_chaves_boletim.json"),
        Path.Combine(DataDirectory, "boletim_chaves_boletim.json")
    ];
    public string PhpmDirectory => Path.Combine(DataDirectory, "phpm");
    public string PhpmTemplatesDirectory => Path.Combine(PhpmDirectory, "templates");
    public string PhpmOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "PHPM");
    public string PhpmCatalogFile => Path.Combine(PhpmDirectory, "catalogo_templates.json");
    public string PhpmHistoryFile => Path.Combine(PhpmDirectory, "historico_geracoes.json");
    public string LegislationDirectory => Path.Combine(DataDirectory, "legislacao");
    public string LegislationDocumentsDirectory => Path.Combine(LegislationDirectory, "documentos");
    public string LegislationDatabaseFile => Path.Combine(LegislationDirectory, "indice_legislacao.sqlite3");
    public string AbsenceDatabaseFile => Path.Combine(DataDirectory, "faltas_atrasos.sqlite3");
    public string AbsenceReportsDirectory => Path.Combine(GeneratedDocumentsDirectory, "Faltas_e_Atrasos");
    public string DutyRosterFile => Path.Combine(DataDirectory, "escala_sgt_dia_wpf.json");
    public string OrganizationCatalogCacheFile => Path.Combine(DataDirectory, "catalogo_oms_oficial.json");
    public string OrganizationLogosDirectory => Path.Combine(DataDirectory, "imagens_oms");
    public string PlanCallSettingsFile => Path.Combine(DataDirectory, "plano_chamada_wpf.json");
    public string PlanCallDatabaseFile => Path.Combine(DataDirectory, "plano_chamada.sqlite3");
    public string PlanCallOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "Plano_de_Chamada");
    public string JudicialPensionSettingsFile => Path.Combine(DataDirectory, "pensao_judicial_wpf.json");
    public string JudicialPensionOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "Pensao_Judicial");
    public string JudicialPensionTemplatesDirectory => Path.Combine(DataDirectory, "pensao_judicial", "templates");
    public string MeasuresTakenSettingsFile => Path.Combine(DataDirectory, "medidas_tomadas_wpf.json");
    public string MeasuresTakenWorksDatabaseFile => Path.Combine(DataDirectory, "historico", "medidas_tomadas_trabalhos.db");
    public string MeasuresTakenOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "Medidas_Tomadas");
    public string BankInconsistencySettingsFile => Path.Combine(DataDirectory, "inconsistencia_bancaria_wpf.json");
    public string BankInconsistencyOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "Inconsistencia_Bancaria");
    public string SinfoppesCriticizedSettingsFile => Path.Combine(DataDirectory, "sinfoppes_lancamentos_criticados_wpf.json");
    public string SinfoppesCriticizedOutputDirectory => Path.Combine(GeneratedDocumentsDirectory, "Lancamentos_Criticados");
    public string BankAutomationProfileDirectory => Path.Combine(DataDirectory, "selenium_cpex_profile_wpf");
    public string SisbolSettingsFile => Path.Combine(DataDirectory, "sisbol_prefs_wpf.json");
    public string SisbolBrowserProfileDirectory => Path.Combine(DataDirectory, "sisbol_browser_profile_wpf");
    public string SisbolDiagnosticDirectory => Path.Combine(DataDirectory, "diagnostico_sisbol");
    public string AssistantSettingsFile => Path.Combine(DataDirectory, "assistente_sigfur_config.json");
    public string AssistantHistoryFile => Path.Combine(DataDirectory, "assistente_sigfur_historico.json");
    public string AssistantUsageFile => Path.Combine(DataDirectory, "assistente_sigfur_consumo.json");
    public string AssistantExportsDirectory => Path.Combine(GeneratedDocumentsDirectory, "Assistente_SIGFUR");

}
