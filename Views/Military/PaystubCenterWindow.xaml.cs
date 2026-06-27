using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class PaystubCenterWindow : Window
{
    private enum ExistingFilePolicy { Skip, Replace, Ask }
    private sealed record ExistingPaystub(CpexPaystubPerson Person, string Path, string Folder, string Label);
    private sealed record ReplacementBackup(ExistingPaystub Existing, string BackupPath);

    private sealed class PaystubCenterSettings
    {
        public int Year { get; set; } = DateTime.Today.Year;
        public int Month { get; set; } = DateTime.Today.Month;
        public string SheetCode { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string ExternalSystem { get; set; } = "SIPPES";
        public string Processing { get; set; } = "Definitivo";
        public string PayrollType { get; set; } = "Normal";
        public string Browser { get; set; } = "Edge";
    }

    public sealed class ExternalPaystubPerson
    {
        public string Name { get; set; } = string.Empty;
        public string Cpf { get; set; } = string.Empty;
        public string Prec { get; set; } = string.Empty;
    }

    private readonly MilitaryRepository _repository;
    private readonly PaystubService _paystubs;
    private readonly List<MilitaryRecord> _military;
    private readonly ObservableCollection<ExternalPaystubPerson> _externalPeople = [];
    private readonly int _initialTab;
    private readonly bool _restrictedToSelection;
    private PaystubCenterSettings _settings = new();
    private bool _busy;
    private bool _loaded;

    public PaystubCenterWindow(
        MilitaryRepository repository, PaystubService paystubs, IReadOnlyList<MilitaryRecord> military,
        int initialTab = 0, bool restrictedToSelection = false)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _paystubs = paystubs;
        _military = military.DistinctBy(x => x.Id).ToList();
        _initialTab = Math.Clamp(initialTab, 0, 3);
        _restrictedToSelection = restrictedToSelection;
        BindMilitaryLists();
        ExternalPeopleGrid.ItemsSource = _externalPeople;
    }

    private void BindMilitaryLists()
    {
        IndividualMilitaryList.ItemsSource = null;
        BatchMilitaryList.ItemsSource = null;
        IndividualMilitaryList.ItemsSource = _military;
        BatchMilitaryList.ItemsSource = _military;
        IndividualMilitaryList.SelectedIndex = _military.Count > 0 ? 0 : -1;
        BatchCountText.Text = _military.Count == 1 ? "1 militar disponível" : $"{_military.Count} militares disponíveis";
        ScopeText.Text = _restrictedToSelection
            ? $"Escopo: {_military.Count} selecionado(s)"
            : $"Escopo: relação atual completa ({_military.Count})";
    }

    private MilitaryRecord? SelectedIndividual => IndividualMilitaryList.SelectedItem as MilitaryRecord ?? _military.FirstOrDefault();
    private static string ComboText(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.Text;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        PopulateCompetenceCombos();
        if (_military.Count == 0)
        {
            _military.AddRange((await _repository.GetAllAsync()).DistinctBy(x => x.Id));
            BindMilitaryLists();
        }
        await LoadSettingsAsync();
        await LoadExternalPeopleAsync();
        MainTabs.SelectedIndex = _initialTab;
        BatchMilitaryList.SelectAll();
        await RefreshAutomationStatusAsync();
    }

    private void PopulateCompetenceCombos()
    {
        var years = Enumerable.Range(DateTime.Today.Year - 7, 9).OrderByDescending(x => x).ToList();
        var months = Enumerable.Range(1, 12).Select(x => new MonthOption(x, new DateTime(2000, x, 1).ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR")))).ToList();
        foreach (var combo in new[] { IndividualYearCombo, BatchYearCombo, ExternalYearCombo }) combo.ItemsSource = years;
        foreach (var combo in new[] { IndividualMonthCombo, BatchMonthCombo, ExternalMonthCombo })
        {
            combo.ItemsSource = months;
            combo.DisplayMemberPath = nameof(MonthOption.Name);
            combo.SelectedValuePath = nameof(MonthOption.Number);
        }
    }

    public sealed record MonthOption(int Number, string Name);

    private static string PreferredPaystubRoot()
        => PersonDocumentStorageService.DefaultRoot(App.Paths);

    private async Task LoadSettingsAsync()
    {
        _settings = await App.Json.LoadAsync<PaystubCenterSettings>(App.Paths.PaystubCenterSettingsFile) ?? new PaystubCenterSettings();
        if (string.IsNullOrWhiteSpace(_settings.OutputDirectory)) _settings.OutputDirectory = PreferredPaystubRoot();
        if (string.IsNullOrWhiteSpace(_settings.SheetCode)) _settings.SheetCode = CalculateSheetCode(_settings.Year, _settings.Month).ToString(CultureInfo.InvariantCulture);
        OutputFolderBox.Text = _settings.OutputDirectory;
        SetCompetence(IndividualYearCombo, IndividualMonthCombo, _settings.Year, _settings.Month);
        SetCompetence(BatchYearCombo, BatchMonthCombo, _settings.Year, _settings.Month);
        SetCompetence(ExternalYearCombo, ExternalMonthCombo, _settings.Year, _settings.Month);
        IndividualCodeBox.Text = _settings.SheetCode;
        BatchCodeBox.Text = _settings.SheetCode;
        SelectComboItem(ExternalSystemCombo, _settings.ExternalSystem);
        SelectComboItem(ProcessingCombo, _settings.Processing);
        SelectComboItem(PayrollTypeCombo, _settings.PayrollType);
        SelectComboItem(BrowserCombo, _settings.Browser);
        UpdateExternalMode();
    }

    private static void SetCompetence(ComboBox yearCombo, ComboBox monthCombo, int year, int month)
    {
        yearCombo.SelectedItem = year;
        monthCombo.SelectedValue = month;
    }

    private static void SelectComboItem(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), value, StringComparison.CurrentCultureIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
    }

    private async Task SaveSettingsAsync()
    {
        var (year, month) = GetCompetence(IndividualYearCombo, IndividualMonthCombo);
        _settings.Year = year;
        _settings.Month = month;
        _settings.SheetCode = IndividualCodeBox.Text.Trim();
        _settings.OutputDirectory = OutputFolderBox.Text.Trim();
        _settings.ExternalSystem = ComboText(ExternalSystemCombo);
        _settings.Processing = ComboText(ProcessingCombo);
        _settings.PayrollType = ComboText(PayrollTypeCombo);
        _settings.Browser = ComboText(BrowserCombo);
        await App.Json.SaveAsync(App.Paths.PaystubCenterSettingsFile, _settings);

        // Mantém a central e o baixador individual usando a mesma competência,
        // código da folha, navegador e pasta, inclusive quando a central é fechada
        // sem iniciar um download naquele momento.
        var native = await App.CpexPaystubs.LoadSettingsAsync();
        native.Year = _settings.Year;
        native.Month = _settings.Month;
        native.SheetCode = _settings.SheetCode;
        native.OutputDirectory = _settings.OutputDirectory;
        native.Browser = _settings.Browser;
        await App.CpexPaystubs.SaveSettingsAsync(native);
    }

    private async Task LoadExternalPeopleAsync()
    {
        var rows = await App.Json.LoadAsync<List<ExternalPaystubPerson>>(App.Paths.ExternalPaystubPeopleFile) ?? [];
        _externalPeople.Clear();
        foreach (var row in rows.Where(x => x is not null)) _externalPeople.Add(row);
    }

    private async Task SaveExternalPeopleAsync() => await App.Json.SaveAsync(App.Paths.ExternalPaystubPeopleFile, _externalPeople.ToList());

    private async Task RefreshAutomationStatusAsync()
    {
        try
        {
            var native = await App.CpexPaystubs.LoadSettingsAsync();
            var password = App.CpexPaystubs.ReadSavedPassword(native);
            AutomationStatusText.Text = App.CpexPaystubs.HasPreparedSession
                ? $"Sessão oculta preparada às {App.CpexPaystubs.PreparedAt:HH:mm}"
                : "Automação nativa C# pronta";
            AutomationDetailText.Text = App.CpexPaystubs.HasPreparedSession
                ? "O navegador oculto já está logado e será reutilizado no próximo download."
                : "Selenium .NET + geração de PDF pelo Edge/Chrome; nenhuma instalação de Python é necessária.";
            InstallAutomationButton.Content = "Verificar automação C#";
            UserBox.Text = native.Login;
            PasswordBox.Password = password;
            CredentialStatusText.Text = !string.IsNullOrWhiteSpace(native.Login) && !string.IsNullOrWhiteSpace(password)
                ? $"Login salvo para {native.Login} com proteção local do Windows."
                : "Informe o login e a senha; o SIGFUR pode protegê-los pelo Windows.";
            ExternalUserBox.Text = native.Login;
            ExternalPasswordBox.Password = password;
            ExternalCredentialStatusText.Text = CredentialStatusText.Text;
        }
        catch (Exception ex)
        {
            AutomationStatusText.Text = "Automação C# precisa de conferência";
            AutomationDetailText.Text = ex.Message;
            CredentialStatusText.Text = ex.Message;
            ExternalCredentialStatusText.Text = ex.Message;
        }
    }

    private Task<bool> EnsureAutomationReadyAsync(bool offerInstall = true)
    {
        AutomationStatusText.Text = "Automação nativa C# pronta";
        AutomationDetailText.Text = "O navegador é iniciado somente durante o download; Python não é utilizado.";
        return Task.FromResult(true);
    }

    private async Task InstallAutomationCoreAsync(bool force)
    {
        await RefreshAutomationStatusAsync();
        StatusText.Text = "Automação nativa em C# verificada. Não há pacote Python para instalar.";
    }

    private async void InstallAutomation_Click(object sender, RoutedEventArgs e)
        => await InstallAutomationCoreAsync(true);

    private async Task SaveNativeCredentialsAsync(string login, string password)
    {
        var normalizedLogin = (login ?? string.Empty).Trim();
        await App.CpexPaystubs.SaveCredentialsAsync(normalizedLogin, password, savePassword: true);
        UserBox.Text = normalizedLogin;
        ExternalUserBox.Text = normalizedLogin;
        PasswordBox.Password = password;
        ExternalPasswordBox.Password = password;
    }

    private async void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text) || string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            SigfurDialog.Show(this, "Informe o usuário/CPF e a senha.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunAsync("Salvando login protegido pelo Windows…", async () =>
        {
            await SaveNativeCredentialsAsync(UserBox.Text, PasswordBox.Password);
            CredentialStatusText.Text = $"Login salvo para {UserBox.Text.Trim()}.";
            ExternalCredentialStatusText.Text = CredentialStatusText.Text;
        });
    }

    private async void ClearCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Remover o login salvo do CPEX/SIPPES?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Removendo credencial…", async () =>
        {
            await App.CpexPaystubs.ClearCredentialsAsync();
            App.CpexPaystubs.DisposePreparedSession();
            UserBox.Clear(); PasswordBox.Clear(); ExternalUserBox.Clear(); ExternalPasswordBox.Clear();
            CredentialStatusText.Text = "Login removido.";
            ExternalCredentialStatusText.Text = "Login removido.";
        });
    }

    private async void OpenLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UserBox.Text) && !string.IsNullOrWhiteSpace(PasswordBox.Password))
            await SaveNativeCredentialsAsync(UserBox.Text, PasswordBox.Password);

        var native = await App.CpexPaystubs.LoadSettingsAsync();
        var directSippes = string.Equals(native.System, "SIPPES", StringComparison.OrdinalIgnoreCase);
        ShellService.OpenPath(directSippes
            ? CpexPaystubAutomationService.SippesLoginUrl
            : CpexPaystubAutomationService.LoginUrl);
        StatusText.Text = directSippes
            ? "Login direto do SIPPES aberto no navegador padrão."
            : "Área Exclusiva do CPEX/SIAPPES aberta no navegador padrão.";
    }

    private async void ConfirmSession_Click(object sender, RoutedEventArgs e)
    {
        var external = MainTabs.SelectedIndex == 2;
        var (year, month) = external ? GetCompetence(ExternalYearCombo, ExternalMonthCombo) : GetCompetence(IndividualYearCombo, IndividualMonthCombo);
        await SaveSettingsAsync();
        await RunAsync("Preparando sessão oculta do CPEX/SIPPES…", async () =>
        {
            var settings = await BuildNativeSettingsAsync(year, month, external ? null : IndividualCodeBox.Text, external);
            var password = ResolvePassword(external);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = p.Message;
            });
            await App.CpexPaystubs.PrepareHiddenSessionAsync(settings, password, progress);
            AutomationStatusText.Text = $"Sessão oculta preparada às {App.CpexPaystubs.PreparedAt:HH:mm}";
            AutomationDetailText.Text = "Login feito em navegador oculto. Agora basta clicar em Baixar automaticamente.";
            CredentialStatusText.Text = $"Login preparado para {settings.Login}.";
            ExternalCredentialStatusText.Text = CredentialStatusText.Text;
            StatusText.Text = "Sessão pronta. O próximo download não abrirá janela separada.";
        });
    }

    private async void SaveExternalCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExternalUserBox.Text) || string.IsNullOrWhiteSpace(ExternalPasswordBox.Password))
        {
            SigfurDialog.Show(this, "Informe o usuário/CPF e a senha da Área Exclusiva da UA.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunAsync("Salvando login protegido pelo Windows…", async () =>
        {
            await SaveNativeCredentialsAsync(ExternalUserBox.Text, ExternalPasswordBox.Password);
            ExternalCredentialStatusText.Text = $"Login salvo para {ExternalUserBox.Text.Trim()}.";
            CredentialStatusText.Text = ExternalCredentialStatusText.Text;
        });
    }

    private async void ClearExternalCredentials_Click(object sender, RoutedEventArgs e)
        => ClearCredentials_Click(sender, e);


    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pasta raiz dos documentos financeiros",
            InitialDirectory = Directory.Exists(OutputFolderBox.Text) ? OutputFolderBox.Text : PreferredPaystubRoot(),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderBox.Text = dialog.FolderName;
            _ = SaveSettingsAsync();
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = OutputFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = PreferredPaystubRoot();
        Directory.CreateDirectory(folder);
        ShellService.OpenPath(folder);
    }

    private async void DownloadIndividual_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIndividual;
        if (selected is null) { WarnSelection(); return; }
        var (year, month) = GetCompetence(IndividualYearCombo, IndividualMonthCombo);
        await SaveSettingsAsync();
        IndividualResultBox.Clear();
        await RunEmbeddedDownloaderAsync(
            [ToPerson(selected)],
            year, month, IndividualCodeBox.Text, IndividualResultBox, external: false,
            OverwriteIndividualCheck.IsChecked == true ? ExistingFilePolicy.Replace : ExistingFilePolicy.Skip);
    }

    private async void DownloadFinancialStatementIndividual_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIndividual;
        if (selected is null) { WarnSelection(); return; }
        var (year, _) = GetCompetence(IndividualYearCombo, IndividualMonthCombo);
        await SaveSettingsAsync();
        IndividualResultBox.Clear();
        await RunEmbeddedFinancialStatementDownloaderAsync(
            [ToPerson(selected)],
            year,
            IndividualResultBox,
            OverwriteIndividualCheck.IsChecked == true ? ExistingFilePolicy.Replace : ExistingFilePolicy.Skip);
    }

    private async void DownloadBatch_Click(object sender, RoutedEventArgs e)
    {
        var rows = BatchMilitaryList.SelectedItems.Cast<MilitaryRecord>().DistinctBy(x => x.Id).ToList();
        if (rows.Count == 0) rows = _military.ToList();
        if (rows.Count == 0) { WarnSelection(); return; }
        var (year, month) = GetCompetence(BatchYearCombo, BatchMonthCombo);
        await SaveSettingsAsync();
        BatchResultBox.Clear();
        await RunEmbeddedDownloaderAsync(
            rows.Select(ToPerson).ToList(),
            year, month, BatchCodeBox.Text, BatchResultBox, external: false, ExistingFilePolicy.Ask);
    }

    private async void DownloadFinancialBatch_Click(object sender, RoutedEventArgs e)
    {
        var rows = BatchMilitaryList.SelectedItems.Cast<MilitaryRecord>().DistinctBy(x => x.Id).ToList();
        if (rows.Count == 0) rows = _military.ToList();
        if (rows.Count == 0) { WarnSelection(); return; }
        var (year, _) = GetCompetence(BatchYearCombo, BatchMonthCombo);
        await SaveSettingsAsync();
        BatchResultBox.Clear();
        await RunEmbeddedFinancialStatementDownloaderAsync(
            rows.Select(ToPerson).ToList(),
            year,
            BatchResultBox,
            ExistingFilePolicy.Ask);
    }

    private void OpenBatchManager_Click(object sender, RoutedEventArgs e) => OpenOutputFolder_Click(sender, e);

    private static CpexPaystubPerson ToPerson(MilitaryRecord military)
        => new(military.Name, military.Cpf, military.ShortRank, military.Id, military.MilitaryId, military.PrecCp);

    private async Task RunEmbeddedDownloaderAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        int year,
        int month,
        string? sheetCode,
        TextBox? resultBox,
        bool external,
        ExistingFilePolicy existingPolicy)
    {
        var settings = await BuildNativeSettingsAsync(year, month, sheetCode, external);
        var password = ResolvePassword(external);
        var downloadPeople = people.ToList();
        var skipped = new List<string>();
        var existing = await FindExistingPaystubsAsync(downloadPeople, year, month, settings.OutputDirectory);

        if (existing.Count > 0 && existingPolicy == ExistingFilePolicy.Ask)
        {
            var answer = SigfurDialog.Show(this,
                $"Encontrei {existing.Count} contracheque(s) de {month:00}/{year} já salvo(s) nas pastas individuais.\n\n" +
                "Sim: substituir pelos novos PDFs.\n" +
                "Não: ignorar essas pessoas e baixar somente as demais.\n" +
                "Cancelar: não iniciar o lote.",
                "Contracheques já existentes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            existingPolicy = answer == MessageBoxResult.Yes ? ExistingFilePolicy.Replace : ExistingFilePolicy.Skip;
        }

        if (existing.Count > 0 && existingPolicy == ExistingFilePolicy.Skip)
        {
            var existingIds = existing.Select(x => PersonKey(x.Person)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            skipped.AddRange(existing.Select(x => x.Label));
            downloadPeople = downloadPeople.Where(x => !existingIds.Contains(PersonKey(x))).ToList();
            if (downloadPeople.Count == 0)
            {
                var onlySkipped = $"Todos já possuíam contracheque de {month:00}/{year}.\n\nPulados: {skipped.Count}";
                if (resultBox is not null) resultBox.Text = onlySkipped;
                StatusText.Text = onlySkipped.Replace("\n", " ");
                return;
            }
        }

        await RunAsync($"Baixando {downloadPeople.Count} contracheque(s) em navegador oculto…", async () =>
        {
            var log = new StringBuilder();
            var backups = new List<ReplacementBackup>();
            if (skipped.Count > 0)
                log.AppendLine($"Pulados por já existir na competência {month:00}/{year}: {skipped.Count}").AppendLine();

            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message))
                    StatusText.Text = p.Message;
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    log.AppendLine($"[{p.Current}/{p.Total}] {p.Name}");
                    if (resultBox is not null)
                    {
                        resultBox.Text = log.ToString();
                        resultBox.ScrollToEnd();
                    }
                }
            });

            try
            {
                if (existingPolicy == ExistingFilePolicy.Replace)
                    backups.AddRange(StageReplacementBackups(existing));

                var result = await App.CpexPaystubs.DownloadPreparedAsync(downloadPeople, settings, password, progress);
                FinalizeReplacementBackups(backups, result.DownloadedFiles);
                _paystubs.InvalidateCache();
                var summary = BuildDownloadSummary(result, skipped, settings.OutputDirectory, year, month);
                if (resultBox is not null) resultBox.Text = summary;
                AutomationStatusText.Text = $"Sessão oculta preparada às {App.CpexPaystubs.PreparedAt:HH:mm}";
                AutomationDetailText.Text = "Download executado dentro da Central, sem abrir o baixador separado.";
                StatusText.Text = $"Concluído: {result.Downloaded} salvo(s), {result.Failures.Count} falha(s), {skipped.Count} pulado(s).";
            }
            catch
            {
                RestoreReplacementBackups(backups);
                throw;
            }
        });
    }

    private async Task RunEmbeddedFinancialStatementDownloaderAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        int year,
        TextBox? resultBox,
        ExistingFilePolicy existingPolicy)
    {
        var settings = await BuildNativeSettingsAsync(year, DateTime.Today.Month, null, external: false);
        settings.System = "SIAPPES";
        settings.Headless = true;
        settings.OpenAfterDownload = false;
        var password = ResolvePassword(external: false);
        var downloadPeople = people.ToList();
        var skipped = new List<string>();
        var existing = await FindExistingFinancialStatementsAsync(downloadPeople, year, settings.OutputDirectory);

        if (existing.Count > 0 && existingPolicy == ExistingFilePolicy.Ask)
        {
            var answer = SigfurDialog.Show(this,
                $"Encontrei {existing.Count} ficha(s) financeira(s) de {year} já salva(s) nas pastas individuais.\n\n" +
                "Sim: substituir pelos novos PDFs.\n" +
                "Não: ignorar essas pessoas e baixar somente as demais.\n" +
                "Cancelar: não iniciar o lote.",
                "Fichas financeiras já existentes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            existingPolicy = answer == MessageBoxResult.Yes ? ExistingFilePolicy.Replace : ExistingFilePolicy.Skip;
        }

        if (existing.Count > 0 && existingPolicy == ExistingFilePolicy.Skip)
        {
            var existingIds = existing.Select(x => PersonKey(x.Person)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            skipped.AddRange(existing.Select(x => x.Label));
            downloadPeople = downloadPeople.Where(x => !existingIds.Contains(PersonKey(x))).ToList();
            if (downloadPeople.Count == 0)
            {
                var onlySkipped = $"Todos já possuíam ficha financeira de {year}.\n\nPulados: {skipped.Count}";
                if (resultBox is not null) resultBox.Text = onlySkipped;
                StatusText.Text = onlySkipped.Replace("\n", " ");
                return;
            }
        }

        await RunAsync($"Baixando {downloadPeople.Count} ficha(s) financeira(s) em navegador oculto…", async () =>
        {
            var log = new StringBuilder();
            var backups = new List<ReplacementBackup>();
            if (skipped.Count > 0)
                log.AppendLine($"Pulados por já existir ficha financeira de {year}: {skipped.Count}").AppendLine();

            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = p.Message;
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    log.AppendLine($"[{p.Current}/{p.Total}] {p.Name}");
                    if (resultBox is not null)
                    {
                        resultBox.Text = log.ToString();
                        resultBox.ScrollToEnd();
                    }
                }
            });

            try
            {
                if (existingPolicy == ExistingFilePolicy.Replace)
                    backups.AddRange(StageReplacementBackups(existing));

                var result = await App.CpexPaystubs.DownloadFinancialStatementsPreparedAsync(downloadPeople, settings, year, password, progress);
                FinalizeReplacementBackups(backups, result.DownloadedFiles);
                _paystubs.InvalidateCache();
                var summary = BuildFinancialStatementSummary(result, skipped, settings.OutputDirectory, year);
                if (resultBox is not null) resultBox.Text = summary;
                AutomationStatusText.Text = $"Sessão oculta CPEx preparada às {App.CpexPaystubs.PreparedAt:HH:mm}";
                AutomationDetailText.Text = "Ficha financeira baixada direto pela Central, sem abrir a Carteira.";
                StatusText.Text = $"Concluído: {result.Downloaded} ficha(s) salva(s), {result.Failures.Count} falha(s), {skipped.Count} pulado(s).";
            }
            catch
            {
                RestoreReplacementBackups(backups);
                throw;
            }
        });
    }

    private async Task<CpexPaystubSettings> BuildNativeSettingsAsync(int year, int month, string? sheetCode, bool external)
    {
        var settings = await App.CpexPaystubs.LoadSettingsAsync();
        settings.Year = year;
        settings.Month = month;
        settings.SheetCode = string.IsNullOrWhiteSpace(sheetCode)
            ? CalculateSheetCode(year, month).ToString(CultureInfo.InvariantCulture)
            : sheetCode.Trim();
        settings.OutputDirectory = string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? PreferredPaystubRoot() : OutputFolderBox.Text.Trim();
        settings.Browser = string.IsNullOrWhiteSpace(_settings.Browser) ? "Edge" : _settings.Browser;
        settings.System = external ? ComboText(ExternalSystemCombo) : "SIPPES";
        settings.Processing = ComboText(ProcessingCombo);
        settings.PayrollType = ComboText(PayrollTypeCombo);
        settings.Headless = true;
        settings.OpenAfterDownload = false;
        settings.Login = (external && !string.IsNullOrWhiteSpace(ExternalUserBox.Text) ? ExternalUserBox.Text : UserBox.Text).Trim();

        var password = ResolvePassword(external);
        if (!string.IsNullOrWhiteSpace(password))
        {
            settings.SavePassword = true;
            settings.ProtectedPassword = WindowsSecretProtector.Protect(password);
        }
        await App.CpexPaystubs.SaveSettingsAsync(settings);
        return settings;
    }

    private string ResolvePassword(bool external)
    {
        if (external && !string.IsNullOrWhiteSpace(ExternalPasswordBox.Password)) return ExternalPasswordBox.Password;
        if (!string.IsNullOrWhiteSpace(PasswordBox.Password)) return PasswordBox.Password;
        return ExternalPasswordBox.Password;
    }

    private async Task<List<ExistingPaystub>> FindExistingPaystubsAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        int year,
        int month,
        string outputDirectory)
    {
        var found = new List<ExistingPaystub>();
        foreach (var person in people)
        {
            var military = _military.FirstOrDefault(x => x.Id == person.SourceId);
            var isExternal = military is null;
            var folder = isExternal
                ? PersonDocumentStorageService.BuildFolder(
                    outputDirectory, person.Rank, person.Name, person.Cpf, person.PrecCp, external: true)
                : PersonDocumentStorageService.PrepareRegisteredFolder(
                    App.Paths, outputDirectory, person.Rank, person.Name, person.Cpf, person.PrecCp);

            var expectedSettings = new CpexPaystubSettings
            {
                OutputDirectory = outputDirectory,
                Year = year,
                Month = month
            };
            var expected = CpexPaystubAutomationService.BuildPaystubOutputPath(expectedSettings, person);
            var expectedFolder = Path.GetDirectoryName(expected) ?? folder;
            if (!Directory.Exists(expectedFolder)) Directory.CreateDirectory(expectedFolder);

            var probe = military ?? new MilitaryRecord
            {
                Id = person.SourceId,
                Rank = person.Rank,
                Name = person.Name,
                Cpf = person.Cpf,
                PrecCp = person.PrecCp,
                MilitaryId = person.MilitaryId
            };
            var path = File.Exists(expected)
                ? expected
                : await _paystubs.FindBestOnlyInDirectoryAsync(probe, month, year, folder);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            // Normaliza automaticamente PDFs que foram salvos com nomes errados em patches anteriores
            // para o padrão antigo do contracheque_manager.py: "JANEIRO - 2026.pdf".
            if (!SamePath(path, expected))
            {
                try
                {
                    File.Move(path, expected, overwrite: true);
                    path = expected;
                    _paystubs.InvalidateCache();
                }
                catch { }
            }

            var label = military is null ? person.Name : $"{military.ShortRank} {military.Name}";
            found.Add(new ExistingPaystub(person, path, expectedFolder, label));
        }
        return found;
    }

    private async Task<List<ExistingPaystub>> FindExistingFinancialStatementsAsync(
        IReadOnlyList<CpexPaystubPerson> people,
        int year,
        string outputDirectory)
    {
        await Task.Yield();
        var found = new List<ExistingPaystub>();
        foreach (var person in people)
        {
            var settings = new CpexPaystubSettings { OutputDirectory = outputDirectory };
            var expected = CpexPaystubAutomationService.BuildFinancialStatementOutputPath(settings, person, year);
            var folder = Path.GetDirectoryName(expected) ?? string.Empty;
            string? path = File.Exists(expected) ? expected : null;

            if (path is null)
            {
                var cpf = MilitaryFormatting.Digits(person.Cpf);
                var candidateFolders = new[] { folder, Path.GetDirectoryName(folder) ?? string.Empty }
                    .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                path = candidateFolders
                    .SelectMany(dir => Directory.EnumerateFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly))
                    .FirstOrDefault(file => IsFinancialStatementFileForYear(file, year, cpf));
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var military = _military.FirstOrDefault(x => x.Id == person.SourceId);
            var label = military is null ? person.Name : $"{military.ShortRank} {military.Name}";
            found.Add(new ExistingPaystub(person, path, folder, label));
        }
        return found;
    }

    private static bool IsFinancialStatementFileForYear(string path, int year, string cpf)
    {
        var file = NormalizeName(Path.GetFileNameWithoutExtension(path));
        var folder = NormalizeName(Path.GetDirectoryName(path) ?? string.Empty);
        var text = file + " " + folder;
        if (!text.Contains("ficha") || !text.Contains("financeira")) return false;
        if (!text.Contains(year.ToString(CultureInfo.InvariantCulture))) return false;
        return string.IsNullOrWhiteSpace(cpf) || MilitaryFormatting.Digits(text).Contains(cpf, StringComparison.Ordinal);
    }

    private static IEnumerable<ReplacementBackup> StageReplacementBackups(IEnumerable<ExistingPaystub> existing)
    {
        var backups = new List<ReplacementBackup>();
        try
        {
            foreach (var item in existing)
            {
                if (!File.Exists(item.Path)) continue;
                var backup = item.Path + $".sigfur-substituir-{Guid.NewGuid():N}.bak";
                File.Move(item.Path, backup);
                backups.Add(new ReplacementBackup(item, backup));
            }
            return backups;
        }
        catch
        {
            RestoreReplacementBackups(backups);
            throw;
        }
    }

    private static void FinalizeReplacementBackups(
        IEnumerable<ReplacementBackup> backups,
        IReadOnlyList<string> downloadedFiles)
    {
        foreach (var backup in backups)
        {
            var replaced = downloadedFiles.Any(path => SameDirectory(path, backup.Existing.Folder));
            if (replaced)
            {
                try { File.Delete(backup.BackupPath); } catch { }
            }
            else
            {
                RestoreReplacementBackup(backup);
            }
        }
    }

    private static void RestoreReplacementBackups(IEnumerable<ReplacementBackup> backups)
    {
        foreach (var backup in backups.Reverse()) RestoreReplacementBackup(backup);
    }

    private static void RestoreReplacementBackup(ReplacementBackup backup)
    {
        try
        {
            if (!File.Exists(backup.BackupPath)) return;
            File.Move(backup.BackupPath, backup.Existing.Path, overwrite: true);
        }
        catch { }
    }

    private static bool SameDirectory(string path, string folder)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }



    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string PersonKey(CpexPaystubPerson person)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        if (!string.IsNullOrWhiteSpace(cpf)) return "CPF:" + cpf;
        var prec = MilitaryFormatting.Digits(person.PrecCp);
        if (!string.IsNullOrWhiteSpace(prec)) return "PREC:" + prec;
        return "ID:" + person.SourceId.ToString(CultureInfo.InvariantCulture) + ":" + NormalizeName(person.Name);
    }

    private static string BuildDownloadSummary(
        CpexPaystubBatchResult result,
        IReadOnlyList<string> skipped,
        string outputDirectory,
        int year,
        int month)
    {
        var sb = new StringBuilder()
            .AppendLine($"CONTRACHEQUES — {month:00}/{year}")
            .AppendLine()
            .AppendLine($"Salvos: {result.Downloaded}")
            .AppendLine($"Pulados: {skipped.Count}")
            .AppendLine($"Falhas: {result.Failures.Count}")
            .AppendLine()
            .AppendLine("Pasta raiz:")
            .AppendLine(outputDirectory);

        if (result.DownloadedFiles.Count > 0)
        {
            sb.AppendLine().AppendLine("Arquivos salvos:");
            foreach (var path in result.DownloadedFiles.Take(30)) sb.AppendLine("• " + path);
            if (result.DownloadedFiles.Count > 30) sb.AppendLine($"• ... mais {result.DownloadedFiles.Count - 30} arquivo(s)");
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine().AppendLine("Pulados por já existir:");
            foreach (var item in skipped.Take(30)) sb.AppendLine("• " + item);
            if (skipped.Count > 30) sb.AppendLine($"• ... mais {skipped.Count - 30} pessoa(s)");
        }

        if (result.Failures.Count > 0)
        {
            sb.AppendLine().AppendLine("Falhas:");
            foreach (var failure in result.Failures.Take(30)) sb.AppendLine("• " + failure);
            if (result.Failures.Count > 30) sb.AppendLine($"• ... mais {result.Failures.Count - 30} falha(s)");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinancialStatementSummary(
        CpexPaystubBatchResult result,
        IReadOnlyList<string> skipped,
        string outputDirectory,
        int year)
    {
        var sb = new StringBuilder()
            .AppendLine($"FICHAS FINANCEIRAS — {year}")
            .AppendLine()
            .AppendLine($"Salvas: {result.Downloaded}")
            .AppendLine($"Puladas: {skipped.Count}")
            .AppendLine($"Falhas: {result.Failures.Count}")
            .AppendLine()
            .AppendLine("Pasta raiz:")
            .AppendLine(outputDirectory)
            .AppendLine()
            .AppendLine("Padrão de salvamento:")
            .AppendLine("...\\MILITAR\\Ficha Financeira\\CPF - NOME - Ficha Financeira - ANO.pdf");

        if (result.DownloadedFiles.Count > 0)
        {
            sb.AppendLine().AppendLine("Arquivos salvos:");
            foreach (var path in result.DownloadedFiles.Take(30)) sb.AppendLine("• " + path);
            if (result.DownloadedFiles.Count > 30) sb.AppendLine($"• ... mais {result.DownloadedFiles.Count - 30} arquivo(s)");
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine().AppendLine("Puladas por já existir:");
            foreach (var item in skipped.Take(30)) sb.AppendLine("• " + item);
            if (skipped.Count > 30) sb.AppendLine($"• ... mais {skipped.Count - 30} pessoa(s)");
        }

        if (result.Failures.Count > 0)
        {
            sb.AppendLine().AppendLine("Falhas:");
            foreach (var failure in result.Failures.Take(30)) sb.AppendLine("• " + failure);
            if (result.Failures.Count > 30) sb.AppendLine($"• ... mais {result.Failures.Count - 30} falha(s)");
        }
        return sb.ToString().TrimEnd();
    }

    private void SelectAllBatch_Click(object sender, RoutedEventArgs e) => BatchMilitaryList.SelectAll();
    private void ClearBatchSelection_Click(object sender, RoutedEventArgs e) => BatchMilitaryList.UnselectAll();

    private async void AddExternal_Click(object sender, RoutedEventArgs e)
    {
        var name = NormalizeName(ExternalNameBox.Text);
        var digits = MilitaryFormatting.Digits(ExternalDocumentBox.Text);
        var system = ComboText(ExternalSystemCombo).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name) || (system == "SIPPES" ? digits.Length != 11 : digits.Length is not (9 or 10)))
        {
            SigfurDialog.Show(this, system == "SIPPES" ? "Informe nome e CPF válido." : "Informe nome e PREC-CP válido.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var existing = _externalPeople.FirstOrDefault(x => system == "SIPPES" ? MilitaryFormatting.Digits(x.Cpf) == digits : MilitaryFormatting.Digits(x.Prec) == digits);
        if (existing is null)
            _externalPeople.Add(new ExternalPaystubPerson { Name = name, Cpf = system == "SIPPES" ? digits : string.Empty, Prec = system == "SIAPPES" ? digits : string.Empty });
        else
            existing.Name = name;
        ExternalPeopleGrid.Items.Refresh();
        ExternalNameBox.Clear();
        ExternalDocumentBox.Clear();
        await SaveExternalPeopleAsync();
    }

    private int AddExternalFromText(string text, string system)
    {
        var added = 0;
        foreach (var line in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = Regex.Matches(line, @"(?<!\d)\d(?:[.\-/\s]?\d){8,10}(?!\d)");
            var match = matches.Cast<Match>().FirstOrDefault(x => system == "SIPPES"
                ? MilitaryFormatting.Digits(x.Value).Length == 11
                : MilitaryFormatting.Digits(x.Value).Length is 9 or 10);
            if (match is null) continue;
            var digits = MilitaryFormatting.Digits(match.Value);
            var name = NormalizeName((line[..match.Index] + " " + line[(match.Index + match.Length)..]).Trim(' ', '-', ':', ';', '|', ','));
            if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) continue;
            var exists = _externalPeople.Any(x => system == "SIPPES"
                ? MilitaryFormatting.Digits(x.Cpf) == digits
                : MilitaryFormatting.Digits(x.Prec) == digits);
            if (exists) continue;
            _externalPeople.Add(new ExternalPaystubPerson
            {
                Name = name,
                Cpf = system == "SIPPES" ? digits : string.Empty,
                Prec = system == "SIAPPES" ? digits : string.Empty
            });
            added++;
        }
        return added;
    }

    private async void PasteExternal_Click(object sender, RoutedEventArgs e)
    {
        var system = ComboText(ExternalSystemCombo).ToUpperInvariant();
        var prompt = new TextPromptWindow("Colar lista de pessoas", system == "SIPPES"
            ? "Cole uma pessoa por linha no formato NOME CPF. O CPF pode ter pontuação."
            : "Cole uma pessoa por linha no formato PREC-CP NOME ou NOME PREC-CP.") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var added = AddExternalFromText(prompt.Value, system);
        await SaveExternalPeopleAsync();
        StatusText.Text = $"{added} pessoa(s) adicionada(s) à lista.";
    }

    private async void ImportExternal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar pessoas fora",
            Filter = "Arquivos de texto ou CSV|*.txt;*.csv|Todos os arquivos|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var system = ComboText(ExternalSystemCombo).ToUpperInvariant();
        var text = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
        var added = AddExternalFromText(text, system);
        await SaveExternalPeopleAsync();
        StatusText.Text = $"Importação concluída: {added} pessoa(s) adicionada(s).";
    }

    private async void ExportExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_externalPeople.Count == 0)
        {
            SigfurDialog.Show(this, "A lista de pessoas fora está vazia.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exportar pessoas fora",
            Filter = "CSV|*.csv|Texto|*.txt",
            FileName = $"Pessoas_fora_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        var lines = new List<string> { "NOME;CPF;PREC_CP" };
        lines.AddRange(_externalPeople.Select(x => $"{x.Name.Replace(";", ",")};{MilitaryFormatting.Digits(x.Cpf)};{MilitaryFormatting.Digits(x.Prec)}"));
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true));
        StatusText.Text = $"Lista exportada: {dialog.FileName}";
    }

    private void SelectAllExternal_Click(object sender, RoutedEventArgs e) => ExternalPeopleGrid.SelectAll();
    private void ClearExternalSelection_Click(object sender, RoutedEventArgs e) => ExternalPeopleGrid.UnselectAll();

    private async void RemoveExternal_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ExternalPeopleGrid.SelectedItems.Cast<ExternalPaystubPerson>().ToList()) _externalPeople.Remove(row);
        await SaveExternalPeopleAsync();
    }

    private async void ClearExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_externalPeople.Count == 0) return;
        if (SigfurDialog.Show(this, "Limpar toda a lista de pessoas fora?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _externalPeople.Clear();
        await SaveExternalPeopleAsync();
    }

    private async void DownloadExternal_Click(object sender, RoutedEventArgs e)
    {
        var selectedExternal = ExternalPeopleGrid.SelectedItems.Cast<ExternalPaystubPerson>().ToList();
        var source = selectedExternal.Count > 0 ? selectedExternal : _externalPeople.ToList();
        var people = source
            .Where(x => MilitaryFormatting.Digits(x.Cpf).Length == 11)
            .Select(x => new CpexPaystubPerson(
                string.IsNullOrWhiteSpace(x.Name) ? MilitaryFormatting.Digits(x.Cpf) : x.Name,
                x.Cpf,
                "Pessoa de fora"))
            .ToList();
        if (people.Count == 0)
        {
            SigfurDialog.Show(this,
                "A automação nativa C# usa CPF. Adicione pessoas com CPF válido para baixar os contracheques.",
                "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (year, month) = GetCompetence(ExternalYearCombo, ExternalMonthCombo);
        if (!string.IsNullOrWhiteSpace(ExternalUserBox.Text)) UserBox.Text = ExternalUserBox.Text;
        if (!string.IsNullOrWhiteSpace(ExternalPasswordBox.Password)) PasswordBox.Password = ExternalPasswordBox.Password;
        ExternalResultBox.Clear();
        await RunEmbeddedDownloaderAsync(people, year, month, null, ExternalResultBox, external: true, ExistingFilePolicy.Ask);
    }

    private void ExternalSystem_Changed(object sender, SelectionChangedEventArgs e) => UpdateExternalMode();

    private void UpdateExternalMode()
    {
        if (ExternalDownloadButton is null || ExternalSystemCombo is null || ExternalDocumentBox is null || ProcessingCombo is null || PayrollTypeCombo is null) return;
        var system = ComboText(ExternalSystemCombo).ToUpperInvariant();
        ExternalDownloadButton.Content = $"Baixar lista {system}";
        ExternalDocumentBox.ToolTip = system == "SIPPES" ? "CPF" : "PREC-CP";
        ProcessingCombo.IsEnabled = system == "SIPPES";
        PayrollTypeCombo.IsEnabled = system == "SIPPES";
    }

    private void Competence_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (sender == IndividualYearCombo || sender == IndividualMonthCombo)
        {
            var (year, month) = GetCompetence(IndividualYearCombo, IndividualMonthCombo);
            IndividualCodeBox.Text = CalculateSheetCode(year, month).ToString(CultureInfo.InvariantCulture);
        }
        else if (sender == BatchYearCombo || sender == BatchMonthCombo)
        {
            var (year, month) = GetCompetence(BatchYearCombo, BatchMonthCombo);
            BatchCodeBox.Text = CalculateSheetCode(year, month).ToString(CultureInfo.InvariantCulture);
        }
        var external = GetCompetence(ExternalYearCombo, ExternalMonthCombo);
        ExternalCompetenceText.Text = $"{external.Month:00}/{external.Year}";
    }

    private static (int Year, int Month) GetCompetence(ComboBox yearCombo, ComboBox monthCombo)
    {
        var year = yearCombo.SelectedItem is int y ? y : DateTime.Today.Year;
        var month = monthCombo.SelectedValue is int m ? m : DateTime.Today.Month;
        return (year, month);
    }

    private static int CalculateSheetCode(int year, int month)
    {
        var deltaMonths = (year - 2026) * 12 + (month - 4);
        return 4178 + deltaMonths * 20;
    }

    private async void OpenLatest_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIndividual;
        if (selected is null) { WarnSelection(); return; }
        await RunAsync($"Localizando o último contracheque de {selected.WarName}…", async () =>
        {
            var files = await _paystubs.FindForMilitaryAsync(selected);
            var latest = files.OrderByDescending(x => x.ModifiedAt).FirstOrDefault();
            if (latest is null || string.IsNullOrWhiteSpace(latest.Path) || !File.Exists(latest.Path))
            {
                SigfurDialog.Show(this, "Nenhum contracheque salvo foi localizado para este militar.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShellService.OpenPath(latest.Path);
            StatusText.Text = $"Aberto: {latest.FileName}";
        });
    }

    private async void OpenSaved_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIndividual;
        if (selected is null) { WarnSelection(); return; }
        var window = new MilitaryWalletWindow(_repository, _paystubs, selected, 6) { Owner = this };
        window.Show();
        window.Activate();
        await Task.CompletedTask;
    }

    private async void OpenSavedFinancialStatements_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIndividual;
        if (selected is null) { WarnSelection(); return; }
        var window = new MilitaryWalletWindow(_repository, _paystubs, selected, 7) { Owner = this };
        window.Show();
        window.Activate();
        await Task.CompletedTask;
    }

    private void OpenAdvancedManager_Click(object sender, RoutedEventArgs e)
        => OpenSavedFinancialStatements_Click(sender, e);

    private void OpenAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_military.Count == 0) { WarnSelection(); return; }
        var window = new PaystubAuditWindow(_repository, _paystubs, _military, OutputFolderBox.Text) { Owner = this };
        window.Show();
    }

    private async void ExportMonth_Click(object sender, RoutedEventArgs e)
    {
        if (_military.Count == 0) { WarnSelection(); return; }
        var dialog = new PaystubExportWindow(_military.Count) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync("Pesquisando e exportando PDFs salvos…", async () =>
        {
            var progress = new Progress<(int Current, int Total, string Name)>(x => StatusText.Text = $"Exportando {x.Current}/{x.Total}: {x.Name}");
            var result = await _paystubs.ExportAsync(_military, dialog.Month, dialog.Year, dialog.Folder, progress);
            SigfurDialog.Show(this, $"Pasta: {dialog.Folder}\n\nExportados: {result.Exported}\nFalhas: {result.Failures.Count}", "Exportar contracheques", MessageBoxButton.OK, result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        });
    }

    private void RefreshSaved_Click(object sender, RoutedEventArgs e)
    {
        _paystubs.InvalidateCache();
        StatusText.Text = "Pesquisa de PDFs atualizada. A próxima abertura fará uma nova varredura.";
    }

    private async Task RunAsync(string status, Func<Task> action, Button? sourceButton = null)
    {
        if (_busy) return;
        try
        {
            _busy = true;
            BusyProgress.Visibility = Visibility.Visible;
            if (sourceButton is not null) sourceButton.IsEnabled = false;
            StatusText.Text = status;
            await action();
            if (StatusText.Text == status) StatusText.Text = "Operação concluída.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "A operação não foi concluída.";
            SigfurDialog.Show(this, ex.Message + "\n\nConfira o login, a conexão com o CPEX/SIPPES e o arquivo de log em AppData\\Local\\SIGFUR\\logs.", "SIGFUR — Contracheques", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            BusyProgress.Visibility = Visibility.Collapsed;
            if (sourceButton is not null) sourceButton.IsEnabled = true;
        }
    }

    private static string BuildResultText(JsonObject result, string title)
    {
        var saved = (result["saved"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var skipped = (result["skipped"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var errors = (result["errors"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var folder = result["folder"]?.GetValue<string>() ?? Path.GetDirectoryName(saved.FirstOrDefault() ?? string.Empty) ?? string.Empty;
        var sb = new StringBuilder().AppendLine(title).AppendLine()
            .AppendLine($"Salvos: {saved.Count}")
            .AppendLine($"Já existentes/pulados: {skipped.Count}")
            .AppendLine($"Falhas: {errors.Count}");
        if (!string.IsNullOrWhiteSpace(folder)) sb.AppendLine().AppendLine("Pasta:").AppendLine(folder);
        if (saved.Count > 0) sb.AppendLine().AppendLine("Arquivos salvos:").AppendLine(string.Join(Environment.NewLine, saved.Take(20)));
        if (errors.Count > 0) sb.AppendLine().AppendLine("Falhas:").AppendLine(string.Join(Environment.NewLine, errors.Take(20)));
        return sb.ToString().Trim();
    }

    private static string NormalizeName(string? value)
    {
        var name = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '.', ',', ':', ';', '-', '_');
        name = Regex.Replace(name, @"^(?:SD\s+RCR|SD\s+EV|SD\s+EF|CB|SOLDADO)\s+", string.Empty, RegexOptions.IgnoreCase);
        return name.ToUpperInvariant();
    }

    private void WarnSelection() => SigfurDialog.Show(this, "Nenhum militar disponível para esta ação.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            await SaveExternalPeopleAsync();
            App.CpexPaystubs.DisposePreparedSession();
        }
        catch { }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            await SaveExternalPeopleAsync();
        }
        catch { }
        Close();
    }
}
