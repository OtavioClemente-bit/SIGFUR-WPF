using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
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
    private readonly MainWindowViewModel _vm;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _sisbolTimer;
    private readonly DispatcherTimer _dashboardTimer;
    private WindowStateData _windowState;
    private Dictionary<string, string> _hotkeys;
    private bool _closingAccepted;
    private bool _sisbolTimerRunning;
    private bool _dashboardTimerRunning;
    private MilitaryListWindow? _militaryListWindow;
    private AssistantChatWindow? _assistantWindow;

    public MainWindow(UiProfile profile, WindowStateData windowState, Dictionary<string, string> hotkeys, string startupWarning)
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        UpdateMaximizeButton();
        App.UiState.Attach(this);
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
        Activated += async (_, _) =>
        {
            if (_sisbolTimerRunning) return;
            _sisbolTimerRunning = true;
            try { await _vm.RefreshSisbolStateAsync(); }
            catch { }
            finally { _sisbolTimerRunning = false; }
        };
        Closing += OnClosing;
        Closed += (_, _) => { _sisbolTimer.Stop(); _dashboardTimer.Stop(); };
    }

    public async Task InitializeAsync()
    {
        await _vm.InitializeAsync();
        await RefreshPaymentRunReminderAsync();
        await ShowUrgentRemindersAsync();
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
                ShowWorkspaceWindow(new AdjustmentAccountsWindow(App.MilitaryRepository, App.Paths, App.Json), _vm.RefreshDashboardSilentAsync);
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
            case "font_up": _vm.IncreaseScaleCommand.Execute(null); ApplyScale(); break;
            case "font_down": _vm.DecreaseScaleCommand.Execute(null); ApplyScale(); break;
            case "font_reset": _vm.ResetScaleCommand.Execute(null); ApplyScale(); break;
            case "sisbol_prepare": _vm.PrepareSisbolCommand.Execute(null); break;
            case "refresh_dashboard": await _vm.RefreshDashboardAsync(); break;
            case "perfil": await OpenProfileAsync(); break;
            case "profile_sync":
                new ProfileSyncSettingsWindow(App.StartupSession, App.Profiles, App.SyncBackup, App.SyncRestore) { Owner = this }.ShowDialog();
                break;
            case "appearance":
                new AppearanceWindow(App.Theme) { Owner = this }.ShowDialog();
                _vm.StatusText = $"Tema aplicado: {App.Theme.Current.DisplayName}.";
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
        AddScaleHotkey("font_up", () => { _vm.IncreaseScaleCommand.Execute(null); ApplyScale(); });
        AddScaleHotkey("font_down", () => { _vm.DecreaseScaleCommand.Execute(null); ApplyScale(); });
        AddScaleHotkey("font_reset", () => { _vm.ResetScaleCommand.Execute(null); ApplyScale(); });
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
        var scale = Math.Clamp(_vm.UiScale, 0.80, 1.35);
        // 1,05 era o padrão histórico, embora visualmente quase igual a 1,00. Esse
        // pequeno aumento ativava LayoutTransform em toda a tela e custava muito na
        // rolagem. Valores próximos de 100% usam a renderização nativa sem transform.
        if (scale is >= 0.96 and <= 1.06) scale = 1d;
        RootGrid.LayoutTransform = Math.Abs(scale - 1d) < 0.001
            ? null
            : new System.Windows.Media.ScaleTransform(scale, scale);
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
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMaximizeButton();

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
