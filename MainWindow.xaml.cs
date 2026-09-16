using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.ViewModels;
using SIGFUR.Wpf.Views;
using SIGFUR.Wpf.Views.Finance;
using SIGFUR.Wpf.Views.Tools;
using SIGFUR.Wpf.Views.Bulletin;
using SIGFUR.Wpf.Views.Military;
using SIGFUR.Wpf.Views.Licensed;
using SIGFUR.Wpf.Views.Reminders;
using SIGFUR.Wpf.Views.Vacation;
using SIGFUR.Wpf.Views.Documents;
using SIGFUR.Wpf.Views.PlanCall;
using SIGFUR.Wpf.Views.Personnel;

using SIGFUR.Wpf.Views.Furriel;
using SIGFUR.Wpf.Views.Intelligent;
using SIGFUR.Wpf.Views.ExternalBulletins;
namespace SIGFUR.Wpf;

public partial class MainWindow : Window
{
    private void SisbolHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = new SisbolSubmissionHistoryWindow { Owner = this };
        window.Show();
        window.Activate();
    }

    private readonly MainWindowViewModel _vm;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _sisbolTimer;
    private readonly DispatcherTimer _dashboardTimer;
    private readonly DispatcherTimer _assistantDeadlineTimer;
    private bool _assistantDeadlinesRunning;
    private List<string> _assistantAlertKeys = [];
    private WindowStateData _windowState;
    private Dictionary<string, string> _hotkeys;
    private bool _closingAccepted;
    private bool _sisbolTimerRunning;
    private bool _dashboardTimerRunning;
    private bool _wasMinimized;
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private MilitaryListWindow? _militaryListWindow;
    private PaystubAuditWindow? _paystubAuditWindow;
    private AssistantChatWindow? _assistantWindow;

    public MainWindow(UiProfile profile, WindowStateData windowState, Dictionary<string, string> hotkeys, string startupWarning)
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        UpdateMaximizeButton();
        // A janela principal já possui persistência própria em WindowStateData.
        // Anexá-la também ao UiState fazia dois arquivos diferentes disputarem
        // tamanho/posição, principalmente na volta de uma minimização.
        GlobalUiScaleService.Attach(this);
        _settings = App.Settings;
        _windowState = windowState;
        _hotkeys = hotkeys;
        _vm = new MainWindowViewModel(App.Dashboard, App.Backup, App.Settings, App.Paths, App.Log, profile, windowState, hotkeys)
        {
            NativeActionRequested = HandleNativeActionAsync,
            NotificationRequested = ShowNotificationAsync,
            NativeMilitaryWalletRequested = OpenNativeMilitaryWalletAsync
        };
        DataContext = _vm;
        RestoreWindowState();
        _lastNonMinimizedState = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        GlobalUiScaleService.ScaleChanged += GlobalUiScaleService_ScaleChanged;
        ApplyScale();
        LoadLogo(profile.LogoPath);
        BuildHotkeys();
        if (!string.IsNullOrWhiteSpace(startupWarning)) _vm.StatusText = "Inicialização com avisos: " + startupWarning;

        // As versões anteriores consultavam o Selenium a cada 2 segundos e
        // reconstruíam todo o dashboard a cada 10 segundos. Em computadores mais
        // fracos isso bloqueava a rolagem. Agora as consultas são espaçadas, não
        // concorrentes e só rodam quando a janela principal está realmente ativa.
        _sisbolTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _sisbolTimer.Tick += async (_, _) =>
        {
            if (_sisbolTimerRunning || !IsVisible || !IsActive || WindowState == WindowState.Minimized) return;
            _sisbolTimerRunning = true;
            try { await _vm.RefreshSisbolStateAsync(); }
            catch (Exception ex) { await App.Log.WriteAsync("Falha na atualização leve do estado do SisBol.", ex); }
            finally { _sisbolTimerRunning = false; }
        };

        _dashboardTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = TimeSpan.FromMinutes(2)
        };
        _dashboardTimer.Tick += async (_, _) =>
        {
            if (_dashboardTimerRunning || !IsVisible || !IsActive || WindowState == WindowState.Minimized) return;
            _dashboardTimerRunning = true;
            try { await _vm.RefreshDashboardSilentAsync(); }
            catch (Exception ex) { await App.Log.WriteAsync("Falha na atualização leve do painel.", ex); }
            finally { _dashboardTimerRunning = false; }
        };

        _sisbolTimer.Start();
        _dashboardTimer.Start();
        _assistantDeadlineTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromMinutes(2) };
        _assistantDeadlineTimer.Tick += async (_, _) => await RefreshAssistantDeadlinesAsync();
        _assistantDeadlineTimer.Start();
        Activated += async (_, _) =>
        {
            if (_sisbolTimerRunning) return;
            _sisbolTimerRunning = true;
            try { await _vm.RefreshSisbolStateAsync(); }
            catch { }
            finally { _sisbolTimerRunning = false; }
        };
        Closing += OnClosing;
        Closed += (_, _) => { _sisbolTimer.Stop(); _dashboardTimer.Stop(); _assistantDeadlineTimer.Stop(); };
    }

    public async Task InitializeAsync()
    {
        // Garante a migração dos lembretes antigos para a fila numerada antes
        // de o Dashboard montar os destaques dos três primeiros itens.
        await App.Reminders.LoadAsync();
        await _vm.InitializeAsync();
        await RefreshPaymentRunReminderAsync();
        await ShowUrgentRemindersAsync();
        await RefreshAssistantDeadlinesAsync();
        ShowBirthdayNotice();
    }

    private void ShowBirthdayNotice()
    {
        var today = _vm.Dashboard.Birthdays.Where(x => x.IsToday).ToList();
        if (today.Count == 0) return;
        var window = new BirthdayNoticeWindow(today) { Owner = this };
        if (window.ShowDialog() == true)
            ShowWorkspaceWindow(new BirthdaysWindow(_vm.Dashboard.Birthdays, App.Paths, App.Json));
    }

    private string AssistantAlertStatePath => Path.Combine(App.Paths.CacheDirectory, "assistant_deadlines_seen.json");

    private async Task RefreshAssistantDeadlinesAsync()
    {
        if (_assistantDeadlinesRunning) return;
        _assistantDeadlinesRunning = true;
        try
        {
            var settings = await App.AssistantStorage.LoadSettingsAsync();
            if (!settings.EnableDeadlineNotifications) { AssistantDeadlineBanner.Visibility = Visibility.Collapsed; return; }
            var snapshot = await App.AssistantOperations.BuildSnapshotAsync();
            var seen = await App.Json.LoadAsync<List<string>>(AssistantAlertStatePath) ?? [];
            var attention = AssistantOperationsService.SelectAttention(snapshot.Items, DateTime.Today, settings.DeadlineLookaheadDays)
                .Where(x => !seen.Contains(AssistantOperationsService.AlertKey(x, DateTime.Today))).ToList();
            _assistantAlertKeys = attention.Select(x => AssistantOperationsService.AlertKey(x, DateTime.Today)).Distinct().ToList();
            AssistantDeadlineBanner.Visibility = attention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AssistantDeadlineText.Text = $"{attention.Count} pendência(s) precisam de atenção";
            AssistantDeadlineDetail.Text = $"{attention.Count(x => x.DueDate?.Date < DateTime.Today)} atrasadas • {attention.Count(x => x.DueDate?.Date == DateTime.Today)} para hoje • próximos {settings.DeadlineLookaheadDays} dias";
        }
        catch (Exception ex) { await App.Log.WriteAsync("Falha ao atualizar alertas do assistente.", ex); }
        finally { _assistantDeadlinesRunning = false; }
    }

    private void AssistantDeadlineOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenAssistant();
        _assistantWindow?.ShowOperations();
    }

    private async void AssistantDeadlineDismiss_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var seen = await App.Json.LoadAsync<List<string>>(AssistantAlertStatePath) ?? [];
            await App.Json.SaveAsync(AssistantAlertStatePath, seen.Concat(_assistantAlertKeys).Distinct().TakeLast(2000).ToList());
            AssistantDeadlineBanner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { await App.Log.WriteAsync("Falha ao dispensar alertas do assistente.", ex); }
    }

    private async Task ShowUrgentRemindersAsync()
    {
        try
        {
            var settings = await App.Reminders.LoadSettingsAsync();
            var urgent = await App.Reminders.GetUrgentAsync(settings.UpcomingDays);
            if (urgent.Count == 0) return;
            var window = new ReminderUrgentWindow(urgent) { Owner = this };
            window.ShowDialog();
            if (window.OpenModule)
            {
                ShowWorkspaceWindow(new ReminderWindow(App.Reminders), _vm.RefreshDashboardAsync);
            }
        }
        catch (Exception ex) { await App.Log.WriteAsync("Falha ao exibir lembretes urgentes.", ex); }
    }

    private void ShowWorkspaceWindow(Window window, Func<Task>? onClosed = null)
    {
        // Abre cada módulo como janela de trabalho normal do SIGFUR, sem travar a tela principal.
        window.Owner = null;
        window.ShowInTaskbar = true;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        GlobalUiScaleService.Attach(window);
        if (onClosed is not null)
            window.Closed += async (_, _) =>
            {
                try { await onClosed(); }
                catch (Exception ex) { await App.Log.WriteAsync("Falha ao atualizar painel após fechar módulo.", ex); }
            };
        window.Show();
        window.Activate();
    }

    private async Task HandleNativeActionAsync(string id)
    {
        switch (id)
        {
            case "boletim":
                ShowWorkspaceWindow(new BulletinWindow(), _vm.RefreshSisbolStateAsync);
                break;
            case "boletim_furriel":
                ShowWorkspaceWindow(new FurrielBulletinWindow(), _vm.RefreshDashboardSilentAsync);
                break;
            case "boletim_resumo":
                ShowWorkspaceWindow(new IntelligentBulletinWindow(), _vm.RefreshDashboardSilentAsync);
                break;
            case "boletins_externos":
                ShowWorkspaceWindow(new ExternalBulletinsWindow(App.ExternalBulletins), _vm.RefreshDashboardSilentAsync);
                break;
            case "lembretes":
                ShowWorkspaceWindow(new ReminderWindow(App.Reminders), _vm.RefreshDashboardAsync);
                break;
            case "cadastro":
            {
                var editor = new MilitaryEditorWindow(App.MilitaryRepository, new MilitaryRecord(), App.MilitaryPreferences) { Owner = this };
                if (editor.ShowDialog() == true) await _vm.RefreshDashboardAsync();
                break;
            }
            case "listar":
                if (_militaryListWindow is { IsLoaded: true })
                {
                    if (_militaryListWindow.WindowState == WindowState.Minimized) _militaryListWindow.WindowState = WindowState.Normal;
                    _militaryListWindow.Activate();
                    _militaryListWindow.Focus();
                }
                else
                {
                    _militaryListWindow = new MilitaryListWindow(App.MilitaryRepository, App.MilitaryPreferences, App.Paystubs)
                    {
                        Owner = null,
                        ShowInTaskbar = true,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    _militaryListWindow.Closed += (_, _) => _militaryListWindow = null;
                    _militaryListWindow.Show();
                }
                break;
            case "lic_transf":
                ShowWorkspaceWindow(new LicensedTransferredWindow(App.LicensedTransferred, App.LicensedSpreadsheets, App.Paystubs), _vm.RefreshDashboardSilentAsync);
                break;
            case "soldos":
                ShowWorkspaceWindow(new SalaryWindow(App.Salaries), _vm.RefreshDashboardSilentAsync);
                break;
            case "grat_representacao":
                ShowWorkspaceWindow(new GratificationWindow(App.Gratifications), _vm.RefreshDashboardSilentAsync);
                break;
            case "ajuste_contas":
                ShowWorkspaceWindow(new AdjustmentAccountsWindow(App.MilitaryRepository, App.LicensedTransferred, App.Paths, App.Json), _vm.RefreshDashboardSilentAsync);
                break;
            case "pensao_judicial":
                ShowWorkspaceWindow(new JudicialPensionWindow(App.JudicialPension), _vm.RefreshDashboardSilentAsync);
                break;
            case "exercicio_anterior":
                ShowWorkspaceWindow(new ExercisePreviousWindow(App.MilitaryRepository, App.Paths, App.Log), _vm.RefreshDashboardSilentAsync);
                break;
            case "relacao_pessoal":
                ShowWorkspaceWindow(new PersonnelRelationWindow(), _vm.RefreshDashboardSilentAsync);
                break;
            case "plano_ferias":
                ShowWorkspaceWindow(new VacationPlanWindow(App.Vacations), _vm.RefreshDashboardSilentAsync);
                break;
            case "plano_chamada":
                ShowWorkspaceWindow(new PlanCallWindow(App.PlanCall), _vm.RefreshDashboardSilentAsync);
                break;
            case "medidas_tomadas":
                ShowWorkspaceWindow(new MeasuresTakenWindow(App.MeasuresTaken), _vm.RefreshDashboardSilentAsync);
                break;
            case "inconsistencia_bancaria":
                ShowWorkspaceWindow(new BankInconsistencyWindow(App.BankInconsistencies), _vm.RefreshDashboardSilentAsync);
                break;
            case "bizurometro_sped":
                ShowWorkspaceWindow(new BizurometroSpedWindow());
                break;
            case "conferencia_pagamento":
                ShowWorkspaceWindow(new PaymentConferenceWindow(App.PaymentConference));
                break;
            case "conferencia_sippes":
                ShowWorkspaceWindow(new SippesPersonnelConferenceWindow(App.MilitaryRepository), _vm.RefreshDashboardSilentAsync);
                break;
            case "auditoria_contracheques":
                await OpenPaystubAuditFromMainAsync();
                break;
            case "ferramentas_pdf":
                ShowWorkspaceWindow(new PdfToolsWindow());
                break;
            case "fila_impressao":
                ShowWorkspaceWindow(new PrintQueueWindow());
                break;
            case "escala_sgt_dia":
                ShowWorkspaceWindow(new DutyRosterWindow(), _vm.RefreshDashboardSilentAsync);
                break;
            case "faltas_atrasos":
                ShowWorkspaceWindow(new AbsenceWindow(), _vm.RefreshDashboardSilentAsync);
                break;
            case "legislacao":
                ShowWorkspaceWindow(new LegislationWindow(App.Legislation));
                break;
            case "phpm":
                ShowWorkspaceWindow(new PhpmWindow(App.Phpm, App.MilitaryRepository));
                break;
            case "sair": Close(); break;
            case "calculadora": ShellService.OpenCalculator(); break;
            case "abrir_dados": ShellService.OpenPath(App.Paths.DataDirectory); break;
            case "font_up": _vm.IncreaseScaleCommand.Execute(null); ApplyScale(); _ = _settings.SaveFontScaleAsync(_vm.UiScale); break;
            case "font_down": _vm.DecreaseScaleCommand.Execute(null); ApplyScale(); _ = _settings.SaveFontScaleAsync(_vm.UiScale); break;
            case "font_reset": _vm.ResetScaleCommand.Execute(null); ApplyScale(); _ = _settings.SaveFontScaleAsync(_vm.UiScale); break;
            case "sisbol_prepare": _vm.PrepareSisbolCommand.Execute(null); break;
            case "refresh_dashboard": await _vm.RefreshDashboardAsync(); break;
            case "perfil": await OpenProfileAsync(); break;
            case "profile_sync":
                new ProfileSyncSettingsWindow(App.StartupSession, App.Profiles, App.SyncBackup, App.SyncRestore) { Owner = this }.ShowDialog();
                break;
            case "appearance":
                new AppearanceWindow(App.Theme, App.Settings, _vm.UiScale) { Owner = this }.ShowDialog();
                _vm.UiScale = GlobalUiScaleService.CurrentScale;
                _vm.StatusText = $"Aparência aplicada: {App.Theme.Current.DisplayName} • {GlobalUiScaleService.CurrentScale:P0}.";
                break;
            case "gerenciar_atalhos": await OpenHotkeysAsync(); break;
            case "assistente": OpenAssistant(); break;
            case "num_extenso": ShowWorkspaceWindow(new NumberToWordsWindow()); break;
            case "aniversariantes": ShowWorkspaceWindow(new BirthdaysWindow(_vm.Dashboard.Birthdays, App.Paths, App.Json)); break;
            case "corrida_pagamento":
                ShowWorkspaceWindow(new PaymentScheduleWindow(App.Paths, App.Json), async () =>
                {
                    await RefreshPaymentRunReminderAsync();
                    await _vm.RefreshDashboardAsync();
                });
                break;
            case "consulta_rapida":
                new QuickSearchWindow(_vm.Dashboard.Military, item => OpenNativeMilitaryWalletAsync(item.Id)) { Owner = this }.ShowDialog();
                break;
        }
    }

    private async Task OpenNativeMilitaryWalletAsync(int militaryId)
    {
        var record = await App.MilitaryRepository.GetByIdAsync(militaryId);
        if (record is null)
        {
            SigfurDialog.Show(this, $"Militar ID {militaryId} não encontrado no banco oficial.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await App.MilitaryPreferences.ApplyAsync(new List<MilitaryRecord> { record });
        ShowWorkspaceWindow(new MilitaryWalletWindow(App.MilitaryRepository, App.Paystubs, record));
    }

    private async Task OpenPaystubAuditFromMainAsync()
    {
        if (_paystubAuditWindow is { IsLoaded: true })
        {
            if (_paystubAuditWindow.WindowState == WindowState.Minimized) _paystubAuditWindow.WindowState = WindowState.Normal;
            _paystubAuditWindow.Activate();
            _paystubAuditWindow.Focus();
            return;
        }

        var military = await App.MilitaryRepository.GetAllAsync();
        await App.MilitaryPreferences.ApplyAsync(military);
        _paystubAuditWindow = new PaystubAuditWindow(App.MilitaryRepository, App.Paystubs, military, App.Paths.PaystubsDirectory)
        {
            Owner = null,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        GlobalUiScaleService.Attach(_paystubAuditWindow);
        _paystubAuditWindow.Closed += (_, _) => _paystubAuditWindow = null;
        _paystubAuditWindow.Show();
        _paystubAuditWindow.Activate();
        _vm.StatusText = "Auditoria dos Contracheques aberta pela janela principal.";
    }

    internal Task ExecuteChildActionAsync(string id) => ExecuteActionByIdAsync(id);

    private async Task ExecuteActionByIdAsync(string id)
    {
        var action = _vm.AllActions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            await HandleNativeActionAsync(id);
            return;
        }
        if (_vm.ExecuteActionCommand.CanExecute(action)) _vm.ExecuteActionCommand.Execute(action);
        await Task.CompletedTask;
    }

    private async Task OpenProfileAsync()
    {
        var profile = await _settings.LoadProfileAsync();
        var window = new ProfileWindow(profile) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await _settings.SaveProfileAsync(window.Profile);
            OrganizationIdentity.Apply(window.Profile);
            _vm.UpdateProfile(window.Profile);
            LoadLogo(window.Profile.LogoPath);
            _vm.StatusText = "Perfil salvo. As novas configurações serão usadas na próxima abertura de módulo.";
        }
    }

    private async Task OpenHotkeysAsync()
    {
        var window = new HotkeysWindow(_vm.AllActions, _hotkeys) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _hotkeys = window.Hotkeys;
            await _settings.SaveHotkeysAsync(_hotkeys);
            BuildHotkeys();
            _vm.StatusText = "Atalhos atualizados.";
        }
    }

    private void BuildHotkeys()
    {
        InputBindings.Clear();
        foreach (var action in _vm.AllActions)
        {
            if (!_hotkeys.TryGetValue(action.Id, out var gestureText) || string.IsNullOrWhiteSpace(gestureText)) continue;
            try
            {
                var gesture = (KeyGesture)new KeyGestureConverter().ConvertFromString(gestureText)!;
                InputBindings.Add(new KeyBinding(new AsyncRelayCommand(() => ExecuteActionByIdAsync(action.Id)), gesture));
            }
            catch { }
        }
        AddScaleHotkey("font_up", () => { _vm.IncreaseScaleCommand.Execute(null); ApplyScale(); _ = _settings.SaveFontScaleAsync(_vm.UiScale); });
        AddScaleHotkey("font_down", () => { _vm.DecreaseScaleCommand.Execute(null); ApplyScale(); _ = _settings.SaveFontScaleAsync(_vm.UiScale); });
        AddScaleHotkey("font_reset", () => { _vm.ResetScaleCommand.Execute(null); ApplyScale(); _ = _settings.SaveFontScaleAsync(_vm.UiScale); });
    }

    private void AddScaleHotkey(string key, Action action)
    {
        if (!_hotkeys.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var gesture = (KeyGesture)new KeyGestureConverter().ConvertFromString(text)!;
            InputBindings.Add(new KeyBinding(new RelayCommand(action), gesture));
        }
        catch { }
    }

    private void RestoreWindowState()
    {
        var work = SystemParameters.WorkArea;

        // Em notebook, monitor remoto ou escala alta do Windows, a área útil pode
        // ficar menor que o MinWidth/MinHeight definido no XAML. Math.Clamp lança
        // exceção quando min > max; era um dos caminhos de fechamento imediato.
        var availableWidth = Math.Max(480d, FiniteOr(work.Width, 1180d));
        var availableHeight = Math.Max(360d, FiniteOr(work.Height, 820d));
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);

        var desiredWidth = FiniteOr(_windowState.Width, Math.Min(1180d, availableWidth));
        var desiredHeight = FiniteOr(_windowState.Height, Math.Min(820d, availableHeight));
        Width = ClampSafe(desiredWidth, MinWidth, availableWidth);
        Height = ClampSafe(desiredHeight, MinHeight, availableHeight);

        var desiredLeft = FiniteOr(_windowState.Left, work.Left + Math.Max(0, (work.Width - Width) / 2));
        var desiredTop = FiniteOr(_windowState.Top, work.Top + Math.Max(0, (work.Height - Height) / 2));
        var maxLeft = Math.Max(work.Left, work.Right - Width);
        var maxTop = Math.Max(work.Top, work.Bottom - Height);
        Left = ClampSafe(desiredLeft, work.Left, maxLeft);
        Top = ClampSafe(desiredTop, work.Top, maxTop);

        if (_windowState.Maximized) WindowState = WindowState.Maximized;
    }

    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    private static double ClampSafe(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(minimum)) minimum = 0;
        if (!double.IsFinite(maximum) || maximum < minimum) maximum = minimum;
        if (!double.IsFinite(value)) value = minimum;
        return Math.Clamp(value, minimum, maximum);
    }

    private void ApplyScale()
    {
        _vm.UiScale = GlobalUiScaleService.Sanitize(_vm.UiScale);
        GlobalUiScaleService.Apply(_vm.UiScale);
    }

    private void GlobalUiScaleService_ScaleChanged(object? sender, EventArgs e)
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                if (Math.Abs(_vm.UiScale - GlobalUiScaleService.CurrentScale) > 0.001)
                    _vm.UiScale = GlobalUiScaleService.CurrentScale;
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (Math.Abs(_vm.UiScale - GlobalUiScaleService.CurrentScale) > 0.001)
                        _vm.UiScale = GlobalUiScaleService.CurrentScale;
                });
            }
        }
        catch { }
    }

    private void LoadLogo(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                HeroLogoImage.Source = image;
                HeroLogoImage.Visibility = Visibility.Visible;
                HeroFallbackLogo.Visibility = Visibility.Collapsed;
                return;
            }
        }
        catch { }
        HeroLogoImage.Visibility = Visibility.Collapsed;
        HeroFallbackLogo.Visibility = Visibility.Visible;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAccepted) return;

        var result = SigfurDialog.Show(
            "Deseja realmente fechar o SIGFUR?",
            "SIGFUR",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Impede o encerramento antes que geometria, escala e menu sejam persistidos.
        e.Cancel = true;
        _closingAccepted = true;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _windowState = new WindowStateData
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            NavigationCollapsed = _vm.IsNavigationCollapsed,
            UiScale = _vm.UiScale
        };
        try { await _settings.SaveWindowStateAsync(_windowState); }
        catch (Exception ex) { await App.Log.WriteAsync("Falha ao salvar o estado da janela.", ex); }
        if (App.StartupSession.IsProfileMode && App.StartupSession.Config?.BackupOnClose == true)
        {
            try
            {
                _vm.StatusText = "Criando backup do Perfil SIGFUR antes de fechar...";
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                await App.SyncBackup.CreateBackupAsync(App.StartupSession, cts.Token);
            }
            catch (Exception ex)
            {
                await App.Log.WriteAsync("Falha no backup ao fechar o Perfil SIGFUR.", ex);
                SigfurDialog.Show(this,
                    "Não foi possível criar o backup ao fechar.\n\n" + ex.Message + "\n\nO SIGFUR será fechado sem apagar o backup anterior.",
                    "SIGFUR - Backup e Sincronização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        Close();
    }

    private Task ShowNotificationAsync(string message)
    {
        SigfurDialog.Show(this, message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeUtilities.FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _wasMinimized = true;
            UpdateMaximizeButton();
            return;
        }

        if (_wasMinimized)
        {
            _wasMinimized = false;

            // O WindowChrome personalizado pode voltar como Normal mesmo quando a
            // janela estava maximizada. Reaplica o estado anterior antes do layout.
            if (_lastNonMinimizedState == WindowState.Maximized && WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Maximized;
                return;
            }

            // Força uma nova medição depois que o Windows terminar de restaurar.
            // Sem isso, em alguns PCs o conteúdo conservava o tamanho reduzido da
            // miniatura até o usuário redimensionar a janela manualmente.
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                if (Content is UIElement content)
                {
                    content.InvalidateMeasure();
                    content.InvalidateArrange();
                    content.InvalidateVisual();
                }
                UpdateLayout();
            }, DispatcherPriority.Loaded);
        }

        _lastNonMinimizedState = WindowState;
        UpdateMaximizeButton();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyWorkAreaMaximize(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaMaximize(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var work = monitorInfo.rcWork;
        var full = monitorInfo.rcMonitor;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        info.ptMaxPosition.X = work.Left - full.Left;
        info.ptMaxPosition.Y = work.Top - full.Top;
        info.ptMaxSize.X = work.Right - work.Left;
        info.ptMaxSize.Y = work.Bottom - work.Top;
        info.ptMaxTrackSize.X = info.ptMaxSize.X;
        info.ptMaxTrackSize.Y = info.ptMaxSize.Y;

        Marshal.StructureToPtr(info, lParam, true);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInfo
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInfo ptReserved;
        public PointInfo ptMaxSize;
        public PointInfo ptMaxPosition;
        public PointInfo ptMinTrackSize;
        public PointInfo ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInfo
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectInfo rcMonitor;
        public RectInfo rcWork;
        public int dwFlags;
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeControlButton is null || MaximizeSingleIcon is null || MaximizeRestoreIcon is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeSingleIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        MaximizeRestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeControlButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private async void MenuAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string id }) await ExecuteActionByIdAsync(id);
    }

    private async void ButtonAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) await ExecuteActionByIdAsync(id);
    }

    private void AssistantChat_Click(object sender, RoutedEventArgs e) => OpenAssistant();

    private void OpenAssistant()
    {
        if (_assistantWindow is { IsLoaded: true })
        {
            if (_assistantWindow.WindowState == WindowState.Minimized) _assistantWindow.WindowState = WindowState.Normal;
            _assistantWindow.Activate();
            _assistantWindow.Focus();
            return;
        }
        _assistantWindow = new AssistantChatWindow
        {
            Owner = null,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        _assistantWindow.Closed += (_, _) => _assistantWindow = null;
        _assistantWindow.Show();
        _assistantWindow.Activate();
    }

    private void IncreaseScale_Click(object sender, RoutedEventArgs e) { _vm.IncreaseScaleCommand.Execute(null); ApplyScale(); }
    private void DecreaseScale_Click(object sender, RoutedEventArgs e) { _vm.DecreaseScaleCommand.Execute(null); ApplyScale(); }
    private void ResetScale_Click(object sender, RoutedEventArgs e) { _vm.ResetScaleCommand.Execute(null); ApplyScale(); }


    private async void NewReminder_Click(object sender, RoutedEventArgs e)
    {
        new ReminderWindow(App.Reminders, createNew: true) { Owner = this }.ShowDialog();
        await _vm.RefreshDashboardAsync();
    }

    private async void DashboardRemindersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DashboardRemindersGrid.SelectedItem is not ReminderItem reminder) return;
        _ = int.TryParse(reminder.Id, out var id);
        new ReminderWindow(App.Reminders, id) { Owner = this }.ShowDialog();
        await _vm.RefreshDashboardAsync();
    }

    private async void CalendarDayBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CalendarDayItem item) return;
        var window = new CalendarDayEditorWindow(item.Date, App.Paths, App.Json) { Owner = this };
        window.ShowDialog();
        await _vm.RefreshDashboardAsync();
    }

    private void OpenBirthdayBulletin_Click(object sender, RoutedEventArgs e)
    {
        var window = new BirthdayBulletinWindow(_vm.Dashboard.Birthdays, App.Paths) { Owner = this };
        window.ShowDialog();
    }

    private async void OpenPaymentReminders_Click(object sender, RoutedEventArgs e)
    {
        new PaymentRemindersWindow(App.Paths, App.Json) { Owner = this }.ShowDialog();
        await _vm.RefreshDashboardAsync();
    }

    private async void PaymentAlertsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        new PaymentRemindersWindow(App.Paths, App.Json) { Owner = this }.ShowDialog();
        await _vm.RefreshDashboardAsync();
    }

    private async void CheckSisbolBulletins_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!App.Sisbol.IsReady)
            {
                SigfurDialog.Show(
                    this,
                    "Prepare o SisBol primeiro no botão superior. Depois de validar o login/captcha, esta verificação consulta os boletins disponíveis sem baixar PDF.",
                    "SIGFUR — Verificar SisBol",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var year = DateTime.Today.Year;
            var progress = new Progress<string>(text => _vm.StatusText = text);
            _vm.StatusText = $"Verificando boletins disponíveis no SisBol em {year}...";

            var bi = await App.Sisbol.CheckGeneratedBulletinsAsync(1, year, progress, CancellationToken.None);
            var adt = await App.Sisbol.CheckGeneratedBulletinsAsync(3, year, progress, CancellationToken.None);
            var missing = await PopulateMissingSisbolBulletinsAsync(year, bi, adt);

            var message =
                $"Consulta concluída. Nada foi baixado.\n\n" +
                $"Boletim Interno no SisBol: {bi.Available} arquivo(s) em {FormatSisbolCheckMonths(bi)}\n" +
                $"Aditamento do Furriel no SisBol: {adt.Available} arquivo(s) em {FormatSisbolCheckMonths(adt)}\n\n" +
                $"Biblioteca local do SIGFUR em {year}: {missing.LocalBi} BI(s) e {missing.LocalAdt} ADT(s) indexado(s).\n" +
                $"Faltando no SIGFUR: {missing.Bi} BI(s) e {missing.Adt} ADT(s).\n\n" +
                "A aba Boletim > Faltando foi atualizada com os números encontrados.";

            var errors = bi.Errors.Concat(adt.Errors).Take(4).ToList();
            if (errors.Count > 0)
                message += "\n\nOcorrências na consulta:\n- " + string.Join("\n- ", errors);

            _vm.StatusText = $"SisBol verificado: faltam {missing.Bi} BI(s) e {missing.Adt} ADT(s) no SIGFUR.";
            SigfurDialog.Show(this, message, "SIGFUR — Verificar SisBol", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Falha ao verificar boletins no SisBol.";
            await App.Log.WriteAsync("Falha ao verificar boletins disponíveis no SisBol.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Verificar SisBol", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatSisbolCheckMonths(SisbolBulletinCheckResult result)
    {
        if (result.AvailableFiles.Count == 0) return "nenhum mês";
        var months = result.AvailableFiles
            .Select(x => $"{x.Month:00}/{x.Year}")
            .Distinct()
            .Take(8)
            .ToList();
        var suffix = result.AvailableFiles.Select(x => $"{x.Month:00}/{x.Year}").Distinct().Count() > months.Count ? "..." : string.Empty;
        return string.Join(", ", months) + suffix;
    }

    private async Task<SisbolMissingSummary> PopulateMissingSisbolBulletinsAsync(int year, SisbolBulletinCheckResult bi, SisbolBulletinCheckResult adt)
    {
        _vm.Dashboard.BulletinMissingSisbolIssues.Clear();
        var localBi = await BuildLocalBulletinReferenceAsync("BI", year);
        var localAdt = await BuildLocalBulletinReferenceAsync("ADT", year);
        var biCount = AddMissingSisbolBulletins(year, "BI", "Boletim Interno", "Boletim Inteligente", bi, localBi, "Abrir Boletim Inteligente e baixar BI pelo SisBol.");
        var adtCount = AddMissingSisbolBulletins(year, "ADT", "Aditamento", "Aditamento Furriel", adt, localAdt, "Abrir Aditamento Furriel e baixar ADT pelo SisBol.");
        return new SisbolMissingSummary(biCount, adtCount, localBi.Count, localAdt.Count);
    }

    private int AddMissingSisbolBulletins(
        int year,
        string localType,
        string sourceLabel,
        string moduleName,
        SisbolBulletinCheckResult result,
        LocalBulletinReference local,
        string action)
    {
        var added = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in result.AvailableFiles
                     .OrderBy(x => x.Month)
                     .ThenBy(x => FirstBulletinNumberKey(x.FileName, x.Year, x.Month) ?? x.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var numberKeys = ExtractBulletinNumberKeys(item.FileName, item.Year, item.Month).ToList();
            var fileKey = NormalizeFileKey(item.FileName);
            var hasLocalNumber = numberKeys.Count > 0 && numberKeys.Any(local.NumberKeys.Contains);
            var hasLocalFile = !string.IsNullOrWhiteSpace(fileKey) && local.FileKeys.Contains(fileKey);
            if (hasLocalNumber || hasLocalFile) continue;

            var displayNumber = numberKeys.Count > 0 ? numberKeys[0].TrimStart('0') : string.Empty;
            var title = string.IsNullOrWhiteSpace(displayNumber)
                ? $"{localType} sem número detectado"
                : $"{localType} nº {displayNumber}";
            var stableKey = $"{localType}|{item.Year}|{item.Month}|{displayNumber}|{fileKey}";
            if (!seen.Add(stableKey)) continue;

            _vm.Dashboard.BulletinMissingSisbolIssues.Add(new OperationalIssueItem
            {
                Severity = "Atenção",
                Category = "Boletim",
                Source = sourceLabel,
                Title = title,
                Detail = $"{item.Month:00}/{item.Year} • {item.FileName}",
                Action = action,
                SortKey = $"{localType}|{item.Year:0000}|{item.Month:00}|{displayNumber}"
            });
            added++;
        }

        if (added == 0 && result.Errors.Count == 0 && result.AvailableFiles.Count > 0)
        {
            _vm.Dashboard.BulletinMissingSisbolIssues.Add(new OperationalIssueItem
            {
                Severity = "Normal",
                Category = "Boletim",
                Source = sourceLabel,
                Title = $"{sourceLabel} em dia",
                Detail = $"Todos os arquivos identificados no SisBol em {year} já parecem estar salvos no SIGFUR.",
                Action = $"Nenhuma ação no {moduleName}.",
                SortKey = $"{localType}|{year:0000}|ok"
            });
        }

        return added;
    }

    private async Task<LocalBulletinReference> BuildLocalBulletinReferenceAsync(string type, int year)
    {
        var numberKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (type.Equals("BI", StringComparison.OrdinalIgnoreCase))
        {
            var store = await App.Json.LoadAsync<IntelligentBulletinStore>(App.Paths.BulletinIndexFile) ?? new IntelligentBulletinStore();
            foreach (var item in store.Items.Where(x => IsIntelligentBulletinFromYear(x, year)))
            {
                AddLocalBulletinReference(
                    year,
                    numberKeys,
                    fileKeys,
                    itemKeys,
                    item.BulletinNumber,
                    item.FileName,
                    item.OriginalFileName,
                    item.PdfPath);
            }
        }
        else
        {
            var store = await App.Json.LoadAsync<FurrielIndexStore>(App.Paths.FurrielIndexFile) ?? new FurrielIndexStore();
            foreach (var item in store.Files.Where(x => IsFurrielBulletinFromYear(x, year)))
            {
                AddLocalBulletinReference(
                    year,
                    numberKeys,
                    fileKeys,
                    itemKeys,
                    item.Bulletin,
                    item.OriginalName,
                    item.SourceOriginalName,
                    item.StoredPath,
                    item.SourcePath);
            }
        }

        foreach (var bulletin in _vm.Dashboard.Bulletins.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && IsDashboardBulletinFromYear(x, year)))
        {
            AddLocalBulletinReference(
                year,
                numberKeys,
                fileKeys,
                itemKeys,
                bulletin.Number,
                bulletin.File,
                bulletin.Path);
        }

        return new LocalBulletinReference(numberKeys, fileKeys, itemKeys.Count);
    }

    private static void AddLocalBulletinReference(
        int year,
        HashSet<string> numberKeys,
        HashSet<string> fileKeys,
        HashSet<string> itemKeys,
        params string?[] values)
    {
        foreach (var value in values)
        {
            foreach (var numberKey in ExtractBulletinNumberKeys(value, year, null))
                numberKeys.Add(numberKey);

            var fileKey = NormalizeFileKey(value);
            if (!string.IsNullOrWhiteSpace(fileKey))
                fileKeys.Add(fileKey);
        }

        var stable = string.Join("|", values.Select(NormalizeFileKey).Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(stable))
            stable = string.Join("|", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        if (!string.IsNullOrWhiteSpace(stable))
            itemKeys.Add(stable);
    }

    private static bool IsDashboardBulletinFromYear(BulletinItem item, int year)
    {
        if (TryParseDashboardDate(item.Date, out var date)) return date.Year == year;
        return ContainsYear(item.Number, year)
            || ContainsYear(item.File, year)
            || ContainsYear(item.Path, year);
    }

    private static bool IsIntelligentBulletinFromYear(IntelligentBulletinFile item, int year)
    {
        if (TryParseDashboardDate(item.DateIso, out var isoDate)) return isoDate.Year == year;
        if (TryParseDashboardDate(item.BulletinDate, out var bulletinDate)) return bulletinDate.Year == year;
        return ContainsYear(item.BulletinNumber, year)
            || ContainsYear(item.Period, year)
            || ContainsYear(item.FileName, year)
            || ContainsYear(item.OriginalFileName, year)
            || ContainsYear(item.PdfPath, year);
    }

    private static bool IsFurrielBulletinFromYear(FurrielBulletinFile item, int year)
    {
        if (TryParseDashboardDate(item.Date, out var date)) return date.Year == year;
        return ContainsYear(item.Bulletin, year)
            || ContainsYear(item.OriginalName, year)
            || ContainsYear(item.SourceOriginalName, year)
            || ContainsYear(item.StoredPath, year)
            || ContainsYear(item.SourcePath, year);
    }

    private static bool ContainsYear(string? value, int year)
    {
        var text = value ?? string.Empty;
        return text.Contains(year.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDashboardDate(string? value, out DateTime date)
    {
        var text = value?.Trim() ?? string.Empty;
        string[] formats = ["dd/MM/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy", "dd-MM-yy"];
        foreach (var format in formats)
            if (DateTime.TryParseExact(text, format, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date))
                return true;
        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date);
    }

    private static string? FirstBulletinNumberKey(string value, int year, int? month)
        => ExtractBulletinNumberKeys(value, year, month).FirstOrDefault();

    private static IEnumerable<string> ExtractBulletinNumberKeys(string? value, int year, int? month)
    {
        var text = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (Regex.IsMatch(text, @"^\d{4}[-_]\d{2}[-_]\d{3}[-_](boletim[_-]?interno|aditamento[_-]?do[_-]?furriel)$", RegexOptions.IgnoreCase))
            yield break;

        foreach (Match match in Regex.Matches(text, @"(?:\bBI\b|B\.I\.|BOLETIM|ADT|ADITAMENTO)[^\d]{0,25}(?<num>\d{1,4})", RegexOptions.IgnoreCase))
        {
            var key = NormalizeBulletinNumber(match.Groups["num"].Value);
            if (!string.IsNullOrWhiteSpace(key) && key != year.ToString(CultureInfo.InvariantCulture))
                yield return key;
        }

        foreach (Match match in Regex.Matches(text, @"(?<!\d)(?<num>\d{1,4})(?!\d)", RegexOptions.IgnoreCase))
        {
            var raw = match.Groups["num"].Value;
            if (raw == year.ToString(CultureInfo.InvariantCulture)) continue;
            if (month.HasValue && raw == month.Value.ToString("00", CultureInfo.InvariantCulture)) continue;
            var key = NormalizeBulletinNumber(raw);
            if (!string.IsNullOrWhiteSpace(key)) yield return key;
        }
    }

    private static string NormalizeBulletinNumber(string value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) return string.Empty;
        digits = digits.TrimStart('0');
        return string.IsNullOrWhiteSpace(digits) ? "0" : digits;
    }

    private static string NormalizeFileKey(string? value)
    {
        var file = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(file)) return string.Empty;
        var normalized = file.Normalize(NormalizationForm.FormD);
        var clean = new string(normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_')
            .ToArray());
        return Regex.Replace(clean, "_+", "_").Trim('_');
    }

    private sealed record LocalBulletinReference(HashSet<string> NumberKeys, HashSet<string> FileKeys, int Count);
    private sealed record SisbolMissingSummary(int Bi, int Adt, int LocalBi, int LocalAdt);

    private async void OperationalIssuesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: OperationalIssueItem issue })
            return;

        var category = (issue.Category ?? string.Empty).Trim();
        var title = (issue.Title ?? string.Empty).Trim();
        var source = (issue.Source ?? string.Empty).Trim();
        var search = $"{category} {title} {source}".ToLowerInvariant();

        if (search.Contains("backup"))
        {
            if (_vm.BackupCommand.CanExecute(null)) _vm.BackupCommand.Execute(null);
            return;
        }

        if (search.Contains("aniversar"))
        {
            ShowWorkspaceWindow(new BirthdaysWindow(_vm.Dashboard.Birthdays, App.Paths, App.Json));
            return;
        }

        if (search.Contains("lembrete"))
        {
            ShowWorkspaceWindow(new ReminderWindow(App.Reminders), _vm.RefreshDashboardAsync);
            return;
        }

        if (category.Equals("Pagamento", StringComparison.OrdinalIgnoreCase))
        {
            if (search.Contains("sat") || search.Contains("transporte") || search.Contains(" at") || search.Contains("cadastro at"))
                await ExecuteActionByIdAsync("aux_transporte");
            else
                await ExecuteActionByIdAsync("conferencia_pagamento");
            return;
        }

        if (category.Equals("Boletim", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteActionByIdAsync(search.Contains("adt") || search.Contains("furriel") ? "boletim_furriel" : "boletim_resumo");
            return;
        }

        if (category.Equals("Cadastro", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteActionByIdAsync(search.Contains("sat") || search.Contains("transporte") || search.Contains(" at") ? "aux_transporte" : "listar");
            return;
        }

        if (category.Equals("Sistema", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteActionByIdAsync("abrir_dados");
            return;
        }

        await ExecuteActionByIdAsync("consulta_rapida");
    }

    private void MissingAtGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.OpenSelectedWalletCommand.CanExecute(null)) _vm.OpenSelectedWalletCommand.Execute(null);
    }

    private void BulletinsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // O painel principal agora usa o espaço de “Boletins recentes” para aniversariantes.
        // Mantemos este handler compatível para algum DataGrid de boletins que ainda o reutilize,
        // mas sem depender do campo x:Name BulletinsGrid, que não existe mais no XAML atual.
        if (sender is not DataGrid grid || grid.SelectedItem is not BulletinItem selectedBulletin)
            return;

        var bulletinPath = selectedBulletin.Path;
        if (string.IsNullOrWhiteSpace(bulletinPath) || !File.Exists(bulletinPath))
            return;

        ShellService.OpenPath(bulletinPath);
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        SigfurDialog.Show(
            this,
            DatabaseDiagnosticFormatter.Format(App.Database.LastReport, App.Paths, App.Theme.Current.DisplayName),
            "SIGFUR — Diagnóstico do sistema",
            MessageBoxButton.OK,
            App.Database.LastReport?.Official.IsValid == true ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => new HelpWindow { Owner = this }.ShowDialog();
    private void PaymentReminderHeader_Click(object sender, RoutedEventArgs e)
        => ShowWorkspaceWindow(new PaymentScheduleWindow(App.Paths, App.Json), async () =>
        {
            await RefreshPaymentRunReminderAsync();
            await _vm.RefreshDashboardAsync();
        });

    private async Task RefreshPaymentRunReminderAsync()
    {
        try
        {
            var root = await App.Json.LoadNodeAsync(App.Paths.AppSettingsFile) as JsonObject;
            var cfg = root?["corrida_pagamento"] as JsonObject;
            var (year, month) = ParsePaymentCompetence(cfg?["competencia"]?.GetValue<string>()) ?? (DateTime.Today.Year, DateTime.Today.Month);
            var reference = cfg?["competencia_texto"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reference)) reference = PaymentReferenceTitle(year, month);

            var first = ParsePaymentDate(cfg?["primeira"]?.GetValue<string>()) ?? DefaultFirstRunDate(year, month);
            var second = ThirdBusinessDay(year, month);

            PaymentReferenceText.Text = reference;
            FirstPaymentRunText.Text = $"1ª corrida: {FormatMilitaryDate(first)}";
            SecondPaymentRunText.Text = $"2ª corrida: {FormatMilitaryDate(second)}";
        }
        catch (Exception ex)
        {
            PaymentReferenceText.Text = "Pagamento não definido";
            FirstPaymentRunText.Text = "1ª corrida: não definida";
            SecondPaymentRunText.Text = "2ª corrida: não definida";
            await App.Log.WriteAsync("Falha ao carregar lembrete da corrida de pagamento.", ex);
        }
    }

    private static readonly string[] PaymentMonthNames =
    [
        "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO",
        "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"
    ];

    private static readonly string[] PaymentMilitaryMonths =
    [
        "JAN", "FEV", "MAR", "ABR", "MAIO", "JUN",
        "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"
    ];

    private static (int Year, int Month)? ParsePaymentCompetence(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var month) && year is >= 2000 and <= 2100 && month is >= 1 and <= 12)
            return (year, month);
        return null;
    }

    private static DateTime? ParsePaymentDate(string? value)
    {
        if (DateTime.TryParseExact(value, ["dd/MM/yyyy", "yyyy-MM-dd"], new CultureInfo("pt-BR"), DateTimeStyles.None, out var date))
            return date;
        return DateTime.TryParse(value, new CultureInfo("pt-BR"), DateTimeStyles.None, out date) ? date : null;
    }

    private static DateTime DefaultFirstRunDate(int year, int month)
    {
        var previous = new DateTime(year, month, 1).AddMonths(-1);
        return new DateTime(previous.Year, previous.Month, Math.Min(15, DateTime.DaysInMonth(previous.Year, previous.Month)));
    }

    private static DateTime ThirdBusinessDay(int year, int month)
    {
        var count = 0;
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            var date = new DateTime(year, month, day);
            if (IsPaymentNonBusinessDay(date)) continue;
            if (++count == 3) return date;
        }
        return new DateTime(year, month, Math.Min(3, DateTime.DaysInMonth(year, month)));
    }


    private static bool IsPaymentNonBusinessDay(DateTime date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsBrazilianFederalHolidayOrBankClosing(date);

    private static bool IsBrazilianFederalHolidayOrBankClosing(DateTime date)
    {
        var fixedHoliday = (date.Month, date.Day) is
            (1, 1) or (4, 21) or (5, 1) or (9, 7) or (10, 12) or (11, 2) or (11, 15) or (11, 20) or (12, 25);
        if (fixedHoliday) return true;
        var easter = EasterSunday(date.Year);
        return date.Date == easter.AddDays(-48).Date || date.Date == easter.AddDays(-47).Date || date.Date == easter.AddDays(-2).Date || date.Date == easter.AddDays(60).Date;
    }

    private static DateTime EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    private static string PaymentReferenceTitle(int year, int month) => $"Pagamento {PaymentMonthNames[month - 1]} {year:0000}";
    private static string FormatMilitaryDate(DateTime date) => $"{date:dd} {PaymentMilitaryMonths[date.Month - 1]} {date:yy}";

}
