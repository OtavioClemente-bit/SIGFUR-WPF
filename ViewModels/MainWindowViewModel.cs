using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DashboardService _dashboardService;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly List<ActionDefinition> _allActions;
    private UiProfile _profile;
    private DashboardSnapshot _dashboard = new();
    private string _navigationSearch = string.Empty;
    private string _statusText = "Pronto.";
    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private bool _isNavigationCollapsed;
    private double _uiScale = GlobalUiScaleService.DefaultScale;
    private string _sisbolText = "🔐 Preparar SisBol";
    private bool _sisbolReady;
    private RankSummaryItem? _selectedRank;
    private bool _silentRefreshRunning;
    private bool _sisbolRefreshRunning;
    private long _lastDashboardChangeStamp;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public MainWindowViewModel(
        DashboardService dashboardService,
        BackupService backup,
        SettingsService settings,
        AppPaths paths,
        LogService log,
        UiProfile profile,
        WindowStateData windowState,
        Dictionary<string, string> hotkeys)
    {
        _dashboardService = dashboardService;
        _backup = backup;
        _settings = settings;
        _paths = paths;
        _log = log;
        _profile = profile;
        _isNavigationCollapsed = windowState.NavigationCollapsed;
        _uiScale = GlobalUiScaleService.Sanitize(windowState.UiScale);
        _allActions = ActionCatalog.Create(hotkeys);

        ExecuteActionCommand = new AsyncRelayCommand(parameter => ExecuteActionAsync(parameter as ActionDefinition));
        RefreshDashboardCommand = new AsyncRelayCommand(RefreshDashboardAsync);
        PreviousCalendarMonthCommand = new AsyncRelayCommand(() => MoveCalendarMonthAsync(-1));
        CurrentCalendarMonthCommand = new AsyncRelayCommand(() => SetCalendarMonthAsync(DateTime.Today));
        NextCalendarMonthCommand = new AsyncRelayCommand(() => MoveCalendarMonthAsync(1));
        BackupCommand = new AsyncRelayCommand(CreateBackupAsync);
        ToggleNavigationCommand = new RelayCommand(() => IsNavigationCollapsed = !IsNavigationCollapsed);
        IncreaseScaleCommand = new RelayCommand(() => UiScale = GlobalUiScaleService.Sanitize(UiScale + 0.05));
        DecreaseScaleCommand = new RelayCommand(() => UiScale = GlobalUiScaleService.Sanitize(UiScale - 0.05));
        ResetScaleCommand = new RelayCommand(() => UiScale = 1.0);
        PrepareSisbolCommand = new AsyncRelayCommand(PrepareSisbolAsync);
        OpenSelectedWalletCommand = new AsyncRelayCommand(OpenSelectedWalletAsync);
        SelectRankCommand = new RelayCommand(parameter => SelectedRank = parameter as RankSummaryItem);
        ApplyNavigationFilter();
    }

    public Func<string, Task>? NativeActionRequested { get; set; }
    public Func<int, Task>? NativeMilitaryWalletRequested { get; set; }
    public Func<string, Task>? NotificationRequested { get; set; }

    public string OperatorName => _profile.Operator;
    public string OrganizationName => _profile.Organization;
    public string ProfileSubtitle => string.Join(" • ", new[] { _profile.Rank, _profile.Function, _profile.Organization }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string LogoPath => _profile.LogoPath;
    public string Greeting => $"{GetGreeting()}, {string.Join(" ", new[] { _profile.Rank, OperatorName }.Where(x => !string.IsNullOrWhiteSpace(x)))}!";
    public string CurrentDate => DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
    public string Version => Dashboard.Version;
    public string DataDirectory => _paths.DataDirectory;
    public IReadOnlyList<ActionDefinition> AllActions => _allActions;

    public ObservableCollection<NavigationGroup> NavigationGroups { get; } = [];

    public DashboardSnapshot Dashboard
    {
        get => _dashboard;
        private set
        {
            if (SetProperty(ref _dashboard, value))
            {
                OnPropertyChanged(nameof(Version));
                SelectedRank = value.RankSummary.FirstOrDefault();
            }
        }
    }

    public string NavigationSearch
    {
        get => _navigationSearch;
        set
        {
            if (SetProperty(ref _navigationSearch, value)) ApplyNavigationFilter();
        }
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string BusyMessage { get => _busyMessage; set => SetProperty(ref _busyMessage, value); }
    public bool IsNavigationCollapsed { get => _isNavigationCollapsed; set => SetProperty(ref _isNavigationCollapsed, value); }
    public double UiScale { get => _uiScale; set => SetProperty(ref _uiScale, GlobalUiScaleService.Sanitize(value)); }
    public string SisbolText { get => _sisbolText; private set => SetProperty(ref _sisbolText, value); }
    public bool SisbolReady { get => _sisbolReady; private set => SetProperty(ref _sisbolReady, value); }
    public RankSummaryItem? SelectedRank { get => _selectedRank; set => SetProperty(ref _selectedRank, value); }
    public MilitaryItem? SelectedMissingTransportMilitary { get; set; }

    public ICommand ExecuteActionCommand { get; }
    public ICommand RefreshDashboardCommand { get; }
    public ICommand PreviousCalendarMonthCommand { get; }
    public ICommand CurrentCalendarMonthCommand { get; }
    public ICommand NextCalendarMonthCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand ToggleNavigationCommand { get; }
    public ICommand IncreaseScaleCommand { get; }
    public ICommand DecreaseScaleCommand { get; }
    public ICommand ResetScaleCommand { get; }
    public ICommand PrepareSisbolCommand { get; }
    public ICommand OpenSelectedWalletCommand { get; }
    public ICommand SelectRankCommand { get; }

    public async Task InitializeAsync()
    {
        await RefreshDashboardAsync();
        _lastDashboardChangeStamp = _dashboardService.GetChangeStamp();
        SisbolReady = false;
        SisbolText = "🔐 Preparar SisBol";
    }

    public async Task RefreshDashboardAsync()
    {
        await RunBusyAsync("Atualizando dashboard…", async () =>
        {
            Dashboard = await _dashboardService.LoadAsync(_calendarMonth);
            _lastDashboardChangeStamp = _dashboardService.GetChangeStamp();
            StatusText = $"{Dashboard.ActiveMilitaryCount} militar(es) carregado(s) de {Dashboard.DatabasePath} — {DateTime.Now:HH:mm:ss}.";
        });
    }

    public async Task RefreshDashboardSilentAsync()
    {
        if (IsBusy || _silentRefreshRunning) return;

        // A versão anterior reconstruía todo o dashboard a cada 10 segundos, mesmo
        // sem qualquer mudança no banco. Isso forçava novo layout durante a rolagem.
        var changeStamp = _dashboardService.GetChangeStamp();
        if (changeStamp == _lastDashboardChangeStamp) return;

        _silentRefreshRunning = true;
        try
        {
            var snapshot = await _dashboardService.LoadAsync(_calendarMonth);
            Dashboard = snapshot;
            _lastDashboardChangeStamp = _dashboardService.GetChangeStamp();
            StatusText = $"{Dashboard.ActiveMilitaryCount} militar(es) carregado(s) — atualização automática às {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha na atualização automática do dashboard.", ex);
        }
        finally
        {
            _silentRefreshRunning = false;
        }
    }

    private async Task MoveCalendarMonthAsync(int offset)
        => await SetCalendarMonthAsync(_calendarMonth.AddMonths(offset));

    private async Task SetCalendarMonthAsync(DateTime month)
    {
        _calendarMonth = new DateTime(month.Year, month.Month, 1);
        await RefreshDashboardAsync();
    }

    public async Task RefreshSisbolStateAsync()
    {
        if (_sisbolRefreshRunning) return;
        _sisbolRefreshRunning = true;
        try
        {
            var state = await App.Sisbol.GetStatusAsync();
            SisbolReady = state.Ready;
            SisbolText = state.Ready ? $"✅ SisBol pronto{(string.IsNullOrWhiteSpace(state.Browser) ? "" : " — " + state.Browser)}"
                : state.Alive ? "🔓 Concluir login SisBol" : "🔐 Preparar SisBol";
        }
        catch
        {
            var cached = App.Sisbol.GetCachedStatus();
            SisbolReady = cached.Ready;
            SisbolText = cached.Ready ? $"✅ SisBol pronto{(string.IsNullOrWhiteSpace(cached.Browser) ? "" : " — " + cached.Browser)}"
                : cached.Alive ? "🔓 Concluir login SisBol" : "🔐 Preparar SisBol";
        }
        finally
        {
            _sisbolRefreshRunning = false;
        }
    }

    public void UpdateProfile(UiProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(nameof(OperatorName));
        OnPropertyChanged(nameof(OrganizationName));
        OnPropertyChanged(nameof(ProfileSubtitle));
        OnPropertyChanged(nameof(LogoPath));
        OnPropertyChanged(nameof(Greeting));
    }

    private async Task ExecuteActionAsync(ActionDefinition? action)
    {
        if (action is null) return;

        // O Auxílio-Transporte agora é nativo C# e não depende mais da ponte Python.
        // A verificação fica aqui para funcionar tanto nos cartões do Dashboard quanto
        // na navegação lateral, na paleta e no atalho Ctrl+T, sem alterar outros arquivos.
        if (action.Id.Equals("aux_transporte", StringComparison.OrdinalIgnoreCase))
        {
            var window = new AuxilioTransporteWindow(App.MilitaryRepository, App.Paths, App.Settings, App.Log)
            {
                Owner = null,
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Closed += async (_, _) =>
            {
                StatusText = "Auxílio-Transporte fechado. Os dados foram mantidos no banco oficial.";
                await RefreshDashboardSilentAsync();
            };
            window.Show();
            window.Activate();
            StatusText = "Auxílio-Transporte aberto em janela independente.";
            return;
        }

        if (action.IsNative)
        {
            if (NativeActionRequested is not null) await NativeActionRequested(action.Id);
            return;
        }
        await NotifyAsync($"O módulo {action.Title} ainda não possui uma janela C# vinculada.");
    }

    private async Task OpenSelectedWalletAsync()
    {
        var selected = SelectedMissingTransportMilitary;
        if (selected is null || selected.Id <= 0)
        {
            await NotifyAsync("Selecione um militar na lista.");
            return;
        }
        await RunBusyAsync("Abrindo carteira do militar…", async () =>
        {
            if (NativeMilitaryWalletRequested is not null) await NativeMilitaryWalletRequested(selected.Id);
            else await NotifyAsync("A carteira nativa C# não está disponível nesta janela.");
        });
    }

    private async Task PrepareSisbolAsync()
    {
        await RunBusyAsync("Preparando sessão do SisBol…", async () =>
        {
            var owner = Application.Current?.MainWindow ?? throw new InvalidOperationException("Janela principal indisponível.");
            await App.Sisbol.PrepareAsync(owner);
            await RefreshSisbolStateAsync();
        });
    }

    private async Task CreateBackupAsync()
    {
        await RunBusyAsync("Criando backup seguro…", async () =>
        {
            var path = await _backup.CreateAsync("backup", 5);
            StatusText = "Backup concluído: " + path;
            await NotifyAsync("Backup concluído com sucesso.");
            Dashboard = await _dashboardService.LoadAsync(_calendarMonth);
        });
    }

    private void ApplyNavigationFilter()
    {
        NavigationGroups.Clear();
        var query = Normalize(NavigationSearch);
        foreach (var group in _allActions.GroupBy(a => a.Category))
        {
            var actions = group.Where(a => string.IsNullOrWhiteSpace(query) || Normalize($"{a.Title} {a.Description} {a.Id}").Contains(query)).ToList();
            if (actions.Count == 0) continue;
            NavigationGroups.Add(new NavigationGroup { Title = group.Key, Actions = actions });
        }
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyMessage = message;
        StatusText = message;
        try { await action(); }
        catch (Exception ex)
        {
            StatusText = "Falha: " + ex.Message;
            await _log.WriteAsync(message, ex);
            await NotifyAsync(ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task NotifyAsync(string message)
    {
        if (NotificationRequested is not null) await NotificationRequested(message);
        else SigfurDialog.Show(message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string GetGreeting()
    {
        var hour = DateTime.Now.Hour;
        return hour < 12 ? "Bom dia" : hour < 18 ? "Boa tarde" : "Boa noite";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }
}

/// <summary>
/// Módulo nativo de Auxílio-Transporte.
/// Mantido neste arquivo para que a atualização possa ser copiada como um único arquivo,
/// sem exigir alteração de XAML, catálogo de ações ou projeto.
/// </summary>
internal sealed class AuxilioTransporteWindow : Window
{
    private const string SisbolLoginUrl = "https://10.122.8.31/band/sisbol.php";
    private const string SisbolMatterUrl = "https://10.122.8.31/band/cadmateriabi.php?codTipoBol=3";
    private const string PbhFaresUrl = "https://prefeitura.pbh.gov.br/sumob/onibus/tarifas-e-integracoes";
    private const string BusSearchBaseUrl = "https://onibusbh.com.br/?s=";
    private const string BusLinesIndexUrl = "https://onibusbh.com.br/linhas-de-onibus-da-bhtrans/";
    private const string SumobLineUrl = "https://portalsumob.pbh.gov.br/quadrodehorario?linha=";
    private const string DefaultDestination = "4ª Companhia de Polícia do Exército, Belo Horizonte - MG";
    private const string ModuleActionError = "Não foi possível concluir esta ação do Auxílio-Transporte. A tela continuará aberta.";
    private static readonly string[] BulletinActions =
    [
        "Auxílio-Transporte - Implantação",
        "Auxílio-Transporte - Atualização de Valores",
        "Auxílio-Transporte - Saque de Atrasado",
        "Auxílio-Transporte - Diferença",
        "Auxílio-Transporte - Devolução a Militar",
        // Despesa a anular possui aba própria neste módulo; não aparece mais como modelo duplicado.
        "Auxílio-Transporte - Excluir benefício"
    ];
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly MilitaryRepository _repository;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly TransportRouteDocumentService _routeDocuments;
    private readonly ObservableCollection<MilitaryRecord> _visibleMilitary = [];
    private readonly ObservableCollection<AtBusLine> _routeBuses = [];
    private readonly ObservableCollection<AtDaRow> _daRows = [];
    private bool _loadingDaCompetence;
    private List<MilitaryRecord> _allMilitary = [];
    private readonly Dictionary<int, int> _customOrderPositions = [];
    private readonly Dictionary<string, int> _customOrderIdentityPositions = new(StringComparer.OrdinalIgnoreCase);
    private string _orderSourceDescription = "hierarquia do EB";
    private readonly Dictionary<int, decimal> _effectiveNetByMilitaryId = [];
    private MilitaryRecord? _current;
    private AtCalculation _currentCalculation = AtCalculation.Empty;
    private BusLookupResult? _lastLookup;
    private bool _loadingSelection;
    private bool _savingTransportRoute;
    private string _currentPrintPath = string.Empty;
    private readonly TextBlock _residenceStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private TransportResidenceService Residence => new(_paths.DataDirectory);
    private readonly System.Windows.Threading.DispatcherTimer _windowPreferencesTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };

    private readonly TextBox _searchBox = new();
    private readonly ComboBox _rankFilter = new();
    private readonly ComboBox _receiveFilter = new();
    private readonly DataGrid _militaryGrid = new();
    private readonly TextBlock _listCounter = new();
    private readonly TextBlock _listSelectedMilitaryText = new();
    private readonly TextBlock _statusText = new();
    private readonly ProgressBar _busyProgress = new()
    {
        IsIndeterminate = true,
        Width = 180,
        Height = 8,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center
    };
    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private readonly TextBlock _headerTotal = new();
    private readonly TextBlock _headerReceive = new();
    private readonly TextBlock _headerNoReceive = new();
    private readonly TextBlock _headerValue = new();
    private readonly TabControl _tabs = new();

    private readonly TextBlock _selectedTitle = new();
    private readonly TextBlock _selectedSubtitle = new();
    private readonly TextBlock _selectedAddress = new();
    private readonly TextBlock _selectedStatus = new();
    private readonly ComboBox _calculatorRankBox = new();
    private readonly TextBox _daysBox = new() { Text = "22" };
    private readonly TextBox _manualLineBox = new();
    private readonly TextBox _manualFareBox = new() { Text = "6,25" };
    private readonly DataGrid _busGrid = new();
    private readonly TextBlock _dailyValue = new();
    private readonly TextBlock _grossValue = new();
    private readonly TextBlock _salaryValue = new();
    private readonly TextBlock _salarySixPercentValue = new();
    private readonly TextBlock _salarySixPercentDailyValue = new();
    private readonly TextBlock _shareValue = new();
    private readonly TextBlock _netValue = new();

    private readonly TextBox _originBox = new();
    private readonly TextBox _routeCepBox = new();
    private readonly TextBox _destinationBox = new() { Text = DefaultDestination };
    private readonly DatePicker _departureDate = new() { SelectedDate = DateTime.Today.AddDays(1) };
    private readonly TextBox _departureTime = new() { Text = "06:00" };
    private readonly TextBox _lineNumberBox = new();
    private readonly ComboBox _lineCategoryBox = new();
    private readonly TextBox _lookupFareBox = new();
    private readonly TextBlock _lookupResultText = new();
    private readonly TextBlock _routeSelectedMilitaryText = new();
    private readonly TextBlock _routeSaveStatus = new();
    private readonly TextBlock _printStatus = new();
    private readonly ComboBox _routeBulletinActionBox = new();

    private readonly ComboBox _bulletinActionBox = new();
    private readonly DatePicker _bulletinBaseDatePicker = new() { SelectedDate = DateTime.Today };
    private readonly DatePicker _bulletinCountFromDatePicker = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _bulletinReferenceBox = new() { IsEditable = false };
    private readonly ComboBox _bulletinMonthBox = new() { IsEditable = false };
    private readonly ComboBox _bulletinYearBox = new() { IsEditable = true };
    private readonly Button _bulletinAddMonthButton = new()
    {
        Content = "+",
        MinWidth = 42,
        Height = 34,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Bottom,
        ToolTip = "Adicionar este mês ao militar antes de incluí-lo."
    };
    private readonly TextBox _bulletinRequiredDetailsBox = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 62,
        MaxHeight = 92,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ToolTip = "Competência individual deste militar. Use uma linha por mês."
    };
    private readonly TextBox _bulletinReceivedValueBox = new()
    {
        MinHeight = 34,
        ToolTip = "Informe quanto este militar efetivamente recebeu na competência."
    };
    private readonly TextBlock _bulletinDifferencePreviewText = new()
    {
        Margin = new Thickness(0, 5, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeights.SemiBold
    };
    private readonly TextBlock _bulletinCompetenceCountText = new();
    private readonly TextBlock _transportReferenceHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.SemiBold };
    private TextBlock? _bulletinReferenceLabel;
    private TextBlock? _bulletinCountFromLabel;
    private TextBlock? _bulletinDetailsLabel;
    private FrameworkElement? _bulletinAddMonthButtonField;
    private FrameworkElement? _bulletinReferenceField;
    private FrameworkElement? _bulletinCountFromField;
    private FrameworkElement? _bulletinDetailsField;
    private FrameworkElement? _bulletinReceivedValueField;
    private readonly TextBlock _bulletinRequiredTitle = new();
    private readonly TextBlock _bulletinRequiredText = new();
    private readonly RichTextBox _bulletinPreview = new();
    private readonly CheckBox _bulletinIncludeConsequencesCheck = new()
    {
        Content = "Incluir texto de consequências",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 0, 0)
    };
    private readonly TextBox _bulletinConsequencesBox = new()
    {
        Text = SisbolTexts.AuxTransportConsequencesText,
        Width = 360,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        ToolTip = "Texto editável enviado ao campo Texto de Fechamento do SisBol."
    };
    private readonly TextBlock _bulletinSelectionText = new();
    private readonly ObservableCollection<AtBulletinQueueItem> _bulletinQueueView = [];
    private readonly List<AtBulletinQueueItem> _bulletinQueueAll = [];
    private readonly DataGrid _bulletinQueueGrid = new();
    private readonly TextBlock _bulletinQueueCounter = new();

    private readonly ComboBox _daMonthBox = new() { IsEditable = false };
    private readonly ComboBox _daYearBox = new() { IsEditable = true };
    private readonly TextBox _daSearchBox = new();
    private readonly DataGrid _daGrid = new();
    private readonly RichTextBox _daPreview = new();
    private readonly CheckBox _daIncludeConsequencesCheck = new()
    {
        Content = "Incluir texto de consequências",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 0, 0)
    };
    private readonly TextBox _daConsequencesBox = new()
    {
        Text = SisbolTexts.AuxTransportConsequencesText,
        Width = 360,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        ToolTip = "Texto editável enviado ao campo Texto de Fechamento do SisBol."
    };
    private readonly TextBlock _daTotalText = new();
    private readonly TextBlock _daSelectedText = new();
    private readonly TextBlock _daCalendarSourceText = new();

    private readonly DataGrid _reportGrid = new();
    private readonly ComboBox _reportFilter = new();
    private readonly ComboBox _reportDocumentModeBox = new() { IsEditable = false };
    private readonly ComboBox _reportDaMonthBox = new() { IsEditable = false };
    private readonly ComboBox _reportDaYearBox = new() { IsEditable = true };
    private readonly TextBlock _reportSummary = new();
    private readonly TextBlock _reportTotalCard = new();
    private readonly TextBlock _reportReceiveCard = new();
    private readonly TextBlock _reportNoReceiveCard = new();
    private readonly TextBlock _reportValueCard = new();
    private readonly Dictionary<int, decimal> _reportDaDeductionByMilitaryId = [];
    private readonly Dictionary<int, string> _reportDaReasonByMilitaryId = [];
    private readonly Dictionary<int, string> _reportVacationPeriodByMilitaryId = [];
    private readonly Dictionary<int, string> _reportVacationDaNoteStatusByMilitaryId = [];
    private DataGridTextColumn? _reportDocumentColumn;
    private AtReportDaValueDataGridColumn? _reportDaColumn;
    private readonly CheckBox _openReportAfterGenerate = new() { Content = "Abrir após gerar", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    private bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            _busyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Cursor = value ? Cursors.Wait : null;
        }
    }

    private string BusyMessage
    {
        get => _busyMessage;
        set
        {
            _busyMessage = value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_busyMessage))
                _statusText.Text = _busyMessage;
        }
    }

    public AuxilioTransporteWindow(MilitaryRepository repository, AppPaths paths, SettingsService settings, LogService log)
    {
        _repository = repository;
        _paths = paths;
        _settings = settings;
        _log = log;
        _routeDocuments = new TransportRouteDocumentService(paths, log);

        Title = "SIGFUR — Auxílio-Transporte";
        Width = 1640;
        Height = 900;
        MinWidth = 1220;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        PopulateMonthSelectors();
        Content = BuildLayout();
        // Esta janela é aberta diretamente pelo ViewModel e não passa pelo helper
        // comum de módulos. Anexa aqui para já nascer com a escala global atual e
        // continuar acompanhando qualquer alteração feita na aparência do SIGFUR.
        GlobalUiScaleService.Attach(this);

        _windowPreferencesTimer.Tick += (_, _) =>
        {
            _windowPreferencesTimer.Stop();
            SaveWindowPreferences();
        };
        Loaded += async (_, _) =>
        {
            InstallContextMenusRecursive(Content as DependencyObject);
            await InitializeAsync();
        };
        LocationChanged += (_, _) => ScheduleWindowPreferencesSave();
        SizeChanged += (_, _) => ScheduleWindowPreferencesSave();
        StateChanged += (_, _) => ScheduleWindowPreferencesSave();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) =>
        {
            _windowPreferencesTimer.Stop();
            SaveWindowPreferences();
        };
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(BuildHeader());

        var body = new Grid { Margin = new Thickness(0, 14, 0, 10) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(590), MinWidth = 560, MaxWidth = 720 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var selector = BuildMilitarySelector();
        Grid.SetColumn(selector, 0);
        body.Children.Add(selector);

        var tabs = BuildTabs();
        Grid.SetColumn(tabs, 2);
        body.Children.Add(tabs);

        var footer = new Grid { Margin = new Thickness(4, 2, 4, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusText.Text = "Carregando…";
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        footer.Children.Add(_statusText);
        Grid.SetColumn(_busyProgress, 1);
        footer.Children.Add(_busyProgress);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildHeader()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        var title = new TextBlock { Text = "🚌  Auxílio-Transporte", FontSize = 25, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        titleStack.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "Cálculo, rotas, tarifas, carteira, boletins e relatórios.",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        titleStack.Children.Add(subtitle);
        panel.Children.Add(titleStack);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Button("Manual SIPPES", (_, _) =>
            SIGFUR.Wpf.Views.Documents.LegislationWindow.ShowTopic(
                this,
                "auxílio-transporte, NR0095, AR0095, FR0095, DR0095 e cota-parte",
                manualOnly: true), "SecondaryButtonStyle"));
        buttons.Children.Add(Button("Normas e resumo", (_, _) =>
            SIGFUR.Wpf.Views.Documents.LegislationWindow.ShowTopic(
                this,
                "auxílio-transporte, cálculo, cota-parte, dias e lançamento no SIPPES",
                showSummary: true), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Atualizar / reaplicar ordem", async (_, _) => await RunModuleActionAsync(ModuleActionError, ReloadAsync), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Abrir pasta AT", (_, _) => OpenTransportFolder(), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Fechar", (_, _) => Close(), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(buttons, 1);
        panel.Children.Add(buttons);
        root.Children.Add(panel);

        var indicators = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) };
        indicators.Children.Add(SummaryChip("Militares exibidos", _headerTotal, Color.FromRgb(234, 243, 255), Color.FromRgb(25, 103, 210)));
        indicators.Children.Add(SummaryChip("Recebem AT", _headerReceive, Color.FromRgb(231, 248, 238), Color.FromRgb(28, 135, 73)));
        indicators.Children.Add(SummaryChip("Não recebem", _headerNoReceive, Color.FromRgb(246, 247, 249), Color.FromRgb(94, 103, 115)));
        indicators.Children.Add(SummaryChip("Valor líquido exibido", _headerValue, Color.FromRgb(245, 239, 255), Color.FromRgb(111, 66, 193)));
        Grid.SetRow(indicators, 1);
        root.Children.Add(indicators);
        return root;
    }

    private Border BuildMilitarySelector()
    {
        var card = Card();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = grid;

        var heading = new TextBlock { Text = "Militares", FontSize = 17, FontWeight = FontWeights.Bold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        grid.Children.Add(heading);

        _searchBox.Margin = new Thickness(0, 12, 0, 8);
        _searchBox.ToolTip = "Pesquisar por nome, nome de guerra, CPF, PREC-CP ou endereço";
        _searchBox.TextChanged += (_, _) => ApplyMilitaryFilter();
        Grid.SetRow(_searchBox, 1);
        grid.Children.Add(_searchBox);

        var filters = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _rankFilter.Items.Add("Todos os P/G");
        _rankFilter.SelectedIndex = 0;
        _rankFilter.SelectionChanged += (_, _) => ApplyMilitaryFilter();
        filters.Children.Add(_rankFilter);
        _receiveFilter.ItemsSource = new[] { "Todos", "Recebe AT", "Não recebe", "Adido/Encostado" };
        _receiveFilter.SelectedIndex = 0;
        _receiveFilter.SelectionChanged += (_, _) => ApplyMilitaryFilter();
        Grid.SetColumn(_receiveFilter, 2);
        filters.Children.Add(_receiveFilter);
        Grid.SetRow(filters, 2);
        grid.Children.Add(filters);

        _militaryGrid.AutoGenerateColumns = false;
        _militaryGrid.CanUserSortColumns = false;
        _militaryGrid.IsReadOnly = true;
        _militaryGrid.SelectionMode = DataGridSelectionMode.Extended;
        _militaryGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _militaryGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _militaryGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _militaryGrid.RowHeight = double.NaN;
        _militaryGrid.MinRowHeight = 35;
        _militaryGrid.ColumnHeaderHeight = 36;
        _militaryGrid.ItemsSource = _visibleMilitary;
        _militaryGrid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 251, 255));
        _militaryGrid.RowStyle = BuildProfessionalMilitaryRowStyle();
        ScrollViewer.SetHorizontalScrollBarVisibility(_militaryGrid, ScrollBarVisibility.Disabled);
        _militaryGrid.Columns.Add(new AtRankDataGridColumn { Header = "P/G", Width = 68, MinWidth = 64, SortMemberPath = nameof(MilitaryRecord.Rank) });
        _militaryGrid.Columns.Add(new WarNameDataGridColumn { Header = "Nome completo", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 245, SortMemberPath = nameof(MilitaryRecord.Name) });
        _militaryGrid.Columns.Add(new AtReceiveFlagDataGridColumn { Header = "Recebe AT?", Width = 92, MinWidth = 88, SortMemberPath = nameof(MilitaryRecord.ReceivesTransportAid) });
        _militaryGrid.Columns.Add(new AtTransportValueDataGridColumn { Header = "Valor recebido", Width = 128, MinWidth = 120, SortMemberPath = nameof(MilitaryRecord.TransportAidValue) });
        _militaryGrid.SelectionChanged += async (_, _) => await RunModuleActionAsync(ModuleActionError, MilitarySelectionChangedAsync);
        _militaryGrid.MouseDoubleClick += async (_, _) => await RunModuleActionAsync(ModuleActionError, HandleMilitaryGridDoubleClickAsync);
        _militaryGrid.Sorting += (_, e) => HandleRankSorting(
            _militaryGrid,
            e,
            direction =>
            {
                var ordered = direction == ListSortDirection.Ascending
                    ? _visibleMilitary.OrderBy(x => AtRankFormatter.GetOrder(x.Rank)).ThenBy(x => Normalize(x.Name))
                    : _visibleMilitary.OrderByDescending(x => AtRankFormatter.GetOrder(x.Rank)).ThenBy(x => Normalize(x.Name));
                ReplaceCollection(_visibleMilitary, ordered.ToList());
            });
        Grid.SetRow(_militaryGrid, 3);
        grid.Children.Add(_militaryGrid);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var listStatus = new StackPanel();
        _listCounter.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        listStatus.Children.Add(_listCounter);
        _listSelectedMilitaryText.Text = "Último selecionado: nenhum";
        _listSelectedMilitaryText.Margin = new Thickness(0, 3, 0, 0);
        _listSelectedMilitaryText.FontWeight = FontWeights.SemiBold;
        _listSelectedMilitaryText.TextTrimming = TextTrimming.CharacterEllipsis;
        _listSelectedMilitaryText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        listStatus.Children.Add(_listSelectedMilitaryText);
        footer.Children.Add(listStatus);
        var wallet = Button("Abrir carteira", async (_, _) => await RunModuleActionAsync(ModuleActionError, OpenWalletAsync), "GhostButtonStyle");
        Grid.SetColumn(wallet, 1);
        footer.Children.Add(wallet);
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);
        return card;
    }

    private TabControl BuildTabs()
    {
        _tabs.Items.Clear();
        _tabs.Items.Add(new TabItem { Header = "Cálculo e cadastro", Content = BuildCalculationTab() });
        _tabs.Items.Add(new TabItem { Header = "Rotas / Endereço", Content = BuildRoutesTab() });
        _tabs.Items.Add(new TabItem { Header = "Boletim / SisBol", Content = BuildBulletinTab() });
        _tabs.Items.Add(new TabItem { Header = "Despesa a Anular", Content = BuildDaTab() });
        _tabs.Items.Add(new TabItem { Header = "Relatórios e lote", Content = BuildReportsTab() });
        return _tabs;
    }

    private UIElement BuildCalculationTab()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(4) };
        scroll.Content = stack;

        var identity = Card(new Thickness(0, 0, 0, 10));
        var identityStack = new StackPanel();
        identity.Child = identityStack;
        _selectedTitle.Text = "Selecione um militar";
        _selectedTitle.FontSize = 20;
        _selectedTitle.FontWeight = FontWeights.Bold;
        _selectedTitle.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        identityStack.Children.Add(_selectedTitle);
        _selectedSubtitle.Margin = new Thickness(0, 3, 0, 0);
        _selectedSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        identityStack.Children.Add(_selectedSubtitle);
        _selectedAddress.Margin = new Thickness(0, 8, 0, 0);
        _selectedAddress.TextWrapping = TextWrapping.Wrap;
        identityStack.Children.Add(_selectedAddress);
        _selectedStatus.Margin = new Thickness(0, 6, 0, 0);
        _selectedStatus.FontWeight = FontWeights.SemiBold;
        identityStack.Children.Add(_selectedStatus);
        var clearMilitaryButton = Button("Limpar militar (modo livre)", (_, _) => ClearSelectedMilitary(), "GhostButtonStyle");
        clearMilitaryButton.HorizontalAlignment = HorizontalAlignment.Left;
        clearMilitaryButton.Margin = new Thickness(0, 10, 0, 0);
        clearMilitaryButton.ToolTip = "Desvincula o militar, mas mantém P/G, rota, tarifas e cálculo atuais para uso livre.";
        identityStack.Children.Add(clearMilitaryButton);
        stack.Children.Add(identity);

        var parameters = Card(new Thickness(0, 0, 0, 10));
        var paramsGrid = new Grid();
        parameters.Child = paramsGrid;
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _calculatorRankBox.ToolTip = "Escolha somente o P/G para calcular, sem precisar selecionar um militar.";
        _calculatorRankBox.SelectionChanged += async (_, _) => await RunModuleActionAsync(ModuleActionError, RecalculateAsync);
        _manualLineBox.ToolTip = "Informe o número da linha. Para metrô ou outro meio, use uma identificação clara, por exemplo: METRÔ.";
        _manualFareBox.ToolTip = "Valor unitário de UMA passagem de ida. O sistema calcula ida e volta automaticamente.";
        paramsGrid.Children.Add(LabeledControl("P/G para cálculo", _calculatorRankBox, 0));
        paramsGrid.Children.Add(LabeledControl("Dias úteis", _daysBox, 2));
        paramsGrid.Children.Add(LabeledControl("Nº da linha / meio", _manualLineBox, 4));
        paramsGrid.Children.Add(LabeledControl("Valor de 1 passagem", _manualFareBox, 6));
        var addButton = Button("Adicionar ônibus", (_, _) => AddManualFare(), "SecondaryButtonStyle");
        addButton.VerticalAlignment = VerticalAlignment.Bottom;
        addButton.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetColumn(addButton, 8);
        paramsGrid.Children.Add(addButton);
        stack.Children.Add(parameters);

        var busesCard = Card(new Thickness(0, 0, 0, 10));
        var busesGrid = new Grid();
        busesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        busesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230) });
        busesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        busesCard.Child = busesGrid;
        var busHeader = new StackPanel();
        var busTitle = new TextBlock { Text = "Ônibus utilizados na ida", FontWeight = FontWeights.Bold, FontSize = 15 };
        busTitle.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        busHeader.Children.Add(busTitle);
        var busHint = new TextBlock
        {
            Text = "Cadastre uma linha por ônibus utilizado. Informe sempre o número/identificação e o valor de UMA passagem; o sistema dobra os valores para ida e volta.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        busHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        busHeader.Children.Add(busHint);
        busesGrid.Children.Add(busHeader);
        ConfigureBusGrid();
        _busGrid.Margin = new Thickness(0, 10, 0, 8);
        Grid.SetRow(_busGrid, 1);
        busesGrid.Children.Add(_busGrid);
        var busActions = new Grid();
        busActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        busActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var editButtons = new WrapPanel();
        editButtons.Children.Add(Button("＋ Nova linha", (_, _) => EditBusLine(null), "SecondaryButtonStyle"));
        editButtons.Children.Add(Button("Editar selecionado", (_, _) => EditBusLine(_busGrid.SelectedItem as AtBusLine), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        editButtons.Children.Add(Button("Remover", (_, _) => RemoveSelectedBus(), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        editButtons.Children.Add(Button("Limpar", (_, _) => ClearBuses(), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        busActions.Children.Add(editButtons);
        var saveButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        saveButtons.Children.Add(Button("Recalcular", async (_, _) => await RunModuleActionAsync(ModuleActionError, RecalculateAsync), "SecondaryButtonStyle"));
        saveButtons.Children.Add(Button("Salvar cálculo e linhas", async (_, _) => await SaveCurrentTransportFromButtonAsync(), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        var addToBulletin = Button("Levar ao boletim", async (_, _) => await RunModuleActionAsync(
            "Não foi possível incluir o militar no boletim de Auxílio-Transporte. A tela continuará aberta.",
            () => AddCurrentCalculationToBulletinAsync()), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0));
        addToBulletin.ToolTip = "Guarda este militar, a rota e o cálculo na lista do modelo selecionado na aba Boletim / SisBol.";
        saveButtons.Children.Add(addToBulletin);
        Grid.SetColumn(saveButtons, 1);
        busActions.Children.Add(saveButtons);
        Grid.SetRow(busActions, 2);
        busesGrid.Children.Add(busActions);
        stack.Children.Add(busesCard);

        var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 10) };
        metrics.Children.Add(MetricCard("Valor diário", _dailyValue));
        metrics.Children.Add(MetricCard("Total s/ cota", _grossValue));
        metrics.Children.Add(MetricCard("Soldo", _salaryValue));
        metrics.Children.Add(MetricCard("6% do soldo", _salarySixPercentValue));
        metrics.Children.Add(MetricCard("6% do soldo / 30", _salarySixPercentDailyValue));
        metrics.Children.Add(MetricCard("6% / 30 x dias", _shareValue));
        metrics.Children.Add(MetricCard("Total mensal líquido", _netValue, true));
        stack.Children.Add(metrics);

        var actions = Card();
        var actionPanel = new WrapPanel();
        actions.Child = actionPanel;
        actionPanel.Children.Add(Button("Gerar fichas dos selecionados", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => GenerateTransportDocumentFromButtonAsync(true)), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        actionPanel.Children.Add(Button("Abrir modelo/pasta gerada", (_, _) => ShellService.OpenPath(_paths.GeneratedDocumentsDirectory), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        stack.Children.Add(actions);
        return scroll;
    }

    private UIElement BuildRoutesTab()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(4) };
        scroll.Content = stack;

        var selectedCard = Card(new Thickness(0, 0, 0, 10));
        var selectedStack = new StackPanel();
        selectedCard.Child = selectedStack;
        var selectedLabel = new TextBlock { Text = "MILITAR DA ROTA", FontSize = 11, FontWeight = FontWeights.Bold };
        selectedLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        selectedStack.Children.Add(selectedLabel);
        _routeSelectedMilitaryText.Text = "Nenhum militar selecionado";
        _routeSelectedMilitaryText.FontSize = 19;
        _routeSelectedMilitaryText.FontWeight = FontWeights.SemiBold;
        _routeSelectedMilitaryText.Margin = new Thickness(0, 4, 0, 0);
        _routeSelectedMilitaryText.TextWrapping = TextWrapping.Wrap;
        _routeSelectedMilitaryText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        selectedStack.Children.Add(_routeSelectedMilitaryText);
        var selectedHint = new TextBlock
        {
            Text = "A linha destacada na lista lateral é a pessoa cuja rota, ônibus e valores estão sendo editados.",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        selectedHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        selectedStack.Children.Add(selectedHint);
        var routeActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        routeActions.Children.Add(Button("Ficha da rota", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => GenerateTransportDocumentFromButtonAsync(false)), "PrimaryButtonStyle"));
        routeActions.Children.Add(Button("Declaração de residência / imprimir", async (_, _) => await RunModuleActionAsync(ModuleActionError, OpenResidenceDeclarationAsync), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        selectedStack.Children.Add(routeActions);
        stack.Children.Add(selectedCard);

        var routeCard = Card(new Thickness(0, 0, 0, 10));
        var routeGrid = new Grid();
        routeCard.Child = routeGrid;
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        routeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        routeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        routeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        routeGrid.Children.Add(LabeledControl("Origem — endereço residencial salvo", _originBox, 0));
        var destinationField = LabeledControl("Destino / OM", _destinationBox, 2);
        routeGrid.Children.Add(destinationField);
        var dateField = LabeledControl("Saída", _departureDate, 0);
        dateField.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(dateField, 1);
        routeGrid.Children.Add(dateField);
        var timeField = LabeledControl("Horário", _departureTime, 2);
        timeField.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(timeField, 1);
        routeGrid.Children.Add(timeField);
        var routeButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var cepField = LabeledControl("CEP da origem", _routeCepBox, 0);
        cepField.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(cepField, 2);
        routeGrid.Children.Add(cepField);
        var cepButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        cepButtons.Children.Add(Button("Pesquisar CEP e preencher origem", async (_, _) => await RunModuleActionAsync(ModuleActionError, LookupRouteZipCodeAsync), "SecondaryButtonStyle"));
        cepButtons.Children.Add(Button("Usar endereço do cadastro", (_, _) => LoadAddressFromCurrent(), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetRow(cepButtons, 2);
        Grid.SetColumn(cepButtons, 2);
        routeGrid.Children.Add(cepButtons);

        routeButtons.Children.Add(Button("Abrir rota no Google Maps", (_, _) => OpenMapsRoute(), "PrimaryButtonStyle"));
        routeButtons.Children.Add(Button("Salvar rota", async (_, _) => await SaveRouteFromButtonAsync(), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetRow(routeButtons, 3);
        Grid.SetColumnSpan(routeButtons, 3);
        routeGrid.Children.Add(routeButtons);
        _routeSaveStatus.Margin = new Thickness(0, 10, 0, 0);
        _routeSaveStatus.TextWrapping = TextWrapping.Wrap;
        _routeSaveStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(_routeSaveStatus, 4);
        Grid.SetColumnSpan(_routeSaveStatus, 3);
        routeGrid.Children.Add(_routeSaveStatus);
        stack.Children.Add(routeCard);

        var lookupCard = Card(new Thickness(0, 0, 0, 10));
        var lookupStack = new StackPanel();
        lookupCard.Child = lookupStack;
        var lookupTitle = new TextBlock { Text = "Pesquisar linha de ônibus", FontWeight = FontWeights.Bold, FontSize = 16 };
        lookupTitle.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        lookupStack.Children.Add(lookupTitle);
        var lookupGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        lookupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lookupGrid.Children.Add(LabeledControl("Nº exato da linha", _lineNumberBox, 0));
        _lineCategoryBox.ItemsSource = new[] { "Auto", "Troncal/Convencional/Estrutural", "Alimentadora/Circular", "Vilas e Favelas", "Metrô" };
        _lineCategoryBox.SelectedIndex = 0;
        lookupGrid.Children.Add(LabeledControl("Categoria", _lineCategoryBox, 2));
        lookupGrid.Children.Add(LabeledControl("Valor de 1 passagem", _lookupFareBox, 4));
        var findPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        findPanel.Children.Add(Button("Pesquisar", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => LookupBusLineAsync(false)), "PrimaryButtonStyle"));
        findPanel.Children.Add(Button("Pesquisar online", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => LookupBusLineAsync(true)), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(findPanel, 6);
        lookupGrid.Children.Add(findPanel);
        lookupStack.Children.Add(lookupGrid);
        _lookupResultText.Margin = new Thickness(0, 12, 0, 0);
        _lookupResultText.TextWrapping = TextWrapping.Wrap;
        lookupStack.Children.Add(_lookupResultText);
        var lookupButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        lookupButtons.Children.Add(Button("Adicionar linha e valor à rota", (_, _) => AddLookupToRoute(), "SecondaryButtonStyle"));
        lookupButtons.Children.Add(Button("Salvar linha no cache", async (_, _) => await RunModuleActionAsync(ModuleActionError, SaveLookupCacheAsync), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        lookupButtons.Children.Add(Button("Abrir página da linha", (_, _) => OpenLookupPage(), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        lookupButtons.Children.Add(Button("Tabela oficial PBH", (_, _) => ShellService.OpenPath(PbhFaresUrl), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        lookupStack.Children.Add(lookupButtons);
        stack.Children.Add(lookupCard);

        var proofCard = Card();
        var proofStack = new StackPanel();
        proofCard.Child = proofStack;
        var proofTitle = new TextBlock { Text = "Documentos e comprovantes da rota", FontWeight = FontWeights.Bold, FontSize = 16 };
        proofTitle.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        proofStack.Children.Add(proofTitle);
        var instruction = new TextBlock
        {
            Text = "O print abre a ferramenta de recorte do Windows. Selecione somente a parte da rota; a imagem será salva na pasta do militar e vinculada à rota.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        };
        instruction.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        proofStack.Children.Add(instruction);
        var proofButtons = new WrapPanel();
        proofButtons.Children.Add(Button("Salvar print da rota", async (_, _) => await CaptureRouteScreenshotAsync(), "PrimaryButtonStyle"));
        proofButtons.Children.Add(Button("Abrir print salvo", (_, _) => OpenSavedPrint(), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        proofButtons.Children.Add(Button("Excluir print", async (_, _) => await RunModuleActionAsync(ModuleActionError, DeleteSavedPrintAsync), "DangerButtonStyle", new Thickness(8, 0, 0, 0)));
        proofStack.Children.Add(proofButtons);
        _printStatus.Margin = new Thickness(0, 10, 0, 0);
        _printStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        proofStack.Children.Add(_printStatus);
        var residenceButtons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        residenceButtons.Children.Add(Button("Anexar comprovante de residência", async (_, _) => await RunModuleActionAsync(ModuleActionError, AttachResidenceAsync), "SecondaryButtonStyle"));
        residenceButtons.Children.Add(Button("Abrir comprovante", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => { if (_current is not null) { var path = Residence.Get(_current.Id); if (path.Length > 0) ShellService.OpenPath(path); } return Task.CompletedTask; }), "SecondaryButtonStyle", new Thickness(8,0,0,0)));
        residenceButtons.Children.Add(Button("Remover anexo", (_, _) => { if (_current is not null) { Residence.Detach(_current.Id); UpdatePrintStatus(); } }, "GhostButtonStyle", new Thickness(8,0,0,0)));
        proofStack.Children.Add(residenceButtons);
        proofStack.Children.Add(_residenceStatus);
        stack.Children.Add(proofCard);

        var sendCard = Card(new Thickness(0, 10, 0, 0));
        var sendGrid = new Grid();
        sendGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sendGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        sendGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sendCard.Child = sendGrid;
        _routeBulletinActionBox.ItemsSource = BulletinActions;
        _routeBulletinActionBox.SelectedIndex = 0;
        _routeBulletinActionBox.MinWidth = 360;
        _routeBulletinActionBox.SelectionChanged += (_, _) =>
        {
            var action = _routeBulletinActionBox.SelectedItem?.ToString();
            if (!string.IsNullOrWhiteSpace(action) && _bulletinActionBox.Items.Contains(action))
                _bulletinActionBox.SelectedItem = action;
        };
        sendGrid.Children.Add(LabeledControl("Boletim de destino", _routeBulletinActionBox, 0));
        var sendButton = Button("Levar militar e rota para o boletim", async (_, _) =>
        {
            var action = _routeBulletinActionBox.SelectedItem?.ToString();
            await RunModuleActionAsync(
                "Não foi possível incluir o militar e a rota no boletim de Auxílio-Transporte. A tela continuará aberta.",
                () => AddCurrentCalculationToBulletinAsync(action, false, true));
        }, "PrimaryButtonStyle");
        sendButton.VerticalAlignment = VerticalAlignment.Bottom;
        sendButton.Margin = new Thickness(0, 20, 0, 0);
        sendButton.ToolTip = "Escolha primeiro o modelo. O militar, os ônibus, a rota e o cálculo atuais serão guardados nesse boletim.";
        Grid.SetColumn(sendButton, 2);
        sendGrid.Children.Add(sendButton);
        stack.Children.Add(sendCard);
        return scroll;
    }

    private UIElement BuildBulletinTab()
    {
        var root = new Grid { Margin = new Thickness(4) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(380) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(topGrid, 0);
        root.Children.Add(topGrid);

        var options = Card();
        var optionsStack = new StackPanel();
        options.Child = new ScrollViewer { Content = optionsStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(options, 0);
        topGrid.Children.Add(options);

        _bulletinActionBox.ItemsSource = BulletinActions;
        _bulletinActionBox.SelectedItem = _routeBulletinActionBox.SelectedItem ?? BulletinActions[0];
        _bulletinActionBox.SelectionChanged += (_, _) =>
        {
            var action = _bulletinActionBox.SelectedItem?.ToString();
            if (!string.IsNullOrWhiteSpace(action) && _routeBulletinActionBox.Items.Contains(action))
                _routeBulletinActionBox.SelectedItem = action;
            UpdateBulletinRequirementUi();
            RefreshBulletinQueue();
        };
        optionsStack.Children.Add(LabeledControl("Modelo", _bulletinActionBox, 0));

        var referenceRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        referenceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        referenceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bulletinReferenceField = LabeledElement("Mês/Ano de referência", BuildBulletinReferencePicker(), 0, out _bulletinReferenceLabel);
        referenceRow.Children.Add(_bulletinReferenceField);
        _bulletinAddMonthButton.SetResourceReference(System.Windows.Controls.Button.StyleProperty, "SecondaryButtonStyle");
        _bulletinAddMonthButton.Click += (_, _) => AddSelectedReferenceMonthToDetails();
        _bulletinAddMonthButtonField = _bulletinAddMonthButton;
        Grid.SetColumn(_bulletinAddMonthButton, 1);
        referenceRow.Children.Add(_bulletinAddMonthButton);
        optionsStack.Children.Add(referenceRow);
        optionsStack.Children.Add(_transportReferenceHint);

        _bulletinReceivedValueField = LabeledControl("Valor recebido pelo militar", _bulletinReceivedValueBox, 0);
        ((FrameworkElement)_bulletinReceivedValueField).Margin = new Thickness(0, 10, 0, 0);
        optionsStack.Children.Add(_bulletinReceivedValueField);
        _bulletinDifferencePreviewText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        optionsStack.Children.Add(_bulletinDifferencePreviewText);
        _bulletinReceivedValueBox.TextChanged += (_, _) => UpdateBulletinDifferencePreview();

        _bulletinCountFromField = LabeledControl("A contar de", _bulletinCountFromDatePicker, 0, out _bulletinCountFromLabel);
        ((FrameworkElement)_bulletinCountFromField).Margin = new Thickness(0, 10, 0, 0);
        optionsStack.Children.Add(_bulletinCountFromField);

        _bulletinDetailsField = LabeledControl("Meses escolhidos", _bulletinRequiredDetailsBox, 0, out _bulletinDetailsLabel);
        ((FrameworkElement)_bulletinDetailsField).Margin = new Thickness(0, 10, 0, 0);
        optionsStack.Children.Add(_bulletinDetailsField);
        _bulletinCompetenceCountText.Margin = new Thickness(0, 4, 0, 0);
        _bulletinCompetenceCountText.Text = "0 competência selecionada.";
        _bulletinCompetenceCountText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        optionsStack.Children.Add(_bulletinCompetenceCountText);
        _bulletinRequiredDetailsBox.TextChanged += (_, _) => UpdateBulletinCompetenceCount();

        _bulletinSelectionText.Text = "Escolha o modelo. Para saque/devolução, selecione os meses do militar antes de adicioná-lo.";
        _bulletinSelectionText.Margin = new Thickness(0, 10, 0, 0);
        _bulletinSelectionText.TextWrapping = TextWrapping.Wrap;
        _bulletinSelectionText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        optionsStack.Children.Add(_bulletinSelectionText);

        var queueCard = Card();
        var queueRoot = new Grid();
        queueRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queueRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        queueRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queueCard.Child = queueRoot;
        Grid.SetColumn(queueCard, 2);
        topGrid.Children.Add(queueCard);

        var queueHeader = new Grid();
        queueHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        queueHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var queueTitle = new TextBlock { Text = "Militares deste modelo — dados individuais", FontSize = 15, FontWeight = FontWeights.Bold };
        queueTitle.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        queueHeader.Children.Add(queueTitle);
        _bulletinQueueCounter.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(_bulletinQueueCounter, 1);
        queueHeader.Children.Add(_bulletinQueueCounter);
        queueRoot.Children.Add(queueHeader);

        ConfigureBulletinQueueGrid();
        _bulletinQueueGrid.Margin = new Thickness(0, 8, 0, 8);
        Grid.SetRow(_bulletinQueueGrid, 1);
        queueRoot.Children.Add(_bulletinQueueGrid);

        var queueButtons = new WrapPanel();
        queueButtons.Children.Add(Button("Adicionar marcados", async (_, _) => await RunModuleActionAsync(
            "Não foi possível adicionar os militares marcados ao boletim de Auxílio-Transporte. A tela continuará aberta.",
            AddSelectedMilitaryToBulletinAsync), "PrimaryButtonStyle"));
        queueButtons.Children.Add(Button("Carregar militar selecionado", async (_, _) => await RunModuleActionAsync(
            "Não foi possível carregar o militar selecionado da fila. A tela continuará aberta.",
            LoadSelectedBulletinQueueMilitaryAsync), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        queueButtons.Children.Add(Button("Remover selecionado", (_, _) => RemoveSelectedBulletinQueueItems(), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        queueButtons.Children.Add(Button("Limpar este modelo", (_, _) => ClearCurrentBulletinQueue(), "DangerButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetRow(queueButtons, 2);
        queueRoot.Children.Add(queueButtons);

        _bulletinPreview.IsReadOnly = false;
        _bulletinPreview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _bulletinPreview.FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily);
        _bulletinPreview.FontSize = BulletinTextFormatter.StandardWpfFontSize;
        _bulletinPreview.FontWeight = FontWeights.Normal;
        _bulletinPreview.Document.PagePadding = new Thickness(20);
        var previewBorder = Card(new Thickness(0, 0, 0, 10));
        previewBorder.Child = _bulletinPreview;
        Grid.SetRow(previewBorder, 1);
        root.Children.Add(previewBorder);

        var buttons = new WrapPanel();
        buttons.Children.Add(Button("Gerar boletim em janela", async (_, _) => await RunModuleActionAsync(
            "Não foi possível gerar a prévia do boletim de Auxílio-Transporte. A tela continuará aberta.",
            () => GenerateBulletinAsync(true)), "PrimaryButtonStyle"));
        buttons.Children.Add(Button("Copiar para Word", (_, _) => CopyRichText(_bulletinPreview), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Salvar RTF", (_, _) => SaveRichText(_bulletinPreview, ".rtf"), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Salvar TXT", (_, _) => SaveRichText(_bulletinPreview, ".txt"), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Aplicar no banco", async (_, _) => await RunModuleActionAsync(
            "Não foi possível aplicar a alteração do Auxílio-Transporte no banco. Nenhuma confirmação incompleta será exibida.",
            ApplyBulletinActionAsync), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(_bulletinIncludeConsequencesCheck);
        buttons.Children.Add(_bulletinConsequencesBox);
        buttons.Children.Add(Button("Enviar ao SisBol", async (_, _) => await RunModuleActionAsync(
            "Não foi possível preparar o boletim de Auxílio-Transporte para o SisBol. A tela continuará aberta.",
            OpenSisbolMatterAndCopyAsync), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        buttons.Children.Add(Button("Enviados ao SISBOL", (_, _) => OpenSisbolSubmissionHistory(), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        UpdateBulletinRequirementUi();
        RefreshBulletinQueue();
        return root;
    }

    private UIElement BuildDaTab()
    {
        var root = new Grid { Margin = new Thickness(4) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = Card(new Thickness(0, 0, 0, 8));
        var topGrid = new Grid();
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.Child = topGrid;

        var explanation = new TextBlock
        {
            Text = "PRETA = dia útil que gera desconto. VERMELHA = sábado, domingo ou feriado que compensa uma preta. O valor diário é o valor líquido mensal cadastrado dividido por 22. Use PDF importado ou boletins salvos; a prévia abre em janela separada.",
            TextWrapping = TextWrapping.Wrap
        };
        topGrid.Children.Add(explanation);

        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(new TextBlock { Text = "Mês:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.SemiBold });
        _daMonthBox.Width = 118;
        _daMonthBox.Margin = new Thickness(0, 0, 8, 0);
        tools.Children.Add(_daMonthBox);
        tools.Children.Add(new TextBlock { Text = "Ano:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.SemiBold });
        _daYearBox.Width = 76;
        _daYearBox.Margin = new Thickness(0, 0, 8, 0);
        tools.Children.Add(_daYearBox);
        _daMonthBox.SelectionChanged += async (_, _) => await RunModuleActionAsync(ModuleActionError, LoadDaAsync);
        _daYearBox.SelectionChanged += async (_, _) => await RunModuleActionAsync(ModuleActionError, LoadDaAsync);
        _daYearBox.LostFocus += async (_, _) => await RunModuleActionAsync(ModuleActionError, LoadDaAsync);
        tools.Children.Add(SmallButton("Carregar", async (_, _) => await RunModuleActionAsync(ModuleActionError, LoadDaAsync), "SecondaryButtonStyle"));
        tools.Children.Add(new TextBlock { Text = "Buscar:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0), FontWeight = FontWeights.SemiBold });
        _daSearchBox.Width = 220;
        _daSearchBox.ToolTip = "Pesquisar por nome, nome de guerra, P/G, CPF ou PREC-CP";
        _daSearchBox.TextChanged += (_, _) => ApplyDaFilter();
        tools.Children.Add(_daSearchBox);
        tools.Children.Add(SmallButton("Importar PDF/ZIP", async (_, _) => await RunModuleActionAsync(ModuleActionError, ImportBulletinsForDaAsync), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        tools.Children.Add(SmallButton("Boletins salvos", async (_, _) => await RunModuleActionAsync(ModuleActionError, ImportSavedBulletinsForDaAsync), "SecondaryButtonStyle", new Thickness(6, 0, 0, 0)));
        tools.Children.Add(SmallButton("Zerar", async (_, _) => await RunModuleActionAsync(ModuleActionError, ResetAllDaAsync), "DangerButtonStyle", new Thickness(6, 0, 0, 0)));
        Grid.SetRow(tools, 1);
        topGrid.Children.Add(tools);
        _daCalendarSourceText.Margin = new Thickness(0, 7, 0, 0);
        _daCalendarSourceText.TextWrapping = TextWrapping.Wrap;
        _daCalendarSourceText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(_daCalendarSourceText, 2);
        topGrid.Children.Add(_daCalendarSourceText);
        root.Children.Add(top);

        ConfigureDaGrid();
        _daGrid.MinHeight = 430;
        var gridCard = Card(new Thickness(0, 0, 0, 8));
        gridCard.Child = _daGrid;
        Grid.SetRow(gridCard, 1);
        root.Children.Add(gridCard);

        var adjustCard = Card(new Thickness(0, 0, 0, 8));
        var adjustGrid = new Grid();
        adjustGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        adjustGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        adjustCard.Child = adjustGrid;
        _daSelectedText.Text = "Selecione um militar na tabela para alterar os dias.";
        _daSelectedText.TextWrapping = TextWrapping.Wrap;
        _daSelectedText.VerticalAlignment = VerticalAlignment.Center;
        adjustGrid.Children.Add(_daSelectedText);
        var adjustButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        adjustButtons.Children.Add(SmallButton("Preta −", (_, _) => AdjustSelectedDa(false, -1), "SecondaryButtonStyle"));
        adjustButtons.Children.Add(SmallButton("Preta +", (_, _) => AdjustSelectedDa(false, 1), "PrimaryButtonStyle", new Thickness(5, 0, 0, 0)));
        adjustButtons.Children.Add(SmallButton("Vermelha −", (_, _) => AdjustSelectedDa(true, -1), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        adjustButtons.Children.Add(SmallButton("Vermelha +", (_, _) => AdjustSelectedDa(true, 1), "PrimaryButtonStyle", new Thickness(5, 0, 0, 0)));
        adjustButtons.Children.Add(SmallButton("Zerar militar", (_, _) => ResetSelectedDa(), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(adjustButtons, 1);
        adjustGrid.Children.Add(adjustButtons);
        Grid.SetRow(adjustCard, 2);
        root.Children.Add(adjustCard);

        _daPreview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _daPreview.FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily);
        _daPreview.FontSize = BulletinTextFormatter.StandardWpfFontSize;
        _daPreview.Visibility = Visibility.Collapsed;

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _daTotalText.VerticalAlignment = VerticalAlignment.Center;
        _daTotalText.FontWeight = FontWeights.Bold;
        footer.Children.Add(_daTotalText);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(SmallButton("Salvar", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => SaveDaAsync()), "SecondaryButtonStyle"));
        buttons.Children.Add(SmallButton("Gerar prévia", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => GenerateDaBulletinAsync(true)), "PrimaryButtonStyle", new Thickness(6, 0, 0, 0)));
        _daIncludeConsequencesCheck.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_daIncludeConsequencesCheck);
        _daConsequencesBox.Width = 260;
        _daConsequencesBox.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(_daConsequencesBox);
        buttons.Children.Add(SmallButton("Enviar SisBol", async (_, _) => await RunModuleActionAsync(ModuleActionError, SendDaToSisbolAsync), "PrimaryButtonStyle", new Thickness(6, 0, 0, 0)));
        buttons.Children.Add(SmallButton("Enviados SISBOL", (_, _) => OpenSisbolSubmissionHistory(), "SecondaryButtonStyle", new Thickness(6, 0, 0, 0)));
        buttons.Children.Add(SmallButton("Copiar Word", async (_, _) => await RunModuleActionAsync(ModuleActionError, CopyDaPreviewAsync), "SecondaryButtonStyle", new Thickness(6, 0, 0, 0)));
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildReportsTab()
    {
        var root = new Grid { Margin = new Thickness(4) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        ConfigureReportControls();

        var top = Card(new Thickness(0, 0, 0, 10));
        var topGrid = new Grid();
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.Child = topGrid;

        var titleRow = BuildReportHeaderRow();
        topGrid.Children.Add(titleRow);

        var reportOptions = BuildReportOptionBar();
        Grid.SetRow(reportOptions, 1);
        topGrid.Children.Add(reportOptions);

        var commands = BuildReportCommandBar();
        Grid.SetRow(commands, 2);
        topGrid.Children.Add(commands);

        var cards = BuildReportSummaryCards();
        Grid.SetRow(cards, 3);
        topGrid.Children.Add(cards);
        root.Children.Add(top);

        ConfigureReportGrid();
        var reportCard = Card(new Thickness(0, 0, 0, 10));
        reportCard.Padding = new Thickness(8);
        reportCard.Child = _reportGrid;
        Grid.SetRow(reportCard, 1);
        root.Children.Add(reportCard);

        _reportSummary.FontWeight = FontWeights.SemiBold;
        _reportSummary.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        Grid.SetRow(_reportSummary, 2);
        root.Children.Add(_reportSummary);
        return root;
    }

    private void ConfigureReportControls()
    {
        _reportFilter.ItemsSource = new[] { "Todos", "Recebem AT", "Faixas R$ 700+", "Não recebem AT", "Sem endereço", "Adido/Encostado" };
        _reportFilter.SelectedIndex = 0;
        _reportFilter.Width = 190;
        _reportFilter.VerticalAlignment = VerticalAlignment.Top;
        _reportFilter.SelectionChanged += (_, _) => RefreshReport();

        _reportDocumentModeBox.Width = 96;
        _reportDocumentModeBox.SelectionChanged += (_, _) =>
        {
            UpdateReportDocumentColumn();
            RefreshReport();
        };

        _reportDaMonthBox.Width = 118;
        _reportDaYearBox.Width = 76;
        _reportDaMonthBox.SelectionChanged += async (_, _) => { if (IsLoaded) await RunModuleActionAsync(ModuleActionError, RefreshReportDaValuesAndGridAsync); };
        _reportDaYearBox.SelectionChanged += async (_, _) => { if (IsLoaded) await RunModuleActionAsync(ModuleActionError, RefreshReportDaValuesAndGridAsync); };
        _reportDaYearBox.LostFocus += async (_, _) => { if (IsLoaded) await RunModuleActionAsync(ModuleActionError, RefreshReportDaValuesAndGridAsync); };
    }

    private Grid BuildReportHeaderRow()
    {
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var reportTitle = new StackPanel();
        var title = new TextBlock
        {
            Text = "Relatório profissional de Auxílio-Transporte",
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        reportTitle.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = "Filtre, confira os valores e gere um relatório pronto para impressão.",
            Margin = new Thickness(0, 3, 18, 0),
            TextWrapping = TextWrapping.Wrap
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        reportTitle.Children.Add(subtitle);
        titleRow.Children.Add(reportTitle);

        Grid.SetColumn(_reportFilter, 1);
        titleRow.Children.Add(_reportFilter);
        return titleRow;
    }

    private WrapPanel BuildReportOptionBar()
    {
        var reportOptions = new WrapPanel { Margin = new Thickness(0, 13, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        reportOptions.Children.Add(InlineReportOption("Identificador", _reportDocumentModeBox));
        reportOptions.Children.Add(InlineReportOption("Mês DA", _reportDaMonthBox, new Thickness(12, 0, 0, 0)));
        reportOptions.Children.Add(InlineReportOption("Ano DA", _reportDaYearBox, new Thickness(8, 0, 0, 0)));
        return reportOptions;
    }

    private WrapPanel BuildReportCommandBar()
    {
        var commands = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        commands.Children.Add(Button("Atualizar valores", async (_, _) => await RunModuleActionAsync(ModuleActionError, RefreshReportValuesAsync), "SecondaryButtonStyle"));
        commands.Children.Add(Button("Gerar Excel profissional", async (_, _) => await RunModuleActionAsync(ModuleActionError, GenerateProfessionalReportAsync), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        commands.Children.Add(Button("Exportar CSV", async (_, _) => await RunModuleActionAsync(ModuleActionError, ExportReportCsvAsync), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        _openReportAfterGenerate.Margin = new Thickness(12, 4, 0, 0);
        _openReportAfterGenerate.VerticalAlignment = VerticalAlignment.Center;
        _openReportAfterGenerate.ToolTip = "Abre automaticamente o arquivo HTML ou CSV após a geração.";
        commands.Children.Add(_openReportAfterGenerate);
        commands.Children.Add(Button("Auditar e recalcular", async (_, _) => await RunModuleActionAsync(ModuleActionError, AuditAllTransportAsync), "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        commands.Children.Add(Button("Gerar fichas de rota", async (_, _) => await RunModuleActionAsync(ModuleActionError, () => GenerateTransportDocumentFromButtonAsync(true)), "SecondaryButtonStyle", new Thickness(8, 0, 0, 0)));
        return commands;
    }

    private UniformGrid BuildReportSummaryCards()
    {
        var cards = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        cards.Children.Add(SummaryChip("Militares", _reportTotalCard, Color.FromRgb(234, 243, 255), Color.FromRgb(25, 103, 210), true));
        cards.Children.Add(SummaryChip("Recebem AT", _reportReceiveCard, Color.FromRgb(231, 248, 238), Color.FromRgb(28, 135, 73), true));
        cards.Children.Add(SummaryChip("Não recebem", _reportNoReceiveCard, Color.FromRgb(246, 247, 249), Color.FromRgb(94, 103, 115), true));
        cards.Children.Add(SummaryChip("Valor líquido", _reportValueCard, Color.FromRgb(245, 239, 255), Color.FromRgb(111, 66, 193), true));
        return cards;
    }

    private async Task InitializeAsync()
    {
        try
        {
            await EnsureRouteDatabaseAsync();
            await EnsureTransportFareMetadataColumnsAsync();
            await ReloadAsync();
            await RestoreWindowPreferencesAsync();
            _statusText.Text = "Auxílio-Transporte carregado.";
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Não foi possível iniciar o Auxílio-Transporte.", ex);
        }
    }

    private async Task ReloadAsync()
    {
        var selectedId = _current?.Id ?? 0;
        var loaded = await _repository.GetAllAsync();
        try { await App.MilitaryPreferences.ApplyAsync(loaded); } catch { }
        var savedOrder = await LoadTransportPreferredOrderAsync();
        RebuildCustomOrderPositions(savedOrder, loaded);

        _allMilitary = SortMilitaryRecords(DeduplicateMilitaryRecords(loaded)).ToList();

        await RebuildEffectiveTransportValuesAsync();

        var ranks = _allMilitary
            .Select(x => AtRankFormatter.CanonicalName(x.Rank))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(AtRankFormatter.GetOrder)
            .ThenBy(x => x)
            .ToList();

        _rankFilter.Items.Clear();
        _rankFilter.Items.Add("Todos os P/G");
        foreach (var rank in ranks) _rankFilter.Items.Add(rank);
        _rankFilter.SelectedIndex = 0;

        var previousCalculationRank = AtRankFormatter.CanonicalName(_calculatorRankBox.SelectedItem?.ToString());
        _calculatorRankBox.Items.Clear();
        foreach (var rank in ranks) _calculatorRankBox.Items.Add(rank);
        if (!string.IsNullOrWhiteSpace(previousCalculationRank) && _calculatorRankBox.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), previousCalculationRank, StringComparison.CurrentCultureIgnoreCase)))
            _calculatorRankBox.SelectedItem = _calculatorRankBox.Items.Cast<object>().First(x => string.Equals(x?.ToString(), previousCalculationRank, StringComparison.CurrentCultureIgnoreCase));
        else if (_calculatorRankBox.Items.Count > 0)
            _calculatorRankBox.SelectedIndex = 0;

        ApplyMilitaryFilter();
        await RefreshReportDaValuesAsync();
        RefreshReport();
        await LoadDaAsync();
        if (selectedId > 0)
        {
            var match = _visibleMilitary.FirstOrDefault(x => x.Id == selectedId);
            if (match is not null) _militaryGrid.SelectedItem = match;
        }
        if (selectedId <= 0) _militaryGrid.SelectedIndex = -1;
        _statusText.Text = $"{_allMilitary.Count} militar(es) carregados na tabela lateral. Ordem: {_orderSourceDescription}; sem repetição visual por reaproveitamento de célula.";
    }

    private async Task<IReadOnlyList<int>> LoadTransportPreferredOrderAsync()
    {
        var combined = new List<int>();
        var sources = new List<string>();

        void AddOrder(IEnumerable<int>? ids, string source)
        {
            if (ids is null) return;
            var before = combined.Count;
            foreach (var id in ids.Where(id => id > 0))
                if (!combined.Contains(id)) combined.Add(id);
            if (combined.Count > before) sources.Add(source);
        }

        try
        {
            var store = await App.Json.LoadAsync<MilitarySavedListStore>(_paths.NamedMilitaryListsFile);
            var lastList = store?.Lists?.FirstOrDefault(x => string.Equals(x.Id, store.LastOpenedListId, StringComparison.OrdinalIgnoreCase));
            AddOrder(lastList?.OrderedMilitaryIds, string.IsNullOrWhiteSpace(lastList?.Name) ? "última relação salva" : $"relação salva '{lastList.Name}'");
        }
        catch { }

        try { AddOrder(await App.MilitaryPreferences.LoadCustomOrderAsync(), "ordem salva do Listar Militares"); } catch { }

        try
        {
            var settings = await App.MilitaryPreferences.LoadListSettingsAsync();
            AddOrder(settings.CustomOrder, "ordem gravada nas preferências do Listar Militares");
        }
        catch { }

        _orderSourceDescription = sources.Count == 0
            ? "hierarquia do EB"
            : string.Join(" + complemento por ", sources.Distinct(StringComparer.OrdinalIgnoreCase));
        return combined;
    }

    private void RebuildCustomOrderPositions(IReadOnlyList<int> savedOrder, IReadOnlyList<MilitaryRecord> loaded)
    {
        _customOrderPositions.Clear();
        _customOrderIdentityPositions.Clear();
        var byId = loaded
            .Where(x => x.Id > 0)
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First());
        var orderIndex = 0;
        foreach (var id in savedOrder.Where(id => id > 0).Distinct())
        {
            if (!_customOrderPositions.ContainsKey(id)) _customOrderPositions[id] = orderIndex;
            if (byId.TryGetValue(id, out var record))
            {
                foreach (var key in MilitaryIdentityKeys(record))
                    if (!_customOrderIdentityPositions.ContainsKey(key)) _customOrderIdentityPositions[key] = orderIndex;
            }
            orderIndex++;
        }
    }

    private List<MilitaryRecord> DeduplicateMilitaryRecords(IEnumerable<MilitaryRecord> source)
    {
        return source
            .Select((record, index) => new { Record = record, Index = index, Key = MilitaryDeduplicationKey(record) })
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(x => CustomOrderPosition(x.Record))
                .ThenByDescending(x => CompletenessScore(x.Record))
                .ThenBy(x => x.Index)
                .Select(x => x.Record)
                .First())
            .ToList();
    }

    private int CustomOrderPosition(MilitaryRecord military)
    {
        if (_customOrderPositions.TryGetValue(military.Id, out var byId)) return byId;
        foreach (var key in MilitaryIdentityKeys(military))
            if (_customOrderIdentityPositions.TryGetValue(key, out var byIdentity)) return byIdentity;
        return int.MaxValue;
    }

    private static IEnumerable<string> MilitaryIdentityKeys(MilitaryRecord military)
    {
        var cpf = Digits(military.Cpf);
        if (cpf.Length >= 11 && cpf.Any(ch => ch != '0')) yield return "CPF:" + cpf;

        var prec = LettersAndDigits(military.PrecCp);
        if (!string.IsNullOrWhiteSpace(prec)) yield return "PREC:" + prec;

        var idt = LettersAndDigits(military.MilitaryId);
        if (!string.IsNullOrWhiteSpace(idt)) yield return "IDT:" + idt;

        var weak = WeakMilitaryNameKey(military);
        if (!string.IsNullOrWhiteSpace(weak)) yield return "NOME:" + weak;
    }

    private static string MilitaryDeduplicationKey(MilitaryRecord military)
    {
        foreach (var key in MilitaryIdentityKeys(military))
        {
            if (key.StartsWith("CPF:", StringComparison.Ordinal) || key.StartsWith("PREC:", StringComparison.Ordinal) || key.StartsWith("IDT:", StringComparison.Ordinal))
                return key;
        }

        var weak = WeakMilitaryNameKey(military);
        if (!string.IsNullOrWhiteSpace(weak)) return "NOME:" + weak;
        return "ID:" + military.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static string MilitaryIdentityKey(MilitaryRecord military) => MilitaryDeduplicationKey(military);

    private static string WeakMilitaryNameKey(MilitaryRecord military)
    {
        var name = Normalize(AtRankFormatter.CleanMilitaryName(military.Name, military.Rank));
        if (name.Length < 6) return string.Empty;
        var rank = AtRankFormatter.CanonicalName(military.Rank);
        var war = Normalize(military.WarName);
        var birth = Digits(military.BirthDate);
        var enlistment = Digits(military.EnlistmentDate);
        var formation = Digits(military.FormationYear);
        var address = Normalize(military.Address);

        // Só usa chave fraca quando há algum detalhe além do nome. Assim a tela não some com homônimos reais.
        if (string.IsNullOrWhiteSpace(war) && string.IsNullOrWhiteSpace(birth) && string.IsNullOrWhiteSpace(enlistment) && string.IsNullOrWhiteSpace(formation) && string.IsNullOrWhiteSpace(address))
            return string.Empty;
        return $"{rank}|{name}|{war}|{birth}|{enlistment}|{formation}|{address}";
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string LettersAndDigits(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private void PopulateMonthSelectors()
    {
        var current = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var months = Enumerable.Range(-24, 49)
            .Select(offset => FormatCompetenceDisplay(current.AddMonths(offset)))
            .ToList();

        _bulletinReferenceBox.ItemsSource = months;
        _bulletinReferenceBox.SelectedItem = FormatCompetenceDisplay(current);
        _bulletinMonthBox.Items.Clear();
        for (var month = 1; month <= 12; month++)
        {
            _bulletinMonthBox.Items.Add(new ComboBoxItem
            {
                Content = PtBr.DateTimeFormat.GetMonthName(month).ToUpperInvariant(),
                Tag = month
            });
        }
        _bulletinMonthBox.SelectedIndex = Math.Clamp(current.Month - 1, 0, 11);
        var bulletinYears = Enumerable.Range(current.Year - 4, 9)
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .ToList();
        _bulletinYearBox.ItemsSource = bulletinYears;
        _bulletinYearBox.SelectedItem = current.Year.ToString(CultureInfo.InvariantCulture);
        _bulletinYearBox.Text = current.Year.ToString(CultureInfo.InvariantCulture);
        SyncBulletinReferenceFromMonthYear();

        _daMonthBox.Items.Clear();
        for (var month = 1; month <= 12; month++)
        {
            _daMonthBox.Items.Add(new ComboBoxItem
            {
                Content = PtBr.DateTimeFormat.GetMonthName(month).ToUpperInvariant(),
                Tag = month
            });
        }
        _daMonthBox.SelectedIndex = Math.Clamp(current.Month - 1, 0, 11);

        var years = Enumerable.Range(current.Year - 2, 5).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
        _daYearBox.ItemsSource = years;
        _daYearBox.SelectedItem = current.Year.ToString(CultureInfo.InvariantCulture);
        _daYearBox.Text = current.Year.ToString(CultureInfo.InvariantCulture);

        _reportDocumentModeBox.ItemsSource = new[] { "CPF", "PREC-CP" };
        _reportDocumentModeBox.SelectedIndex = 0;

        _reportDaMonthBox.Items.Clear();
        for (var month = 1; month <= 12; month++)
        {
            _reportDaMonthBox.Items.Add(new ComboBoxItem
            {
                Content = PtBr.DateTimeFormat.GetMonthName(month).ToUpperInvariant(),
                Tag = month
            });
        }
        _reportDaMonthBox.SelectedIndex = Math.Clamp(current.Month - 1, 0, 11);
        _reportDaYearBox.ItemsSource = years.ToList();
        _reportDaYearBox.SelectedItem = current.Year.ToString(CultureInfo.InvariantCulture);
        _reportDaYearBox.Text = current.Year.ToString(CultureInfo.InvariantCulture);
    }

    private Grid BuildBulletinReferencePicker()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        _bulletinMonthBox.MinWidth = 128;
        _bulletinYearBox.Width = 92;
        _bulletinMonthBox.SelectionChanged += (_, _) => SyncBulletinReferenceFromMonthYear();
        _bulletinYearBox.SelectionChanged += (_, _) => SyncBulletinReferenceFromMonthYear();
        _bulletinYearBox.LostFocus += (_, _) => SyncBulletinReferenceFromMonthYear();
        row.Children.Add(_bulletinMonthBox);
        Grid.SetColumn(_bulletinYearBox, 2);
        row.Children.Add(_bulletinYearBox);
        return row;
    }

    private string CurrentBulletinReferenceText()
    {
        if (TryGetBulletinMonthYear(out var month, out var year))
            return FormatCompetenceDisplay(new DateTime(year, month, 1));
        return FormatCompetenceDisplay(_bulletinReferenceBox.Text);
    }

    private void SetCurrentBulletinReference(string? value)
    {
        var date = TryParseCompetence(value, out var parsed)
            ? parsed
            : DateTime.Today;
        if (_bulletinMonthBox.Items.Count > 0)
            _bulletinMonthBox.SelectedIndex = Math.Clamp(date.Month - 1, 0, 11);
        var yearText = date.Year.ToString(CultureInfo.InvariantCulture);
        _bulletinYearBox.SelectedItem = yearText;
        _bulletinYearBox.Text = yearText;
        _bulletinReferenceBox.Text = FormatCompetenceDisplay(date);
    }

    private void SyncBulletinReferenceFromMonthYear()
    {
        UpdateTransportReferenceHint();
        if (TryGetBulletinMonthYear(out var month, out var year))
            _bulletinReferenceBox.Text = FormatCompetenceDisplay(new DateTime(year, month, 1));
    }

    private bool TryGetBulletinMonthYear(out int month, out int year)
    {
        month = 0;
        year = 0;
        if (_bulletinMonthBox.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is int tag) month = tag;
            else int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out month);
        }
        if (month is < 1 or > 12) return false;
        var rawYear = (_bulletinYearBox.Text ?? _bulletinYearBox.SelectedItem?.ToString() ?? string.Empty).Trim();
        if (!int.TryParse(rawYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return false;
        if (year < 100) year += 2000;
        return year is >= 2000 and <= 2099;
    }

    private static int CompletenessScore(MilitaryRecord military)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(military.Name)) score += 20;
        if (!string.IsNullOrWhiteSpace(military.Rank)) score += 10;
        if (!string.IsNullOrWhiteSpace(military.Cpf)) score += 10;
        if (!string.IsNullOrWhiteSpace(military.PrecCp)) score += 10;
        if (!string.IsNullOrWhiteSpace(military.MilitaryId)) score += 5;
        if (!string.IsNullOrWhiteSpace(military.Address)) score += 5;
        if (MilitaryRecord.IsYes(military.ReceivesTransportAid)) score += 3;
        if (ParseMoney(military.TransportAidValue) > 0m) score += 3;
        return score;
    }

    private IEnumerable<MilitaryRecord> SortMilitaryRecords(IEnumerable<MilitaryRecord> source)
        => source
            .OrderBy(CustomOrderPosition)
            .ThenBy(x => AtRankFormatter.GetOrder(x.Rank))
            .ThenBy(x => Normalize(AtRankFormatter.CleanMilitaryName(x.Name, x.Rank)))
            .ThenBy(x => x.Id);

    private IEnumerable<AtBulletinQueueItem> SortBulletinQueueItems(IEnumerable<AtBulletinQueueItem> source)
        => source
            .OrderBy(x => CustomOrderPosition(x.Military))
            .ThenBy(x => AtRankFormatter.GetOrder(x.Military.Rank))
            .ThenBy(x => Normalize(AtRankFormatter.CleanMilitaryName(x.Military.Name, x.Military.Rank)))
            .ThenBy(x => x.Military.Id);

    private IEnumerable<AtDaRow> SortDaRows(IEnumerable<AtDaRow> source)
        => source
            .OrderBy(x => CustomOrderPosition(x.Military))
            .ThenBy(x => AtRankFormatter.GetOrder(x.Military.Rank))
            .ThenBy(x => Normalize(AtRankFormatter.CleanMilitaryName(x.Military.Name, x.Military.Rank)))
            .ThenBy(x => x.Military.Id);

    private decimal EffectiveNet(MilitaryRecord military)
    {
        if (military.IsAttached || !MilitaryRecord.IsYes(military.ReceivesTransportAid)) return 0m;
        return _effectiveNetByMilitaryId.TryGetValue(military.Id, out var value)
            ? value
            : ParseMoney(military.TransportAidValue);
    }

    private async Task RebuildEffectiveTransportValuesAsync()
    {
        _effectiveNetByMilitaryId.Clear();
        var fareSums = new Dictionary<int, decimal>();
        var salaries = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var connection = OpenMainDatabase();
            await connection.OpenAsync();

            await using (var faresCommand = connection.CreateCommand())
            {
                faresCommand.CommandText = "SELECT militar_id, COALESCE(SUM(tarifa),0) FROM aux_transporte_tarifas GROUP BY militar_id;";
                await using var reader = await faresCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    var sum = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                    if (id > 0) fareSums[id] = Math.Max(0m, sum);
                }
            }

            await using (var salaryCommand = connection.CreateCommand())
            {
                salaryCommand.CommandText = "SELECT posto, soldo FROM soldos_por_posto;";
                await using var reader = await salaryCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var rank = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var salary = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(rank) || salary <= 0) continue;
                    salaries[rank] = salary;
                    salaries[AtRankFormatter.CanonicalName(rank)] = salary;
                }
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Não foi possível recalcular todos os valores líquidos do Auxílio-Transporte.", ex);
        }

        foreach (var military in _allMilitary)
        {
            var days = military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22;
            var canonicalRank = AtRankFormatter.CanonicalName(military.Rank);
            var salary = salaries.TryGetValue(military.Rank ?? string.Empty, out var exactSalary)
                ? exactSalary
                : salaries.TryGetValue(canonicalRank, out var canonicalSalary)
                    ? canonicalSalary
                    : SalaryFallback.TryGetValue(canonicalRank, out var fallback)
                        ? fallback
                        : 0m;
            var fareSum = fareSums.TryGetValue(military.Id, out var storedFareSum) ? storedFareSum : 0m;
            var gross = fareSum > 0m
                ? Round(fareSum * 2m * days)
                : Round((decimal)(military.TransportGrossTotal ?? 0d));
            var share = Round(salary * 0.06m * (days / 30m));
            var storedNet = ParseMoney(military.TransportAidValue);
            var calculated = gross > 0m ? Math.Max(0m, Round(gross - share)) : storedNet;
            var net = military.IsAttached || !MilitaryRecord.IsYes(military.ReceivesTransportAid) ? 0m : calculated;
            _effectiveNetByMilitaryId[military.Id] = net;
            military.TransportAidValue = net.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private void ApplyMilitaryFilter()
    {
        var query = Normalize(_searchBox.Text);
        var rank = _rankFilter.SelectedItem?.ToString() ?? "Todos os P/G";
        var receive = _receiveFilter.SelectedItem?.ToString() ?? "Todos";
        var filtered = _allMilitary.Where(m =>
        {
            if (rank != "Todos os P/G" && !AtRankFormatter.CanonicalName(m.Rank).Equals(rank, StringComparison.CurrentCultureIgnoreCase)) return false;
            if (receive == "Recebe AT" && (!MilitaryRecord.IsYes(m.ReceivesTransportAid) || m.IsAttached)) return false;
            if (receive == "Não recebe" && (MilitaryRecord.IsYes(m.ReceivesTransportAid) || m.IsAttached)) return false;
            if (receive == "Adido/Encostado" && !m.IsAttached) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            return Normalize($"{m.Rank} {m.Name} {m.WarName} {m.Cpf} {m.PrecCp} {m.Address}").Contains(query, StringComparison.Ordinal);
        }).ToList();
        filtered = SortMilitaryRecords(filtered).ToList();
        ReplaceCollection(_visibleMilitary, filtered);
        _listCounter.Text = $"{filtered.Count} de {_allMilitary.Count}";
        UpdateHeaderMetrics(filtered);
    }

    private async Task MilitarySelectionChangedAsync()
    {
        if (_loadingSelection) return;
        if (_militaryGrid.SelectedItem is not MilitaryRecord selected) return;
        await LoadMilitaryAsync(selected);
        UpdateBulletinSelectionText();
        _statusText.Text = _tabs.SelectedIndex == 2
            ? $"{AtRankFormatter.ShortName(selected.Rank)} {selected.Name} selecionado. Dê duplo clique para adicioná-lo ao boletim escolhido."
            : $"Carregado: {AtRankFormatter.ShortName(selected.Rank)} {selected.Name}.";
    }

    private async Task HandleMilitaryGridDoubleClickAsync()
    {
        if (_militaryGrid.SelectedItem is not MilitaryRecord selected) return;
        if (_current?.Id != selected.Id) await LoadMilitaryAsync(selected);

        if (_tabs.SelectedIndex == 2)
        {
            var action = _bulletinActionBox.SelectedItem?.ToString();
            await AddCurrentCalculationToBulletinAsync(action, false, true);
            return;
        }

        await OpenWalletAsync();
    }

    private void ClearSelectedMilitary()
    {
        _loadingSelection = true;
        try
        {
            _current = null;
            _militaryGrid.UnselectAll();
            _militaryGrid.SelectedItem = null;
            _selectedTitle.Inlines.Clear();
            _selectedTitle.Inlines.Add(new Run("Calculadora livre por P/G"));
            _selectedSubtitle.Text = "Nenhum militar vinculado ao cálculo atual.";
            _selectedAddress.Text = "A rota, as tarifas, os dias úteis e o P/G permanecem na tela. Nada será salvo no banco enquanto um militar não for selecionado.";
            _selectedStatus.Text = "Modo livre ativo.";
            _listSelectedMilitaryText.Inlines.Clear();
            _listSelectedMilitaryText.Inlines.Add(new Run("Último selecionado: nenhum"));
            _routeSelectedMilitaryText.Inlines.Clear();
            _routeSelectedMilitaryText.Inlines.Add(new Run("Nenhum militar selecionado"));
            _selectedStatus.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
            _statusText.Text = "Militar desvinculado. A calculadora permanece disponível em modo livre.";
            UpdateBulletinSelectionText();
        }
        finally
        {
            _loadingSelection = false;
        }
    }

    private async Task<bool> AddCurrentCalculationToBulletinAsync(
        string? actionOverride = null,
        bool showConfirmation = false,
        bool warnOnFailure = true)
    {
        if (_current is null)
        {
            if (warnOnFailure)
                Warn("Selecione um militar antes de levar o cálculo ao boletim. Depois, você poderá limpar o militar e continuar usando a calculadora em modo livre.");
            else
                _statusText.Text = "Selecione um militar para levá-lo ao boletim.";
            return false;
        }
        var action = string.IsNullOrWhiteSpace(actionOverride)
            ? _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty
            : actionOverride.Trim();
        if (string.IsNullOrWhiteSpace(action) || !BulletinActions.Contains(action, StringComparer.OrdinalIgnoreCase))
        {
            if (warnOnFailure) Warn("Escolha o modelo do boletim antes de levar o militar.");
            else _statusText.Text = "Escolha o modelo do boletim antes de adicionar o militar.";
            return false;
        }

        // Mantém a aba Boletim e a aba Rotas apontando para o mesmo modelo.
        if (_bulletinActionBox.Items.Contains(action)) _bulletinActionBox.SelectedItem = action;
        if (_routeBulletinActionBox.Items.Contains(action)) _routeBulletinActionBox.SelectedItem = action;

        var allowsClickOnly = IsTransportSaqueAction(action)
            || IsTransportDifferenceAction(action)
            || IsTransportDevolucaoAction(action)
            || IsTransportDespesaAnularAction(action)
            || IsTransportExclusionAction(action);
        var isConcession = IsTransportImplantationAction(action);
        var currentHasFare = _routeBuses.Any(x => x.Fare > 0);
        if (isConcession && !currentHasFare)
        {
            var warning = $"{AtRankFormatter.ShortName(_current.Rank)} {_current.Name} está sem ônibus e sem valor de Auxílio-Transporte calculado. Cadastre ao menos uma linha, informe o valor da passagem e salve antes de incluí-lo na implantação.";
            Warn(warning);
            _statusText.Text = "Implantação não incluída: o militar ainda não possui rota e valor de AT.";
            return false;
        }
        if (isConcession && currentHasFare && !MilitaryRecord.IsYes(_current.ReceivesTransportAid) && showConfirmation)
        {
            if (!Confirm("Confirmar implantação", $"Este militar está marcado atualmente como NÃO recebe Auxílio-Transporte.\n\n{AtRankFormatter.ShortName(_current.Rank)} {_current.Name}\n\nAo continuar, o cálculo atual será levado para a implantação. Deseja prosseguir?"))
                return false;
        }
        AtCalculation calculation;
        List<AtBusLine> snapshotBuses;
        string origin;
        string destination;

        if (currentHasFare)
        {
            var days = ParsePositiveInt(_daysBox.Text, 22);
            var salary = await GetSalaryAsync(_current.Rank);
            var fareSum = _routeBuses.Sum(x => Math.Max(0m, x.Fare));
            var daily = Round(fareSum * 2m);
            var gross = Round(daily * days);
            var share = Round(salary * 0.06m * (days / 30m));
            calculation = new AtCalculation(days, salary, daily, gross, share, Math.Max(0m, Round(gross - share)));
            snapshotBuses = CloneBuses(_routeBuses);
            origin = _originBox.Text.Trim();
            destination = _destinationBox.Text.Trim();
        }
        else if (allowsClickOnly)
        {
            var state = await ReadTransportStateAsync(_current);
            var route = await ReadStoredRouteAsync(_current);
            calculation = state.Calculation;
            snapshotBuses = CloneBuses(route.Buses.Count > 0 ? route.Buses : state.Buses);
            origin = string.IsNullOrWhiteSpace(route.Origin) ? _current.Address ?? string.Empty : route.Origin;
            destination = string.IsNullOrWhiteSpace(route.Destination) ? DefaultDestination : route.Destination;
        }
        else
        {
            if (warnOnFailure)
                Warn("Adicione ao menos uma linha e o valor da passagem antes de levar o militar a este modelo de boletim.");
            else
                _statusText.Text = $"{AtRankFormatter.ShortName(_current.Rank)} {_current.Name} não foi levado: cadastre a rota e o valor da passagem.";
            return false;
        }

        var competenceForThisMilitary = CaptureBulletinCompetencesForAction(action);
        if (IsTransportMultiCompetenceAction(action) && string.IsNullOrWhiteSpace(competenceForThisMilitary))
        {
            if (warnOnFailure) Warn("Informe a(s) competência(s) deste militar antes de levá-lo ao saque/devolução. Use o botão + para adicionar mais de um mês.");
            else _statusText.Text = "Competência individual não informada.";
            return false;
        }
        var receivedValue = 0m;
        if (IsTransportDifferenceAction(action))
        {
            receivedValue = ParseMoney(_bulletinReceivedValueBox.Text);
            if (receivedValue < 0m || string.IsNullOrWhiteSpace(_bulletinReceivedValueBox.Text))
            {
                Warn("Informe o valor efetivamente recebido por este militar na competência.");
                _bulletinReceivedValueBox.Focus();
                return false;
            }
            if (calculation.Net <= 0m || calculation.DailyGross <= 0m)
            {
                Warn("A SAT deste militar ainda não possui valor total e valor diário válidos. Salve ou recalcule a SAT antes de gerar a diferença.");
                return false;
            }
            if (receivedValue >= calculation.Net)
            {
                Warn($"Não existe diferença positiva a pagar. Valor da SAT: {FormatMoney(calculation.Net)}; valor recebido: {FormatMoney(receivedValue)}.");
                return false;
            }
        }

        _bulletinQueueAll.RemoveAll(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase) && x.Military.Id == _current.Id);
        _bulletinQueueAll.Add(new AtBulletinQueueItem
        {
            Action = action,
            Military = _current,
            Calculation = calculation,
            Buses = snapshotBuses,
            Origin = origin,
            Destination = destination,
            CompetencesText = competenceForThisMilitary,
            ReceivedValue = receivedValue,
            AddedAt = DateTime.Now
        });
        // Não sobrescreve uma rota antiga com lista vazia em saque/exclusão por clique.
        var routeSaveWarning = string.Empty;
        if (currentHasFare)
        {
            try
            {
                await SaveRouteAsync(false);
            }
            catch (Exception ex)
            {
                // A fila já possui uma fotografia íntegra do cálculo e das linhas. Uma
                // falha no banco secundário de rotas não deve perder a inclusão feita
                // pelo usuário nem escapar como erro global da interface.
                routeSaveWarning = " A pessoa foi mantida no boletim, mas a rota não pôde ser gravada novamente; use ‘Salvar rota’ para tentar de novo.";
                await _log.WriteAsync($"Militar incluído no boletim de Auxílio-Transporte, mas a rota não pôde ser persistida: {_current.Name}.", ex);
            }
        }
        if (IsTransportDifferenceAction(action))
        {
            _bulletinReceivedValueBox.Clear();
            _statusText.Text = $"{AtRankFormatter.ShortName(_current.Rank)} {_current.Name} incluído em '{action}'. Informe o valor recebido do próximo militar.";
        }
        else if (IsTransportMultiCompetenceAction(action))
        {
            _bulletinRequiredDetailsBox.Clear();
            _statusText.Text = $"{AtRankFormatter.ShortName(_current.Rank)} {_current.Name} levado para '{action}'. Competências limpas para o próximo militar.";
        }
        else
        {
            _statusText.Text = $"{AtRankFormatter.ShortName(_current.Rank)} {_current.Name} levado para '{action}'.{routeSaveWarning}";
        }
        RefreshBulletinQueue();
        return true;
    }

    private async Task AddSelectedMilitaryToBulletinAsync()
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(action))
        {
            Warn("Escolha o modelo do boletim.");
            return;
        }

        var selected = SortMilitaryRecords(GetSelectedMilitary()
            .GroupBy(MilitaryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
            .ToList();
        if (selected.Count == 0)
        {
            Warn("Marque um ou mais militares na lista lateral.");
            return;
        }
        if (IsTransportDifferenceAction(action) && selected.Count > 1)
        {
            Warn("Na nota de diferença, adicione um militar por vez, pois cada pessoa possui um valor recebido próprio.");
            return;
        }

        var competenceForSelected = CaptureBulletinCompetencesForAction(action);
        if (IsTransportMultiCompetenceAction(action) && string.IsNullOrWhiteSpace(competenceForSelected))
        {
            Warn("Informe a(s) competência(s) do militar antes de adicioná-lo ao saque/devolução. Para militares com meses diferentes, adicione um por vez.");
            _bulletinRequiredDetailsBox.Focus();
            return;
        }
        var receivedValue = 0m;
        if (IsTransportDifferenceAction(action))
        {
            receivedValue = ParseMoney(_bulletinReceivedValueBox.Text);
            if (string.IsNullOrWhiteSpace(_bulletinReceivedValueBox.Text))
            {
                Warn("Informe o valor efetivamente recebido pelo militar selecionado.");
                _bulletinReceivedValueBox.Focus();
                return;
            }
        }

        var added = 0;
        var skipped = new List<string>();
        foreach (var military in selected)
        {
            try
            {
                var state = await ReadTransportStateAsync(military);
                var route = await ReadStoredRouteAsync(military);
                var buses = route.Buses.Count > 0 ? route.Buses : state.Buses;
                var needsTransportValue = IsTransportImplantationAction(action)
                    || IsTransportUpdateAction(action)
                    || IsTransportDifferenceAction(action);
                if (needsTransportValue && (buses.Count == 0 || state.Calculation.Net <= 0m))
                {
                    skipped.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name}");
                    continue;
                }
                if (IsTransportDifferenceAction(action) && receivedValue >= state.Calculation.Net)
                {
                    skipped.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name} — sem diferença positiva");
                    continue;
                }

                _bulletinQueueAll.RemoveAll(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase) && x.Military.Id == military.Id);
                _bulletinQueueAll.Add(new AtBulletinQueueItem
                {
                    Action = action,
                    Military = military,
                    Calculation = state.Calculation,
                    Buses = CloneBuses(buses),
                    Origin = string.IsNullOrWhiteSpace(route.Origin) ? military.Address ?? string.Empty : route.Origin,
                    Destination = string.IsNullOrWhiteSpace(route.Destination) ? DefaultDestination : route.Destination,
                    CompetencesText = competenceForSelected,
                    ReceivedValue = receivedValue,
                    AddedAt = DateTime.Now
                });
                added++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name}");
                await _log.WriteAsync($"Falha ao adicionar {military.Name} ao boletim de Auxílio-Transporte.", ex);
            }
        }

        RefreshBulletinQueue();
        if (IsTransportDifferenceAction(action) && added > 0)
            _bulletinReceivedValueBox.Clear();
        if (IsTransportMultiCompetenceAction(action) && selected.Count == 1 && added == 1)
        {
            _bulletinRequiredDetailsBox.Clear();
            _statusText.Text = skipped.Count == 0
                ? $"{added} militar adicionado a '{action}'. Competências limpas para o próximo militar."
                : $"{added} adicionado e {skipped.Count} ignorado por falta de rota/valor salvo.";
            return;
        }
        _statusText.Text = skipped.Count == 0
            ? $"{added} militar(es) adicionados a '{action}'."
            : $"{added} adicionados e {skipped.Count} ignorados por falta de rota/valor salvo.";
    }

    private void ConfigureBulletinQueueGrid()
    {
        _bulletinQueueGrid.AutoGenerateColumns = false;
        _bulletinQueueGrid.IsReadOnly = true;
        _bulletinQueueGrid.SelectionMode = DataGridSelectionMode.Extended;
        _bulletinQueueGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _bulletinQueueGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _bulletinQueueGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _bulletinQueueGrid.RowHeight = 36;
        _bulletinQueueGrid.ColumnHeaderHeight = 34;
        _bulletinQueueGrid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 251, 255));
        _bulletinQueueGrid.RowStyle = BuildProfessionalMilitaryRowStyle("Military.");
        _bulletinQueueGrid.ItemsSource = _bulletinQueueView;
        ScrollViewer.SetHorizontalScrollBarVisibility(_bulletinQueueGrid, ScrollBarVisibility.Auto);
        var centerCellStyle = new Style(typeof(TextBlock));
        centerCellStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        centerCellStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        var moneyCellStyle = new Style(typeof(TextBlock));
        moneyCellStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        moneyCellStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        moneyCellStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));

        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(AtBulletinQueueItem.Position)), Width = 44, MinWidth = 40, ElementStyle = centerCellStyle });
        _bulletinQueueGrid.Columns.Add(new AtRankDataGridColumn { Header = "P/G", Width = 76, MinWidth = 68, SortMemberPath = "Military.Rank" });
        _bulletinQueueGrid.Columns.Add(new WarNameDataGridColumn { Header = "Nome", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 220, SortMemberPath = "Military.Name" });
        _bulletinQueueGrid.Columns.Add(new AtReceiveFlagDataGridColumn { Header = "AT", Width = 50, MinWidth = 46, SortMemberPath = "Military.ReceivesTransportAid" });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Folha → SIPPES", Binding = new Binding(nameof(AtBulletinQueueItem.CompetenceMappingDisplay)), Width = 190, MinWidth = 150 });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Dias", Binding = new Binding(nameof(AtBulletinQueueItem.DaysText)), Width = 58, MinWidth = 54, ElementStyle = centerCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Ida/volta dia", Binding = new Binding(nameof(AtBulletinQueueItem.DailyFormatted)), Width = 112, MinWidth = 104, ElementStyle = moneyCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "S/ cota-parte", Binding = new Binding(nameof(AtBulletinQueueItem.MonthGrossFormatted)), Width = 116, MinWidth = 108, ElementStyle = moneyCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Cota-parte", Binding = new Binding(nameof(AtBulletinQueueItem.ShareFormatted)), Width = 108, MinWidth = 100, ElementStyle = moneyCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new Binding(nameof(AtBulletinQueueItem.NetFormatted)), Width = 110, MinWidth = 102, ElementStyle = moneyCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Recebido", Binding = new Binding(nameof(AtBulletinQueueItem.ReceivedFormatted)), Width = 105, MinWidth = 96, ElementStyle = moneyCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Diferença", Binding = new Binding(nameof(AtBulletinQueueItem.DifferenceFormatted)), Width = 105, MinWidth = 96, ElementStyle = moneyCellStyle });
        _bulletinQueueGrid.Columns.Add(new DataGridTextColumn { Header = "Incluído", Binding = new Binding(nameof(AtBulletinQueueItem.AddedText)), Width = 78, MinWidth = 72, ElementStyle = centerCellStyle });
        _bulletinQueueGrid.Sorting += (_, e) => HandleRankSorting(
            _bulletinQueueGrid,
            e,
            direction =>
            {
                var ordered = direction == ListSortDirection.Ascending
                    ? _bulletinQueueView.OrderBy(x => AtRankFormatter.GetOrder(x.Military.Rank)).ThenBy(x => Normalize(x.Military.Name))
                    : _bulletinQueueView.OrderByDescending(x => AtRankFormatter.GetOrder(x.Military.Rank)).ThenBy(x => Normalize(x.Military.Name));
                ReplaceCollection(_bulletinQueueView, ordered.ToList());
                for (var index = 0; index < _bulletinQueueView.Count; index++) _bulletinQueueView[index].Position = index + 1;
                _bulletinQueueGrid.Items.Refresh();
            });
        _bulletinQueueGrid.MouseDoubleClick += async (_, _) => await RunModuleActionAsync(
            "Não foi possível carregar o militar selecionado da fila. A tela continuará aberta.",
            LoadSelectedBulletinQueueMilitaryAsync);
    }

    private void RefreshBulletinQueue()
    {
        if (_bulletinActionBox.SelectedItem is null) return;
        var action = _bulletinActionBox.SelectedItem.ToString() ?? string.Empty;
        var items = SortBulletinQueueItems(_bulletinQueueAll
            .Where(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => MilitaryIdentityKey(x.Military), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.AddedAt).First()))
            .ToList();
        for (var index = 0; index < items.Count; index++) items[index].Position = index + 1;
        ReplaceCollection(_bulletinQueueView, items);
        _bulletinQueueCounter.Text = items.Count == 1 ? "1 militar" : $"{items.Count} militares";
        _bulletinSelectionText.Text = items.Count > 0
            ? IsTransportDifferenceAction(action)
                ? $"Nota de diferença com {items.Count} militar(es). A fila mostra o valor da SAT, o recebido e o saldo calculado de cada pessoa."
                : $"Este modelo possui {items.Count} militar(es). Cada um sairá com competência, dias e valores próprios."
            : IsTransportDifferenceAction(action)
                ? "Escolha a competência, informe o valor recebido e adicione um militar por vez. O restante virá automaticamente da SAT."
                : "Nenhum militar neste modelo. Calcule/salve a rota ou marque militares e clique em ‘Adicionar marcados’.";
    }

    private void RemoveSelectedBulletinQueueItems()
    {
        var selected = _bulletinQueueGrid.SelectedItems.Cast<object>().OfType<AtBulletinQueueItem>().ToList();
        if (selected.Count == 0) return;
        foreach (var item in selected) _bulletinQueueAll.Remove(item);
        RefreshBulletinQueue();
    }

    private void ClearCurrentBulletinQueue()
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        var count = _bulletinQueueAll.Count(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        if (count == 0) return;
        if (!Confirm("Limpar modelo", $"Remover os {count} militar(es) guardados em ‘{action}’?")) return;
        _bulletinQueueAll.RemoveAll(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        RefreshBulletinQueue();
    }

    private async Task LoadSelectedBulletinQueueMilitaryAsync()
    {
        if (_bulletinQueueGrid.SelectedItem is not AtBulletinQueueItem item) return;
        var visible = _visibleMilitary.FirstOrDefault(x => x.Id == item.Military.Id);
        if (visible is null)
        {
            _searchBox.Clear();
            _rankFilter.SelectedIndex = 0;
            _receiveFilter.SelectedIndex = 0;
            ApplyMilitaryFilter();
            visible = _visibleMilitary.FirstOrDefault(x => x.Id == item.Military.Id);
        }
        if (visible is not null)
        {
            _militaryGrid.SelectedItem = visible;
            _militaryGrid.ScrollIntoView(visible);
        }
        else
        {
            await LoadMilitaryAsync(item.Military);
        }
        if (IsTransportDifferenceAction(item.Action))
            _bulletinReceivedValueBox.Text = item.ReceivedValue.ToString("N2", PtBr);
        _statusText.Text = $"Militar carregado da lista do boletim: {AtRankFormatter.ShortName(item.Military.Rank)} {item.Military.Name}.";
    }

    private void UpdateActiveMilitaryIndicators(MilitaryRecord military)
    {
        SetMilitaryName(_listSelectedMilitaryText, military, "Último selecionado: " + AtRankFormatter.ShortName(military.Rank) + " ", true);
        SetMilitaryName(_routeSelectedMilitaryText, military, AtRankFormatter.ShortName(military.Rank) + " ", true);
    }

    private static Style BuildPersistentSelectedRowStyle()
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(218, 235, 255))));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(8, 55, 120))));
        selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(25, 103, 210))));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(4, 0, 0, 0)));
        style.Triggers.Add(selected);
        return style;
    }

    private static List<AtBusLine> CloneBuses(IEnumerable<AtBusLine> buses) => buses.Select(x => new AtBusLine
    {
        Index = x.Index,
        Number = x.Number,
        Name = x.Name,
        Category = x.Category,
        Fare = x.Fare,
        SourceUrl = x.SourceUrl
    }).ToList();

    private static Style BuildProfessionalMilitaryRowStyle(string propertyPrefix = "")
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));

        var receives = new DataTrigger
        {
            Binding = new Binding(propertyPrefix + nameof(MilitaryRecord.TransportStatus)),
            Value = "Recebe"
        };
        receives.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(244, 252, 247))));
        receives.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(70, 165, 111))));
        style.Triggers.Add(receives);

        var blocked = new DataTrigger
        {
            Binding = new Binding(propertyPrefix + nameof(MilitaryRecord.TransportStatus)),
            Value = "Bloqueado"
        };
        blocked.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 248, 232))));
        blocked.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(219, 147, 31))));
        style.Triggers.Add(blocked);

        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(218, 235, 255))));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(8, 55, 120))));
        selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(25, 103, 210))));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(4, 0, 0, 0)));
        style.Triggers.Add(selected);
        return style;
    }

    private async Task LoadMilitaryAsync(MilitaryRecord military)
    {
        _loadingSelection = true;
        try
        {
            _current = military;
            SetMilitaryName(_selectedTitle, military, AtRankFormatter.ShortName(military.Rank) + " ", true);
            _selectedSubtitle.Text = $"CPF: {military.FormattedCpf}   •   PREC-CP: {Blank(military.PrecCp)}";
            _selectedAddress.Text = "Endereço: " + Blank(military.Address);
            _selectedStatus.Text = military.IsAttached
                ? "Auxílio-Transporte bloqueado: militar Adido/Encostado."
                : $"Situação atual: {military.TransportStatus} — {FormatMoney(ParseMoney(military.TransportAidValue))}.";
            _selectedStatus.SetResourceReference(TextBlock.ForegroundProperty, military.IsAttached ? "DangerBrush" : "SuccessBrush");
            UpdateActiveMilitaryIndicators(military);
            _daysBox.Text = (military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22).ToString(PtBr);
            if (_calculatorRankBox.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), military.Rank, StringComparison.CurrentCultureIgnoreCase)))
                _calculatorRankBox.SelectedItem = _calculatorRankBox.Items.Cast<object>().First(x => string.Equals(x?.ToString(), military.Rank, StringComparison.CurrentCultureIgnoreCase));
            _originBox.Text = military.Address ?? string.Empty;
            _routeCepBox.Text = PlanCallService.FormatZipCode(military.ZipCode);
            await LoadTransportAndRouteAsync(military);
            await RecalculateAsync();
            _statusText.Text = $"Carregado: {AtRankFormatter.ShortName(military.Rank)} {military.Name}.";
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Falha ao carregar os dados de Auxílio-Transporte.", ex);
        }
        finally { _loadingSelection = false; }
    }

    private async Task LoadTransportAndRouteAsync(MilitaryRecord military)
    {
        _routeBuses.Clear();
        await EnsureTransportFareMetadataColumnsAsync();
        await using (var connection = OpenMainDatabase())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT idx,tarifa,COALESCE(linha,''),COALESCE(nome,''),COALESCE(categoria,''),COALESCE(url,'') FROM aux_transporte_tarifas WHERE militar_id=$id ORDER BY idx;";
            command.Parameters.AddWithValue("$id", military.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                _routeBuses.Add(new AtBusLine
                {
                    Index = reader.GetInt32(0),
                    Fare = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetDouble(1), CultureInfo.InvariantCulture),
                    Number = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Category = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    SourceUrl = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                });
            }
        }

        await using var routeDb = OpenRouteDatabase();
        await routeDb.OpenAsync();
        await using var route = routeDb.CreateCommand();
        route.CommandText = "SELECT origem,destino,dias_uteis,linhas_json,print_path FROM rotas_militar WHERE militar_id=$id LIMIT 1;";
        route.Parameters.AddWithValue("$id", military.Id);
        await using var rr = await route.ExecuteReaderAsync();
        if (await rr.ReadAsync())
        {
            var origin = rr.IsDBNull(0) ? string.Empty : rr.GetString(0);
            var destination = rr.IsDBNull(1) ? string.Empty : rr.GetString(1);
            var days = rr.IsDBNull(2) ? 0 : rr.GetInt32(2);
            var json = rr.IsDBNull(3) ? string.Empty : rr.GetString(3);
            _currentPrintPath = rr.IsDBNull(4) ? string.Empty : rr.GetString(4);
            if (!string.IsNullOrWhiteSpace(origin)) _originBox.Text = origin;
            if (!string.IsNullOrWhiteSpace(destination)) _destinationBox.Text = destination;
            if (days > 0) _daysBox.Text = days.ToString(PtBr);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<List<AtBusLine>>(json) ?? new List<AtBusLine>();
                    if (saved.Count > 0)
                    {
                        _routeBuses.Clear();
                        foreach (var item in saved.OrderBy(x => x.Index)) _routeBuses.Add(item);
                    }
                }
                catch { }
            }
        }
        else
        {
            _currentPrintPath = string.Empty;
            _destinationBox.Text = DefaultDestination;
        }

        await MergeDetailedRouteAsync(military, _routeBuses);
        ReindexBuses();
        UpdatePrintStatus();
    }

    private async Task RecalculateAsync()
    {
        var rank = _calculatorRankBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(rank)) rank = _current?.Rank;
        if (string.IsNullOrWhiteSpace(rank))
        {
            RenderCalculation(AtCalculation.Empty);
            return;
        }
        var days = ParsePositiveInt(_daysBox.Text, 22);
        var salary = await GetSalaryAsync(rank);
        var fareSum = _routeBuses.Sum(x => Math.Max(0m, x.Fare));
        var daily = Round(fareSum * 2m);
        var gross = Round(daily * days);
        var share = Round(salary * 0.06m * (days / 30m));
        // A calculadora por P/G é independente do cadastro. O bloqueio de Adido/Encostado
        // continua sendo aplicado somente quando os dados são efetivamente salvos no militar.
        var net = Math.Max(0m, Round(gross - share));
        _currentCalculation = new AtCalculation(days, salary, daily, gross, share, net);
        RenderCalculation(_currentCalculation);
        UpdateBulletinDifferencePreview();
    }

    private void RenderCalculation(AtCalculation calc)
    {
        var sixPercent = Round(calc.Salary * 0.06m);
        var sixPercentDaily = Round(sixPercent / 30m);
        _dailyValue.Text = FormatMoney(calc.DailyGross);
        _grossValue.Text = FormatMoney(calc.MonthGross);
        _salaryValue.Text = FormatMoney(calc.Salary);
        _salarySixPercentValue.Text = FormatMoney(sixPercent);
        _salarySixPercentDailyValue.Text = FormatMoney(sixPercentDaily);
        _shareValue.Text = FormatMoney(calc.Share);
        _netValue.Text = FormatMoney(calc.Net);
    }

    private void AddManualFare()
    {
        var number = NormalizeBusLine(_manualLineBox.Text);
        if (string.IsNullOrWhiteSpace(number))
        {
            Warn("Informe o número da linha. Para metrô ou outro meio de transporte, use uma identificação clara, por exemplo: METRÔ.");
            _manualLineBox.Focus();
            return;
        }
        var fare = ParseMoney(_manualFareBox.Text);
        if (fare <= 0)
        {
            Warn("Informe o valor de UMA passagem, maior que zero.");
            _manualFareBox.Focus();
            return;
        }
        _routeBuses.Add(new AtBusLine
        {
            Index = _routeBuses.Count,
            Number = number,
            Name = number.Equals("METRO", StringComparison.OrdinalIgnoreCase) ? "Metrô" : $"Linha {number}",
            Category = "Informado manualmente",
            Fare = fare
        });
        _manualLineBox.Clear();
        _routeSaveStatus.Text = $"{number} adicionado: {FormatMoney(fare)} por passagem; {FormatMoney(fare * 2m)} por dia (ida e volta).";
        _ = RecalculateAsync();
    }

    private void AddLookupToRoute()
    {
        var number = NormalizeBusLine(_lineNumberBox.Text);
        if (string.IsNullOrWhiteSpace(number))
        {
            Warn("Informe o número exato da linha antes de adicionar à rota.");
            _lineNumberBox.Focus();
            return;
        }
        var fare = ParseMoney(_lookupFareBox.Text);
        if (fare <= 0)
        {
            Warn("Pesquise a linha ou informe o valor de UMA passagem antes de adicionar.");
            _lookupFareBox.Focus();
            return;
        }
        var lookupMatches = _lastLookup is not null && _lastLookup.Line.Equals(number, StringComparison.OrdinalIgnoreCase);
        _routeBuses.Add(new AtBusLine
        {
            Index = _routeBuses.Count,
            Number = number,
            Name = lookupMatches && !string.IsNullOrWhiteSpace(_lastLookup!.Name) ? _lastLookup.Name : $"Linha {number}",
            Category = lookupMatches && !string.IsNullOrWhiteSpace(_lastLookup!.Category)
                ? _lastLookup.Category
                : (_lineCategoryBox.SelectedItem?.ToString() ?? string.Empty),
            Fare = fare,
            SourceUrl = lookupMatches ? _lastLookup!.Url : string.Empty
        });
        _routeSaveStatus.Text = $"Linha {number} adicionada: {FormatMoney(fare)} por passagem; {FormatMoney(fare * 2m)} por dia (ida e volta). Salve para vincular à carteira.";
        _ = RecalculateAsync();
    }

    private void RemoveSelectedBus()
    {
        if (_busGrid.SelectedItem is not AtBusLine item) return;
        _routeBuses.Remove(item);
        ReindexBuses();
        _ = RecalculateAsync();
    }

    private void ClearBuses()
    {
        if (_routeBuses.Count == 0) return;
        if (!Confirm("Limpar tarifas", "Remover todos os ônibus da tela atual?")) return;
        _routeBuses.Clear();
        _ = RecalculateAsync();
    }

    private async Task SaveCurrentTransportFromButtonAsync()
    {
        if (_savingTransportRoute) return;
        try
        {
            _savingTransportRoute = true;
            IsBusy = true;
            BusyMessage = "Salvando cálculo, linhas e rota…";
            if (!CommitRouteGridEdits()) return;
            await SaveCurrentTransportAsync();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Não foi possível salvar o Auxílio-Transporte. A tela continuará aberta e nenhum dado incompleto será confirmado.", ex);
        }
        finally
        {
            IsBusy = false;
            _savingTransportRoute = false;
        }
    }

    private async Task SaveRouteFromButtonAsync()
    {
        if (_savingTransportRoute) return;
        try
        {
            _savingTransportRoute = true;
            IsBusy = true;
            BusyMessage = "Salvando a rota alterada…";
            if (!CommitRouteGridEdits()) return;
            await SaveRouteAsync();
        }
        catch (Exception ex)
        {
            _routeSaveStatus.Text = "A rota não foi alterada. Confira os campos e tente novamente.";
            await ReportErrorAsync("Não foi possível salvar a rota alterada. O Auxílio-Transporte continuará aberto.", ex);
        }
        finally
        {
            IsBusy = false;
            _savingTransportRoute = false;
        }
    }

    private bool CommitRouteGridEdits()
    {
        if (!_busGrid.CommitEdit(DataGridEditingUnit.Cell, true)
            || !_busGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            _routeSaveStatus.Text = "Existe um valor inválido na linha que está sendo editada.";
            Warn("Confira o número, o nome e principalmente o valor da passagem. Use um valor como 5,25.");
            return false;
        }

        for (var index = 0; index < _routeBuses.Count; index++)
            _routeBuses[index].Index = index;
        return true;
    }

    private async Task SaveCurrentTransportAsync(bool showConfirmation = true)
    {
        if (_current is null) { Warn("Selecione um militar."); return; }
        if (_current.IsAttached)
        {
            Warn("O militar está marcado como Adido/Encostado. O benefício permanecerá bloqueado e com valor zero.");
        }
        await RecalculateAsync();
        await SaveTransportStateAsync(_current, _routeBuses.ToList(), _currentCalculation.Days);
        await SaveRouteAsync(false);
        var savedLines = string.Join("\n", _routeBuses.Select(x => "• " + x.ClearSummary));
        if (showConfirmation) Info($"Cálculo, rota e ônibus salvos no banco oficial.\n\n{savedLines}\n\nO número, a descrição e o valor de cada linha ficam vinculados ao militar e são exibidos na carteira.");
        _statusText.Text = $"Auxílio-Transporte salvo para {AtRankFormatter.ShortName(_current.Rank)} {_current.Name}: {_routeBuses.Count} linha(s) detalhada(s).";
        await RefreshMilitaryRecordAsync(_current.Id);
    }

    private async Task SaveTransportStateAsync(MilitaryRecord military, IReadOnlyList<AtBusLine> buses, int days)
    {
        await EnsureTransportFareMetadataColumnsAsync();
        var salary = await GetSalaryAsync(military.Rank);
        var fareSum = buses.Sum(x => Math.Max(0m, x.Fare));
        var gross = Round(fareSum * 2m * Math.Max(0, days));
        var share = Round(salary * 0.06m * (Math.Max(0, days) / 30m));
        var net = military.IsAttached ? 0m : Math.Max(0m, Round(gross - share));
        var receives = !military.IsAttached && fareSum > 0 && gross > 0;

        await using var connection = OpenMainDatabase();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = (SqliteTransaction)transaction;
                clear.CommandText = "DELETE FROM aux_transporte_tarifas WHERE militar_id=$id;";
                clear.Parameters.AddWithValue("$id", military.Id);
                await clear.ExecuteNonQueryAsync();
            }
            for (var i = 0; i < buses.Count; i++)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = "INSERT INTO aux_transporte_tarifas(militar_id,idx,tarifa,linha,nome,categoria,url) VALUES($id,$idx,$fare,$linha,$nome,$categoria,$url);";
                insert.Parameters.AddWithValue("$id", military.Id);
                insert.Parameters.AddWithValue("$idx", i);
                insert.Parameters.AddWithValue("$fare", (double)buses[i].Fare);
                insert.Parameters.AddWithValue("$linha", buses[i].Number ?? string.Empty);
                insert.Parameters.AddWithValue("$nome", buses[i].Name ?? string.Empty);
                insert.Parameters.AddWithValue("$categoria", buses[i].Category ?? string.Empty);
                insert.Parameters.AddWithValue("$url", buses[i].SourceUrl ?? string.Empty);
                await insert.ExecuteNonQueryAsync();
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = "UPDATE militares SET recebe_aux_transporte=$recebe,valor_aux_transporte=$valor,aux_total_bruto=$bruto,aux_dias_uteis=$dias,aux_base_ts=$ts WHERE id=$id;";
                update.Parameters.AddWithValue("$recebe", receives ? "Sim" : "Não");
                update.Parameters.AddWithValue("$valor", net.ToString("0.00", CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$bruto", (double)gross);
                update.Parameters.AddWithValue("$dias", Math.Max(0, days));
                update.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$id", military.Id);
                await update.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        military.ReceivesTransportAid = receives ? "Sim" : "Não";
        military.TransportAidValue = net.ToString("0.00", CultureInfo.InvariantCulture);
        _effectiveNetByMilitaryId[military.Id] = net;
        military.TransportGrossTotal = (double)gross;
        military.TransportWorkingDays = Math.Max(0, days);
        military.TransportBaseTimestamp = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
    }

    private async Task SaveRouteAsync(bool showMessage = true)
    {
        if (_current is null) { if (showMessage) Warn("Selecione um militar."); return; }
        var origin = _originBox.Text.Trim();
        var destination = _destinationBox.Text.Trim();
        var days = ParsePositiveInt(_daysBox.Text, 22);
        var buses = _routeBuses.ToList();
        await SaveRouteRecordsAsync(_current.Id, origin, destination, days, buses, _currentPrintPath);

        var addressWarning = string.Empty;
        try
        {
            await SaveOriginAsMilitaryAddressAsync(_current, origin);
        }
        catch (Exception ex)
        {
            addressWarning = " A rota foi salva, mas o endereço do cadastro do militar não pôde ser atualizado.";
            await _log.WriteAsync("A rota foi salva, mas falhou a sincronização do endereço residencial.", ex);
        }

        var lines = string.Join("; ", buses.Select(x => x.ClearSummary));
        _routeSaveStatus.Text = $"Rota salva em {DateTime.Now:dd/MM/yyyy HH:mm}. Linhas: {(string.IsNullOrWhiteSpace(lines) ? "nenhuma" : lines)}.{addressWarning}";
        if (showMessage)
        {
            var addressMessage = string.IsNullOrWhiteSpace(addressWarning)
                ? string.Empty
                : "\n\nA rota foi preservada, mas o endereço geral do cadastro não foi atualizado.";
            Info($"Rota e ônibus salvos para o militar selecionado.\n\n{(string.IsNullOrWhiteSpace(lines) ? "Nenhuma linha foi cadastrada." : lines)}{addressMessage}");
        }
    }

    private async Task SaveRouteRecordsAsync(
        int militaryId,
        string origin,
        string destination,
        int days,
        IReadOnlyList<AtBusLine> buses,
        string? printPath)
    {
        await EnsureRouteDatabaseAsync();
        var normalizedOrigin = origin ?? string.Empty;
        var normalizedDestination = destination ?? string.Empty;
        var json = JsonSerializer.Serialize(buses);
        await using var connection = OpenRouteDatabase();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO rotas_militar(militar_id,origem,destino,dias_uteis,linhas_json,print_path,updated_at)
                    VALUES($id,$origem,$destino,$dias,$json,$print,$ts)
                    ON CONFLICT(militar_id) DO UPDATE SET origem=excluded.origem,destino=excluded.destino,dias_uteis=excluded.dias_uteis,
                        linhas_json=excluded.linhas_json,print_path=excluded.print_path,updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$id", militaryId);
                command.Parameters.AddWithValue("$origem", normalizedOrigin);
                command.Parameters.AddWithValue("$destino", normalizedDestination);
                command.Parameters.AddWithValue("$dias", Math.Max(1, days));
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$print", printPath ?? string.Empty);
                command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync();
            }

            await SaveLegacyCompatibleRouteAsync(connection, (SqliteTransaction)transaction, militaryId, normalizedOrigin, normalizedDestination, buses);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task SaveOriginAsMilitaryAddressAsync(MilitaryRecord military, string origin)
    {
        var address = (origin ?? string.Empty).Trim();
        if (military.Id <= 0 || string.IsNullOrWhiteSpace(address)) return;
        if (string.Equals((military.Address ?? string.Empty).Trim(), address, StringComparison.Ordinal)) return;

        await using var connection = OpenMainDatabase();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE militares SET endereco=$endereco WHERE id=$id;";
        command.Parameters.AddWithValue("$endereco", address);
        command.Parameters.AddWithValue("$id", military.Id);
        await command.ExecuteNonQueryAsync();

        military.Address = address;
        var index = _allMilitary.FindIndex(x => x.Id == military.Id);
        if (index >= 0) _allMilitary[index].Address = address;
        if (_current?.Id == military.Id)
        {
            _current.Address = address;
            _selectedAddress.Text = "Endereço: " + Blank(address);
            _listSelectedMilitaryText.ToolTip = address;
        }
    }

    private void LoadAddressFromCurrent()
    {
        if (_current is null) { Warn("Selecione um militar."); return; }
        _originBox.Text = _current.Address ?? string.Empty;
        _routeCepBox.Text = PlanCallService.FormatZipCode(_current.ZipCode);
        if (string.IsNullOrWhiteSpace(_originBox.Text)) Warn("O militar não possui endereço salvo no banco de dados.");
    }

    private async Task LookupRouteZipCodeAsync()
    {
        var cep = MilitaryFormatting.Digits(_routeCepBox.Text);
        if (cep.Length != 8)
        {
            Warn("Informe um CEP com 8 dígitos para pesquisar a origem.");
            _routeCepBox.Focus();
            _routeCepBox.SelectAll();
            return;
        }

        try
        {
            IsBusy = true;
            BusyMessage = "Pesquisando CEP da origem…";
            var address = await App.PlanCall.LookupZipCodeAsync(cep);
            if (address is null)
                throw new InvalidOperationException("CEP não encontrado. Confira os números digitados.");

            var formattedZip = PlanCallService.FormatZipCode(address.ZipCode);
            var formattedAddress = FormatViaCepAddressForRoute(address);
            _routeCepBox.Text = formattedZip;
            _originBox.Text = formattedAddress;
            SelectAddressNumberPlaceholder(_originBox);

            if (_current is not null)
            {
                _current.ZipCode = formattedZip;
                await SaveCurrentZipCodeAsync(_current, formattedZip);
            }

            _routeSaveStatus.Text = "CEP localizado. Origem preenchida com Rua, S/N, Bairro e Cidade/UF. Troque o S/N pelo número e clique em Salvar rota.";
            _statusText.Text = "Endereço da origem localizado pelo CEP.";
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Não foi possível consultar o CEP informado.", ex);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task SaveCurrentZipCodeAsync(MilitaryRecord military, string zipCode)
    {
        if (military.Id <= 0 || string.IsNullOrWhiteSpace(zipCode)) return;
        await using var connection = OpenMainDatabase();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE militares SET cep=$cep WHERE id=$id;";
        command.Parameters.AddWithValue("$cep", MilitaryFormatting.Digits(zipCode));
        command.Parameters.AddWithValue("$id", military.Id);
        await command.ExecuteNonQueryAsync();

        var index = _allMilitary.FindIndex(x => x.Id == military.Id);
        if (index >= 0) _allMilitary[index].ZipCode = zipCode;
    }

    private static string FormatViaCepAddressForRoute(ViaCepAddress address)
    {
        var formatted = PlanCallService.FormatAddress(address.Street, "S/N", string.Empty, address.District, address.CityState, string.Empty);
        if (!string.IsNullOrWhiteSpace(formatted)) return formatted;
        return string.Join(" - ", new[] { address.District, address.CityState }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static void SelectAddressNumberPlaceholder(TextBox box)
    {
        box.Focus();
        var numberIndex = box.Text.IndexOf("S/N", StringComparison.OrdinalIgnoreCase);
        if (numberIndex >= 0)
            box.Select(numberIndex, 3);
        else
            box.CaretIndex = box.Text.Length;
    }

    private void OpenMapsRoute()
    {
        var origin = _originBox.Text.Trim();
        var destination = _destinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(origin)) { Warn("Informe a origem ou carregue o endereço do cadastro."); return; }
        if (string.IsNullOrWhiteSpace(destination)) { Warn("Informe o destino."); return; }
        var date = _departureDate.SelectedDate ?? DateTime.Today.AddDays(1);
        if (!TimeSpan.TryParse(_departureTime.Text.Trim(), PtBr, out var time)) time = new TimeSpan(6, 0, 0);
        var departure = date.Date.Add(time);
        var seconds = (long)(departure - new DateTime(1970, 1, 1)).TotalSeconds;
        var url = $"https://www.google.com/maps/dir/{Uri.EscapeDataString(origin)}/{Uri.EscapeDataString(destination)}/data=!4m6!4m5!2m3!6e0!7e2!8j{seconds}!3e3";
        ShellService.OpenPath(url);
        _routeSaveStatus.Text = $"Rota aberta para saída em {departure:dd/MM/yyyy 'às' HH:mm}.";
    }

    private async Task LookupBusLineAsync(bool forceOnline = false)
    {
        var line = NormalizeBusLine(_lineNumberBox.Text);
        if (string.IsNullOrWhiteSpace(line)) { Warn("Informe o número da linha."); return; }
        _lineNumberBox.Text = line;
        _lookupResultText.Text = forceOnline ? "Consultando fontes online…" : "Pesquisando linha…";
        try
        {
            if (!forceOnline)
            {
                var cached = await LoadBusCacheAsync(line);
                if (cached is not null && cached.Fare > 0 && !string.IsNullOrWhiteSpace(cached.Name))
                {
                    ApplyLookupResult(cached with { Message = "Resultado carregado do banco local. Use “Pesquisar online” para atualizar." });
                    return;
                }
            }

            if (KnownSupplementaryLines.TryGetValue(line, out var known) && !forceOnline)
            {
                var result = new BusLookupResult(true, line, known.Name, "Suplementar", known.Fare, PbhFaresUrl, "Linha suplementar identificada na tabela incorporada do módulo.");
                ApplyLookupResult(result);
                await SaveBusCacheAsync(result);
                return;
            }

            var category = _lineCategoryBox.SelectedItem?.ToString() ?? "Auto";
            var online = await SearchBusOnlineAsync(line);
            if (online.Success)
            {
                if (online.Fare <= 0 && CategoryFares.TryGetValue(category, out var categoryFare))
                    online = online with { Fare = categoryFare, Category = category, Message = online.Message + " Tarifa preenchida pela categoria selecionada." };
                else if (string.IsNullOrWhiteSpace(online.Category) && !category.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                    online = online with { Category = category };
                ApplyLookupResult(online);
                if (online.Fare > 0) await SaveBusCacheAsync(online);
                return;
            }

            if (KnownSupplementaryLines.TryGetValue(line, out known))
            {
                var result = new BusLookupResult(true, line, known.Name, "Suplementar", known.Fare, PbhFaresUrl,
                    "A consulta online não respondeu, mas a linha foi identificada na tabela suplementar incorporada.");
                ApplyLookupResult(result);
                await SaveBusCacheAsync(result);
                return;
            }

            if (CategoryFares.TryGetValue(category, out var fare))
            {
                ApplyLookupResult(new BusLookupResult(true, line, $"Linha {line}", category, fare, SumobLineUrl + Uri.EscapeDataString(line),
                    online.Message + " Tarifa sugerida pela categoria; confirme no quadro oficial."));
                return;
            }
            ApplyLookupResult(online);
        }
        catch (Exception ex)
        {
            _lookupResultText.Text = "Falha na consulta automática. Informe a categoria ou a tarifa manualmente e use “Abrir página da linha”.";
            _lookupResultText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            await _log.WriteAsync("Falha pesquisando linha de ônibus no módulo C#.", ex);
        }
    }

    private void ApplyLookupResult(BusLookupResult result)
    {
        _lastLookup = result;
        _lookupFareBox.Text = result.Fare > 0 ? result.Fare.ToString("N2", PtBr) : string.Empty;
        _lookupResultText.Text = result.Success
            ? $"Linha {result.Line} — {Blank(result.Name)}\nCategoria: {Blank(result.Category)}   •   Tarifa: {(result.Fare > 0 ? FormatMoney(result.Fare) : "não identificada")}\n{result.Message}"
            : result.Message;
        _lookupResultText.SetResourceReference(TextBlock.ForegroundProperty, result.Success ? "SuccessBrush" : "WarningBrush");
    }

    private static async Task<BusLookupResult> SearchBusOnlineAsync(string line)
    {
        var searchUrl = BusSearchBaseUrl + Uri.EscapeDataString(line);
        var messages = new List<string>();
        var candidates = new List<string>();

        static void AddCandidate(List<string> target, string? url, string lineValue)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (!uri.Host.Contains("onibusbh.com.br", StringComparison.OrdinalIgnoreCase)) return;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!Regex.IsMatch(path, $@"/{Regex.Escape(lineValue)}(?:-|/|$)", RegexOptions.IgnoreCase)) return;
            if (!target.Contains(uri.ToString(), StringComparer.OrdinalIgnoreCase)) target.Add(uri.ToString());
        }

        try
        {
            var restUrl = $"https://onibusbh.com.br/wp-json/wp/v2/search?search={Uri.EscapeDataString(line)}&per_page=30";
            var rest = await Http.GetStringAsync(restUrl);
            using var json = JsonDocument.Parse(rest);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out var url)) AddCandidate(candidates, url.GetString(), line);
                }
            }
        }
        catch (Exception ex) { messages.Add("API do portal: " + ex.Message); }

        foreach (var sourceUrl in new[] { searchUrl, BusLinesIndexUrl })
        {
            try
            {
                var html = await Http.GetStringAsync(sourceUrl);
                foreach (Match match in Regex.Matches(html, "href=[\\\"'](?<u>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase))
                {
                    var raw = WebUtility.HtmlDecode(match.Groups["u"].Value);
                    if (Uri.TryCreate(new Uri(sourceUrl), raw, out var absolute)) AddCandidate(candidates, absolute.ToString(), line);
                }
            }
            catch (Exception ex) { messages.Add((sourceUrl == searchUrl ? "Pesquisa" : "Índice") + ": " + ex.Message); }
        }

        foreach (var pageUrl in candidates.Take(8))
        {
            try
            {
                var page = await Http.GetStringAsync(pageUrl);
                var titleMatch = Regex.Match(page, "<h1[^>]*>(?<v>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!titleMatch.Success) titleMatch = Regex.Match(page, "<title[^>]*>(?<v>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var name = titleMatch.Success ? CleanHtml(titleMatch.Groups["v"].Value) : $"Linha {line}";
                if (!Normalize(name).Contains(Normalize(line), StringComparison.Ordinal)) continue;
                var fareMatch = Regex.Match(page, @"Valor\s+da\s+passagem\s*:?\s*(?:<[^>]+>\s*)*R\$\s*(?<v>[0-9.,]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var fare = fareMatch.Success ? ParseMoney(fareMatch.Groups["v"].Value) : 0m;
                return new BusLookupResult(true, line, name, string.Empty, fare, pageUrl,
                    fare > 0 ? "Consulta concluída no Ônibus BH." : "Linha encontrada no Ônibus BH; informe ou confirme a tarifa.");
            }
            catch (Exception ex) { messages.Add("Página da linha: " + ex.Message); }
        }

        var officialUrl = SumobLineUrl + Uri.EscapeDataString(line);
        try
        {
            var official = await Http.GetStringAsync(officialUrl);
            var clean = CleanHtml(official);
            var officialMatch = Regex.Match(clean, $@"Linha\s*:?\s*{Regex.Escape(line)}\s+(?<v>.{{3,120}}?)(?:CEP|TELEFONE|Horário|$)", RegexOptions.IgnoreCase);
            var name = officialMatch.Success ? Regex.Replace(officialMatch.Groups["v"].Value, @"\s+", " ").Trim(" -–—:".ToCharArray()) : $"Linha {line}";
            if (official.Contains(line, StringComparison.OrdinalIgnoreCase))
                return new BusLookupResult(true, line, name, string.Empty, 0m, officialUrl,
                    "Linha localizada no quadro oficial da SUMOB. A tarifa deve ser confirmada pela categoria ou informada manualmente.");
        }
        catch (Exception ex) { messages.Add("SUMOB: " + ex.Message); }

        var detail = messages.Count == 0 ? string.Empty : " Detalhes: " + string.Join(" | ", messages.Take(3));
        return new BusLookupResult(false, line, string.Empty, string.Empty, 0m, searchUrl,
            "Nenhum resultado automático seguro foi encontrado. Use a categoria, informe a tarifa ou abra o quadro oficial." + detail);
    }

    private async Task SaveLookupCacheAsync()
    {
        if (_lastLookup is null)
        {
            var line = NormalizeBusLine(_lineNumberBox.Text);
            var fare = ParseMoney(_lookupFareBox.Text);
            if (string.IsNullOrWhiteSpace(line) || fare <= 0) { Warn("Pesquise uma linha ou informe número e tarifa."); return; }
            _lastLookup = new BusLookupResult(true, line, $"Linha {line}", _lineCategoryBox.SelectedItem?.ToString() ?? string.Empty, fare, PbhFaresUrl, "Registro manual.");
        }
        await SaveBusCacheAsync(_lastLookup);
        _lookupResultText.Text += "\nLinha salva no cache local.";
    }

    private async Task SaveBusCacheAsync(BusLookupResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Line) || result.Fare <= 0) return;
        await using var connection = OpenRouteDatabase();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO linha_cache(linha,nome,categoria,tarifa,url,updated_at)
            VALUES($linha,$nome,$categoria,$tarifa,$url,$ts)
            ON CONFLICT(linha) DO UPDATE SET nome=excluded.nome,categoria=excluded.categoria,tarifa=excluded.tarifa,url=excluded.url,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$linha", result.Line);
        command.Parameters.AddWithValue("$nome", result.Name ?? string.Empty);
        command.Parameters.AddWithValue("$categoria", result.Category ?? string.Empty);
        command.Parameters.AddWithValue("$tarifa", (double)result.Fare);
        command.Parameters.AddWithValue("$url", result.Url ?? string.Empty);
        command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<BusLookupResult?> LoadBusCacheAsync(string line)
    {
        await using var connection = OpenRouteDatabase();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT nome,categoria,tarifa,url FROM linha_cache WHERE linha=$linha LIMIT 1;";
        command.Parameters.AddWithValue("$linha", line);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new BusLookupResult(true, line, reader.IsDBNull(0) ? string.Empty : reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.IsDBNull(2) ? 0m : (decimal)reader.GetDouble(2), reader.IsDBNull(3) ? PbhFaresUrl : reader.GetString(3), "");
    }

    private void OpenLookupPage()
    {
        var url = _lastLookup?.Url;
        if (string.IsNullOrWhiteSpace(url)) url = BusSearchBaseUrl + Uri.EscapeDataString(NormalizeBusLine(_lineNumberBox.Text));
        ShellService.OpenPath(url);
    }

    private async Task CaptureRouteScreenshotAsync()
    {
        if (_current is null) { Warn("Selecione um militar."); return; }
        var previousState = WindowState;
        var previousSequence = GetClipboardSequenceNumber();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(500);
            Process.Start(new ProcessStartInfo { FileName = "ms-screenclip:", UseShellExecute = true });
            BitmapSource? image = null;
            var deadline = DateTime.Now.AddMinutes(2);
            while (DateTime.Now < deadline)
            {
                await Task.Delay(350);
                if (GetClipboardSequenceNumber() == previousSequence) continue;
                try
                {
                    if (Clipboard.ContainsImage())
                    {
                        image = Clipboard.GetImage();
                        if (image is not null) break;
                    }
                }
                catch { }
            }
            WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
            Activate();
            if (image is null)
            {
                Warn("Nenhum recorte foi recebido. A captura pode ter sido cancelada.");
                return;
            }
            var folder = Path.Combine(_paths.DataDirectory, "aux_transporte_rotas", "prints", $"{_current.Id:000000}_{SafeFileName(_current.WarNameOrName())}");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"rota_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            await using (var stream = File.Create(path)) encoder.Save(stream);
            _currentPrintPath = path;
            await SaveRouteAsync(false);
            UpdatePrintStatus();
            Info("Print da rota salvo e vinculado ao militar selecionado.");
        }
        catch (Exception ex)
        {
            WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
            await ReportErrorAsync("Não foi possível salvar o print da rota.", ex);
        }
    }

    private async Task OpenResidenceDeclarationAsync()
    {
        var selected = _militaryGrid.SelectedItem as MilitaryRecord ?? _current;
        if (selected is null) { Warn("Selecione o militar na lista lateral."); return; }
        var fresh = await _repository.GetByIdAsync(selected.Id);
        if (fresh is null) { Warn("O militar selecionado não foi encontrado no cadastro. Atualize a lista."); return; }
        new SIGFUR.Wpf.Views.Military.ResidenceDeclarationWindow(fresh, Residence) { Owner = this }.ShowDialog();
    }

    private Task AttachResidenceAsync()
    {
        if (_current is null) { Warn("Selecione um militar."); return Task.CompletedTask; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Comprovante de residência (opcional)", Filter = "Comprovante PDF ou imagem|*.pdf;*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) == true) { Residence.Attach(_current.Id, dialog.FileName); UpdatePrintStatus(); }
        return Task.CompletedTask;
    }

    private void OpenSavedPrint()
    {
        if (string.IsNullOrWhiteSpace(_currentPrintPath) || !File.Exists(_currentPrintPath)) { Warn("Não existe print salvo para este militar."); return; }
        ShellService.OpenPath(_currentPrintPath);
    }

    private async Task DeleteSavedPrintAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPrintPath)) return;
        if (!Confirm("Excluir print", "Excluir o print salvo desta rota?")) return;
        try { if (File.Exists(_currentPrintPath)) File.Delete(_currentPrintPath); } catch { }
        _currentPrintPath = string.Empty;
        await SaveRouteAsync(false);
        UpdatePrintStatus();
    }

    private void UpdatePrintStatus()
    {
        try { var proof = _current is null ? "" : Residence.Get(_current.Id); _residenceStatus.Text = proof.Length == 0 ? "Residência: sem anexo (opcional). A ficha pode ser emitida normalmente." : "Comprovante anexado · será incluído em páginas próprias na ficha."; }
        catch (Exception ex) { _residenceStatus.Text = ex.Message; }

        _printStatus.Text = !string.IsNullOrWhiteSpace(_currentPrintPath) && File.Exists(_currentPrintPath)
            ? $"Print salvo: {Path.GetFileName(_currentPrintPath)}"
            : "Nenhum print salvo para o militar selecionado.";
    }

    private async Task GenerateTransportDocumentFromButtonAsync(bool useMultiSelection)
    {
        if (_savingTransportRoute) return;
        try
        {
            _savingTransportRoute = true;
            IsBusy = true;
            BusyMessage = useMultiSelection ? "Gerando fichas das rotas…" : "Gerando ficha da rota…";
            if (!useMultiSelection && !CommitRouteGridEdits()) return;
            await GenerateTransportDocumentAsync(useMultiSelection);
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Não foi possível gerar o documento da rota. A tela continuará aberta.", ex);
        }
        finally
        {
            IsBusy = false;
            _savingTransportRoute = false;
        }
    }

    private async Task GenerateTransportDocumentAsync(bool useMultiSelection)
    {
        var rows = useMultiSelection
            ? GetSelectedMilitary()
            : _current is null ? new List<MilitaryRecord>() : new List<MilitaryRecord> { _current };
        if (rows.Count == 0) { Warn("Selecione ao menos um militar."); return; }

        AtTransportState? currentState = null;
        AtStoredRoute? currentRoute = null;
        if (!useMultiSelection && _current is not null)
        {
            // A ficha é uma saída dos dados que já estão visíveis. Não deve
            // depender de uma gravação no SQLite para ser gerada: além de ser
            // desnecessário, uma conexão de consulta aberta por outro módulo
            // podia deixar o cache compartilhado como somente leitura.
            await RecalculateAsync();
            var currentBuses = CloneBuses(_routeBuses);
            currentState = new AtTransportState(currentBuses, _currentCalculation);
            currentRoute = new AtStoredRoute(
                _originBox.Text.Trim(),
                _destinationBox.Text.Trim(),
                currentBuses,
                _currentPrintPath);
        }

        var output = Path.Combine(_paths.GeneratedDocumentsDirectory, "auxilio_transporte", "fichas_rota", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(output);
        var generated = new List<TransportRouteDocumentResult>();
        var failures = new List<string>();

        foreach (var military in SortMilitaryRecords(rows))
        {
            try
            {
                var usesCurrentScreen = !useMultiSelection && _current?.Id == military.Id && currentState is not null && currentRoute is not null;
                var state = usesCurrentScreen ? currentState! : await ReadTransportStateAsync(military);
                var route = usesCurrentScreen ? currentRoute! : await ReadStoredRouteAsync(military);
                var buses = route.Buses.Count > 0 ? route.Buses : state.Buses;
                if (string.IsNullOrWhiteSpace(route.PrintPath) || !File.Exists(route.PrintPath))
                {
                    failures.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name}: salve o print da rota antes de gerar a ficha.");
                    continue;
                }
                var result = await _routeDocuments.GenerateAsync(new TransportRouteDocumentData
                {
                    Military = military,
                    Origin = string.IsNullOrWhiteSpace(route.Origin) ? military.Address : route.Origin,
                    Destination = string.IsNullOrWhiteSpace(route.Destination) ? DefaultDestination : route.Destination,
                    Reference = CurrentBulletinReferenceText(),
                    ScreenshotPath = route.PrintPath,
                    ResidenceProofPath = Residence.Get(military.Id),
                    WorkingDays = state.Calculation.Days,
                    DailyGross = state.Calculation.DailyGross,
                    MonthGross = state.Calculation.MonthGross,
                    Share = state.Calculation.Share,
                    Net = state.Calculation.Net,
                    Buses = buses.Select(x => new TransportRouteDocumentBus(x.Number, x.Name, x.Category, x.Fare)).ToList()
                }, output);
                generated.Add(result);
            }
            catch (Exception ex)
            {
                failures.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name}: {ex.Message}");
                await _log.WriteAsync($"Falha ao gerar Auxílio-Transporte de {military.Name}.", ex);
            }
        }

        if (generated.Count == 0)
        {
            Warn("Nenhuma ficha foi gerada.\n\n" + string.Join("\n", failures.Take(8)));
            return;
        }
        if (generated.Count == 1)
            ShellService.OpenPath(generated[0].PrintablePath);
        else
            ShellService.OpenPath(output);

        var pdfCount = generated.Count(x => !string.IsNullOrWhiteSpace(x.PdfPath));
        var conversionWarnings = generated.Where(x => !string.IsNullOrWhiteSpace(x.Warning)).Select(x => x.Warning!).Distinct().ToList();
        var message = $"{generated.Count} ficha(s) de rota gerada(s).";
        message += pdfCount > 0 ? $"\nPDF pronto para impressão: {pdfCount}." : "\nOs arquivos DOCX estão prontos para abrir e imprimir.";
        if (failures.Count > 0) message += $"\nNão geradas: {failures.Count}.";
        if (conversionWarnings.Count > 0) message += "\n\n" + conversionWarnings[0];
        Info(message);
    }

    private async Task<AtStoredRoute> ReadStoredRouteAsync(MilitaryRecord military)
    {
        var origin = military.Address ?? string.Empty;
        var destination = DefaultDestination;
        var printPath = string.Empty;
        var buses = new List<AtBusLine>();

        if (!File.Exists(_paths.TransportRoutesDatabaseFile))
            return new AtStoredRoute(origin, destination, buses, printPath);

        await using (var connection = OpenRouteDatabaseReadOnly())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT origem,destino,linhas_json,print_path FROM rotas_militar WHERE militar_id=$id LIMIT 1;";
            command.Parameters.AddWithValue("$id", military.Id);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                origin = reader.IsDBNull(0) ? origin : reader.GetString(0);
                destination = reader.IsDBNull(1) ? destination : reader.GetString(1);
                var json = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                printPath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try { buses = JsonSerializer.Deserialize<List<AtBusLine>>(json) ?? new List<AtBusLine>(); }
                    catch { buses = new List<AtBusLine>(); }
                }
            }
        }

        await MergeDetailedRouteAsync(military, buses);
        var legacy = await _repository.GetTransportRouteDetailsAsync(military.Id);
        if (string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(legacy.Origin)) origin = legacy.Origin;
        if ((string.IsNullOrWhiteSpace(destination) || destination.Equals(DefaultDestination, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(legacy.Destination)) destination = legacy.Destination;
        return new AtStoredRoute(origin, destination, buses.OrderBy(x => x.Index).ToList(), printPath);
    }

    private async Task MergeDetailedRouteAsync(MilitaryRecord military, IList<AtBusLine> target)
    {
        var legacy = await _repository.GetTransportRouteDetailsAsync(military.Id);
        if (legacy.Buses.Count == 0) return;

        if (target.Count == 0)
        {
            foreach (var item in legacy.Buses.OrderBy(x => x.Index))
                target.Add(new AtBusLine { Index = item.Index, Number = item.Number, Name = item.Description, Fare = Convert.ToDecimal(item.Fare, CultureInfo.InvariantCulture) });
            return;
        }

        for (var index = 0; index < legacy.Buses.Count; index++)
        {
            var source = legacy.Buses[index];
            if (index >= target.Count)
            {
                target.Add(new AtBusLine { Index = index, Number = source.Number, Name = source.Description, Fare = Convert.ToDecimal(source.Fare, CultureInfo.InvariantCulture) });
                continue;
            }
            var current = target[index];
            if (string.IsNullOrWhiteSpace(current.Number)) current.Number = source.Number;
            if (string.IsNullOrWhiteSpace(current.Name)) current.Name = source.Description;
            if (current.Fare <= 0 && source.Fare > 0) current.Fare = Convert.ToDecimal(source.Fare, CultureInfo.InvariantCulture);
        }
    }

    private static async Task SaveLegacyCompatibleRouteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int militaryId,
        string origin,
        string destination,
        IReadOnlyList<AtBusLine> buses)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var normalized = buses.Select((x, index) => new
        {
            linha = (x.Number ?? string.Empty).Trim().ToUpperInvariant(),
            numero = (x.Number ?? string.Empty).Trim().ToUpperInvariant(),
            linha_nome = (x.Name ?? string.Empty).Trim().ToUpperInvariant(),
            descricao = (x.Name ?? string.Empty).Trim().ToUpperInvariant(),
            categoria = (x.Category ?? string.Empty).Trim().ToUpperInvariant(),
            valor = x.Fare.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR")),
            tarifa = x.Fare,
            url = x.SourceUrl ?? string.Empty,
            idx = index
        }).ToList();
        var fares = buses.Select(x => x.Fare.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"))).ToList();
        var first = buses.FirstOrDefault();
        command.CommandText = """
            INSERT INTO rota_manual(militar_id,origem,destino,linha,linha_nome,categoria,consulta_url,tarifas_json,onibus_json,atualizado_em)
            VALUES($id,$origem,$destino,$linha,$nome,$categoria,$url,$tarifas,$onibus,$ts)
            ON CONFLICT(militar_id) DO UPDATE SET origem=excluded.origem,destino=excluded.destino,linha=excluded.linha,
                linha_nome=excluded.linha_nome,categoria=excluded.categoria,consulta_url=excluded.consulta_url,
                tarifas_json=excluded.tarifas_json,onibus_json=excluded.onibus_json,atualizado_em=excluded.atualizado_em;
            """;
        command.Parameters.AddWithValue("$id", militaryId);
        command.Parameters.AddWithValue("$origem", origin ?? string.Empty);
        command.Parameters.AddWithValue("$destino", destination ?? string.Empty);
        command.Parameters.AddWithValue("$linha", first?.Number ?? string.Empty);
        command.Parameters.AddWithValue("$nome", first?.Name ?? string.Empty);
        command.Parameters.AddWithValue("$categoria", first?.Category ?? string.Empty);
        command.Parameters.AddWithValue("$url", first?.SourceUrl ?? string.Empty);
        command.Parameters.AddWithValue("$tarifas", JsonSerializer.Serialize(fares));
        command.Parameters.AddWithValue("$onibus", JsonSerializer.Serialize(normalized));
        command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static Paragraph BulletinBlankLine(double height = 10)
        => new(new Run(string.Empty))
        {
            Margin = new Thickness(0, height / 2, 0, height / 2),
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
            FontSize = BulletinTextFormatter.StandardWpfFontSize
        };

    private static string AuxTransportSisbolSubject(string action)
    {
        if (IsTransportImplantationAction(action)) return "AUXILIO-TRANSPORTE - Implantação";
        if (IsTransportUpdateAction(action)) return "AUXILIO-TRANSPORTE - Atualização de Valores";
        if (IsTransportSaqueAction(action)) return "AUXILIO-TRANSPORTE - Saque de Atrasado";
        if (IsTransportDifferenceAction(action)) return "AUXILIO-TRANSPORTE - Diferença";
        if (IsTransportDevolucaoAction(action)) return "AUXILIO-TRANSPORTE - Devolução a Militar";
        if (IsTransportExclusionAction(action)) return "AUXILIO-TRANSPORTE - Exclusão de Beneficiários";
        if (IsTransportDespesaAnularAction(action)) return "AUXILIO-TRANSPORTE - Despesa a anular";
        return "AUXILIO-TRANSPORTE";
    }

    private static bool IsTransportImplantationAction(string action)
        => action.Contains("Implant", StringComparison.OrdinalIgnoreCase) || action.Contains("Concessão", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportUpdateAction(string action)
        => action.Contains("Atualiza", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportSaqueAction(string action)
        => action.Contains("Saque", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportDifferenceAction(string action)
        => action.Contains("Diferença", StringComparison.OrdinalIgnoreCase)
           || action.Contains("Diferenca", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportDevolucaoAction(string action)
        => action.Contains("Devolução", StringComparison.OrdinalIgnoreCase) || action.Contains("Devolucao", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportDespesaAnularAction(string action)
        => action.Contains("Despesa", StringComparison.OrdinalIgnoreCase) || action.Contains("Anular", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportExclusionAction(string action)
        => action.Contains("Excluir", StringComparison.OrdinalIgnoreCase) || action.Contains("Exclus", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportMultiCompetenceAction(string action)
        => IsTransportSaqueAction(action) || IsTransportDevolucaoAction(action);

    private void AddSelectedReferenceMonthToDetails()
    {
        var month = CurrentBulletinReferenceText();
        if (string.IsNullOrWhiteSpace(month)) return;

        var existing = SplitCompetenceMonths(_bulletinRequiredDetailsBox.Text).ToList();
        if (!existing.Any(x => x.Equals(month, StringComparison.OrdinalIgnoreCase)))
            existing.Add(month);
        _bulletinRequiredDetailsBox.Text = string.Join(Environment.NewLine, existing);
        _bulletinRequiredDetailsBox.CaretIndex = _bulletinRequiredDetailsBox.Text.Length;
        _bulletinRequiredDetailsBox.Focus();
        UpdateBulletinCompetenceCount();
        _statusText.Text = $"Competência {month} adicionada para o próximo militar incluído.";
    }

    private void UpdateBulletinCompetenceCount()
    {
        if (_bulletinCompetenceCountText is null) return;
        var count = SplitCompetenceMonths(_bulletinRequiredDetailsBox.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        _bulletinCompetenceCountText.Text = count == 1
            ? "1 competência selecionada."
            : $"{count} competências selecionadas.";
    }

    private string CaptureBulletinCompetencesForAction(string action)
    {
        if (!IsTransportMultiCompetenceAction(action)) return string.Empty;
        var months = SplitCompetenceMonths(_bulletinRequiredDetailsBox.Text).ToList();
        var reference = CurrentBulletinReferenceText();
        if (months.Count == 0 && !string.IsNullOrWhiteSpace(reference)) months.Add(reference);
        return string.Join(Environment.NewLine, months.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitCompetenceMonths(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var raw in Regex.Split(value, @"\r\n|\n|\r|;|,"))
        {
            var item = raw.Trim();
            if (string.IsNullOrWhiteSpace(item)) continue;
            var pipe = item.IndexOf('|');
            if (pipe >= 0) item = item[..pipe].Trim();
            item = Regex.Replace(item, @"\s+", " ").Trim(' ', '-', '—', ',', ';');
            if (!string.IsNullOrWhiteSpace(item)) yield return item;
        }
    }

    private static string BuildCompetenceListText(string reference, string details)
    {
        var months = SplitCompetenceMonths(details)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (months.Count == 0 && !string.IsNullOrWhiteSpace(reference)) months.Add(reference.Trim());
        if (months.Count == 0) return "não informado";
        return string.Join(", ", months);
    }

    private void UpdateBulletinRequirementUi()
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(action)) action = BulletinActions[0];

        var isImplantation = IsTransportImplantationAction(action);
        var isUpdate = IsTransportUpdateAction(action);
        var isSaque = IsTransportSaqueAction(action);
        var isDifference = IsTransportDifferenceAction(action);
        var isDevolucao = IsTransportDevolucaoAction(action);
        var isExclusion = IsTransportExclusionAction(action);
        var isMulti = isSaque || isDevolucao;

        if (_bulletinReferenceField is not null)
            _bulletinReferenceField.Visibility = isExclusion ? Visibility.Collapsed : Visibility.Visible;
        if (_bulletinReceivedValueField is not null)
            _bulletinReceivedValueField.Visibility = isDifference ? Visibility.Visible : Visibility.Collapsed;
        _bulletinDifferencePreviewText.Visibility = isDifference ? Visibility.Visible : Visibility.Collapsed;
        UpdateBulletinDifferencePreview();
        if (_bulletinAddMonthButtonField is not null)
            _bulletinAddMonthButtonField.Visibility = isMulti ? Visibility.Visible : Visibility.Collapsed;
        if (_bulletinCountFromField is not null)
            _bulletinCountFromField.Visibility = isExclusion ? Visibility.Visible : Visibility.Collapsed;
        if (_bulletinDetailsField is not null)
            _bulletinDetailsField.Visibility = (isMulti || isExclusion) ? Visibility.Visible : Visibility.Collapsed;
        _bulletinCompetenceCountText.Visibility = isMulti ? Visibility.Visible : Visibility.Collapsed;

        if (_bulletinReferenceLabel is not null)
        {
            _bulletinReferenceLabel.Text = isSaque ? "Folha em que não recebeu (contracheque)" : isImplantation
                ? "Ano de referência"
                : isMulti
                    ? "Mês do militar"
                    : "Mês/Ano de referência";
        }
        if (_bulletinCountFromLabel is not null)
            _bulletinCountFromLabel.Text = "Cessar a contar de";

        if (_bulletinDetailsLabel is not null)
        {
            _bulletinDetailsLabel.Text = isSaque ? "Folhas sem recebimento deste militar" : isMulti
                ? "Competência(s) deste militar"
                : isExclusion
                    ? "Motivo da exclusão / cessação"
                    : string.Empty;
        }

        UpdateTransportReferenceHint();
        _bulletinRequiredTitle.Text = string.Empty;
        _bulletinRequiredText.Text = string.Empty;

        _bulletinRequiredDetailsBox.ToolTip = isMulti
            ? "Competência individual do militar. Uma linha por mês."
            : isExclusion
                ? "Informe o motivo da cessação. Ex.: alteração de endereço/rota ou cessação do deslocamento."
                : string.Empty;

        if (isImplantation)
            _bulletinSelectionText.Text = "Implantação: informe o ano de referência. No corpo do militar saem apenas Valor Diário, Quantidade de dias e Ano de referência.";
        else if (isUpdate)
            _bulletinSelectionText.Text = "Atualização: informe o mês/ano de referência. No corpo do militar saem apenas Valor Diário, Quantidade de dias, Mês e Ano de referência.";
        else if (isSaque)
            _bulletinSelectionText.Text = "AR0095 · Escolha a folha em que faltou o pagamento. O boletim usa o mês seguinte, conforme o manual SIPPES, p. 130. Confira dias, tarifa, soldo e graduação vigentes no direito; o cálculo atual é uma estimativa. Exercícios anteriores exigem análise própria.";
        else if (isDifference)
            _bulletinSelectionText.Text = "Diferença: escolha a competência, informe quanto o militar recebeu e adicione uma pessoa por vez. O valor total e o valor diário vêm automaticamente da SAT; o saldo a pagar é calculado pelo SIGFUR.";
        else if (isDevolucao)
            _bulletinSelectionText.Text = "Devolução a Militar: escolha o mês deste militar e depois adicione-o. No corpo saem apenas Valor Diário, Quantidade de dias, Mês e Ano de referência.";
        else if (isExclusion)
            _bulletinSelectionText.Text = "Exclusão: informe a data de cessação e o motivo.";
        else
            _bulletinSelectionText.Text = string.Empty;
    }

    private void UpdateBulletinDifferencePreview()
    {
        if (!IsTransportDifferenceAction(_bulletinActionBox.SelectedItem?.ToString() ?? string.Empty))
        {
            _bulletinDifferencePreviewText.Text = string.Empty;
            return;
        }
        var received = ParseMoney(_bulletinReceivedValueBox.Text);
        var difference = Math.Max(0m, Round(_currentCalculation.Net - received));
        _bulletinDifferencePreviewText.Text = _current is null
            ? "Selecione um militar para visualizar os valores da SAT."
            : $"SAT: {FormatMoney(_currentCalculation.Net)}  •  Valor diário: {FormatMoney(_currentCalculation.DailyGross)}  •  {_currentCalculation.Days} dias  •  Diferença: {FormatMoney(difference)}";
    }

    private void UpdateTransportReferenceHint()
    {
        var isSaque = IsTransportSaqueAction(_bulletinActionBox.SelectedItem?.ToString() ?? "");
        _transportReferenceHint.Visibility = isSaque ? Visibility.Visible : Visibility.Collapsed;
        if (isSaque && TryGetBulletinMonthYear(out var month, out var year))
            _transportReferenceHint.Text = TransportCompetenceService.Explain(new DateTime(year, month, 1));
    }

    internal static string SaqueBenefitReferences(string reference)
        => string.Join("; ", SplitCompetenceMonths(reference).Select(value =>
            TryParseCompetence(value, out var date)
                ? TransportCompetenceService.BenefitForUnpaidPayroll(date).ToString("MM/yyyy", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("Informe mês e ano válidos da folha sem recebimento.")).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string DefaultMultiCompetenceLine(string reference, AtCalculation calculation)
        => string.IsNullOrWhiteSpace(reference) ? "MÊS/ANO" : reference.Trim();

    private static string ExtractReferenceYear(string reference)
    {
        var value = (reference ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return "não informado";
        var match = Regex.Match(value, @"(?<!\d)(20\d{2}|\d{2})(?!\d)");
        if (!match.Success) return value;
        var year = match.Value;
        return year.Length == 2 ? "20" + year : year;
    }

    private static string RequiredReferenceText(string action, string reference)
    {
        var normalized = NormalizeBulletinDetails(reference);
        if (IsTransportImplantationAction(action)) return ExtractReferenceYear(reference);
        return normalized;
    }

    private static IReadOnlyList<string> BuildReferenceMonthYearLines(string reference)
    {
        var competences = SplitCompetenceMonths(reference)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (competences.Count == 0 && !string.IsNullOrWhiteSpace(reference))
            competences.Add(reference.Trim());

        if (competences.Count == 1 && TryParseCompetence(competences[0], out var date))
        {
            return
            [
                $"Mês de referência: {PtBr.DateTimeFormat.GetMonthName(date.Month).ToUpperInvariant()}",
                $"Ano de referência: {date.Year}"
            ];
        }

        if (competences.Count == 1)
            return [$"Mês/Ano de referência: {NormalizeBulletinDetails(competences[0])}"];

        return [$"Mês/Ano de referência: {string.Join(", ", competences)}"];
    }

    private static IReadOnlyList<string> BuildRequiredTransportLines(string action, AtCalculation calculation, string reference, decimal receivedValue = 0m)
    {
        if (IsTransportImplantationAction(action))
        {
            return
            [
                $"Valor diário: {FormatMoney(calculation.DailyGross)}",
                $"Quantidade de dias: {calculation.Days}",
                $"Ano de referência: {RequiredReferenceText(action, reference)}"
            ];
        }

        if (IsTransportSaqueAction(action))
        {
            var competences = SplitCompetenceMonths(SaqueBenefitReferences(reference)).ToList();
            if (competences.Count == 0) competences.Add(RequiredReferenceText(action, reference));
            var total = Round(calculation.Net * Math.Max(1, competences.Count));
            return new[]
            {
                $"Valor diário: {FormatMoney(calculation.DailyGross)}",
                $"Valor total: {FormatMoney(total)}",
                $"Quantidade de dias: {calculation.Days}"
            }.Concat(BuildReferenceMonthYearLines(SaqueBenefitReferences(reference))).ToList();
        }

        if (IsTransportDifferenceAction(action))
        {
            var difference = Math.Max(0m, Round(calculation.Net - receivedValue));
            return new[]
            {
                $"Valor total devido conforme a SAT: {FormatMoney(calculation.Net)}",
                $"Valor recebido: {FormatMoney(receivedValue)}",
                $"Valor diário na competência ({calculation.Days} dias): {FormatMoney(calculation.DailyGross)}",
                $"Diferença a ser paga: {FormatMoney(difference)}"
            }.Concat(BuildReferenceMonthYearLines(reference)).ToList();
        }

        if (IsTransportDevolucaoAction(action))
        {
            var competences = SplitCompetenceMonths(reference).ToList();
            if (competences.Count == 0) competences.Add(RequiredReferenceText(action, reference));
            return new[]
            {
                $"Valor diário: {FormatMoney(calculation.DailyGross)}",
                $"Quantidade de dias: {calculation.Days}"
            }.Concat(BuildReferenceMonthYearLines(reference)).ToList();
        }

        return new[]
        {
            $"Valor diário: {FormatMoney(calculation.DailyGross)}",
            $"Quantidade de dias: {calculation.Days}"
        }.Concat(BuildReferenceMonthYearLines(reference)).ToList();
    }

    private static string ResolveEntryReference(AtBulletinQueueItem entry, string action, string fallbackReference, string fallbackDetails)
    {
        if (!IsTransportMultiCompetenceAction(action)) return fallbackReference;
        var own = BuildCompetenceListText(string.Empty, entry.CompetencesText);
        if (!own.Equals("não informado", StringComparison.OrdinalIgnoreCase)) return own;
        return BuildCompetenceListText(fallbackReference, fallbackDetails);
    }

    private async Task GenerateBulletinAsync(bool showPreview = false)
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        var isImplantation = IsTransportImplantationAction(action);
        var isUpdate = IsTransportUpdateAction(action);
        var isSaque = IsTransportSaqueAction(action);
        var isDifference = IsTransportDifferenceAction(action);
        var isDevolucao = IsTransportDevolucaoAction(action);
        var isExclusion = IsTransportExclusionAction(action);
        var isMulti = IsTransportMultiCompetenceAction(action);

        var queued = SortBulletinQueueItems(_bulletinQueueAll
            .Where(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => MilitaryIdentityKey(x.Military), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.AddedAt).First()))
            .ToList();

        if (queued.Count == 0)
        {
            if (isDifference)
            {
                Warn("Inclua o militar na fila da nota de diferença depois de informar o valor recebido. Esse modelo não adiciona pessoas automaticamente.");
                return;
            }
            var selected = GetSelectedMilitary();
            if (selected.Count == 0)
            {
                Warn("Não há militares neste modelo. Calcule a rota e clique em ‘Levar ao boletim escolhido’. ");
                return;
            }
            foreach (var military in SortMilitaryRecords(selected))
            {
                var state = await ReadTransportStateAsync(military);
                queued.Add(new AtBulletinQueueItem
                {
                    Action = action,
                    Military = military,
                    Calculation = state.Calculation,
                    Buses = state.Buses,
                    Origin = military.Address ?? string.Empty,
                    Destination = DefaultDestination,
                    CompetencesText = CaptureBulletinCompetencesForAction(action),
                    AddedAt = DateTime.Now
                });
            }
        }

        var reference = CurrentBulletinReferenceText();
        var details = _bulletinRequiredDetailsBox.Text.Trim();
        var effectiveDate = _bulletinCountFromDatePicker.SelectedDate ?? _bulletinBaseDatePicker.SelectedDate ?? DateTime.Today;
        var effectiveDateText = FormatMilitaryDateShort(effectiveDate);

        if (!isExclusion && string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(details))
        {
            Warn(isMulti
                ? "Informe pelo menos uma competência de referência. Para mais de um mês, use uma linha por mês no campo de competências."
                : "Informe o mês/ano de referência obrigatório deste lançamento.");
            _bulletinReferenceBox.Focus();
            return;
        }

        if (isExclusion && string.IsNullOrWhiteSpace(details))
        {
            Warn("Informe o motivo da exclusão/cessação do Auxílio-Transporte. Ex.: deixou de fazer jus ao benefício por alteração de endereço ou cessação do deslocamento.");
            _bulletinRequiredDetailsBox.Focus();
            return;
        }

        if (!isExclusion)
        {
            var invalid = queued.Where(x =>
                x.Calculation.Days <= 0
                || x.Calculation.DailyGross <= 0
                || ((isSaque || isDifference) && x.Calculation.Net <= 0)
                || (isDifference && x.ReceivedValue >= x.Calculation.Net)).ToList();
            if (invalid.Count > 0)
            {
                Warn("Há militar(es) sem dias, valor diário, valor total ou diferença positiva. Carregue a SAT, confira o valor recebido e recalcule antes de gerar:\n\n" + string.Join("\n", invalid.Select(x => $"- {x.Military.ShortRank} {x.Military.Name}")));
                return;
            }
        }

        if (isMulti && string.IsNullOrWhiteSpace(details))
        {
            _bulletinRequiredDetailsBox.Text = DefaultMultiCompetenceLine(reference, queued.FirstOrDefault()?.Calculation ?? AtCalculation.Empty);
            details = _bulletinRequiredDetailsBox.Text.Trim();
        }

        if (isMulti)
        {
            var missingCompetence = queued
                .Where(x => BuildCompetenceListText(string.Empty, x.CompetencesText).Equals("não informado", StringComparison.OrdinalIgnoreCase))
                .Select(x => $"- {AtRankFormatter.ShortName(x.Military.Rank)} {x.Military.Name}")
                .ToList();
            if (missingCompetence.Count > 0)
            {
                Warn("Há militar(es) sem competência individual no saque/devolução. Remova e adicione novamente com o(s) mês(es) correto(s):\n\n" + string.Join("\n", missingCompetence));
                return;
            }
        }

        if (isSaque)
        {
            var restricted = queued.Where(entry => BulletinRankKey(entry.Military.Rank) == "sd_ev"
                && SplitCompetenceMonths(ResolveEntryReference(entry, action, reference, details))
                    .Any(value => TryParseCompetence(value, out var payroll)
                        && TransportCompetenceService.BenefitForUnpaidPayroll(payroll).Month is 12 or 1 or 2)).ToList();
            if (restricted.Count > 0)
            {
                Warn("O manual SIPPES (p. 129, item 2) excetua soldados do Efetivo Variável em dezembro, janeiro e fevereiro do saque pela AR0095. Confira o procedimento aplicável antes de gerar este modelo para: " + string.Join(", ", restricted.Select(x => x.Military.Name)));
                return;
            }
        }

        var headerReference = isMulti
            ? string.Empty
            : reference;

        var document = new FlowDocument
        {
            PagePadding = new Thickness(10),
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
            FontSize = BulletinTextFormatter.StandardWpfFontSize,
            FontWeight = FontWeights.Normal
        };

        document.Blocks.Add(new Paragraph(new Run(BulletinHeader(action, headerReference, effectiveDateText)))
        {
            TextAlignment = TextAlignment.Justify,
            Margin = new Thickness(0, 0, 0, 6),
            FontWeight = FontWeights.Normal
        });

        document.Blocks.Add(BulletinBlankLine(3));

        var highValue = new List<string>();
        var entryIndex = 0;
        foreach (var entry in queued)
        {
            if (entryIndex++ > 0) document.Blocks.Add(BulletinBlankLine(5));
            var military = entry.Military;
            var calculation = entry.Calculation;
            var nameParagraph = new Paragraph { Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.Normal };
            nameParagraph.Inlines.Add(new Run(AtRankFormatter.ShortName(military.Rank) + " ") { FontWeight = FontWeights.Normal });
            AddNameWithWarBold(nameParagraph, military);
            document.Blocks.Add(nameParagraph);
            document.Blocks.Add(new Paragraph(new Run($"Prec-CP {Blank(military.PrecCp)} CPF {military.FormattedCpf}") { FontWeight = FontWeights.Normal })
            {
                Margin = new Thickness(0),
                FontWeight = FontWeights.Normal
            });

            if (isExclusion)
            {
                var exclusionLines = new[]
                {
                    $"Cessação do direito: a contar de {effectiveDateText}",
                    $"Motivo: {NormalizeBulletinDetails(details)}"
                };
                document.Blocks.Add(new Paragraph(new Run(string.Join(Environment.NewLine, exclusionLines)) { FontWeight = FontWeights.Normal })
                {
                    Margin = new Thickness(0, 0, 0, 4),
                    FontWeight = FontWeights.Normal
                });
                continue;
            }

            var entryReference = ResolveEntryReference(entry, action, reference, details);
            var lines = BuildRequiredTransportLines(action, calculation, entryReference, entry.ReceivedValue).ToList();

            document.Blocks.Add(new Paragraph(new Run(string.Join(Environment.NewLine, lines)) { FontWeight = FontWeights.Normal })
            {
                Margin = new Thickness(0, 0, 0, 4),
                FontWeight = FontWeights.Normal
            });

            if (isSaque)
            {
                var competenceCount = SplitCompetenceMonths(SaqueBenefitReferences(entryReference)).Count();
                var saqueTotal = Round(calculation.Net * Math.Max(1, competenceCount));
                if (saqueTotal >= 1200m)
                    highValue.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name}: {FormatMoney(saqueTotal)}");
            }
        }

        _bulletinPreview.Document = document;
        if (showPreview)
            OpenBulletinPreviewWindow(CloneFlowDocument(document), queued.Count, action);
        if (highValue.Count > 0)
            Warn("Atenção — saque de AT igual ou superior a R$ 1.200,00. Confira autorização da DAP/SISCONTRANS:\n\n" + string.Join("\n", highValue));
        _statusText.Text = $"Boletim gerado para {queued.Count} militar(es), somente com os dados exigidos pelo SIPPES no corpo de cada militar.";
    }

    private static FlowDocument CloneFlowDocument(FlowDocument document)
        => (FlowDocument)System.Windows.Markup.XamlReader.Parse(System.Windows.Markup.XamlWriter.Save(document));

    private void OpenBulletinPreviewWindow(FlowDocument document, int affectedCount, string action)
    {
        var preview = new RichTextBox
        {
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
            FontSize = BulletinTextFormatter.StandardWpfFontSize,
            Document = document
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(preview);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summary = new TextBlock
        {
            Text = affectedCount == 0 ? "Nenhum militar na prévia." : $"{affectedCount} militar(es) na prévia.",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };
        footer.Children.Add(summary);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(SmallButton("Copiar", (_, _) => CopyRichText(preview), "SecondaryButtonStyle"));
        actions.Children.Add(SmallButton("Fechar", (_, _) => Window.GetWindow(preview)?.Close(), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var dialog = new Window
        {
            Title = "Prévia — " + AuxTransportSisbolSubject(action),
            Owner = this,
            Width = 940,
            Height = 760,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        AttachPersistentPlacement(dialog, "previa_boletim_aux_transporte");
        dialog.Loaded += (_, _) => InstallContextMenusRecursive(dialog.Content as DependencyObject);
        dialog.Show();
        dialog.Activate();
    }

    private string BulletinHeader(string action, string reference, string effectiveDateText)
    {
        const string legalBasis = "Com fundamento na Medida Provisória nº 2.165-36, de 23 AGO 01, na Portaria nº 849-Cmt Ex, de 14 JUL 16, e nas Normas para Controle da Solicitação e Concessão do Auxílio-Transporte no âmbito do Exército Brasileiro, aprovadas pela Portaria nº 098-DGP, de 31 OUT 01, alterada pela Portaria nº 269-DGP, de 11 DEZ 07";
        if (IsTransportImplantationAction(action))
            return legalBasis + ", seja implantado, sem prazo, o auxílio-transporte em favor do(s) militar(es) abaixo relacionado(s), para lançamento no SIPPES conforme os dados discriminados:";
        if (IsTransportUpdateAction(action))
            return legalBasis + ", seja atualizada a base de cálculo do auxílio-transporte do(s) militar(es) abaixo relacionado(s), conforme mês/ano de referência, valor diário e quantidade de dias:";
        if (IsTransportSaqueAction(action))
            return legalBasis + ", seja realizado o saque de atrasados de auxílio-transporte em favor do(s) militar(es) abaixo relacionado(s), nas competências individualmente indicadas:";
        if (IsTransportDifferenceAction(action))
            return $"Seja realizado o saque da diferença do valor do auxílio-transporte em benefício do(s) militar(es) abaixo relacionado(s), da {OrganizationIdentity.Name}, por ter(em) recebido valor a menor, conforme a respectiva SAT e nos termos da Portaria nº 334-Cmt Ex, de 25 JUN 99, e da Portaria nº 014-DGS, de 30 JUN 99:";
        if (IsTransportDevolucaoAction(action))
            return legalBasis + ", seja realizada a devolução de auxílio-transporte ao(s) militar(es) abaixo relacionado(s), referente à competência em que o benefício deixou de ser percebido:";
        return legalBasis + $", seja cessado/excluído o auxílio-transporte do(s) militar(es) abaixo relacionado(s), a contar de {effectiveDateText}:";
    }

    private static string BuildMandatoryReferenceLine(string action, string reference)
    {
        var normalized = NormalizeBulletinDetails(reference);
        if (IsTransportSaqueAction(action)) return "Competência(s): " + normalized;
        if (IsTransportDevolucaoAction(action)) return "Competência(s): " + normalized;
        return "Referência: " + normalized;
    }

    private static string NormalizeBulletinDetails(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim(), @"\r\n|\n|\r", "; ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim(' ', ';');
        return string.IsNullOrWhiteSpace(normalized) ? "não informado" : normalized;
    }

    private async Task ApplyBulletinActionAsync()
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        var queued = _bulletinQueueAll.Where(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = queued.Count > 0
            ? queued.Select(x => x.Military).DistinctBy(x => x.Id).ToList()
            : GetSelectedMilitary();
        if (rows.Count == 0) { Warn("Não há militares neste modelo."); return; }
        if (IsTransportSaqueAction(action) || IsTransportDifferenceAction(action) || IsTransportDevolucaoAction(action) || IsTransportDespesaAnularAction(action))
        {
            Info("Este modelo gera texto de lançamento eventual e não altera o cadastro-base do Auxílio-Transporte.");
            return;
        }
        if (!Confirm("Confirmar alteração", $"Aplicar '{action}' no banco para {rows.Count} militar(es)?")) return;
        foreach (var military in rows)
        {
            if (action.Contains("Excluir", StringComparison.OrdinalIgnoreCase))
            {
                await SetTransportExcludedAsync(military);
                continue;
            }
            var item = queued.FirstOrDefault(x => x.Military.Id == military.Id);
            if (item is not null)
                await SaveTransportStateAsync(military, item.Buses, item.Calculation.Days);
            else
            {
                var state = await ReadTransportStateAsync(military);
                await SaveTransportStateAsync(military, state.Buses, state.Calculation.Days);
            }
        }
        await ReloadAsync();
        Info("Alterações aplicadas no banco oficial.");
    }

    private async Task SetTransportExcludedAsync(MilitaryRecord military)
    {
        await using var connection = OpenMainDatabase();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM aux_transporte_tarifas WHERE militar_id=$id;";
            clear.Parameters.AddWithValue("$id", military.Id);
            await clear.ExecuteNonQueryAsync();
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE militares SET recebe_aux_transporte='Não',valor_aux_transporte='0.00',aux_total_bruto=0,aux_base_ts=$ts WHERE id=$id;";
            update.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", military.Id);
            await update.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        military.ReceivesTransportAid = "Não";
        military.TransportAidValue = "0.00";
        _effectiveNetByMilitaryId[military.Id] = 0m;
    }

    private void OpenSisbolLogin()
    {
        ShellService.OpenPath(SisbolLoginUrl);
        _statusText.Text = "Login do SisBol aberto. Conclua autenticação/captcha no navegador.";
    }

    private static void OpenSisbolSubmissionHistory()
    {
        var window = new SIGFUR.Wpf.Views.Bulletin.SisbolSubmissionHistoryWindow
        {
            Owner = Application.Current?.MainWindow
        };
        window.Show();
        window.Activate();
    }

    private async Task OpenSisbolMatterAndCopyAsync()
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        var range = new TextRange(_bulletinPreview.Document.ContentStart, _bulletinPreview.Document.ContentEnd);
        var text = range.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            await GenerateBulletinAsync();
            range = new TextRange(_bulletinPreview.Document.ContentStart, _bulletinPreview.Document.ContentEnd);
            text = range.Text.Trim();
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Warn("Gere o boletim antes de enviar ao SisBol.");
            return;
        }

        var military = _bulletinQueueAll
            .Where(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Military)
            .DistinctBy(x => x.Id)
            .ToList();
        if (military.Count == 0) military = GetSelectedMilitary();
        if (military.Count == 0)
        {
            Warn("Não há militares neste boletim para enviar ao SisBol.");
            return;
        }
        if (!App.Sisbol.IsReady)
        {
            Warn("SisBol não preparado. Use o botão ‘Preparar SisBol’ no topo da janela principal, conclua o login/captcha e valide a sessão.");
            return;
        }

        try
        {
            IsBusy = true;
            BusyMessage = "Enviando Auxílio-Transporte ao SisBol…";
            var subject = AuxTransportSisbolSubject(action);
            await App.Sisbol.SendAsync(
                text,
                military,
                subject,
                _bulletinIncludeConsequencesCheck.IsChecked == true,
                _bulletinConsequencesBox.Text);
            _statusText.Text = $"SisBol OK: {subject} enviado, incluído e registrado no histórico.";
        }
        catch (Exception ex)
        {
            CopyRichText(_bulletinPreview);
            await _log.WriteAsync("Falha ao enviar boletim de Auxílio-Transporte ao SisBol.", ex);
            Warn("O envio automático ao SisBol não foi concluído. O texto foi copiado para conferência manual.\n\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private void ApplyDaFilter()
    {
        var query = Normalize(_daSearchBox.Text);
        var view = CollectionViewSource.GetDefaultView(_daRows);
        view.Filter = item =>
        {
            if (item is not AtDaRow row) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            var m = row.Military;
            return Normalize($"{m.Rank} {AtRankFormatter.ShortName(m.Rank)} {m.Name} {m.WarName} {m.Cpf} {m.PrecCp}").Contains(query, StringComparison.Ordinal);
        };
        view.Refresh();
    }

    private void UpdateDaSelectedText()
    {
        if (_daGrid.SelectedItem is not AtDaRow row)
        {
            _daSelectedText.Inlines.Clear();
            _daSelectedText.Inlines.Add(new Run("Selecione um militar na tabela para alterar os dias."));
            return;
        }
        SetMilitaryName(_daSelectedText, row.Military, "Selecionado: " + AtRankFormatter.ShortName(row.Military.Rank) + " ", true);
        _daSelectedText.Inlines.Add(new Run($"  •  Preta: {row.BlackDays}  •  Vermelha: {row.RedDays}  •  Saldo: {row.NetDays}  •  Total: {row.DeductionFormatted}  •  Origem: {row.SourceText}"));
    }

    private async Task ApplyDaCalendarAsync()
    {
        if (!TryGetDaCompetence(out var competence, out var month, out var year))
            return;
        if (_daRows.Count == 0)
        {
            await LoadDaAsync();
            if (_daRows.Count == 0) return;
        }

        var counts = await BuildDaCalendarCountsAsync(month, year);
        foreach (var row in _daRows)
        {
            counts.TryGetValue(row.Military.Id, out var calendar);
            row.SetCalendarBase(calendar.Black, calendar.Red);
            row.ApplyCounts(calendar.Black, calendar.Red, manualOverride: false);
        }

        _daGrid.Items.Refresh();
        ApplyDaFilter();
        UpdateDaTotal();
        UpdateDaSelectedText();
        await SaveDaAsync(false);
        UpdateDaCalendarSourceText(competence, counts, 0);
        Info($"Despesa a Anular atualizada pela Escala Sgt Dia/calendário em {competence}.\n\nServiços na escala: {counts.Values.Sum(x => x.Black + x.Red)}\nPretas: {counts.Values.Sum(x => x.Black)}\nVermelhas: {counts.Values.Sum(x => x.Red)}");
    }

    private void AdjustSelectedDa(bool red, int delta)
    {
        if (_daGrid.SelectedItem is not AtDaRow row)
        {
            Warn("Selecione um militar na tabela de Despesa a Anular.");
            return;
        }
        if (red) row.RedDays = Math.Max(0, row.RedDays + delta);
        else row.BlackDays = Math.Max(0, row.BlackDays + delta);
        row.ManualOverride = true;
        _daGrid.Items.Refresh();
        UpdateDaTotal();
        UpdateDaSelectedText();
        _ = SaveDaAsync(false);
    }

    private void ResetSelectedDa()
    {
        if (_daGrid.SelectedItem is not AtDaRow row)
        {
            Warn("Selecione um militar na tabela de Despesa a Anular.");
            return;
        }
        row.BlackDays = 0;
        row.RedDays = 0;
        row.ManualOverride = true;
        _daGrid.Items.Refresh();
        UpdateDaTotal();
        UpdateDaSelectedText();
        _ = SaveDaAsync(false);
    }

    private async Task ResetAllDaAsync()
    {
        if (_daRows.Count == 0) return;
        if (!TryGetDaCompetence(out var competence, out _, out _)) return;
        if (!Confirm("Zerar Despesa a Anular", $"Zerar preta e vermelha dos {_daRows.Count} militares na competência {competence}?")) return;
        foreach (var row in _daRows)
        {
            row.BlackDays = 0;
            row.RedDays = 0;
            row.ManualOverride = true;
        }
        _daGrid.Items.Refresh();
        UpdateDaTotal();
        UpdateDaSelectedText();
        await SaveDaAsync(false);
        Info("Ajustes zerados para a competência atual.");
    }

    private async Task ImportBulletinsForDaAsync()
    {
        if (_daRows.Count == 0)
        {
            Warn("Não há militares que recebem Auxílio-Transporte carregados nesta competência.");
            return;
        }
        if (!TryGetDaCompetence(out var selectedCompetence, out var selectedMonth, out var selectedYear))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Selecionar boletins internos em PDF ou ZIP",
            Filter = "Boletins PDF ou ZIP (*.pdf;*.zip)|*.pdf;*.zip|PDF (*.pdf)|*.pdf|ZIP (*.zip)|*.zip",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;

        IsBusy = true;
        BusyMessage = $"Preparando {dialog.FileNames.Length} arquivo(s)…";
        _statusText.Text = "Lendo boletins e identificando militares de serviço…";
        var blocks = new List<AtBulletinImportBlock>();
        var errors = new List<string>();
        var tempDirectories = new List<string>();
        List<AtBulletinSourceFile> sources = [];
        try
        {
            sources = ExpandDaBulletinImportSources(dialog.FileNames, tempDirectories, errors).ToList();
            if (sources.Count == 0)
            {
                Warn("Nenhum PDF válido foi encontrado nos arquivos selecionados." +
                     (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(8)) : string.Empty));
                return;
            }

            for (var fileIndex = 0; fileIndex < sources.Count; fileIndex++)
            {
                var source = sources[fileIndex];
                BusyMessage = $"Importando boletim {fileIndex + 1}/{sources.Count}: {source.DisplayName}";
                try
                {
                    var text = await ExtractPdfTextAsync(source.Path);
                    if (string.IsNullOrWhiteSpace(text))
                        throw new InvalidOperationException("O PDF não retornou texto pesquisável.");
                    blocks.AddRange(ParseServiceBulletin(text, source.DisplayName));
                }
                catch (Exception ex)
                {
                    errors.Add(source.DisplayName + ": " + ex.Message);
                    await _log.WriteAsync("Falha ao importar boletim para Despesa a Anular: " + source.Path, ex);
                }
            }
        }
        finally
        {
            foreach (var directory in tempDirectories)
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
            IsBusy = false;
            BusyMessage = string.Empty;
        }

        var totalBlocksFound = blocks.Count;
        ApplyDaCorrections(blocks);
        blocks = blocks
            .Where(x => x.Date.Month == selectedMonth && x.Date.Year == selectedYear)
            .Where(x => x.PublishedNames.Count > 0 || x.Matched.Count > 0 || x.NotReceiving.Count > 0 || x.Unmatched.Count > 0 || x.Ambiguous.Count > 0 || x.RankMismatch.Count > 0)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.SourceFile)
            .ToList();
        if (blocks.Count == 0)
        {
            Warn($"Nenhuma escala de serviço válida foi encontrada para a competência {selectedCompetence}." +
                 (totalBlocksFound > 0 ? $"\n\nOs PDFs possuíam {totalBlocksFound} dia(s) de escala, mas de outra competência." : string.Empty) +
                 (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(8)) : string.Empty));
            return;
        }

        var importDialog = BuildBulletinImportDialog(blocks, dialog.FileNames.Length, errors);
        if (importDialog.ShowDialog() != true) return;

        foreach (var row in _daRows)
        {
            row.ApplyCounts(0, 0, manualOverride: true);
        }
        var byId = _daRows.ToDictionary(x => x.Military.Id);
        var appliedBlack = 0;
        var appliedRed = 0;
        var duplicateServicesIgnored = 0;
        var appliedPerMilitaryDay = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            var isRed = block.Type.Equals("Vermelha", StringComparison.OrdinalIgnoreCase);
            foreach (var military in block.Matched.DistinctBy(x => x.Id))
            {
                if (!byId.TryGetValue(military.Id, out var row)) continue;
                var key = military.Id.ToString(CultureInfo.InvariantCulture) + "|" + block.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!appliedPerMilitaryDay.Add(key))
                {
                    duplicateServicesIgnored++;
                    continue;
                }
                if (isRed) { row.RedDays++; appliedRed++; }
                else { row.BlackDays++; appliedBlack++; }
                row.ManualOverride = true;
            }
        }

        _daGrid.Items.Refresh();
        ApplyDaFilter();
        UpdateDaTotal();
        UpdateDaSelectedText();
        await SaveDaAsync(false);
        _daCalendarSourceText.Text = $"Base {selectedCompetence}: PDF/ZIP importado ({blocks.Count} dia(s) da competência, {appliedBlack} preta(s), {appliedRed} vermelha(s), {duplicateServicesIgnored} duplicidade(s) ignorada(s)).";
        _statusText.Text = $"Boletins importados: {blocks.Count} dia(s), {appliedBlack} preta(s), {appliedRed} vermelha(s) e {duplicateServicesIgnored} duplicidade(s) ignorada(s).";
        Info($"Importação concluída.\n\nPDFs lidos: {sources.Count}\nDias encontrados: {blocks.Count}\nLançamentos PRETA: {appliedBlack}\nLançamentos VERMELHA: {appliedRed}\nDuplicidades ignoradas: {duplicateServicesIgnored}\nMilitares com desconto final: {_daRows.Count(x => x.NetDays > 0)}" +
             (errors.Count > 0 ? $"\n\nArquivos com aviso: {errors.Count}" : string.Empty));
    }

    private async Task ImportSavedBulletinsForDaAsync()
    {
        if (_daRows.Count == 0)
        {
            Warn("Não há militares que recebem Auxílio-Transporte carregados nesta competência.");
            return;
        }

        if (!TryGetDaCompetence(out var competence, out var month, out var year))
            return;

        var saved = await BuildSavedDaBulletinSourcesAsync(month, year);

        if (saved.Count == 0)
        {
            Warn($"Nenhum boletim salvo com PDF existente foi encontrado para {competence}.");
            return;
        }

        var savedBulletinsSummary = FormatSavedDaBulletinSummary(saved);
        if (!Confirm("Usar boletins salvos",
                $"Ler {saved.Count} boletim(ns) salvo(s), incluindo publicações dos meses vizinhos. Somente serviços de {competence} serão aplicados para preencher a Despesa a Anular?\n\n{savedBulletinsSummary}"))
            return;

        IsBusy = true;
        BusyMessage = $"Preparando {saved.Count} boletim(ns) salvo(s)…";
        _statusText.Text = "Lendo boletins salvos e identificando militares de serviço…";
        var blocks = new List<AtBulletinImportBlock>();
        var errors = new List<string>();

        try
        {
            for (var index = 0; index < saved.Count; index++)
            {
                var file = saved[index];
                BusyMessage = $"Importando boletim salvo {index + 1}/{saved.Count}: {file.FileName}";
                try
                {
                    var text = file.IndexedFile is not null
                        ? await App.IntelligentBulletins.ReadCachedTextAsync(file.IndexedFile)
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                        text = await ExtractPdfTextAsync(file.PdfPath);
                    if (string.IsNullOrWhiteSpace(text))
                        throw new InvalidOperationException("O PDF salvo não retornou texto pesquisável.");

                    var display = $"{file.DisplayNumber} - {file.DisplayDate} - {file.FileName}".Trim(' ', '-');
                    blocks.AddRange(ParseServiceBulletin(text, display).Where(x => x.Date.Month == month && x.Date.Year == year));
                }
                catch (Exception ex)
                {
                    var label = string.IsNullOrWhiteSpace(file.FileName) ? Path.GetFileName(file.PdfPath) : file.FileName;
                    errors.Add(label + ": " + ex.Message);
                    await _log.WriteAsync("Falha ao importar boletim salvo para Despesa a Anular: " + file.PdfPath, ex);
                }
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }

        ApplyDaCorrections(blocks);
        blocks = blocks
            .Where(x => x.PublishedNames.Count > 0 || x.Matched.Count > 0 || x.NotReceiving.Count > 0 || x.Unmatched.Count > 0 || x.Ambiguous.Count > 0 || x.RankMismatch.Count > 0)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.SourceFile)
            .ToList();
        if (blocks.Count == 0)
        {
            Warn("Nenhuma escala de serviço válida foi encontrada nos boletins salvos." +
                 (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(8)) : string.Empty));
            return;
        }

        var importDialog = BuildBulletinImportDialog(blocks, saved.Count, errors);
        if (importDialog.ShowDialog() != true) return;

        foreach (var row in _daRows)
        {
            row.ApplyCounts(0, 0, manualOverride: true);
        }
        var byId = _daRows.ToDictionary(x => x.Military.Id);
        var appliedBlack = 0;
        var appliedRed = 0;
        var duplicateServicesIgnored = 0;
        var appliedPerMilitaryDay = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            var isRed = block.Type.Equals("Vermelha", StringComparison.OrdinalIgnoreCase);
            foreach (var military in block.Matched.DistinctBy(x => x.Id))
            {
                if (!byId.TryGetValue(military.Id, out var row)) continue;
                var key = military.Id.ToString(CultureInfo.InvariantCulture) + "|" + block.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!appliedPerMilitaryDay.Add(key))
                {
                    duplicateServicesIgnored++;
                    continue;
                }
                if (isRed) { row.RedDays++; appliedRed++; }
                else { row.BlackDays++; appliedBlack++; }
                row.ManualOverride = true;
            }
        }

        _daGrid.Items.Refresh();
        ApplyDaFilter();
        UpdateDaTotal();
        UpdateDaSelectedText();
        await SaveDaAsync(false);
        _daCalendarSourceText.Text = $"Base {competence}: boletins salvos importados ({blocks.Count} dia(s), {appliedBlack} preta(s), {appliedRed} vermelha(s), {duplicateServicesIgnored} duplicidade(s) ignorada(s)).";
        _statusText.Text = $"Boletins salvos importados: {blocks.Count} dia(s), {appliedBlack} preta(s), {appliedRed} vermelha(s) e {duplicateServicesIgnored} duplicidade(s) ignorada(s).";
        Info($"Importação dos boletins salvos concluída.\n\nBoletins lidos: {saved.Count}\nDias encontrados: {blocks.Count}\nLançamentos PRETA: {appliedBlack}\nLançamentos VERMELHA: {appliedRed}\nDuplicidades ignoradas: {duplicateServicesIgnored}\nMilitares com desconto final: {_daRows.Count(x => x.NetDays > 0)}" +
             (errors.Count > 0 ? $"\n\nArquivos com aviso: {errors.Count}" : string.Empty));
    }

    private async Task<List<DaSavedBulletinSource>> BuildSavedDaBulletinSourcesAsync(int month, int year)
    {
        var result = new List<DaSavedBulletinSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string pdfPath, string fileName, DateTime date, string number, IntelligentBulletinFile? indexedFile)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath)) return;
            var full = Path.GetFullPath(pdfPath);
            if (!seen.Add(full)) return;
            result.Add(new DaSavedBulletinSource(
                full,
                string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(full) : fileName,
                date,
                string.IsNullOrWhiteSpace(number) ? "—" : number,
                indexedFile));
        }

        var store = await App.IntelligentBulletins.LoadAsync();
        foreach (var file in store.Items)
        {
            if (string.IsNullOrWhiteSpace(file.PdfPath) || !File.Exists(file.PdfPath)) continue;
            var parsedFromName = TryParseSavedBulletinFileName(file.PdfPath, out var nameDate, out var nameNumber);
            var hasDate = TryParseSavedBulletinDate(file.DateIso, file.BulletinDate, out var date);
            if (!hasDate && parsedFromName) date = nameDate;
            if (!hasDate && !parsedFromName) continue;
            if (date < new DateTime(year, month, 1).AddMonths(-1) || date >= new DateTime(year, month, 1).AddMonths(2)) continue;

            var number = !string.IsNullOrWhiteSpace(file.BulletinNumber) && file.BulletinNumber != "—"
                ? file.BulletinNumber
                : nameNumber;
            Add(file.PdfPath, file.FileName, date, number, file);
        }

        if (Directory.Exists(_paths.IntelligentBulletinLibraryDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_paths.IntelligentBulletinLibraryDirectory, "*.pdf", SearchOption.AllDirectories))
            {
                if (!TryParseSavedBulletinFileName(path, out var date, out var number)) continue;
                if (date < new DateTime(year, month, 1).AddMonths(-1) || date >= new DateTime(year, month, 1).AddMonths(2)) continue;
                Add(path, Path.GetFileName(path), date, number, null);
            }
        }

        return result
            .OrderBy(x => x.Date)
            .ThenBy(x => int.TryParse(Regex.Match(x.DisplayNumber, @"\d+").Value, out var n) ? n : int.MaxValue)
            .ThenBy(x => x.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string FormatSavedDaBulletinSummary(IReadOnlyList<DaSavedBulletinSource> saved, int limit = 30)
    {
        var lines = new List<string> { "Boletins que serão lidos:" };
        foreach (var file in saved.Take(limit))
        {
            var number = string.IsNullOrWhiteSpace(file.DisplayNumber) || file.DisplayNumber == "—"
                ? "sem número"
                : $"Nr {file.DisplayNumber}";
            lines.Add($"- BI {number}, de {file.DisplayDate}");
        }

        if (saved.Count > limit)
            lines.Add($"- ... e mais {saved.Count - limit} boletim(ns).");

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<Dictionary<int, DaCalendarCount>> BuildDaCalendarCountsAsync(int month, int year)
    {
        var result = new Dictionary<int, DaCalendarCount>();
        try
        {
            var store = await App.DutyRoster.LoadAsync() ?? new DutyRosterStore();
            store.Months ??= new(StringComparer.OrdinalIgnoreCase);
            await App.RedDays.MigrateDutyRosterRedDaysAsync(store);
            var redDayStore = await App.RedDays.LoadAsync();
            if (!store.Months.TryGetValue(DutyRosterService.MonthKey(year, month), out var rosterMonth))
                return result;

            var appliedPerMilitaryDay = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assignment in rosterMonth.Assignments)
            {
                if (!DateTime.TryParseExact(assignment.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (date.Month != month || date.Year != year) continue;
                if (!TryGetDutyRosterMilitaryId(assignment.Value, out var militaryId)) continue;
                if (!appliedPerMilitaryDay.Add(militaryId.ToString(CultureInfo.InvariantCulture) + "|" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
                    continue;

                result.TryGetValue(militaryId, out var current);
                var isRed = DutyRosterService.IsRedDay(rosterMonth, date, redDayStore);
                result[militaryId] = isRed
                    ? current with { Red = current.Red + 1 }
                    : current with { Black = current.Black + 1 };
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao calcular DA pela Escala Sgt Dia/calendário.", ex);
        }
        return result;
    }

    private void UpdateDaCalendarSourceText(string competence, IReadOnlyDictionary<int, DaCalendarCount> counts, int manualOverrides)
    {
        var serviceTotal = counts.Values.Sum(x => x.Black + x.Red);
        if (serviceTotal > 0)
        {
            var black = counts.Values.Sum(x => x.Black);
            var red = counts.Values.Sum(x => x.Red);
            _daCalendarSourceText.Text =
                $"Base {competence}: {serviceTotal} lançamento(s) aplicados por boletim/PDF ({black} preta(s), {red} vermelha(s)). " +
                $"{manualOverrides} ajuste(s) preservado(s).";
            return;
        }

        _daCalendarSourceText.Text =
            $"Base {competence}: carregue a DA por “Importar PDF/ZIP” ou “Boletins salvos”. " +
            $"{manualOverrides} militar(es) já têm ajuste salvo nesta competência.";
    }

    private static bool TryGetDutyRosterMilitaryId(string personKey, out int militaryId)
    {
        militaryId = 0;
        if (string.IsNullOrWhiteSpace(personKey)) return false;
        var value = personKey.Trim();
        if (value.StartsWith("M:", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out militaryId) && militaryId > 0;
    }

    private bool TryGetDaCompetence(out string competence, out int month, out int year)
    {
        month = 0;
        year = 0;
        competence = string.Empty;

        if (_daMonthBox.SelectedItem is ComboBoxItem selectedMonth && selectedMonth.Tag is int taggedMonth)
            month = taggedMonth;
        else if (_daMonthBox.SelectedIndex >= 0)
            month = _daMonthBox.SelectedIndex + 1;

        var selectedYear = _daYearBox.SelectedItem?.ToString();
        var rawYear = !string.IsNullOrWhiteSpace(_daYearBox.Text) ? _daYearBox.Text : selectedYear;
        rawYear = Regex.Match(rawYear ?? string.Empty, @"\d{4}").Value;
        if (!int.TryParse(rawYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out year) || year < 2000 || year > 2100 || month is < 1 or > 12)
        {
            Warn("Selecione mês e ano válidos para a Despesa a Anular.");
            month = 0;
            year = 0;
            return false;
        }

        var date = new DateTime(year, month, 1);
        competence = date.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        _daMonthBox.SelectedIndex = month - 1;
        var yearText = year.ToString(CultureInfo.InvariantCulture);
        if (!_daYearBox.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), yearText, StringComparison.OrdinalIgnoreCase)))
        {
            var years = _daYearBox.Items.Cast<object>().Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            years.Add(yearText);
            _daYearBox.ItemsSource = years.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        }
        _daYearBox.SelectedItem = yearText;
        _daYearBox.Text = yearText;
        return true;
    }

    private bool TryPeekDaCompetence(out string competence, out int month, out int year)
    {
        month = 0;
        year = 0;
        competence = string.Empty;

        if (_daMonthBox.SelectedItem is ComboBoxItem selectedMonth && selectedMonth.Tag is int taggedMonth)
            month = taggedMonth;
        else if (_daMonthBox.SelectedIndex >= 0)
            month = _daMonthBox.SelectedIndex + 1;

        var selectedYear = _daYearBox.SelectedItem?.ToString();
        var rawYear = !string.IsNullOrWhiteSpace(_daYearBox.Text) ? _daYearBox.Text : selectedYear;
        rawYear = Regex.Match(rawYear ?? string.Empty, @"\d{4}").Value;
        if (!int.TryParse(rawYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out year) || year < 2000 || year > 2100 || month is < 1 or > 12)
        {
            month = 0;
            year = 0;
            return false;
        }

        competence = new DateTime(year, month, 1).ToString("MM/yyyy", CultureInfo.InvariantCulture);
        return true;
    }

    private void SyncReportDaCompetenceFromDaSelection()
    {
        if (!TryPeekDaCompetence(out _, out var month, out var year)) return;
        SyncReportDaCompetence(month, year);
    }

    private void SyncReportDaCompetence(int month, int year)
    {
        if (month is < 1 or > 12 || year < 2000 || year > 2100) return;
        if (_reportDaMonthBox.Items.Count >= 12 && _reportDaMonthBox.SelectedIndex != month - 1)
            _reportDaMonthBox.SelectedIndex = month - 1;

        var yearText = year.ToString(CultureInfo.InvariantCulture);
        if (!_reportDaYearBox.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), yearText, StringComparison.OrdinalIgnoreCase)))
        {
            var years = _reportDaYearBox.Items.Cast<object>()
                .Select(x => x?.ToString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            years.Add(yearText);
            _reportDaYearBox.ItemsSource = years.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        }

        if (!string.Equals(_reportDaYearBox.SelectedItem?.ToString(), yearText, StringComparison.OrdinalIgnoreCase))
            _reportDaYearBox.SelectedItem = yearText;
        if (!string.Equals(_reportDaYearBox.Text, yearText, StringComparison.OrdinalIgnoreCase))
            _reportDaYearBox.Text = yearText;
    }

    private IReadOnlyList<AtBulletinSourceFile> ExpandDaBulletinImportSources(IEnumerable<string> selectedFiles, List<string> tempDirectories, List<string> errors)
    {
        var result = new List<AtBulletinSourceFile>();
        foreach (var selectedFile in selectedFiles.Where(File.Exists))
        {
            var extension = Path.GetExtension(selectedFile);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new AtBulletinSourceFile(selectedFile, Path.GetFileName(selectedFile)));
                continue;
            }

            if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Path.GetFileName(selectedFile) + ": formato ignorado. Selecione PDF ou ZIP.");
                continue;
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "SIGFUR", "da_boletins", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDirectory);
                tempDirectories.Add(tempDirectory);
                using var archive = ZipFile.OpenRead(selectedFile);
                var pdfEntries = archive.Entries
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (pdfEntries.Count == 0)
                {
                    errors.Add(Path.GetFileName(selectedFile) + ": ZIP sem boletins PDF.");
                    continue;
                }

                foreach (var entry in pdfEntries)
                {
                    var safeParts = entry.FullName.Replace('\\', '/')
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => Regex.Replace(x, @"[^A-Za-z0-9_. -]+", "_").Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x) && x != "." && x != "..")
                        .ToArray();
                    if (safeParts.Length == 0) continue;
                    var destination = Path.Combine(new[] { tempDirectory }.Concat(safeParts).ToArray());
                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    entry.ExtractToFile(destination, true);
                    result.Add(new AtBulletinSourceFile(destination, Path.GetFileName(selectedFile) + " > " + entry.FullName));
                }
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(selectedFile) + ": " + ex.Message);
            }
        }
        return result;
    }

    private Window BuildBulletinImportDialog(IReadOnlyList<AtBulletinImportBlock> blocks, int fileCount, IReadOnlyList<string> errors)
    {
        var dialog = new Window
        {
            Title = "Importar boletins para Despesa a Anular",
            Owner = Application.Current?.MainWindow,
            Width = 1120,
            Height = 780,
            MinWidth = 880,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        AttachPersistentPlacement(dialog, "importar_boletim_despesa_anular");
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = new TextBlock { Text = "Confira os dias e os militares detectados", FontSize = 19, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        header.Children.Add(title);
        header.Children.Add(new TextBlock
        {
            Text = "Cada data pode ser alterada para PRETA ou VERMELHA antes da aplicação. Somente correspondências seguras e militares que recebem AT serão lançados.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        var coverage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold };
        var dates = blocks.Select(x => x.Date.Date).Distinct().Order().ToList();
        var pending = blocks.Sum(x => x.Unmatched.Count + x.Ambiguous.Count);
        if (dates.Count > 0)
        {
            var first = new DateTime(dates[0].Year, dates[0].Month, 1);
            var missing = Enumerable.Range(0, DateTime.DaysInMonth(first.Year, first.Month)).Select(offset => first.AddDays(offset)).Where(x => !dates.Contains(x)).ToList();
            coverage.Text = $"Cobertura: {dates.Count} dias · {pending} identificação(ões) pendente(s) · {blocks.Count(x => x.IsCorrection)} substituição(ões).";
            if (missing.Count > 0) coverage.Text += " Dias sem escala localizada: " + string.Join(", ", missing.Select(x => x.ToString("dd/MM"))) + ".";
            if (pending > 0) coverage.Text += " Revise as pendências abaixo; elas não entram automaticamente no total.";
        }
        header.Children.Add(coverage);
        root.Children.Add(header);

        var stack = new StackPanel();
        foreach (var block in blocks)
        {
            var card = Card(new Thickness(0, 0, 0, 8));
            var panel = new StackPanel();
            card.Child = panel;
            var top = new DockPanel();
            var date = new TextBlock
            {
                Text = $"{block.Date:dd/MM/yyyy}  |  {block.DayLabel}  |  {block.SourceFile}",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Bold,
                FontSize = 14
            };
            date.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
            DockPanel.SetDock(date, Dock.Left);
            top.Children.Add(date);
            var combo = new ComboBox { Width = 125, ItemsSource = new[] { "Preta", "Vermelha" }, SelectedItem = block.Type };
            combo.SelectionChanged += (_, _) => block.Type = combo.SelectedItem?.ToString() ?? block.Type;
            DockPanel.SetDock(combo, Dock.Right);
            top.Children.Add(combo);
            panel.Children.Add(top);

            panel.Children.Add(new TextBlock
            {
                Text = $"Publicados: {block.PublishedNames.Count}  •  Aplicáveis no AT: {block.Matched.Count}  •  Fora do AT: {block.NotReceiving.Count}  •  Divergência de P/G: {block.RankMismatch.Count}  •  Não casados: {block.Unmatched.Count}  •  Ambíguos: {block.Ambiguous.Count}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 7, 0, 3),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(CreateNamesLine("Aplicáveis: ", block.Matched, "—"));
            if (block.NotReceiving.Count > 0) panel.Children.Add(CreateNamesLine("Fora do AT: ", block.NotReceiving, "—"));
            if (block.RankMismatch.Count > 0) panel.Children.Add(TextLine("Divergência de P/G: " + string.Join(", ", block.RankMismatch.Take(20))));
            if (block.Unmatched.Count > 0) panel.Children.Add(TextLine("Não casados: " + string.Join(", ", block.Unmatched.Take(20))));
            if (block.Ambiguous.Count > 0) panel.Children.Add(TextLine("Ambíguos: " + string.Join(", ", block.Ambiguous.Take(20))));
            stack.Children.Add(card);
        }
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summary = new TextBlock
        {
            Text = $"{dates.Count} dias distintos em {fileCount} PDF(s)." + (errors.Count > 0 ? $" {errors.Count} arquivo(s) tiveram aviso." : string.Empty),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };
        footer.Children.Add(summary);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Cancelar", (_, _) => { dialog.DialogResult = false; dialog.Close(); }, "SecondaryButtonStyle"));
        actions.Children.Add(Button("Aplicar na Despesa a Anular", (_, _) => { dialog.DialogResult = true; dialog.Close(); }, "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        dialog.Content = root;
        dialog.Loaded += (_, _) => InstallContextMenusRecursive(dialog.Content as DependencyObject);
        return dialog;
    }

    private TextBlock CreateNamesLine(string prefix, IEnumerable<MilitaryRecord> military, string empty)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        line.Inlines.Add(new Run(prefix) { FontWeight = FontWeights.SemiBold });
        var list = military.DistinctBy(x => x.Id).Take(24).ToList();
        if (list.Count == 0) line.Inlines.Add(new Run(empty));
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) line.Inlines.Add(new Run(", "));
            AddNameWithWarBold(line, list[i], false);
        }
        return line;
    }

    private static TextBlock TextLine(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    private List<AtBulletinImportBlock> ParseServiceBulletin(string text, string sourceFile)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var sectionMatch = Regex.Match(normalized, @"ESCALA\s+DE\s+SERVI[ÇC]O(?<v>.*?)(?:\n\s*2\s*[ªº°A]\s*PARTE|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var section = sectionMatch.Success ? sectionMatch.Groups["v"].Value : normalized;
        var dateRegex = new Regex(@"\bDIA\s+(?<d>\d{1,2})\s+(?<m>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{3,12})\.?\s+(?<y>\d{2,4})\s*\((?<w>[^)]*)\)", RegexOptions.IgnoreCase);
        var matches = dateRegex.Matches(section).Cast<Match>().ToList();
        var result = new List<AtBulletinImportBlock>();
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (!TryParseBulletinDate(match, out var date)) continue;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : section.Length;
            var body = section.Substring(match.Index + match.Length, Math.Max(0, end - match.Index - match.Length));
            var stop = Regex.Match(body, @"(?:\n\s*2\s*[ªº°A]\s*PARTE|\n\s*\d+\.\s*Altera[çc][ãa]o)", RegexOptions.IgnoreCase);
            if (stop.Success) body = body[..stop.Index];
            foreach (Match explicitDate in Regex.Matches(body, @"(?im)^[^\n]*(?:Seguran[çc]a|Escolta)[^\n]*\bdia\s+(?<d>\d{1,2})\s+(?<m>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{3,12})\s+(?<y>\d{2,4})[^\n]*").Cast<Match>().Reverse())
            {
                if (!TryParseBulletinDate(explicitDate, out var missionDate) || missionDate == date) continue;
                var start = explicitDate.Index;
                if (start > 0)
                {
                    var previousStart = body.LastIndexOf('\n', Math.Max(0, start - 2)) + 1;
                    var previous = body[previousStart..start].Trim();
                    if (!previous.EndsWith('.') && Regex.IsMatch(previous, @"\b(?:Sgt|Sd|Cb|Ten)\b", RegexOptions.IgnoreCase)) start = previousStart;
                }
                var endMission = explicitDate.Index + explicitDate.Length;
                while (endMission < body.Length && !body[start..endMission].TrimEnd().EndsWith('.'))
                {
                    var next = body.IndexOf('\n', endMission + 1); endMission = next < 0 ? body.Length : next;
                }
                var missionText = body[start..endMission];
                var missionPersons = ExtractBulletinPersons(missionText);
                var mission = new AtBulletinImportBlock { Date = missionDate, SourceFile = sourceFile, DayLabel = "Data expressa da missão", Type = ClassifyDaDay(missionDate, ""), PublishedNames = missionPersons.Select(x => x.Name).ToList() };
                MatchBulletinPersons(mission, missionPersons); result.Add(mission);
                body = body.Remove(start, endMission - start);
            }
            var persons = ExtractBulletinPersons(body);
            if (persons.Count == 0) continue;
            var block = new AtBulletinImportBlock
            {
                Date = date,
                DayLabel = match.Groups["w"].Value.Trim(),
                SourceFile = sourceFile,
                Type = ClassifyDaDay(date, match.Groups["w"].Value),
                PublishedNames = persons.Select(x => x.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList()
            };
            MatchBulletinPersons(block, persons);
            result.Add(block);
        }
        var correctionText = Regex.Replace(section, @"(?im)^.*Continua[çc][ãa]o.*$", " ");
        correctionText = Regex.Replace(correctionText, @"(?m)^\s*[-,]+\s*$", " ");
        correctionText = Regex.Replace(correctionText, @"\s+", " ");
        var corrections = Regex.Matches(correctionText,
            @"Entrou\s+de\s+servi[çc]o.*?\bno\s+dia\s+(?<d>\d{1,2})\s+(?<m>[A-ZÇÁÀÂÃÉÊÍÓÔÕÚÜ]{3,12})\s+(?<y>\d{2,4}),?\s+o\s+(?<incoming>.*?)\s*,?\s*em\s+substitui[çc][ãa]o\s+ao?\s+(?<outgoing>.*?)(?=\.\s*(?:-?Entrou|Em\s+consequ|$))", RegexOptions.IgnoreCase);
        foreach (Match correction in corrections)
        {
            if (!TryParseBulletinDate(correction, out var date)) continue;
            var entrants = ExtractBulletinPersons(correction.Groups["incoming"].Value);
            var outgoing = ExtractBulletinPersons(correction.Groups["outgoing"].Value);
            var block = new AtBulletinImportBlock { Date = date, DayLabel = "Substituição publicada", SourceFile = sourceFile, Type = ClassifyDaDay(date, ""), IsCorrection = true, PublishedNames = entrants.Select(x => x.Name).ToList() };
            MatchBulletinPersons(block, entrants);
            var removed = new AtBulletinImportBlock(); MatchBulletinPersons(removed, outgoing);
            if (entrants.Count == 1 && outgoing.Count == 1 && block.Matched.Count + block.NotReceiving.Count == 1 && removed.Matched.Count + removed.NotReceiving.Count == 1)
                block.ReplacedMilitaryIds = removed.Matched.Concat(removed.NotReceiving).Select(x => x.Id).ToList();
            else
            {
                block.Unmatched.Add("Revisar substituição: " + correction.Value);
                block.Matched.Clear(); block.NotReceiving.Clear();
            }
            result.Add(block);
        }
        return result;
    }

    private static void ApplyDaCorrections(List<AtBulletinImportBlock> blocks)
    {
        // A publication may exchange several duties on the same day. Remove the outgoing
        // group first, then retain all entrants; this preserves circular exchanges.
        var preceding = new List<AtBulletinImportBlock>();
        foreach (var publication in blocks.GroupBy(x => x.SourceFile))
        {
            var current = publication.ToList();
            foreach (var correction in current.Where(x => x.IsCorrection))
                foreach (var prior in preceding.Concat(current.Where(x => !x.IsCorrection)).Where(x => x.Date == correction.Date))
                    prior.Matched.RemoveAll(x => correction.ReplacedMilitaryIds.Contains(x.Id));
            preceding.AddRange(current);
        }
    }

    private void MatchBulletinPersons(AtBulletinImportBlock block, IReadOnlyList<AtBulletinPerson> persons)
    {
        var allAliases = _allMilitary.Select(m => new
        {
            Military = m,
            Rank = BulletinRankKey(m.Rank),
            Aliases = MilitaryBulletinAliases(m)
        }).ToList();

        foreach (var person in persons)
        {
            var alias = NormalizeBulletinName(person.Name);
            if (string.IsNullOrWhiteSpace(alias)) continue;

            var sameRank = allAliases
                .Where(x => x.Rank == person.RankKey && MilitaryAliasMatches(x.Aliases, alias))
                .Select(x => x.Military)
                .DistinctBy(x => x.Id)
                .ToList();

            var resolvedSameRank = ResolveAmbiguousBulletinMatch(alias, sameRank);
            if (TryApplyDaMatch(block, resolvedSameRank))
                continue;

            if (sameRank.Count > 1)
            {
                block.Ambiguous.Add(DescribeAmbiguousBulletinPerson(person.Name, sameRank));
                continue;
            }

            var anyRank = allAliases
                .Where(x => MilitaryAliasMatches(x.Aliases, alias))
                .Select(x => x.Military)
                .DistinctBy(x => x.Id)
                .ToList();

            var resolvedAnyRank = ResolveAmbiguousBulletinMatch(alias, anyRank);
            if (resolvedAnyRank is not null)
            {
                // A escala às vezes vem com abreviação genérica (ex.: "Sd" em vez de "Sd Ef Vrv")
                // ou com quebra de linha; para DA é melhor aplicar o militar certo e só registrar o aviso.
                block.RankMismatch.Add(person.Name);
                TryApplyDaMatch(block, resolvedAnyRank);
            }
            else if (anyRank.Count > 1)
            {
                block.Ambiguous.Add(DescribeAmbiguousBulletinPerson(person.Name, anyRank));
            }
            else
            {
                block.Unmatched.Add(person.Name);
            }
        }

        block.Matched = block.Matched.DistinctBy(x => x.Id).ToList();
        block.NotReceiving = block.NotReceiving.DistinctBy(x => x.Id).ToList();
    }

    private static bool TryApplyDaMatch(AtBulletinImportBlock block, MilitaryRecord? military)
    {
        if (military is null) return false;
        if (MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached) block.Matched.Add(military);
        else block.NotReceiving.Add(military);
        return true;
    }

    private static List<AtBulletinPerson> ExtractBulletinPersons(string body)
    {
        var text = Regex.Replace(body ?? string.Empty, @"(?im)^.*\bPerito\b.*$", " ");
        text = Regex.Replace(text, @"(?im)^\(Continuaç[aã]o.*$", " ");
        text = Regex.Replace(text, @"(?im)^\s*(?:Gd\s+Rsd\s+Cmt\s+4[ªº°]?\s*RM|Gd\s+QG\s+[Il12]+)\s*$", " ");
        // Table labels may sit in the middle of a wrapped list (or even of "Sd Ef Vrv").
        // Remove the label, preserving both sides; truncation would lose real names.
        text = Regex.Replace(text, @"(?ix)\b(?:
            (?:Sombra\s+|Auxiliar\s+)?Sgt\s+Dia(?:\s+4[ªº°]?\s+Cia\s+PE)? |
            Cb\s+Dia | (?:Cmt\s+)?Gd\s+Rsd\s+Cmt\s+4[ªº°]?\s*RM |
            Gd\s+QG\s+[Il12]+ | Plant[ãa]o\s+ao\s+Alojamento |
            CAF\s+EsFECEx\s+Policiamento | Tr[âa]nsito\s+Formatura\s+Veterano |
            Escolta\s+de\s+Preso | Seguran[çc]a\s+(?:do\s+Preso|ao\s+Pmed)(?:\s*\([^)]*\))? |
            Miss[ãa]o | Miss[õo]es )(?=\s|[,.;]|$)", " ");
        text = Regex.Replace(text.Replace('\n', ' '), @"\s+", " ").Trim();

        var markers = BulletinRankMarkers();
        var pattern = string.Join("|", markers.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape));
        var matches = Regex.Matches(text, @"(?<![\p{L}\d])(?:" + pattern + @")(?![\p{L}\d])", RegexOptions.IgnoreCase).Cast<Match>().ToList();
        var result = new List<AtBulletinPerson>();
        var cut = new Regex(@"\b(?:SERVI[ÇC]OS?\s+EXTERNOS|SERVI[ÇC]OS?\s+INTERNOS|MISS[ÕO]ES|ADJ\s+OF\s+DIA|OF\s+DIA|CB\s+DIA|SGT\s+DIA|GD\s+QG|GD\s+RSD|CMT\s+GD\s+RSD|PLANT[ÃA]O\s+AO\s+ALOJAMENTO|PLANT[ÃA]O\s+A\s+RESERVA(?:\s+DE\s+ARMAMENTO)?|CIN[ÓO]FILO|SOMBRA\s+CIN[ÓO]FILO|MOT\s+DIA|MOTORISTA\s+DE\s+DIA|PERMAN[ÊE]NCIA\s+AO\s+PMED|POSTO\s+M[ÉE]DICO|ESTAFETA|BALIZAMENTO|GUARDA|CMT\s+DA\s+GUARDA|APOIOS?|APOIO\b|DIA\s+\d{1,2}\s+[A-ZÇÁÀÂÃÉÊÍÓÔÕÚÜ]{3,12})\b", RegexOptions.IgnoreCase);
        for (var i = 0; i < matches.Count; i++)
        {
            var marker = matches[i];
            var key = markers.First(x => x.Key.Equals(marker.Value, StringComparison.OrdinalIgnoreCase)).Value;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var segment = text.Substring(marker.Index + marker.Length, Math.Max(0, end - marker.Index - marker.Length));
            var cutMatch = cut.Match(segment);
            if (cutMatch.Success) segment = segment[..cutMatch.Index];

            foreach (var name in SplitBulletinNames(segment))
                result.Add(new AtBulletinPerson(key, name));
        }
        return result
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .DistinctBy(x => x.RankKey + "|" + NormalizeBulletinName(x.Name))
            .ToList();
    }

    private static Dictionary<string, string> BulletinRankMarkers() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Al CFST"] = "al_cfst", ["Sgt"] = "sgt", ["Asp Of"] = "asp", ["Aspirante"] = "asp", ["Al/CFST"] = "al_cfst", ["Aluno CFST"] = "al_cfst",
        ["2º Ten"] = "2_ten", ["2° Ten"] = "2_ten", ["2 Ten"] = "2_ten", ["1º Ten"] = "1_ten", ["1° Ten"] = "1_ten", ["1 Ten"] = "1_ten",
        ["Cap"] = "cap", ["Capitão"] = "cap", ["Capitao"] = "cap", ["S Ten"] = "s_ten", ["STen"] = "s_ten", ["Subten"] = "s_ten",
        ["1º Sgt"] = "1_sgt", ["1° Sgt"] = "1_sgt", ["1 Sgt"] = "1_sgt", ["2º Sgt"] = "2_sgt", ["2° Sgt"] = "2_sgt", ["2 Sgt"] = "2_sgt",
        ["3º Sgt"] = "3_sgt", ["3° Sgt"] = "3_sgt", ["3 Sgt"] = "3_sgt", ["Cb Ef Profl"] = "cb_ep", ["Cb EP"] = "cb_ep", ["Cabo"] = "cb_ep", ["Cb"] = "cb_ep",
        ["Sd Ef Profl"] = "sd_ep", ["Sd EP"] = "sd_ep", ["Soldado Ef Profl"] = "sd_ep", ["Soldado"] = "sd_ep", ["Sd Ef Vrv"] = "sd_ev", ["Sd EV"] = "sd_ev", ["Sd Vrv"] = "sd_ev", ["Sd"] = "sd_ep"
    };

    private static IEnumerable<string> SplitBulletinNames(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) yield break;

        // Normaliza nomes quebrados pelo PDF, como "R. AZEVEDO" e "Gd QG II" grudado no bloco.
        segment = Regex.Replace(segment, @"\s+", " ").Trim(' ', '.', '-', ',', ';', ':', '/');
        segment = Regex.Replace(segment, @"\b([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ])\.\s+([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ])", "$1.$2", RegexOptions.IgnoreCase);
        if (string.IsNullOrWhiteSpace(segment)) yield break;

        var rankPattern = string.Join("|", BulletinRankMarkers().Keys.OrderByDescending(x => x.Length).Select(Regex.Escape));
        segment = Regex.Replace(segment, $@"\b(?:{rankPattern})\b", ",", RegexOptions.IgnoreCase);

        foreach (var raw in segment.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Regex.Replace(raw, @"\s+", " ").Trim(' ', '.', '-', ',', ';', ':', '/');
            name = Regex.Replace(name, @"^(?:E|OU)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!IsLikelyBulletinPersonName(name)) continue;
            yield return name;
        }
    }

    private static bool IsLikelyBulletinPersonName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 2 || name.Any(char.IsDigit)) return false;
        var normalized = NormalizeBulletinName(name);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (Regex.IsMatch(normalized, @"^(?:E|OU|DE|EF|DIA|NIL|SEM|FOLGA|ALTERACAO|ALTERACOES|CONTINUACAO|SERVICO|SERVICOS|EXTERNOS|INTERNOS|APOIO|APOIOS|MISSOES)$", RegexOptions.IgnoreCase)) return false;
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6) return false;
        return true;
    }

    private static HashSet<string> MilitaryBulletinAliases(MilitaryRecord military)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            var normalized = NormalizeBulletinName(value);
            if (!string.IsNullOrWhiteSpace(normalized)) aliases.Add(normalized);
        }

        Add(military.WarName);
        Add(military.Name);
        var cleanName = AtRankFormatter.CleanMilitaryName(military.Name, military.Rank);
        Add(cleanName);
        Add(MilitaryDaKey(military));

        var tokens = Regex.Split(cleanName ?? string.Empty, @"\s+")
            .Select(x => NormalizeBulletinName(x))
            .Where(IsUsefulSingleNameAlias)
            .ToArray();

        if (tokens.Length >= 2) Add(string.Join(' ', tokens.Take(2)));
        if (tokens.Length >= 3) Add(string.Join(' ', tokens.Take(3)));
        if (tokens.Length >= 1) Add(tokens[^1]);
        foreach (var token in tokens.Take(Math.Max(0, tokens.Length - 1)).Where(IsUsefulSingleNameAlias))
            Add(token[0] + " " + tokens[^1]);

        // O BI normalmente publica apenas o nome de guerra/sobrenome (DUTRA, PEIXOTO, MEIRA, BELTRAME etc.).
        // Quando o cadastro está sem nome de guerra, esses sobrenomes precisam virar aliases;
        // ambiguidade continua sendo tratada no ResolveAmbiguousBulletinMatch.
        foreach (var token in tokens)
            Add(token);

        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (new[] { "DE", "DA", "DO", "DAS", "DOS" }.Contains(tokens[i].ToUpperInvariant()) && tokens[i + 1].Length >= 4)
            {
                Add(tokens[i] + " " + tokens[i + 1]);
                break;
            }
        }
        return aliases;
    }

    private static bool IsUsefulSingleNameAlias(string? value)
    {
        var token = NormalizeBulletinName(value);
        if (string.IsNullOrWhiteSpace(token) || token.Length < 3) return false;
        if (token.Any(char.IsDigit)) return false;
        if (new[] { "DE", "DA", "DO", "DAS", "DOS", "E", "EF", "VRV", "PROFL", "JUNIOR", "FILHO", "NETO", "SOBRINHO" }.Contains(token)) return false;
        return true;
    }

    private static string BulletinRankKey(string? rank)
    {
        var n = Normalize(rank).Replace("º", string.Empty).Replace("°", string.Empty);
        if (n.Contains("al") && n.Contains("cfst")) return "al_cfst";
        if (n.Contains("asp")) return "asp";
        if (n.Contains("sd") && (n.Contains("vrv") || n.Contains("variavel"))) return "sd_ev";
        if (n.Contains("soldado") && n.Contains("vari")) return "sd_ev";
        if (n.Contains("soldado") || n.StartsWith("sd")) return "sd_ep";
        if (n.Contains("cabo") || n.StartsWith("cb")) return "cb_ep";
        if (n.Contains("subten") || n == "s ten" || n == "st") return "s_ten";
        if (n.StartsWith("1") && (n.Contains("sgt") || n.Contains("sarg"))) return "1_sgt";
        if (n.StartsWith("2") && (n.Contains("sgt") || n.Contains("sarg"))) return "2_sgt";
        if (n.StartsWith("3") && (n.Contains("sgt") || n.Contains("sarg"))) return "3_sgt";
        if (n.StartsWith("1") && n.Contains("ten")) return "1_ten";
        if (n.StartsWith("2") && n.Contains("ten")) return "2_ten";
        if (n.Contains("capitao") || n == "cap") return "cap";
        return n;
    }

    private static string NormalizeBulletinName(string? value) => Regex.Replace(Normalize(value).ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();

    private static bool MilitaryAliasMatches(HashSet<string> aliases, string candidate)
    {
        candidate = NormalizeBulletinName(candidate);
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (aliases.Contains(candidate)) return true;
        if (!CanUsePartialBulletinAlias(candidate)) return false;
        return aliases.Any(alias =>
            (alias.Length > candidate.Length &&
             (alias.StartsWith(candidate + " ", StringComparison.Ordinal) ||
              alias.EndsWith(" " + candidate, StringComparison.Ordinal) ||
              alias.Contains(" " + candidate + " ", StringComparison.Ordinal))) ||
            (CanUsePartialBulletinAlias(alias) && candidate.Length > alias.Length &&
             (candidate.StartsWith(alias + " ", StringComparison.Ordinal) ||
              candidate.EndsWith(" " + alias, StringComparison.Ordinal) ||
              candidate.Contains(" " + alias + " ", StringComparison.Ordinal))));
    }

    private static MilitaryRecord? ResolveAmbiguousBulletinMatch(string alias, IReadOnlyList<MilitaryRecord> candidates)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var normalizedAlias = NormalizeBulletinName(alias);
        List<MilitaryRecord> Pick(Func<MilitaryRecord, bool> predicate) => candidates.Where(predicate).DistinctBy(x => x.Id).ToList();

        var exactWarName = Pick(x => NormalizeBulletinName(x.WarName) == normalizedAlias);
        if (exactWarName.Count == 1) return exactWarName[0];

        var exactFullName = Pick(x => NormalizeBulletinName(AtRankFormatter.CleanMilitaryName(x.Name, x.Rank)) == normalizedAlias || MilitaryDaKey(x) == normalizedAlias);
        if (exactFullName.Count == 1) return exactFullName[0];

        var exactToken = Pick(x =>
        {
            var clean = AtRankFormatter.CleanMilitaryName(x.Name, x.Rank);
            return Regex.Split(clean ?? string.Empty, @"\s+")
                .Select(NormalizeBulletinName)
                .Any(t => IsUsefulSingleNameAlias(t) && t == normalizedAlias);
        });
        if (exactToken.Count == 1) return exactToken[0];



        return null;
    }

    private static string DescribeAmbiguousBulletinPerson(string publishedName, IReadOnlyList<MilitaryRecord> candidates)
    {
        var names = candidates
            .DistinctBy(x => x.Id)
            .Take(5)
            .Select(x => $"{AtRankFormatter.ShortName(x.Rank)} {AtRankFormatter.CleanMilitaryName(x.Name, x.Rank)}")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return names.Count == 0 ? publishedName : $"{publishedName} (candidatos: {string.Join("; ", names)})";
    }


    private static bool CanUsePartialBulletinAlias(string candidate)
        => candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 && candidate.Length >= 6;

    private static string MilitaryDaKey(MilitaryRecord military)
        => NormalizeBulletinName(AtRankFormatter.CleanMilitaryName(military.Name, military.Rank));

    private static decimal ReadSqliteDecimal(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return 0m;
        try { return Convert.ToDecimal(reader.GetValue(index), CultureInfo.InvariantCulture); }
        catch { return 0m; }
    }

    private static string ClassifyDaDay(DateTime date, string? dayLabel)
        => IsDaRedDay(date, dayLabel) ? "Vermelha" : "Preta";

    private static bool IsDaRedDay(DateTime date, string? dayLabel)
    {
        try
        {
            if (App.RedDays.GetInfo(date).IsRedDay) return true;
        }
        catch
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
            if (RedDayService.TryGetHolidayName(date, out _)) return true;
        }
        var label = Normalize(dayLabel);
        if (label.Contains("feriado", StringComparison.OrdinalIgnoreCase) || label.Contains("vermelha", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsFixedNationalHoliday(DateTime date)
        => (date.Month, date.Day) is
        (1, 1) or   // Confraternização Universal
        (4, 21) or  // Tiradentes
        (5, 1) or   // Dia do Trabalho
        (9, 7) or   // Independência do Brasil
        (10, 12) or // Nossa Senhora Aparecida
        (11, 2) or  // Finados
        (11, 15) or // Proclamação da República
        (11, 20) or // Consciência Negra
        (12, 25);   // Natal

    private static bool IsMoveableNationalHoliday(DateTime date)
    {
        var easter = EasterSunday(date.Year);
        return date.Date == easter.AddDays(-48).Date   // segunda-feira de Carnaval
            || date.Date == easter.AddDays(-47).Date   // terça-feira de Carnaval
            || date.Date == easter.AddDays(-2).Date    // Sexta-feira Santa
            || date.Date == easter.AddDays(60).Date;   // Corpus Christi
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

    private static bool TryParseBulletinDate(Match match, out DateTime date)
    {
        date = default;
        if (!int.TryParse(match.Groups["d"].Value, out var day)) return false;
        if (!int.TryParse(match.Groups["y"].Value, out var year)) return false;
        if (year < 100) year += 2000;
        var monthName = Normalize(match.Groups["m"].Value).ToUpperInvariant();
        var month = monthName[..Math.Min(3, monthName.Length)] switch
        {
            "JAN" => 1, "FEV" => 2, "MAR" => 3, "ABR" => 4, "MAI" => 5, "JUN" => 6,
            "JUL" => 7, "AGO" => 8, "SET" => 9, "OUT" => 10, "NOV" => 11, "DEZ" => 12, _ => 0
        };
        if (month == 0) return false;
        try { date = new DateTime(year, month, day); return true; }
        catch { return false; }
    }

    private static bool TryParseSavedBulletinDate(string? isoDate, string? displayDate, out DateTime date)
    {
        if (DateTime.TryParseExact(isoDate ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        var text = (displayDate ?? string.Empty).Trim();
        foreach (var format in new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy" })
            if (DateTime.TryParseExact(text, format, PtBr, DateTimeStyles.None, out date))
                return true;

        if (DateTime.TryParse(text, PtBr, DateTimeStyles.None, out date))
            return true;

        date = default;
        return false;
    }

    private static bool TryParseSavedBulletinFileName(string? pathOrName, out DateTime date, out string number)
    {
        date = default;
        number = string.Empty;
        var name = Path.GetFileNameWithoutExtension(pathOrName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return false;

        var sisbol = Regex.Match(name,
            @"^(?<seq>\d{1,4})_(?<year>\d{4})_(?<date>\d{4}-\d{2}-\d{2})_(?:\d+_)?(?<num>\d{1,4})_boletim_interno$",
            RegexOptions.IgnoreCase);
        if (!sisbol.Success)
        {
            sisbol = Regex.Match(name,
                @"(?<date>\d{4}-\d{2}-\d{2})[_ -]+(?:0_)?(?<num>\d{1,4})[_ -]+boletim[_ -]+interno",
                RegexOptions.IgnoreCase);
        }

        if (!sisbol.Success) return false;
        if (!DateTime.TryParseExact(sisbol.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return false;

        number = int.TryParse(sisbol.Groups["num"].Value, out var parsed)
            ? parsed.ToString("000", CultureInfo.InvariantCulture)
            : sisbol.Groups["num"].Value;
        return true;
    }

    private async Task<string> ExtractPdfTextAsync(string path)
    {
        var text = await App.PdfText.ExtractAsync(path);
        if (!string.IsNullOrWhiteSpace(text)) return text;
        throw new InvalidOperationException(
            "O PDF não possui texto pesquisável. Use o boletim original exportado em PDF ou aplique OCR antes da importação.");
    }

    private static string ExtractPdfTextBestEffort(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var chunks = new List<byte[]> { bytes };
        var streamToken = Encoding.ASCII.GetBytes("stream");
        var endToken = Encoding.ASCII.GetBytes("endstream");
        var position = 0;
        while ((position = IndexOf(bytes, streamToken, position)) >= 0)
        {
            var dataStart = position + streamToken.Length;
            if (dataStart < bytes.Length && bytes[dataStart] == '\r') dataStart++;
            if (dataStart < bytes.Length && bytes[dataStart] == '\n') dataStart++;
            var end = IndexOf(bytes, endToken, dataStart);
            if (end < 0) break;
            var length = end - dataStart;
            if (length > 0 && length < 20_000_000)
            {
                var data = bytes.AsSpan(dataStart, length).ToArray();
                var dictStart = Math.Max(0, position - 500);
                var dictionary = Encoding.Latin1.GetString(bytes, dictStart, position - dictStart);
                if (dictionary.Contains("FlateDecode", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var input = new MemoryStream(data);
                        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        zlib.CopyTo(output);
                        chunks.Add(output.ToArray());
                    }
                    catch { }
                }
                else chunks.Add(data);
            }
            position = end + endToken.Length;
        }

        var sb = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var content = Encoding.Latin1.GetString(chunk);
            foreach (Match match in Regex.Matches(content, @"\((?<v>(?:\\.|[^\\)])*)\)\s*(?:Tj|'|"")", RegexOptions.Singleline))
                sb.AppendLine(DecodePdfLiteral(match.Groups["v"].Value));
            foreach (Match array in Regex.Matches(content, @"\[(?<v>.*?)\]\s*TJ", RegexOptions.Singleline))
            {
                foreach (Match part in Regex.Matches(array.Groups["v"].Value, @"\((?<v>(?:\\.|[^\\)])*)\)"))
                    sb.Append(DecodePdfLiteral(part.Groups["v"].Value));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string DecodePdfLiteral(string value)
    {
        return Regex.Replace(value, @"\\(?:(?<oct>[0-7]{1,3})|(?<c>.))", match =>
        {
            if (match.Groups["oct"].Success) return ((char)Convert.ToInt32(match.Groups["oct"].Value, 8)).ToString();
            return match.Groups["c"].Value switch { "n" => "\n", "r" => "\r", "t" => "\t", "b" => "\b", "f" => "\f", _ => match.Groups["c"].Value };
        });
    }

    private static int IndexOf(byte[] source, byte[] pattern, int start)
    {
        for (var i = Math.Max(0, start); i <= source.Length - pattern.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < pattern.Length; j++) if (source[i + j] != pattern[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private async Task LoadDaAsync()
    {
        if (_loadingDaCompetence) return;
        if (_allMilitary.Count == 0) return;
        if (!TryGetDaCompetence(out var competence, out var month, out var year))
            return;

        _loadingDaCompetence = true;
        try
        {
            var calendarCounts = new Dictionary<int, DaCalendarCount>();
            var saved = new Dictionary<int, (int Black, int Red, bool ManualOverride, decimal MonthlyValue)>();
            await using (var connection = OpenRouteDatabase())
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT militar_id,pretas,vermelhas,manual_override,COALESCE(valor_mensal,0) FROM da_state WHERE competencia=$c;";
                command.Parameters.AddWithValue("$c", competence);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) saved[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetInt32(2), !reader.IsDBNull(3) && reader.GetInt32(3) != 0, ReadSqliteDecimal(reader, 4));
            }

            var rows = new List<AtDaRow>();
            foreach (var military in SortMilitaryRecords(_allMilitary.Where(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached)))
            {
                var hasSaved = saved.TryGetValue(military.Id, out var count);
                var black = hasSaved ? count.Black : 0;
                var red = hasSaved ? count.Red : 0;
                // Regra operacional igual ao módulo Python: a diária da DA é o
                // valor líquido mensal efetivamente cadastrado dividido por 22.
                // Quando a competência já foi salva, reaproveita o valor salvo daquela competência;
                // se for registro antigo sem valor, recalcula pelo valor efetivo atual do militar.
                var registeredNet = hasSaved && count.MonthlyValue > 0m ? count.MonthlyValue : MonthlyTransportValueForReport(military);
                var row = new AtDaRow(military, registeredNet, 22, black, red);
                row.SetCalendarBase(0, 0);
                row.ManualOverride = hasSaved && (black > 0 || red > 0 || count.ManualOverride);
                rows.Add(row);
            }
            ReplaceCollection(_daRows, rows);
            ApplyDaFilter();
            UpdateDaTotal();
            UpdateDaSelectedText();
            SyncReportDaCompetence(month, year);
            UpdateDaCalendarSourceText(competence, calendarCounts, _daRows.Count(x => x.ManualOverride));
        }
        finally
        {
            _loadingDaCompetence = false;
        }
    }

    private async Task SaveDaAsync(bool showConfirmation = true)
    {
        if (!TryGetDaCompetence(out var competence, out _, out _))
            return;
        await using var connection = OpenRouteDatabase();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var row in _daRows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO da_state(militar_id,competencia,pretas,vermelhas,manual_override,valor_mensal,valor_diario,valor_da,nome_chave,updated_at)
                VALUES($id,$c,$p,$v,$manual,$mensal,$diario,$valorDa,$nomeChave,$ts)
                ON CONFLICT(militar_id,competencia) DO UPDATE SET
                    pretas=excluded.pretas,
                    vermelhas=excluded.vermelhas,
                    manual_override=excluded.manual_override,
                    valor_mensal=excluded.valor_mensal,
                    valor_diario=excluded.valor_diario,
                    valor_da=excluded.valor_da,
                    nome_chave=excluded.nome_chave,
                    updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", row.Military.Id);
            command.Parameters.AddWithValue("$c", competence);
            command.Parameters.AddWithValue("$p", Math.Max(0, row.BlackDays));
            command.Parameters.AddWithValue("$v", Math.Max(0, row.RedDays));
            command.Parameters.AddWithValue("$manual", row.ManualOverride ? 1 : 0);
            command.Parameters.AddWithValue("$mensal", (double)row.NetMonthly);
            command.Parameters.AddWithValue("$diario", (double)row.DailyNet);
            command.Parameters.AddWithValue("$valorDa", (double)row.Deduction);
            command.Parameters.AddWithValue("$nomeChave", MilitaryDaKey(row.Military));
            command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        SyncReportDaCompetenceFromDaSelection();
        await RefreshReportDaValuesAndGridAsync();
        if (showConfirmation) Info($"Ajustes de Despesa a Anular salvos na competência {competence}.");
    }

    private async Task GenerateDaBulletinAsync(bool showPreview = false)
    {
        if (!TryGetDaCompetence(out _, out var month, out var year))
            return;

        await SaveDaAsync(false);
        var affected = SortDaRows(_daRows.Where(x => x.NetDays > 0 && x.Deduction > 0)).ToList();
        _daPreview.Document = BuildDaBulletinDocument(affected, month, year);
        UpdateDaTotal();
        _statusText.Text = $"{affected.Count} militar(es) no boletim de Despesa a Anular. A prévia fica em janela separada para leitura.";

        if (showPreview)
            OpenDaPreviewWindow(BuildDaBulletinDocument(affected, month, year), affected.Count);
    }

    private FlowDocument BuildDaBulletinDocument(IReadOnlyList<AtDaRow> affected, int month, int year)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(18),
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
            FontSize = BulletinTextFormatter.StandardWpfFontSize,
            LineHeight = 20,
            FontWeight = FontWeights.Normal
        };

        if (affected.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("Não há valor a anular nesta competência.")));
            return doc;
        }

        var monthName = PtBr.DateTimeFormat.GetMonthName(month).ToUpperInvariant();
        var opening = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            TextAlignment = TextAlignment.Justify
        };
        opening.Inlines.Add(new Run(
            $"Seja realizada a despesa a anular do auxílio-transporte dos militares abaixo relacionados, " +
            $"referente a {monthName} de {year}, para lançamento no SIPPES."));
        doc.Blocks.Add(opening);
        doc.Blocks.Add(BulletinBlankLine(5));

        var daIndex = 0;
        foreach (var row in affected)
        {
            if (daIndex++ > 0) doc.Blocks.Add(BulletinBlankLine(5));
            var nameLine = new Paragraph { Margin = new Thickness(0, 4, 0, 0), KeepWithNext = true };
            nameLine.Inlines.Add(new Run(AtRankFormatter.ShortName(row.Military.Rank) + " "));
            AddNameWithWarBold(nameLine, row.Military);
            doc.Blocks.Add(nameLine);

            var identity = new Paragraph { Margin = new Thickness(0), KeepWithNext = true };
            identity.Inlines.Add(new Run(
                $"Prec-CP {MilitaryFormatting.Digits(row.Military.PrecCp)} CPF {MilitaryFormatting.FormatCpf(row.Military.Cpf)}"));
            doc.Blocks.Add(identity);

            var value = new Paragraph { Margin = new Thickness(0, 0, 0, 4), KeepTogether = true };
            value.Inlines.Add(new Run($"Rubrica: DR0095 · Lançamento por valor\nMês de referência: {monthName}\nAno de referência: {year}\nValor total a descontar: {FormatMoney(row.Deduction)}"));
            doc.Blocks.Add(value);
        }

        return doc;
    }

    private void OpenDaPreviewWindow(FlowDocument document, int affectedCount)
    {
        var preview = new RichTextBox
        {
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
            FontSize = BulletinTextFormatter.StandardWpfFontSize,
            Document = document
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(preview);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summary = new TextBlock
        {
            Text = affectedCount == 0 ? "Nenhum militar com valor a anular." : $"{affectedCount} militar(es) na prévia de DA.",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };
        footer.Children.Add(summary);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(SmallButton("Copiar", (_, _) => CopyRichText(preview), "SecondaryButtonStyle"));
        actions.Children.Add(SmallButton("Fechar", (_, _) => Window.GetWindow(preview)?.Close(), "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var dialog = new Window
        {
            Title = "Prévia — Despesa a Anular do Auxílio-Transporte",
            Owner = this,
            Width = 940,
            Height = 760,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        AttachPersistentPlacement(dialog, "previa_despesa_anular_aux_transporte");
        dialog.Loaded += (_, _) => InstallContextMenusRecursive(dialog.Content as DependencyObject);
        dialog.Show();
        dialog.Activate();
    }

    private async Task CopyDaPreviewAsync()
    {
        var range = new TextRange(_daPreview.Document.ContentStart, _daPreview.Document.ContentEnd);
        var text = range.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            await GenerateDaBulletinAsync(false);
        CopyRichText(_daPreview);
    }

    private async Task SendDaToSisbolAsync()
    {
        var range = new TextRange(_daPreview.Document.ContentStart, _daPreview.Document.ContentEnd);
        var text = range.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("Não há valor", StringComparison.OrdinalIgnoreCase))
        {
            await GenerateDaBulletinAsync();
            range = new TextRange(_daPreview.Document.ContentStart, _daPreview.Document.ContentEnd);
            text = range.Text.Trim();
        }

        var military = _daRows
            .Where(x => x.NetDays > 0 && x.Deduction > 0)
            .Select(x => x.Military)
            .DistinctBy(x => x.Id)
            .ToList();
        if (military.Count == 0)
        {
            Warn("Não há militares com valor de DA para enviar ao SisBol.");
            return;
        }

        try
        {
            IsBusy = true;
            if (!App.Sisbol.IsReady)
            {
                Warn("SisBol não preparado. Use o botão ‘Preparar SisBol’ no topo da janela principal, conclua o login/captcha e valide a sessão.");
                return;
            }
            BusyMessage = "Enviando Despesa a Anular ao SisBol…";
            await App.Sisbol.SendAsync(
                text,
                military,
                "AUXILIO-TRANSPORTE - Despesa a anular",
                _daIncludeConsequencesCheck.IsChecked == true,
                _daConsequencesBox.Text);
            _statusText.Text = "Boletim de Despesa a Anular enviado ao SisBol e registrado no histórico.";
        }
        catch (Exception ex)
        {
            CopyRichText(_daPreview);
            await _log.WriteAsync("Falha ao enviar Despesa a Anular ao SisBol.", ex);
            Warn("O envio automático da DA não foi concluído. O texto foi copiado para conferência manual.\n\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private void UpdateDaTotal()
    {
        var count = _daRows.Count(x => x.NetDays > 0);
        var total = _daRows.Sum(x => x.Deduction);
        _daTotalText.Text = $"{count} militar(es) com desconto • Total a anular: {FormatMoney(total)}";
    }

    private async Task RefreshReportValuesAsync()
    {
        await RebuildEffectiveTransportValuesAsync();
        await RefreshReportDaValuesAsync();
        ApplyMilitaryFilter();
        RefreshReport();
        _statusText.Text = "Relatório atualizado com a soma líquida efetiva de cada militar.";
    }

    private async Task RefreshReportDaValuesAndGridAsync()
    {
        try
        {
            await RefreshReportDaValuesAsync();
            RefreshReport();
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao carregar valores de DA para o relatorio de Auxilio-Transporte.", ex);
            _reportDaDeductionByMilitaryId.Clear();
            _reportDaReasonByMilitaryId.Clear();
            _reportVacationPeriodByMilitaryId.Clear();
            _reportVacationDaNoteStatusByMilitaryId.Clear();
            _reportGrid.Items.Refresh();
        }
    }

    private async Task RefreshReportDaValuesAsync(bool showWarning = false)
    {
        _reportDaDeductionByMilitaryId.Clear();
        _reportDaReasonByMilitaryId.Clear();
        _reportVacationPeriodByMilitaryId.Clear();
        _reportVacationDaNoteStatusByMilitaryId.Clear();
        if (!TryGetReportDaCompetence(out var competence, out var month, out var year, showWarning))
        {
            _reportGrid.Items.Refresh();
            return;
        }

        var values = await ReadReportDaDeductionsAsync(competence, month, year, _allMilitary);
        foreach (var pair in values)
        {
            _reportDaDeductionByMilitaryId[pair.Key] = pair.Value.Value;
            _reportDaReasonByMilitaryId[pair.Key] = pair.Value.Reason;
            if (pair.Value.HasVacation)
            {
                _reportVacationPeriodByMilitaryId[pair.Key] = pair.Value.VacationPeriod;
                _reportVacationDaNoteStatusByMilitaryId[pair.Key] = pair.Value.VacationDaNoteStatus;
            }
        }
        _reportGrid.Items.Refresh();
    }

    private async Task<Dictionary<int, ReportDaInfo>> ReadReportDaDeductionsAsync(string competence, int month, int year, IEnumerable<MilitaryRecord> source)
    {
        var militaryById = source
            .Where(x => x.Id > 0)
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First());
        var result = new Dictionary<int, ReportDaInfo>();
        if (militaryById.Count == 0) return result;
        var militaryByDaKey = militaryById.Values
            .Select(x => new { Key = MilitaryDaKey(x), Military = x })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.First().Military, StringComparer.OrdinalIgnoreCase);

        await using (var connection = OpenRouteDatabase())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT militar_id,pretas,vermelhas,COALESCE(valor_mensal,0),COALESCE(valor_diario,0),COALESCE(valor_da,0),COALESCE(nome_chave,'') FROM da_state WHERE competencia=$c;";
            command.Parameters.AddWithValue("$c", competence);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var savedId = reader.GetInt32(0);
                var savedKey = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                if (!militaryById.TryGetValue(savedId, out var military))
                {
                    if (string.IsNullOrWhiteSpace(savedKey) || !militaryByDaKey.TryGetValue(savedKey, out military)) continue;
                }
                var black = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                var red = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                var netDays = Math.Max(0, black - red);
                if (netDays <= 0) continue;
                var savedMonthly = ReadSqliteDecimal(reader, 3);
                var savedDaily = ReadSqliteDecimal(reader, 4);
                var savedDeduction = ReadSqliteDecimal(reader, 5);
                var monthly = savedMonthly > 0m ? savedMonthly : MonthlyTransportValueForReport(military);
                var daily = savedDaily > 0m ? savedDaily : monthly > 0m ? Round(monthly / 22m) : 0m;
                var deduction = savedDeduction > 0m ? savedDeduction : Round(daily * netDays);
                if (deduction > 0m)
                    result[military.Id] = new ReportDaInfo(deduction, $"DA salva no módulo: {black} preta(s) - {red} vermelha(s) = {netDays} dia(s).");
            }
        }

        OverlayCurrentDaRowsForReport(result, competence, militaryById);

        foreach (var pair in await ReadVacationTransportDaDeductionsAsync(month, year, militaryById))
        {
            if (!result.TryGetValue(pair.Key, out var saved))
            {
                result[pair.Key] = pair.Value;
                continue;
            }

            var military = militaryById[pair.Key];
            var monthly = MonthlyTransportValueForReport(military);
            var combined = monthly > 0m
                ? Math.Min(monthly, Round(saved.Value + pair.Value.Value))
                : Round(saved.Value + pair.Value.Value);
            var reason = saved.Value > 0m && pair.Value.Value > 0m
                ? saved.Reason + " " + pair.Value.Reason + " Valor final limitado ao SAT mensal para evitar desconto duplicado."
                : saved.Reason + " " + pair.Value.Reason;
            result[pair.Key] = new ReportDaInfo(
                combined,
                reason.Trim(),
                pair.Value.HasVacation,
                pair.Value.VacationPeriod,
                pair.Value.VacationDaNoteStatus);
        }

        return result;
    }

    private void OverlayCurrentDaRowsForReport(Dictionary<int, ReportDaInfo> result, string competence, IReadOnlyDictionary<int, MilitaryRecord> militaryById)
    {
        if (_daRows.Count == 0) return;
        if (!TryPeekDaCompetence(out var currentDaCompetence, out _, out _)) return;
        if (!string.Equals(currentDaCompetence, competence, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var row in _daRows)
        {
            var id = row.Military.Id;
            if (id <= 0 || !militaryById.ContainsKey(id)) continue;
            if (row.Deduction > 0m)
            {
                result[id] = new ReportDaInfo(row.Deduction, "DA carregada/editada na aba Despesa a Anular para a competência selecionada.");
            }
            else if (row.ManualOverride || row.BlackDays > 0 || row.RedDays > 0)
            {
                result.Remove(id);
            }
        }
    }

    private async Task<Dictionary<int, ReportDaInfo>> ReadVacationTransportDaDeductionsAsync(int month, int year, IReadOnlyDictionary<int, MilitaryRecord> militaryById)
    {
        var result = new Dictionary<int, ReportDaInfo>();
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        VacationBulletinStore bulletinStore;
        try
        {
            bulletinStore = await App.Vacations.LoadBulletinStoreAsync();
        }
        catch (Exception ex)
        {
            bulletinStore = new VacationBulletinStore();
            await _log.WriteAsync("Falha ao carregar o histórico de publicações de férias para conferir a nota de DA do Auxílio-Transporte.", ex);
        }
        var transportDaNotes = bulletinStore.Saved.Where(IsVacationTransportDaBulletin).ToList();

        foreach (var vacationYear in new[] { year - 1, year, year + 1 }.Distinct())
        {
            try
            {
                var periods = (await App.Vacations.GetPeriodsAsync(vacationYear))
                    .Where(x => x.StartDate is not null && x.EndDate is not null && x.StartDate.Value.Date <= end && x.EndDate.Value.Date >= start)
                    .ToDictionary(x => x.Id);
                if (periods.Count == 0) continue;

                foreach (var allocation in await App.Vacations.GetAllocationsAsync(vacationYear, null, militaryById))
                {
                    if (!periods.TryGetValue(allocation.PeriodId, out var period)) continue;
                    if (!militaryById.TryGetValue(allocation.MilitaryId, out var military)) continue;
                    if (military.IsAttached || !MilitaryRecord.IsYes(military.ReceivesTransportAid)) continue;

                    var monthly = MonthlyTransportValueForReport(military);
                    if (monthly <= 0m) continue;

                    var vacationStart = period.StartDate.GetValueOrDefault();
                    var vacationEnd = period.EndDate.GetValueOrDefault();
                    var periodText = $"{period.DisplayName} ({vacationStart.ToString("dd/MM/yyyy", PtBr)} a {vacationEnd.ToString("dd/MM/yyyy", PtBr)})";
                    var noteGenerated = transportDaNotes.Any(note =>
                        note.Year == vacationYear &&
                        note.PeriodId == period.Id &&
                        VacationDaNoteIncludesMilitary(note, military));
                    var noteStatus = noteGenerated ? "Gerada no SisBol" : "Pendente";
                    var reason = $"Férias lançadas no Plano de Férias: {periodText}. Militar em férias não faz jus ao SAT da competência; SAT mensal integral lançado como Despesa a Anular. Nota de DA do AT: {noteStatus}.";
                    if (result.TryGetValue(military.Id, out var current))
                    {
                        var periodLabels = string.Join(" • ", new[] { current.VacationPeriod, periodText }
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                        var combinedNoteStatus = current.VacationDaNoteStatus == "Pendente" || noteStatus == "Pendente"
                            ? "Pendente"
                            : "Gerada no SisBol";
                        result[military.Id] = new ReportDaInfo(
                            Math.Max(current.Value, monthly),
                            current.Reason + " " + reason,
                            true,
                            periodLabels,
                            combinedNoteStatus);
                    }
                    else
                    {
                        result[military.Id] = new ReportDaInfo(monthly, reason, true, periodText, noteStatus);
                    }
                }
            }
            catch (Exception ex)
            {
                await _log.WriteAsync($"Falha ao consultar férias de {vacationYear} para a DA automática do relatório de Auxílio-Transporte.", ex);
            }
        }
        return result;
    }

    private static bool IsVacationTransportDaBulletin(VacationSavedBulletin bulletin)
    {
        var identity = Normalize($"{bulletin.Title} {bulletin.ModelName}");
        return identity.Contains("auxilio transporte", StringComparison.Ordinal) &&
               identity.Contains("despesa a anular", StringComparison.Ordinal) &&
               identity.Contains("ferias", StringComparison.Ordinal);
    }

    private static bool VacationDaNoteIncludesMilitary(VacationSavedBulletin bulletin, MilitaryRecord military)
    {
        var normalizedText = Normalize(bulletin.Text);
        var name = Normalize(AtRankFormatter.CleanMilitaryName(military.Name, military.Rank));
        if (!string.IsNullOrWhiteSpace(name) && normalizedText.Contains(name, StringComparison.Ordinal)) return true;

        var document = MilitaryFormatting.Digits(military.PrecCp);
        if (string.IsNullOrWhiteSpace(document)) document = MilitaryFormatting.Digits(military.Cpf);
        if (document.Length < 6) return false;
        return MilitaryFormatting.Digits(bulletin.Text).Contains(document, StringComparison.Ordinal);
    }

    private decimal ReportDaDeduction(MilitaryRecord military)
        => military.Id > 0 && _reportDaDeductionByMilitaryId.TryGetValue(military.Id, out var value) ? value : 0m;

    private string ReportDaReason(MilitaryRecord military)
        => military.Id > 0 && _reportDaReasonByMilitaryId.TryGetValue(military.Id, out var reason) && !string.IsNullOrWhiteSpace(reason) ? reason : "—";

    private bool ReportHasVacation(MilitaryRecord military)
        => military.Id > 0 && _reportVacationPeriodByMilitaryId.ContainsKey(military.Id);

    private string ReportVacationPeriod(MilitaryRecord military)
        => military.Id > 0 && _reportVacationPeriodByMilitaryId.TryGetValue(military.Id, out var period) && !string.IsNullOrWhiteSpace(period) ? period : "—";

    private string ReportVacationDaNoteStatus(MilitaryRecord military)
        => military.Id > 0 && _reportVacationDaNoteStatusByMilitaryId.TryGetValue(military.Id, out var status) && !string.IsNullOrWhiteSpace(status) ? status : "Não se aplica";

    private decimal MonthlyTransportValueForReport(MilitaryRecord military)
    {
        var value = EffectiveNet(military);
        if (value <= 0m) value = ParseMoney(military.TransportAidValue);
        if (value <= 0m && military.TransportGrossTotal.GetValueOrDefault() > 0)
            value = (decimal)military.TransportGrossTotal.Value;
        return value > 5000m ? Round(value / 100m) : Round(value);
    }

    private bool ReportUsesPrec()
        => string.Equals(_reportDocumentModeBox.SelectedItem?.ToString(), "PREC-CP", StringComparison.OrdinalIgnoreCase);

    private string CurrentReportDocumentHeader()
        => ReportUsesPrec() ? "PREC-CP" : "CPF";

    private string CurrentReportDocumentValue(MilitaryRecord military)
        => ReportUsesPrec() ? MilitaryFormatting.Digits(military.PrecCp) : military.FormattedCpf;

    private void UpdateReportDocumentColumn()
    {
        if (_reportDocumentColumn is null) return;
        var usePrec = ReportUsesPrec();
        _reportDocumentColumn.Header = usePrec ? "PREC-CP" : "CPF";
        _reportDocumentColumn.Binding = new Binding(usePrec ? nameof(MilitaryRecord.PrecCp) : nameof(MilitaryRecord.FormattedCpf));
        _reportDocumentColumn.Width = usePrec ? 116 : 122;
        _reportGrid.Items.Refresh();
    }

    private bool TryGetReportDaCompetence(out string competence, out int month, out int year, bool warn = true)
    {
        month = 0;
        year = 0;
        competence = string.Empty;

        if (_reportDaMonthBox.SelectedItem is ComboBoxItem selectedMonth && selectedMonth.Tag is int taggedMonth)
            month = taggedMonth;
        else if (_reportDaMonthBox.SelectedIndex >= 0)
            month = _reportDaMonthBox.SelectedIndex + 1;

        var selectedYear = _reportDaYearBox.SelectedItem?.ToString();
        var rawYear = !string.IsNullOrWhiteSpace(_reportDaYearBox.Text) ? _reportDaYearBox.Text : selectedYear;
        rawYear = Regex.Match(rawYear ?? string.Empty, @"\d{4}").Value;
        if (!int.TryParse(rawYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out year) || year < 2000 || year > 2100 || month is < 1 or > 12)
        {
            if (warn) Warn("Selecione mes e ano validos para a coluna Despesa a Anular do relatorio.");
            month = 0;
            year = 0;
            return false;
        }

        competence = new DateTime(year, month, 1).ToString("MM/yyyy", CultureInfo.InvariantCulture);
        _reportDaMonthBox.SelectedIndex = month - 1;
        var yearText = year.ToString(CultureInfo.InvariantCulture);
        if (!_reportDaYearBox.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), yearText, StringComparison.OrdinalIgnoreCase)))
        {
            var years = _reportDaYearBox.Items.Cast<object>().Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            years.Add(yearText);
            _reportDaYearBox.ItemsSource = years.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        }
        _reportDaYearBox.SelectedItem = yearText;
        _reportDaYearBox.Text = yearText;
        return true;
    }

    private void RefreshReport()
    {
        if (_reportGrid is null) return;
        var mode = _reportFilter.SelectedItem?.ToString() ?? "Todos";
        IEnumerable<MilitaryRecord> rows = _allMilitary;
        rows = mode switch
        {
            "Recebem AT" => rows.Where(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached),
            "Faixas R$ 700+" => rows.Where(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached && EffectiveNet(x) >= 700m),
            "Não recebem AT" => rows.Where(x => !MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached),
            "Sem endereço" => rows.Where(x => string.IsNullOrWhiteSpace(x.Address)),
            "Adido/Encostado" => rows.Where(x => x.IsAttached),
            _ => rows
        };
        var list = SortMilitaryRecords(rows
            .GroupBy(MilitaryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
            .ToList();
        _reportGrid.ItemsSource = list;
        var receives = list.Count(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached);
        var noReceive = list.Count - receives;
        var total = list.Sum(EffectiveNet);
        var midRange = list.Count(x => EffectiveNet(x) >= 700m && EffectiveNet(x) < 1200m);
        var highRange = list.Count(x => EffectiveNet(x) >= 1200m);
        _reportTotalCard.Text = list.Count.ToString(PtBr);
        _reportReceiveCard.Text = receives.ToString(PtBr);
        _reportNoReceiveCard.Text = noReceive.ToString(PtBr);
        _reportValueCard.Text = FormatMoney(total);
        var daCount = list.Count(x => ReportDaDeduction(x) > 0m);
        var daTotal = list.Sum(ReportDaDeduction);
        var vacationCount = list.Count(ReportHasVacation);
        var pendingVacationDa = list.Count(x => ReportVacationDaNoteStatus(x) == "Pendente");
        _reportSummary.Text = $"{list.Count} militar(es) • Recebem AT: {receives} • Não recebem: {noReceive} • R$ 700 a R$ 1.200: {midRange} • R$ 1.200 ou mais: {highRange} • Soma líquida efetiva: {FormatMoney(total)} • DA: {daCount} militar(es), {FormatMoney(daTotal)} • Férias: {vacationCount} • Notas DA pendentes: {pendingVacationDa}";
    }

    private async Task GenerateProfessionalReportAsync()
    {
        await RefreshReportDaValuesAsync(true);
        RefreshReport();
        var militaryRows = SortMilitaryRecords(((_reportGrid.ItemsSource as IEnumerable<MilitaryRecord>)?.ToList() ?? new List<MilitaryRecord>())
            .GroupBy(MilitaryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
            .ToList();
        if (militaryRows.Count == 0) { Warn("Não há dados no relatório atual."); return; }
        if (!TryGetReportDaCompetence(out var competence, out _, out _, true)) return;

        var dialog = new SaveFileDialog
        {
            Title = "Gerar relatório profissional de Auxílio-Transporte",
            Filter = "Pasta de trabalho do Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"SAT_Maior_Auxilio_Transporte_{competence.Replace('/', '_')}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;

        var rows = militaryRows.Select(military =>
        {
            var receives = MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached;
            return new TransportAidProfessionalReportRow(
                AtRankFormatter.ShortName(military.Rank),
                AtRankFormatter.CleanMilitaryName(military.Name, military.Rank),
                CurrentReportDocumentValue(military),
                receives ? "Sim" : "Não",
                TransportValueRange(EffectiveNet(military)),
                EffectiveNet(military),
                ReportHasVacation(military) ? "Sim" : "Não",
                ReportVacationPeriod(military),
                ReportVacationDaNoteStatus(military),
                ReportDaDeduction(military),
                ReportDaReason(military));
        }).ToList();

        IsBusy = true;
        BusyMessage = "Gerando relatório Excel profissional da SAT Maior…";
        try
        {
            var report = new TransportAidProfessionalReport(
                competence,
                _reportFilter.SelectedItem?.ToString() ?? "Todos",
                CurrentReportDocumentHeader(),
                DateTime.Now,
                rows);
            await new TransportAidProfessionalSpreadsheetService().ExportAsync(dialog.FileName, report);
            if (_openReportAfterGenerate.IsChecked == true) ShellService.OpenPath(dialog.FileName);
            var pending = rows.Count(x => x.VacationDaNoteStatus == "Pendente");
            _statusText.Text = $"Excel profissional gerado: {Path.GetFileName(dialog.FileName)} • {pending} nota(s) de DA pendente(s).";
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao gerar o relatório Excel profissional de Auxílio-Transporte.", ex);
            Warn("Não foi possível gerar o relatório Excel. " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task GenerateLegacyHtmlReportAsync()
    {
        await RefreshReportDaValuesAsync(true);
        RefreshReport();
        var rows = ((_reportGrid.ItemsSource as IEnumerable<MilitaryRecord>)?.ToList() ?? new List<MilitaryRecord>())
            .GroupBy(MilitaryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (rows.Count == 0) { Warn("Não há dados no relatório atual."); return; }
        var dialog = new SaveFileDialog
        {
            Title = "Gerar relatório de Auxílio-Transporte",
            Filter = "Relatório HTML (*.html)|*.html",
            FileName = $"relatorio_auxilio_transporte_{DateTime.Now:yyyyMMdd_HHmm}.html"
        };
        if (dialog.ShowDialog(this) != true) return;
        var receives = rows.Count(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached);
        var total = rows.Sum(EffectiveNet);
        var midRange = rows.Count(x => EffectiveNet(x) >= 700m && EffectiveNet(x) < 1200m);
        var highRange = rows.Count(x => EffectiveNet(x) >= 1200m);
        var rangeMode = string.Equals(_reportFilter.SelectedItem?.ToString(), "Faixas R$ 700+", StringComparison.OrdinalIgnoreCase);
        var documentHeader = CurrentReportDocumentHeader();
        var daTotal = rows.Sum(ReportDaDeduction);
        var daCompetence = TryGetReportDaCompetence(out var reportDaCompetence, out _, out _, false) ? reportDaCompetence : string.Empty;
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang='pt-BR'><head><meta charset='utf-8'><title>Relatório de Auxílio-Transporte</title><style>");
        html.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:28px;color:#172033}h1{margin:0;color:#0d47a1;font-size:25px}p.sub{margin:5px 0 18px;color:#5e6773}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(135px,1fr));gap:10px;margin:16px 0}.card{border:1px solid #dce4ef;border-radius:10px;padding:12px;background:#f8fbff}.card b{display:block;font-size:20px;margin-top:4px}.label{font-size:11px;color:#5e6773;text-transform:uppercase;font-weight:700}table{width:100%;border-collapse:collapse;font-size:12px}th{background:#0d47a1;color:white;text-align:left;padding:9px 8px}td{padding:8px;border-bottom:1px solid #e4e9f0;vertical-align:top}tr:nth-child(even){background:#f8fbff}.group td{background:#eaf2ff!important;color:#0d47a1;font-weight:800;text-transform:uppercase;letter-spacing:.02em}.yes{color:#147a43;font-weight:700}.no{color:#69717c;font-weight:700}.range{font-weight:700;white-space:nowrap}.money{text-align:right;font-weight:700;white-space:nowrap}.footer{margin-top:15px;color:#5e6773;font-size:11px}@media print{body{margin:12mm}.no-print{display:none}.cards{grid-template-columns:repeat(3,1fr)}} </style></head><body>");
        html.Append("<h1>Relatório de Auxílio-Transporte</h1><p class='sub'>SIGFUR • Gerado em ").Append(DateTime.Now.ToString("dd/MM/yyyy 'às' HH:mm", PtBr)).Append(" • Filtro: ").Append(WebUtility.HtmlEncode(_reportFilter.SelectedItem?.ToString() ?? "Todos")).Append("</p>");
        html.Append("<div class='cards'><div class='card'><span class='label'>Militares</span><b>").Append(rows.Count).Append("</b></div><div class='card'><span class='label'>Recebem AT</span><b>").Append(receives).Append("</b></div><div class='card'><span class='label'>Nao recebem</span><b>").Append(rows.Count - receives).Append("</b></div><div class='card'><span class='label'>R$ 700 a R$ 1.200</span><b>").Append(midRange).Append("</b></div><div class='card'><span class='label'>R$ 1.200 ou mais</span><b>").Append(highRange).Append("</b></div><div class='card'><span class='label'>Valor liquido</span><b>").Append(WebUtility.HtmlEncode(FormatMoney(total))).Append("</b></div><div class='card'><span class='label'>Despesa a anular</span><b>").Append(WebUtility.HtmlEncode(FormatMoney(daTotal))).Append("</b></div></div>");
        html.Append("<table><thead><tr><th>P/G</th><th>Nome completo</th><th>").Append(WebUtility.HtmlEncode(documentHeader)).Append("</th><th>Endereço</th><th>Recebe</th><th>Faixa</th><th>Valor AT</th><th>Despesa a Anular</th></tr></thead><tbody>");
        var printableRows = rangeMode
            ? rows.OrderBy(TransportRangeOrder).ThenBy(x => AtRankFormatter.GetOrder(x.Rank)).ThenBy(x => Normalize(x.Name)).ToList()
            : rows;
        var lastRange = string.Empty;
        foreach (var m in printableRows)
        {
            var net = EffectiveNet(m);
            var range = TransportValueRange(net);
            if (rangeMode && !range.Equals(lastRange, StringComparison.OrdinalIgnoreCase))
            {
                html.Append("<tr class='group'><td colspan='8'>").Append(WebUtility.HtmlEncode(range)).Append("</td></tr>");
                lastRange = range;
            }
            var yes = MilitaryRecord.IsYes(m.ReceivesTransportAid) && !m.IsAttached;
            var da = ReportDaDeduction(m);
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(AtRankFormatter.ShortName(m.Rank)))
                .Append("</td><td>").Append(HtmlNameWithWarBold(m))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(CurrentReportDocumentValue(m)))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(Blank(m.Address)))
                .Append("</td><td class='").Append(yes ? "yes" : "no").Append("'>").Append(yes ? "Sim" : "Não")
                .Append("</td><td class='range'>").Append(WebUtility.HtmlEncode(range))
                .Append("</td><td class='money'>").Append(WebUtility.HtmlEncode(FormatMoney(net)))
                .Append("</td><td class='money'>").Append(WebUtility.HtmlEncode(FormatMoney(da)))
                .Append("</td></tr>");
        }
        html.Append("</tbody></table><p class='footer'>Nome de guerra destacado em negrito dentro do nome completo. Valores conforme cadastro do SIGFUR. Despesa a Anular: ").Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(daCompetence) ? "competencia nao selecionada" : daCompetence)).Append(". A coluna de DA soma a DA salva para a competência selecionada e, quando houver férias no Plano de Férias dentro dessa competência, lança o SAT mensal integral como Despesa a Anular.</p></body></html>");
        await File.WriteAllTextAsync(dialog.FileName, html.ToString(), new UTF8Encoding(true));
        if (_openReportAfterGenerate.IsChecked == true) ShellService.OpenPath(dialog.FileName);
        _statusText.Text = $"Relatório profissional gerado: {Path.GetFileName(dialog.FileName)}";
    }

    private async Task ExportReportCsvAsync()
    {
        await RefreshReportDaValuesAsync(true);
        RefreshReport();
        var rows = SortMilitaryRecords(((_reportGrid.ItemsSource as IEnumerable<MilitaryRecord>)?.ToList() ?? new List<MilitaryRecord>())
            .GroupBy(MilitaryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
            .ToList();
        if (rows.Count == 0) { Warn("Não há dados no relatório atual."); return; }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar relatório de Auxílio-Transporte",
            Filter = "CSV UTF-8 para Excel (*.csv)|*.csv",
            FileName = $"auxilio_transporte_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        var documentHeader = CurrentReportDocumentHeader();
        var sb = new StringBuilder();
        sb.AppendLine("sep=;");
        sb.AppendLine($"P/G;Nome completo;Nome de guerra;{documentHeader};Endereco;Recebe AT;Faixa de valor;Valor liquido (R$);Dias uteis;Adido/Encostado;Ferias na competencia;Periodo de ferias;Nota DA do AT;Despesa a anular (R$);Observacao / fundamento");
        foreach (var military in rows)
        {
            var receives = MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached;
            var net = EffectiveNet(military);
            var fields = new[]
            {
                Csv(AtRankFormatter.ShortName(military.Rank)),
                Csv(AtRankFormatter.CleanMilitaryName(military.Name, military.Rank)),
                Csv((military.WarName ?? string.Empty).Trim()),
                CsvExcelText(CurrentReportDocumentValue(military)),
                Csv(military.Address),
                Csv(receives ? "Sim" : "Não"),
                Csv(TransportValueRange(net)),
                Csv(net.ToString("0.00", PtBr)),
                Csv((military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22).ToString(PtBr)),
                Csv(military.IsAttached ? "Sim" : "Não"),
                Csv(ReportHasVacation(military) ? "Sim" : "Não"),
                Csv(ReportVacationPeriod(military)),
                Csv(ReportVacationDaNoteStatus(military)),
                Csv(ReportDaDeduction(military).ToString("0.00", PtBr)),
                Csv(ReportDaReason(military))
            };
            sb.AppendLine(string.Join(';', fields));
        }

        var total = rows.Sum(EffectiveNet);
        var daTotal = rows.Sum(ReportDaDeduction);
        sb.AppendLine();
        sb.AppendLine(string.Join(';', Csv("TOTAL LIQUIDO"), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(total.ToString("0.00", PtBr)), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(string.Empty), Csv(daTotal.ToString("0.00", PtBr)), Csv(string.Empty)));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        if (_openReportAfterGenerate.IsChecked == true) ShellService.OpenPath(dialog.FileName);
        _statusText.Text = $"CSV profissional gerado: {Path.GetFileName(dialog.FileName)} • Total líquido {FormatMoney(total)}.";
    }

    private async Task AuditAllTransportAsync()
    {
        var candidates = _allMilitary.Where(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached).ToList();
        if (!Confirm("Auditar Auxílio-Transporte", $"Recalcular {candidates.Count} cadastro(s) usando tarifas salvas, soldo atual e cota proporcional aos dias úteis?")) return;
        var changed = 0;
        var failures = new List<string>();
        foreach (var military in candidates)
        {
            try
            {
                var state = await ReadTransportStateAsync(military);
                if (state.Buses.Count == 0) continue;
                var before = ParseMoney(military.TransportAidValue);
                await SaveTransportStateAsync(military, state.Buses, state.Calculation.Days);
                if (Math.Abs(before - ParseMoney(military.TransportAidValue)) >= 0.01m) changed++;
            }
            catch (Exception ex) { failures.Add($"{AtRankFormatter.ShortName(military.Rank)} {military.Name}: {ex.Message}"); }
        }
        await ReloadAsync();
        Info($"Auditoria concluída. {changed} valor(es) atualizado(s)." + (failures.Count > 0 ? $"\nFalhas: {failures.Count}\n" + string.Join("\n", failures.Take(8)) : string.Empty));
    }

    private async Task<AtTransportState> ReadTransportStateAsync(MilitaryRecord military)
    {
        var buses = new List<AtBusLine>();
        await using var connection = OpenMainDatabaseReadOnly();
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT idx,tarifa FROM aux_transporte_tarifas WHERE militar_id=$id ORDER BY idx;";
            command.Parameters.AddWithValue("$id", military.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                buses.Add(new AtBusLine { Index = reader.GetInt32(0), Fare = reader.IsDBNull(1) ? 0m : (decimal)reader.GetDouble(1) });
        }
        var days = military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22;
        var salary = await GetSalaryAsync(military.Rank, connection);
        var daily = Round(buses.Sum(x => x.Fare) * 2m);
        var gross = daily > 0 ? Round(daily * days) : Round((decimal)(military.TransportGrossTotal ?? 0d));
        if (daily <= 0 && gross > 0 && days > 0) daily = Round(gross / days);
        var share = Round(salary * 0.06m * (days / 30m));
        var net = Math.Max(0m, Round(gross - share));
        if (gross <= 0) net = ParseMoney(military.TransportAidValue);
        return new AtTransportState(buses, new AtCalculation(days, salary, daily, gross, share, military.IsAttached ? 0m : net));
    }

    private async Task<decimal> GetSalaryAsync(string rank, SqliteConnection? openConnection = null)
    {
        var owns = openConnection is null;
        var connection = openConnection ?? OpenMainDatabaseReadOnly();
        try
        {
            if (owns) await connection.OpenAsync();
            var candidates = new[] { rank ?? string.Empty, NormalizeRank(rank) }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT soldo FROM soldos_por_posto WHERE posto=$posto LIMIT 1;";
                command.Parameters.AddWithValue("$posto", candidate);
                var value = await command.ExecuteScalarAsync();
                if (value is not null and not DBNull)
                {
                    var salary = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    if (salary > 0) return salary;
                }
            }
        }
        catch { }
        finally { if (owns) await connection.DisposeAsync(); }
        return SalaryFallback.TryGetValue(NormalizeRank(rank), out var fallback) ? fallback : 0m;
    }

    private async Task EnsureTransportFareMetadataColumnsAsync()
    {
        await using var connection = OpenMainDatabase();
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS aux_transporte_tarifas(militar_id INTEGER NOT NULL,idx INTEGER NOT NULL,tarifa REAL NOT NULL DEFAULT 0,PRIMARY KEY(militar_id,idx));";
            await create.ExecuteNonQueryAsync();
        }
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(aux_transporte_tarifas);";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (!reader.IsDBNull(1)) columns.Add(reader.GetString(1));
        }
        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["linha"] = "TEXT NOT NULL DEFAULT ''",
            ["nome"] = "TEXT NOT NULL DEFAULT ''",
            ["categoria"] = "TEXT NOT NULL DEFAULT ''",
            ["url"] = "TEXT NOT NULL DEFAULT ''"
        };
        foreach (var addition in additions)
        {
            if (columns.Contains(addition.Key)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE aux_transporte_tarifas ADD COLUMN {addition.Key} {addition.Value};";
            await alter.ExecuteNonQueryAsync();
        }
    }

    private async Task EnsureRouteDatabaseAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.TransportRoutesDatabaseFile)!);
        await using var connection = OpenRouteDatabase();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS rotas_militar(
                militar_id INTEGER PRIMARY KEY,
                origem TEXT NOT NULL DEFAULT '',
                destino TEXT NOT NULL DEFAULT '',
                dias_uteis INTEGER NOT NULL DEFAULT 22,
                linhas_json TEXT NOT NULL DEFAULT '[]',
                print_path TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS rota_manual(
                militar_id INTEGER PRIMARY KEY,
                origem TEXT,
                destino TEXT,
                linha TEXT,
                linha_nome TEXT,
                categoria TEXT,
                consulta_url TEXT,
                tarifas_json TEXT,
                onibus_json TEXT,
                atualizado_em TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS linha_cache(
                linha TEXT PRIMARY KEY,
                nome TEXT NOT NULL DEFAULT '',
                categoria TEXT NOT NULL DEFAULT '',
                tarifa REAL NOT NULL DEFAULT 0,
                url TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS da_state(
                militar_id INTEGER NOT NULL,
                competencia TEXT NOT NULL,
                pretas INTEGER NOT NULL DEFAULT 0,
                vermelhas INTEGER NOT NULL DEFAULT 0,
                manual_override INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(militar_id,competencia)
            );
            """;
        await command.ExecuteNonQueryAsync();
        await EnsureRouteDatabaseColumnAsync(connection, "da_state", "manual_override", "INTEGER NOT NULL DEFAULT 0");
        await EnsureRouteDatabaseColumnAsync(connection, "da_state", "valor_mensal", "REAL NOT NULL DEFAULT 0");
        await EnsureRouteDatabaseColumnAsync(connection, "da_state", "valor_diario", "REAL NOT NULL DEFAULT 0");
        await EnsureRouteDatabaseColumnAsync(connection, "da_state", "valor_da", "REAL NOT NULL DEFAULT 0");
        await EnsureRouteDatabaseColumnAsync(connection, "da_state", "nome_chave", "TEXT NOT NULL DEFAULT ''");
    }

    private static async Task EnsureRouteDatabaseColumnAsync(SqliteConnection connection, string table, string column, string ddl)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (!reader.IsDBNull(1)) columns.Add(reader.GetString(1));
        }
        if (columns.Contains(column)) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl};";
        await alter.ExecuteNonQueryAsync();
    }

    private async Task RefreshMilitaryRecordAsync(int id)
    {
        var fresh = await _repository.GetByIdAsync(id);
        if (fresh is null) return;
        var index = _allMilitary.FindIndex(x => x.Id == id);
        if (index >= 0) _allMilitary[index] = fresh;
        ApplyMilitaryFilter();
        var visible = _visibleMilitary.FirstOrDefault(x => x.Id == id);
        if (visible is not null)
        {
            _militaryGrid.SelectedItem = visible;
            await LoadMilitaryAsync(visible);
        }
        RefreshReport();
    }

    private async Task OpenWalletAsync()
    {
        if (_militaryGrid.SelectedItem is not MilitaryRecord selected) return;
        var record = await _repository.GetByIdAsync(selected.Id);
        if (record is null) return;
        var wallet = new SIGFUR.Wpf.Views.Military.MilitaryWalletWindow(_repository, App.Paystubs, record) { Owner = Application.Current?.MainWindow };
        // A carteira já possui agora um único quadro completo de Auxílio-Transporte.
        // Não injeta mais um segundo card dinâmico, evitando a duplicidade vista na aba.
        wallet.Closed += async (_, _) => await RunModuleActionAsync(ModuleActionError, ReloadAsync);
        wallet.Show();
        wallet.Activate();
    }

    private async Task AddDetailedTransportCardToWalletAsync(Window wallet, MilitaryRecord military)
    {
        if (wallet.FindName("FaresGrid") is not DataGrid faresGrid || faresGrid.Parent is not StackPanel parent) return;
        if (parent.Children.OfType<FrameworkElement>().Any(x => Equals(x.Tag, "SIGFUR_AT_DETALHES"))) return;

        var route = await ReadStoredRouteAsync(military);
        var buses = route.Buses.OrderBy(x => x.Index).ToList();
        if (buses.Count == 0)
        {
            await EnsureTransportFareMetadataColumnsAsync();
            await using var connection = OpenMainDatabase();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT idx,tarifa,COALESCE(linha,''),COALESCE(nome,''),COALESCE(categoria,''),COALESCE(url,'') FROM aux_transporte_tarifas WHERE militar_id=$id ORDER BY idx;";
            command.Parameters.AddWithValue("$id", military.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                buses.Add(new AtBusLine
                {
                    Index = reader.GetInt32(0),
                    Fare = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetDouble(1), CultureInfo.InvariantCulture),
                    Number = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Category = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    SourceUrl = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                });
            }
        }
        var walletBuses = new ObservableCollection<AtBusLine>(buses);
        var border = new Border
        {
            Tag = "SIGFUR_AT_DETALHES",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceElevatedBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var stack = new StackPanel();
        border.Child = stack;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        var title = new TextBlock { Text = "Auxílio-Transporte — linhas e valores", FontWeight = FontWeights.Bold, FontSize = 17 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        heading.Children.Add(title);
        var routeText = new TextBlock { Text = $"{Blank(route.Origin)} → {Blank(route.Destination)}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        routeText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(routeText);
        header.Children.Add(heading);
        var statusBadge = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Top };
        statusBadge.Background = new SolidColorBrush(MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached ? Color.FromRgb(231, 248, 238) : Color.FromRgb(246, 247, 249));
        statusBadge.Child = new TextBlock
        {
            Text = MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached ? $"Recebe • {FormatMoney(ParseMoney(military.TransportAidValue))}" : "Não recebe",
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached ? Color.FromRgb(28, 135, 73) : Color.FromRgb(94, 103, 115))
        };
        Grid.SetColumn(statusBadge, 1);
        header.Children.Add(statusBadge);
        stack.Children.Add(header);

        var details = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserAddRows = false,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = walletBuses,
            MaxHeight = 290,
            MinHeight = 150,
            RowHeight = 34,
            ColumnHeaderHeight = 36,
            Margin = new Thickness(0, 12, 0, 10),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 251, 255)),
            RowStyle = BuildPersistentSelectedRowStyle()
        };
        details.Columns.Add(new DataGridTextColumn { Header = "Nº / linha", Binding = new Binding(nameof(AtBusLine.Number)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 105 });
        details.Columns.Add(new DataGridTextColumn { Header = "Nome / trajeto", Binding = new Binding(nameof(AtBusLine.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 220 });
        details.Columns.Add(new DataGridTextColumn { Header = "Categoria", Binding = new Binding(nameof(AtBusLine.Category)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 150 });
        details.Columns.Add(new DataGridTextColumn { Header = "1 passagem", Binding = new Binding(nameof(AtBusLine.Fare)) { StringFormat = "N2", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 });
        details.Columns.Add(new DataGridTextColumn { Header = "Ida + volta", Binding = new Binding(nameof(AtBusLine.RoundTripFormatted)), Width = 112, IsReadOnly = true });
        details.MouseDoubleClick += (_, _) =>
        {
            if (details.SelectedItem is AtBusLine selected) { ShowBusEditor(wallet, selected, false); UpdateWalletGrid(details); }
        };
        stack.Children.Add(details);

        var summary = new TextBlock { Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        void UpdateSummary()
        {
            var oneWay = walletBuses.Sum(x => Math.Max(0m, x.Fare));
            var days = military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22;
            summary.Text = $"{walletBuses.Count} linha(s) • Ida: {FormatMoney(oneWay)} • Ida e volta: {FormatMoney(oneWay * 2m)} • {days} dias úteis";
        }
        foreach (var bus in walletBuses) bus.PropertyChanged += (_, _) => UpdateSummary();
        walletBuses.CollectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
        stack.Children.Add(summary);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var editButtons = new WrapPanel();
        editButtons.Children.Add(Button("＋ Adicionar linha", (_, _) =>
        {
            var item = new AtBusLine { Index = walletBuses.Count, Category = "Informado na carteira" };
            if (!ShowBusEditor(wallet, item, true)) return;
            item.Index = walletBuses.Count;
            item.PropertyChanged += (_, _) => UpdateSummary();
            walletBuses.Add(item);
            details.SelectedItem = item;
            details.ScrollIntoView(item);
        }, "SecondaryButtonStyle"));
        editButtons.Children.Add(Button("Editar selecionado", (_, _) =>
        {
            if (details.SelectedItem is AtBusLine selected) { ShowBusEditor(wallet, selected, false); UpdateSummary(); }
            else Warn("Selecione uma linha para editar.");
        }, "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        editButtons.Children.Add(Button("Remover", (_, _) =>
        {
            if (details.SelectedItem is not AtBusLine selected) { Warn("Selecione uma linha para remover."); return; }
            walletBuses.Remove(selected);
            for (var i = 0; i < walletBuses.Count; i++) walletBuses[i].Index = i;
            UpdateSummary();
        }, "GhostButtonStyle", new Thickness(8, 0, 0, 0)));
        footer.Children.Add(editButtons);
        var saveButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var saveStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        saveButtons.Children.Add(saveStatus);
        saveButtons.Children.Add(Button("Salvar na carteira", async (_, _) =>
        {
            try
            {
                saveStatus.Text = "Salvando…";
                if (!details.CommitEdit(DataGridEditingUnit.Cell, true)
                    || !details.CommitEdit(DataGridEditingUnit.Row, true))
                {
                    saveStatus.Text = "Valor inválido";
                    Warn("Confira o valor da passagem. Use um valor como 5,25.");
                    return;
                }
                if (walletBuses.Any(x => string.IsNullOrWhiteSpace(x.Number) || x.Fare <= 0))
                {
                    saveStatus.Text = "Dados incompletos";
                    Warn("Todas as linhas devem ter número/identificação e valor maior que zero.");
                    return;
                }
                for (var i = 0; i < walletBuses.Count; i++) walletBuses[i].Index = i;
                await SaveWalletTransportAsync(military, walletBuses.ToList(), route);
                saveStatus.Text = "Salvo agora";
                var walletReceives = MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached;
                statusBadge.Background = new SolidColorBrush(walletReceives ? Color.FromRgb(231, 248, 238) : Color.FromRgb(246, 247, 249));
                if (statusBadge.Child is TextBlock badgeText)
                {
                    badgeText.Text = walletReceives ? $"Recebe • {FormatMoney(ParseMoney(military.TransportAidValue))}" : "Não recebe";
                    badgeText.Foreground = new SolidColorBrush(walletReceives ? Color.FromRgb(28, 135, 73) : Color.FromRgb(94, 103, 115));
                }
                Info("Número, nome e valor dos ônibus foram salvos no banco e vinculados à carteira do militar.");
            }
            catch (Exception ex)
            {
                saveStatus.Text = "Não foi salvo";
                await ReportErrorAsync("Não foi possível salvar a rota alterada na carteira. A janela continuará aberta.", ex);
            }
        }, "PrimaryButtonStyle"));
        Grid.SetColumn(saveButtons, 1);
        footer.Children.Add(saveButtons);
        stack.Children.Add(footer);

        var note = new TextBlock
        {
            Text = "Duplo clique ou botão Editar para alterar número, nome/trajeto, categoria e valor. As alterações só entram no cadastro após clicar em Salvar na carteira.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 9, 0, 0)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(note);
        parent.Children.Insert(parent.Children.IndexOf(faresGrid) + 1, border);
        InstallContextMenusRecursive(border);
    }

    private static void UpdateWalletGrid(DataGrid grid)
    {
        grid.Items.Refresh();
        if (grid.SelectedItem is not null) grid.ScrollIntoView(grid.SelectedItem);
    }

    private async Task SaveWalletTransportAsync(MilitaryRecord military, IReadOnlyList<AtBusLine> buses, AtStoredRoute route)
    {
        var days = military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22;
        await SaveTransportStateAsync(military, buses, days);
        await SaveRouteRecordsAsync(
            military.Id,
            route.Origin ?? string.Empty,
            route.Destination ?? DefaultDestination,
            days,
            buses,
            route.PrintPath);
        try
        {
            await SaveOriginAsMilitaryAddressAsync(military, route.Origin ?? string.Empty);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("A carteira do Auxílio-Transporte foi salva, mas falhou a sincronização do endereço residencial.", ex);
        }
        var fresh = await _repository.GetByIdAsync(military.Id);
        if (fresh is not null)
        {
            military.ReceivesTransportAid = fresh.ReceivesTransportAid;
            military.TransportAidValue = fresh.TransportAidValue;
            military.TransportGrossTotal = fresh.TransportGrossTotal;
            military.TransportWorkingDays = fresh.TransportWorkingDays;
            var index = _allMilitary.FindIndex(x => x.Id == fresh.Id);
            if (index >= 0) _allMilitary[index] = fresh;
            if (_current?.Id == fresh.Id) _current = fresh;
        }
        ApplyMilitaryFilter();
        RefreshReport();
        _statusText.Text = $"Carteira atualizada para {AtRankFormatter.ShortName(military.Rank)} {military.Name}.";
    }

    private List<MilitaryRecord> GetSelectedMilitary()
    {
        var rows = _militaryGrid.SelectedItems.Cast<object>()
            .OfType<MilitaryRecord>()
            .GroupBy(MilitaryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (rows.Count == 0 && _current is not null) rows.Add(_current);
        return rows;
    }

    private void UpdateBulletinSelectionText()
    {
        var action = _bulletinActionBox.SelectedItem?.ToString() ?? string.Empty;
        var queued = _bulletinQueueAll.Count(x => x.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        if (queued > 0)
        {
            _bulletinSelectionText.Text = IsTransportMultiCompetenceAction(action)
                ? $"Este modelo possui {queued} militar(es). Cada militar guarda sua própria competência de saque/devolução."
                : $"Este modelo possui {queued} militar(es) guardados com rota e cálculo próprios.";
            return;
        }
        var count = _militaryGrid.SelectedItems.Count;
        if (IsTransportMultiCompetenceAction(action))
        {
            _bulletinSelectionText.Text = count switch
            {
                0 => "Escolha o(s) mês(es) do militar e depois adicione-o ao saque/devolução.",
                1 => "1 militar selecionado. Escolha o(s) mês(es) dele e clique em Adicionar marcados.",
                _ => $"{count} militares selecionados. Use isso somente se todos tiverem as mesmas competências."
            };
            return;
        }
        _bulletinSelectionText.Text = count switch
        {
            0 => "Nenhum militar guardado neste modelo. Calcule e use ‘Levar ao boletim escolhido’.",
            1 => "1 militar selecionado na lista. Para guardar a rota e o cálculo, use ‘Levar ao boletim escolhido’.",
            _ => $"{count} militares selecionados na lista. A fila por modelo continua vazia."
        };
    }

    private void UpdateHeaderMetrics(IEnumerable<MilitaryRecord> source)
    {
        var rows = source.ToList();
        var receives = rows.Count(x => MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached);
        _headerTotal.Text = rows.Count.ToString(PtBr);
        _headerReceive.Text = receives.ToString(PtBr);
        _headerNoReceive.Text = (rows.Count - receives).ToString(PtBr);
        _headerValue.Text = FormatMoney(rows.Sum(EffectiveNet));
    }

    private static string HtmlNameWithWarBold(MilitaryRecord military)
    {
        var name = AtRankFormatter.CleanMilitaryName(military.Name, military.Rank).ToUpper(PtBr);
        var war = (military.WarName ?? string.Empty).Trim().ToUpper(PtBr);
        if (string.IsNullOrWhiteSpace(war)) return WebUtility.HtmlEncode(name);
        static string FoldWithMap(string value, List<int>? map = null)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                foreach (var ch in value[i].ToString().Normalize(NormalizationForm.FormD))
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                    sb.Append(char.ToUpperInvariant(ch));
                    map?.Add(i);
                }
            }
            return sb.ToString();
        }
        var indexes = new List<int>();
        var source = FoldWithMap(name, indexes);
        var target = FoldWithMap(war);
        var pos = source.IndexOf(target, StringComparison.Ordinal);
        if (pos < 0 || pos + target.Length - 1 >= indexes.Count)
            return WebUtility.HtmlEncode(name) + " — <strong>" + WebUtility.HtmlEncode(war) + "</strong>";
        var start = indexes[pos];
        var end = indexes[pos + target.Length - 1] + 1;
        return WebUtility.HtmlEncode(name[..start]) + "<strong>" + WebUtility.HtmlEncode(name[start..end]) + "</strong>" + WebUtility.HtmlEncode(name[end..]);
    }

    private static string TransportValueRange(decimal value)
    {
        if (value >= 1200m) return "R$ 1.200,00 ou mais";
        if (value >= 700m) return "R$ 700,00 a R$ 1.199,99";
        return "Abaixo de R$ 700,00";
    }

    private int TransportRangeOrder(MilitaryRecord military)
    {
        var value = EffectiveNet(military);
        if (value >= 700m && value < 1200m) return 0;
        if (value >= 1200m) return 1;
        return 2;
    }

    private void EditBusLine(AtBusLine? item)
    {
        var isNew = item is null;
        var target = item ?? new AtBusLine
        {
            Index = _routeBuses.Count,
            Category = "Informado manualmente",
            Fare = 0m
        };
        if (!ShowBusEditor(this, target, isNew)) return;
        if (isNew) _routeBuses.Add(target);
        ReindexBuses();
        _busGrid.SelectedItem = target;
        _busGrid.ScrollIntoView(target);
        _ = RecalculateAsync();
        _routeSaveStatus.Text = $"Linha {target.DisplayNumber} atualizada: {target.Name} • {target.FareFormatted} por passagem.";
    }

    private void DuplicateSelectedBus()
    {
        if (_busGrid.SelectedItem is not AtBusLine selected) return;
        var clone = new AtBusLine
        {
            Index = _routeBuses.Count,
            Number = selected.Number,
            Name = selected.Name,
            Category = selected.Category,
            Fare = selected.Fare,
            SourceUrl = selected.SourceUrl
        };
        _routeBuses.Add(clone);
        ReindexBuses();
        _busGrid.SelectedItem = clone;
        _ = RecalculateAsync();
    }

    private bool ShowBusEditor(Window owner, AtBusLine target, bool isNew)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = isNew ? "Adicionar linha de ônibus" : "Editar linha de ônibus",
            Width = 620,
            Height = 430,
            MinWidth = 520,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        AttachPersistentPlacement(dialog, "editor_linha_onibus");
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        var title = new TextBlock { Text = isNew ? "Nova linha da rota" : "Editar linha selecionada", FontSize = 20, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        heading.Children.Add(title);
        var hint = new TextBlock { Text = "Altere o número/identificação, o nome ou trajeto e o valor de uma passagem. O valor diário de ida e volta é calculado automaticamente.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(hint);
        root.Children.Add(heading);
        var card = Card(new Thickness(0, 14, 0, 14));
        var form = new Grid();
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var number = new TextBox { Text = target.Number };
        var name = new TextBox { Text = target.Name };
        var category = new TextBox { Text = target.Category };
        var fare = new TextBox { Text = target.Fare > 0 ? target.Fare.ToString("N2", PtBr) : string.Empty };
        form.Children.Add(LabeledControl("Número / identificação", number, 0));
        form.Children.Add(LabeledControl("Nome / trajeto", name, 2));
        var categoryField = LabeledControl("Categoria / tipo", category, 0);
        categoryField.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(categoryField, 1);
        form.Children.Add(categoryField);
        var fareField = LabeledControl("Valor de uma passagem (R$)", fare, 2);
        fareField.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(fareField, 1);
        form.Children.Add(fareField);
        card.Child = form;
        Grid.SetRow(card, 1);
        root.Children.Add(card);
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var preview = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryDarkBrush");
        footer.Children.Add(preview);
        void UpdatePreview()
        {
            var value = ParseMoney(fare.Text);
            preview.Text = value > 0 ? $"Ida + volta por dia: {FormatMoney(value * 2m)}" : "Informe o valor de uma passagem.";
        }
        fare.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var accepted = false;
        buttons.Children.Add(Button("Cancelar", (_, _) => dialog.Close(), "GhostButtonStyle"));
        buttons.Children.Add(Button("Salvar linha", (_, _) =>
        {
            var numberValue = NormalizeBusLine(number.Text);
            var fareValue = ParseMoney(fare.Text);
            if (string.IsNullOrWhiteSpace(numberValue)) { Warn("Informe o número ou uma identificação clara para a linha."); number.Focus(); return; }
            if (fareValue <= 0) { Warn("Informe o valor de uma passagem, maior que zero."); fare.Focus(); return; }
            target.Number = numberValue;
            target.Name = string.IsNullOrWhiteSpace(name.Text) ? $"Linha {numberValue}" : name.Text.Trim();
            target.Category = string.IsNullOrWhiteSpace(category.Text) ? "Informado manualmente" : category.Text.Trim();
            target.Fare = fareValue;
            accepted = true;
            dialog.Close();
        }, "PrimaryButtonStyle", new Thickness(8, 0, 0, 0)));
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        dialog.Content = root;
        InstallContextMenusRecursive(root);
        dialog.ShowDialog();
        return accepted;
    }

    private async Task RestoreWindowPreferencesAsync()
    {
        var file = Path.Combine(_paths.DataDirectory, "aux_transporte_wpf.json");
        if (!File.Exists(file)) return;
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var prefs = JsonSerializer.Deserialize<AtWindowPreferences>(json);
            if (prefs is null) return;
            if (!string.IsNullOrWhiteSpace(prefs.Destination)) _destinationBox.Text = prefs.Destination;
            if (!string.IsNullOrWhiteSpace(prefs.DepartureTime)) _departureTime.Text = prefs.DepartureTime;
            if (!string.IsNullOrWhiteSpace(prefs.BulletinReference)) SetCurrentBulletinReference(prefs.BulletinReference);
            if (!string.IsNullOrWhiteSpace(prefs.BulletinRequiredDetails)) _bulletinRequiredDetailsBox.Text = prefs.BulletinRequiredDetails;
            if (DateTime.TryParse(prefs.BulletinCountFromDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var countFromDate))
                _bulletinCountFromDatePicker.SelectedDate = countFromDate;
            RestorePlacement(this, prefs.Placement, MinWidth, MinHeight);
        }
        catch { }
    }

    private void ScheduleWindowPreferencesSave()
    {
        if (!IsLoaded) return;
        _windowPreferencesTimer.Stop();
        _windowPreferencesTimer.Start();
    }

    private void SaveWindowPreferences()
    {
        try
        {
            var prefs = new AtWindowPreferences
            {
                Destination = _destinationBox.Text.Trim(),
                DepartureTime = _departureTime.Text.Trim(),
                BulletinReference = CurrentBulletinReferenceText(),
                BulletinRequiredDetails = _bulletinRequiredDetailsBox.Text.Trim(),
                BulletinCountFromDate = (_bulletinCountFromDatePicker.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Placement = CapturePlacement(this)
            };
            var file = Path.Combine(_paths.DataDirectory, "aux_transporte_wpf.json");
            File.WriteAllText(file, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void AttachPersistentPlacement(Window window, string key)
    {
        var file = Path.Combine(_paths.DataDirectory, "aux_transporte_janelas.json");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };

        void Save()
        {
            try
            {
                Dictionary<string, AtWindowPlacement> map;
                if (File.Exists(file))
                    map = JsonSerializer.Deserialize<Dictionary<string, AtWindowPlacement>>(File.ReadAllText(file)) ?? new(StringComparer.OrdinalIgnoreCase);
                else
                    map = new(StringComparer.OrdinalIgnoreCase);
                map[key] = CapturePlacement(window);
                File.WriteAllText(file, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        void Schedule()
        {
            if (!window.IsLoaded) return;
            timer.Stop();
            timer.Start();
        }

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Save();
        };

        try
        {
            if (File.Exists(file))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, AtWindowPlacement>>(File.ReadAllText(file)) ?? new(StringComparer.OrdinalIgnoreCase);
                if (map.TryGetValue(key, out var saved)) RestorePlacement(window, saved, window.MinWidth, window.MinHeight);
            }
        }
        catch { }

        window.LocationChanged += (_, _) => Schedule();
        window.SizeChanged += (_, _) => Schedule();
        window.StateChanged += (_, _) => Schedule();
        window.Closing += (_, _) =>
        {
            timer.Stop();
            Save();
        };
    }

    private static AtWindowPlacement CapturePlacement(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        return new AtWindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            State = window.WindowState == WindowState.Minimized ? WindowState.Normal.ToString() : window.WindowState.ToString()
        };
    }

    private static void RestorePlacement(Window window, AtWindowPlacement? placement, double minWidth, double minHeight)
    {
        if (placement is null || placement.Width <= 0 || placement.Height <= 0) return;
        var width = Math.Max(minWidth, Math.Min(placement.Width, SystemParameters.VirtualScreenWidth));
        var height = Math.Max(minHeight, Math.Min(placement.Height, SystemParameters.VirtualScreenHeight));
        var left = placement.Left;
        var top = placement.Top;
        var virtualRect = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var candidate = new Rect(left, top, width, height);
        if (!virtualRect.IntersectsWith(candidate) || candidate.Right < virtualRect.Left + 80 || candidate.Bottom < virtualRect.Top + 60)
        {
            var work = SystemParameters.WorkArea;
            left = work.Left + Math.Max(0, (work.Width - width) / 2);
            top = work.Top + Math.Max(0, (work.Height - height) / 2);
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
        if (Enum.TryParse<WindowState>(placement.State, out var state) && state != WindowState.Minimized)
            window.WindowState = state;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = ReloadAsync(); e.Handled = true; return; }
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _searchBox.Focus(); _searchBox.SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Close(); e.Handled = true; }
    }

    private void ConfigureBusGrid()
    {
        _busGrid.AutoGenerateColumns = false;
        _busGrid.ItemsSource = _routeBuses;
        _busGrid.CanUserAddRows = false;
        _busGrid.CanUserReorderColumns = true;
        _busGrid.CanUserResizeColumns = true;
        _busGrid.SelectionMode = DataGridSelectionMode.Single;
        _busGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _busGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _busGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _busGrid.RowHeight = 35;
        _busGrid.ColumnHeaderHeight = 37;
        _busGrid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 251, 255));
        _busGrid.RowStyle = BuildPersistentSelectedRowStyle();
        _busGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(AtBusLine.DisplayIndex)), Width = 42, IsReadOnly = true });
        _busGrid.Columns.Add(new DataGridTextColumn { Header = "Nº / linha", Binding = new Binding(nameof(AtBusLine.Number)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 105 });
        _busGrid.Columns.Add(new DataGridTextColumn { Header = "Nome / trajeto da linha", Binding = new Binding(nameof(AtBusLine.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 250 });
        _busGrid.Columns.Add(new DataGridTextColumn { Header = "Categoria", Binding = new Binding(nameof(AtBusLine.Category)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 155 });
        _busGrid.Columns.Add(new DataGridTextColumn { Header = "1 passagem", Binding = new Binding(nameof(AtBusLine.Fare)) { StringFormat = "N2", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 115 });
        _busGrid.Columns.Add(new DataGridTextColumn { Header = "Ida + volta", Binding = new Binding(nameof(AtBusLine.RoundTripFormatted)), Width = 118, IsReadOnly = true });
        _busGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = RecalculateAsync()));
        _busGrid.MouseDoubleClick += (_, _) => EditBusLine(_busGrid.SelectedItem as AtBusLine);

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Adicionar nova linha", (_, _) => EditBusLine(null)));
        menu.Items.Add(MenuItem("Editar número, nome e valor", (_, _) => EditBusLine(_busGrid.SelectedItem as AtBusLine)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Remover selecionado", (_, _) => RemoveSelectedBus()));
        menu.Items.Add(MenuItem("Duplicar linha", (_, _) => DuplicateSelectedBus()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Copiar linha", (_, _) => CopyDataGridSelection(_busGrid)));
        _busGrid.ContextMenu = menu;
        EnsureRightClickSelectsRow(_busGrid);
    }

    private void ConfigureDaGrid()
    {
        _daGrid.AutoGenerateColumns = false;
        _daGrid.ItemsSource = _daRows;
        _daGrid.CanUserAddRows = false;
        _daGrid.IsReadOnly = false;
        _daGrid.SelectionMode = DataGridSelectionMode.Single;
        _daGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _daGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _daGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _daGrid.RowHeight = double.NaN;
        _daGrid.MinRowHeight = 32;
        _daGrid.ColumnHeaderHeight = 34;
        _daGrid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 251, 255));
        _daGrid.Columns.Add(new AtRankDataGridColumn { Header = "P/G", Width = 70, MinWidth = 64, SortMemberPath = "Military.Rank" });
        _daGrid.Columns.Add(new WarNameDataGridColumn { Header = "Nome completo", Width = new DataGridLength(1, DataGridLengthUnitType.Star), SortMemberPath = "Military.Name", IsReadOnly = true });
        _daGrid.Columns.Add(new DataGridTextColumn { Header = "Pretas", Binding = new Binding(nameof(AtDaRow.BlackDays)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = 68 });
        _daGrid.Columns.Add(new DataGridTextColumn { Header = "Verm.", Binding = new Binding(nameof(AtDaRow.RedDays)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = 68 });
        _daGrid.Columns.Add(new DataGridTextColumn { Header = "Origem", Binding = new Binding(nameof(AtDaRow.SourceText)), Width = 130, IsReadOnly = true });
        _daGrid.Columns.Add(new DataGridTextColumn { Header = "Dias", Binding = new Binding(nameof(AtDaRow.NetDays)), Width = 64, IsReadOnly = true });
        _daGrid.Columns.Add(new DataGridTextColumn { Header = "Valor diário", Binding = new Binding(nameof(AtDaRow.DailyNetFormatted)), Width = 96, IsReadOnly = true });
        _daGrid.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new Binding(nameof(AtDaRow.DeductionFormatted)), Width = 100, IsReadOnly = true });
        _daGrid.CellEditEnding += (_, e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is AtDaRow row) row.ManualOverride = true;
            UpdateDaTotal();
            UpdateDaSelectedText();
            _ = SaveDaAsync(false);
        }));
        _daGrid.SelectionChanged += (_, _) => UpdateDaSelectedText();
        _daGrid.Sorting += (_, e) => HandleRankSorting(
            _daGrid,
            e,
            direction =>
            {
                var ordered = direction == ListSortDirection.Ascending
                    ? _daRows.OrderBy(x => AtRankFormatter.GetOrder(x.Military.Rank)).ThenBy(x => Normalize(x.Military.Name))
                    : _daRows.OrderByDescending(x => AtRankFormatter.GetOrder(x.Military.Rank)).ThenBy(x => Normalize(x.Military.Name));
                ReplaceCollection(_daRows, ordered.ToList());
                ApplyDaFilter();
            });
        _daGrid.MouseDoubleClick += (_, _) => AdjustSelectedDa(false, 1);
        _daGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            var delta = e.Key == Key.Right ? 1 : -1;
            AdjustSelectedDa(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), delta);
            e.Handled = true;
        };
        _daGrid.PreviewTextInput += (_, e) =>
        {
            if (!e.Text.All(char.IsDigit)) e.Handled = true;
        };

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Preta +1", (_, _) => AdjustSelectedDa(false, 1)));
        menu.Items.Add(MenuItem("Preta −1", (_, _) => AdjustSelectedDa(false, -1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Vermelha +1", (_, _) => AdjustSelectedDa(true, 1)));
        menu.Items.Add(MenuItem("Vermelha −1", (_, _) => AdjustSelectedDa(true, -1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Zerar este militar", (_, _) => ResetSelectedDa()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Copiar nome", (_, _) =>
        {
            if (_daGrid.SelectedItem is AtDaRow row) Clipboard.SetText(row.Military.Name ?? string.Empty);
        }));
        menu.Items.Add(MenuItem("Copiar linha", (_, _) => CopyDataGridSelection(_daGrid)));
        _daGrid.ContextMenu = menu;
        EnsureRightClickSelectsRow(_daGrid);
    }

    private void ConfigureReportGrid()
    {
        _reportGrid.AutoGenerateColumns = false;
        _reportGrid.IsReadOnly = true;
        _reportGrid.CanUserReorderColumns = true;
        _reportGrid.CanUserResizeColumns = true;
        _reportGrid.SelectionMode = DataGridSelectionMode.Extended;
        _reportGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _reportGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _reportGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _reportGrid.RowHeight = double.NaN;
        _reportGrid.MinRowHeight = 35;
        _reportGrid.ColumnHeaderHeight = 38;
        _reportGrid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 251, 255));
        _reportGrid.RowStyle = BuildProfessionalMilitaryRowStyle();
        _reportGrid.Columns.Add(new AtRankDataGridColumn { Header = "P/G", Width = 70, MinWidth = 66, SortMemberPath = nameof(MilitaryRecord.Rank) });
        _reportGrid.Columns.Add(new WarNameDataGridColumn { Header = "Nome completo", Width = new DataGridLength(2.2, DataGridLengthUnitType.Star), MinWidth = 300, SortMemberPath = nameof(MilitaryRecord.Name) });
        _reportDocumentColumn = new DataGridTextColumn { Header = "CPF", Binding = new Binding(nameof(MilitaryRecord.FormattedCpf)), Width = 122 };
        _reportGrid.Columns.Add(_reportDocumentColumn);
        _reportGrid.Columns.Add(new DataGridTextColumn { Header = "Endereço", Binding = new Binding(nameof(MilitaryRecord.Address)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), MinWidth = 230 });
        _reportGrid.Columns.Add(new AtReceiveFlagDataGridColumn { Header = "Recebe", Width = 84, MinWidth = 80, SortMemberPath = nameof(MilitaryRecord.ReceivesTransportAid) });
        _reportGrid.Columns.Add(new AtTransportValueDataGridColumn { Header = "Valor AT", Width = 112, MinWidth = 106, SortMemberPath = nameof(MilitaryRecord.TransportAidValue) });
        _reportGrid.Columns.Add(new AtReportDaReasonDataGridColumn(m => ReportHasVacation(m) ? "Sim" : "Não") { Header = "Férias", Width = 76, MinWidth = 70, CanUserSort = false });
        _reportGrid.Columns.Add(new AtReportDaReasonDataGridColumn(ReportVacationDaNoteStatus) { Header = "Nota DA do AT", Width = 138, MinWidth = 126, CanUserSort = false });
        _reportDaColumn = new AtReportDaValueDataGridColumn(ReportDaDeduction, ReportDaReason) { Header = "Despesa a Anular", Width = 136, MinWidth = 124, CanUserSort = false };
        _reportGrid.Columns.Add(_reportDaColumn);
        UpdateReportDocumentColumn();
        _reportGrid.Sorting += (_, e) => HandleRankSorting(
            _reportGrid,
            e,
            direction =>
            {
                var currentRows = _reportGrid.ItemsSource?.Cast<MilitaryRecord>().ToList() ?? [];
                _reportGrid.ItemsSource = direction == ListSortDirection.Ascending
                    ? currentRows.OrderBy(x => AtRankFormatter.GetOrder(x.Rank)).ThenBy(x => Normalize(x.Name)).ToList()
                    : currentRows.OrderByDescending(x => AtRankFormatter.GetOrder(x.Rank)).ThenBy(x => Normalize(x.Name)).ToList();
            });
        _reportGrid.MouseDoubleClick += async (_, _) =>
        {
            await RunModuleActionAsync(ModuleActionError, async () =>
            {
                if (_reportGrid.SelectedItem is not MilitaryRecord record) return;
                var fresh = await _repository.GetByIdAsync(record.Id);
                if (fresh is null) return;
                var wallet = new SIGFUR.Wpf.Views.Military.MilitaryWalletWindow(_repository, App.Paystubs, fresh) { Owner = Application.Current?.MainWindow };
                wallet.Closed += async (_, _) => await RunModuleActionAsync(ModuleActionError, ReloadAsync);
                wallet.Show();
                wallet.Activate();
            });
        };
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Abrir carteira", async (_, _) =>
        {
            await RunModuleActionAsync(ModuleActionError, async () =>
            {
                if (_reportGrid.SelectedItem is not MilitaryRecord record) return;
                var visible = _visibleMilitary.FirstOrDefault(x => x.Id == record.Id);
                if (visible is not null) _militaryGrid.SelectedItem = visible;
                await OpenWalletAsync();
            });
        }));
        menu.Items.Add(MenuItem("Copiar linha", (_, _) => CopyDataGridSelection(_reportGrid)));
        _reportGrid.ContextMenu = menu;
        EnsureRightClickSelectsRow(_reportGrid);
    }

    private static Border Card(Thickness? margin = null)
    {
        var border = new Border { Padding = new Thickness(16), Margin = margin ?? new Thickness(0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private static StackPanel InlineReportOption(string label, Control control, Thickness? margin = null)
    {
        control.Margin = new Thickness(0, 3, 0, 0);
        var panel = new StackPanel { Margin = margin ?? new Thickness(0), MinWidth = control.Width };
        var caption = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(caption);
        panel.Children.Add(control);
        return panel;
    }

    private static FrameworkElement LabeledControl(string label, Control control, int column)
    {
        return LabeledControl(label, control, column, out _);
    }

    private static FrameworkElement LabeledControl(string label, Control control, int column, out TextBlock labelBlock)
    {
        return LabeledElement(label, control, column, out labelBlock);
    }

    private static FrameworkElement LabeledElement(string label, FrameworkElement element, int column, out TextBlock labelBlock)
    {
        var panel = new StackPanel();
        labelBlock = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        panel.Children.Add(labelBlock);
        panel.Children.Add(element);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private static Border SummaryChip(string caption, TextBlock value, Color background, Color foreground, bool stretch = false)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, foreground.R, foreground.G, foreground.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 10, 0),
            MinWidth = stretch ? 0 : 150,
            MinHeight = 60,
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(foreground),
            Opacity = 0.84,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        value.FontSize = 18;
        value.FontWeight = FontWeights.Bold;
        value.Margin = new Thickness(0, 5, 0, 0);
        value.MinHeight = 24;
        value.Foreground = new SolidColorBrush(foreground);
        value.Text = "0";
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.TextWrapping = TextWrapping.NoWrap;
        stack.Children.Add(value);

        border.Child = stack;
        return border;
    }

    private static Border MetricCard(string caption, TextBlock value, bool primary = false)
    {
        var card = Card(new Thickness(0, 0, 8, 0));
        if (primary) card.SetResourceReference(Border.BackgroundProperty, "PrimarySoftBrush");
        var stack = new StackPanel();
        var cap = new TextBlock { Text = caption, FontSize = 11 };
        cap.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        value.FontSize = 18;
        value.FontWeight = FontWeights.Bold;
        value.Margin = new Thickness(0, 4, 0, 0);
        value.MinHeight = 25;
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.None;
        value.Visibility = Visibility.Visible;
        value.Opacity = 1;
        value.SetResourceReference(TextBlock.ForegroundProperty, primary ? "PrimaryDarkBrush" : "TextBrush");
        stack.Children.Add(cap);
        stack.Children.Add(value);
        card.Child = stack;
        return card;
    }

    private static Button Button(string text, RoutedEventHandler handler, string styleKey, Thickness? margin = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = margin ?? new Thickness(0), MinHeight = 36 };
        if (Application.Current.TryFindResource(styleKey) is Style style) button.Style = style;
        button.Click += handler;
        return button;
    }

    private static Button SmallButton(string text, RoutedEventHandler handler, string styleKey, Thickness? margin = null)
    {
        var button = Button(text, handler, styleKey, margin);
        button.Padding = new Thickness(9, 4, 9, 4);
        button.MinHeight = 28;
        button.FontSize = 12;
        return button;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string text, RoutedEventHandler handler)
    {
        var item = new System.Windows.Controls.MenuItem { Header = text };
        item.Click += handler;
        return item;
    }

    private void InstallContextMenusRecursive(DependencyObject? root)
    {
        if (root is null) return;
        InstallContextMenu(root);

        // Run, Paragraph e outros ContentElement não são Visual/Visual3D.
        // Chamar VisualTreeHelper neles gera exatamente o erro informado pelo usuário.
        var visualChildren = 0;
        if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
        {
            try { visualChildren = VisualTreeHelper.GetChildrenCount(root); }
            catch { visualChildren = 0; }
            for (var i = 0; i < visualChildren; i++)
            {
                DependencyObject? child = null;
                try { child = VisualTreeHelper.GetChild(root, i); } catch { }
                if (child is not null) InstallContextMenusRecursive(child);
            }
        }

        if (visualChildren == 0)
        {
            try
            {
                foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                    InstallContextMenusRecursive(child);
            }
            catch { }
        }
    }

    private void InstallContextMenu(DependencyObject obj)
    {
        switch (obj)
        {
            case DataGrid grid:
                EnsureRightClickSelectsRow(grid);
                if (grid.ContextMenu is null) grid.ContextMenu = BuildDataGridContextMenu(grid);
                break;
            case TextBox textBox when textBox.ContextMenu is null:
                textBox.ContextMenu = BuildTextBoxContextMenu(textBox);
                break;
            case RichTextBox rich when rich.ContextMenu is null:
                rich.ContextMenu = BuildRichTextContextMenu(rich);
                break;
            case ComboBox combo when combo.ContextMenu is null:
                combo.ContextMenu = BuildComboContextMenu(combo);
                break;
            case TextBlock text when text.ContextMenu is null:
                text.ContextMenu = BuildCopyOnlyContextMenu(() => GetTextBlockText(text));
                break;
            case Label label when label.ContextMenu is null:
                label.ContextMenu = BuildCopyOnlyContextMenu(() => label.Content?.ToString() ?? string.Empty);
                break;
            case Button button when button.ContextMenu is null:
                button.ContextMenu = BuildCopyOnlyContextMenu(() => button.Content?.ToString() ?? string.Empty);
                break;
        }
    }

    private static ContextMenu BuildTextBoxContextMenu(TextBox box)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Recortar", (_, _) => box.Cut()));
        menu.Items.Add(MenuItem("Copiar", (_, _) => box.Copy()));
        menu.Items.Add(MenuItem("Colar", (_, _) => box.Paste()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Selecionar tudo", (_, _) => box.SelectAll()));
        menu.Items.Add(MenuItem("Limpar campo", (_, _) => box.Clear()));
        menu.Opened += (_, _) =>
        {
            ((System.Windows.Controls.MenuItem)menu.Items[0]).IsEnabled = !box.IsReadOnly && box.SelectionLength > 0;
            ((System.Windows.Controls.MenuItem)menu.Items[1]).IsEnabled = box.SelectionLength > 0;
            ((System.Windows.Controls.MenuItem)menu.Items[2]).IsEnabled = !box.IsReadOnly && Clipboard.ContainsText();
            ((System.Windows.Controls.MenuItem)menu.Items[5]).IsEnabled = !box.IsReadOnly && box.Text.Length > 0;
        };
        return menu;
    }

    private static ContextMenu BuildRichTextContextMenu(RichTextBox box)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Recortar", (_, _) => box.Cut()));
        menu.Items.Add(MenuItem("Copiar formatado", (_, _) => box.Copy()));
        menu.Items.Add(MenuItem("Colar", (_, _) => box.Paste()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Selecionar tudo", (_, _) => box.SelectAll()));
        menu.Items.Add(MenuItem("Limpar", (_, _) => box.Document.Blocks.Clear()));
        menu.Opened += (_, _) =>
        {
            ((System.Windows.Controls.MenuItem)menu.Items[0]).IsEnabled = !box.IsReadOnly && !box.Selection.IsEmpty;
            ((System.Windows.Controls.MenuItem)menu.Items[1]).IsEnabled = !box.Selection.IsEmpty;
            ((System.Windows.Controls.MenuItem)menu.Items[2]).IsEnabled = !box.IsReadOnly && Clipboard.ContainsText();
            ((System.Windows.Controls.MenuItem)menu.Items[5]).IsEnabled = !box.IsReadOnly;
        };
        return menu;
    }

    private static ContextMenu BuildComboContextMenu(ComboBox combo)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Copiar valor", (_, _) => Clipboard.SetText(combo.Text ?? combo.SelectedItem?.ToString() ?? string.Empty)));
        menu.Items.Add(MenuItem("Colar", (_, _) =>
        {
            if (!combo.IsEditable || !Clipboard.ContainsText()) return;
            combo.Text = Clipboard.GetText();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Limpar", (_, _) =>
        {
            combo.SelectedItem = null;
            if (combo.IsEditable) combo.Text = string.Empty;
        }));
        menu.Opened += (_, _) =>
        {
            ((System.Windows.Controls.MenuItem)menu.Items[1]).IsEnabled = combo.IsEditable && Clipboard.ContainsText();
            ((System.Windows.Controls.MenuItem)menu.Items[3]).IsEnabled = combo.IsEditable || combo.SelectedItem is not null;
        };
        return menu;
    }

    private ContextMenu BuildDataGridContextMenu(DataGrid grid)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Copiar célula", (_, _) => CopyDataGridCurrentCell(grid)));
        menu.Items.Add(MenuItem("Copiar linha", (_, _) => CopyDataGridSelection(grid, true)));
        menu.Items.Add(MenuItem("Copiar selecionados", (_, _) => CopyDataGridSelection(grid)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Selecionar tudo", (_, _) => grid.SelectAll()));
        menu.Items.Add(MenuItem("Limpar seleção", (_, _) => grid.UnselectAll()));
        return menu;
    }

    private static ContextMenu BuildCopyOnlyContextMenu(Func<string> valueFactory)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Copiar texto", (_, _) =>
        {
            var text = valueFactory();
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }));
        return menu;
    }

    private static void EnsureRightClickSelectsRow(DataGrid grid)
    {
        if (grid.Tag is string marker && marker.Contains("sigfur-context", StringComparison.Ordinal)) return;
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row is null) return;
            if (!row.IsSelected)
            {
                grid.SelectedItems.Clear();
                row.IsSelected = true;
                grid.CurrentItem = row.Item;
            }
        };
        grid.Tag = ((grid.Tag?.ToString() ?? string.Empty) + " sigfur-context").Trim();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;

            DependencyObject? parent = null;
            if (child is ContentElement content)
            {
                parent = ContentOperations.GetParent(content);
                if (parent is null && content is FrameworkContentElement frameworkContent)
                    parent = frameworkContent.Parent;
            }
            else
            {
                try
                {
                    if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
                        parent = VisualTreeHelper.GetParent(child);
                }
                catch { }

                if (parent is null && child is FrameworkElement framework)
                    parent = framework.Parent ?? framework.TemplatedParent;
            }
            child = parent;
        }
        return null;
    }

    private static void CopyDataGridCurrentCell(DataGrid grid)
    {
        if (grid.CurrentCell.Item is null || grid.CurrentCell.Column is null) return;
        var value = GetColumnValue(grid.CurrentCell.Column, grid.CurrentCell.Item);
        if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value);
    }

    private static void CopyDataGridSelection(DataGrid grid, bool currentRowOnly = false)
    {
        var rows = currentRowOnly
            ? (grid.CurrentItem is null ? new List<object>() : new List<object> { grid.CurrentItem })
            : grid.SelectedItems.Cast<object>().ToList();
        if (rows.Count == 0 && grid.CurrentItem is not null) rows.Add(grid.CurrentItem);
        if (rows.Count == 0) return;
        var lines = rows.Select(row => string.Join('\t', grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => GetColumnValue(c, row))));
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private static string GetColumnValue(DataGridColumn column, object row)
    {
        if (column is WarNameDataGridColumn)
        {
            var military = row as MilitaryRecord ?? (row as AtDaRow)?.Military;
            return military is null ? string.Empty : AtRankFormatter.CleanMilitaryName(military.Name, military.Rank);
        }
        if (column is AtReceiveFlagDataGridColumn)
        {
            var military = row as MilitaryRecord ?? (row as AtDaRow)?.Military;
            return military is not null && MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached ? "Sim" : "Não";
        }
        if (column is AtReportDaValueDataGridColumn reportDaColumn)
            return reportDaColumn.GetValueText(row);
        var path = column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(path) && column is DataGridBoundColumn bound && bound.Binding is Binding binding)
            path = binding.Path?.Path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        object? value = row;
        foreach (var part in path.Split('.'))
        {
            if (value is null) break;
            value = value.GetType().GetProperty(part)?.GetValue(value);
        }
        return value switch
        {
            null => string.Empty,
            decimal d => d.ToString("N2", PtBr),
            double d => d.ToString("N2", PtBr),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string GetTextBlockText(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text;
        var sb = new StringBuilder();
        foreach (var inline in block.Inlines)
        {
            if (inline is Run run) sb.Append(run.Text);
            else sb.Append(inline.ToString());
        }
        return sb.ToString();
    }

    private void ReindexBuses()
    {
        for (var i = 0; i < _routeBuses.Count; i++) _routeBuses[i].Index = i;
        _busGrid.Items.Refresh();
    }

    private static void SetMilitaryName(TextBlock target, MilitaryRecord military, string prefix = "", bool upper = true)
    {
        target.Inlines.Clear();
        if (!string.IsNullOrEmpty(prefix)) target.Inlines.Add(new Run(prefix));
        AddNameWithWarBold(target, military, upper);
    }

    private static void AddNameWithWarBold(Paragraph paragraph, MilitaryRecord military)
    {
        AddNameRuns(paragraph.Inlines, military, true);
    }

    private static void AddNameWithWarBold(TextBlock textBlock, MilitaryRecord military, bool upper = true)
    {
        AddNameRuns(textBlock.Inlines, military, upper);
    }

    private static void AddNameRuns(InlineCollection inlines, MilitaryRecord military, bool upper)
    {
        var originalName = AtRankFormatter.CleanMilitaryName(military.Name, military.Rank);
        var originalWar = military.WarName ?? string.Empty;
        var name = upper ? originalName.ToUpper(PtBr) : originalName;
        var war = upper ? originalWar.ToUpper(PtBr) : originalWar;
        if (string.IsNullOrWhiteSpace(war))
        {
            inlines.Add(new Run(name));
            return;
        }
        var range = FindNormalizedRange(name, war);
        if (range.Start < 0)
        {
            inlines.Add(new Run(name));
            if (!string.IsNullOrWhiteSpace(war))
            {
                inlines.Add(new Run(" — "));
                inlines.Add(new Run(war) { FontWeight = FontWeights.Bold });
            }
            return;
        }
        if (range.Start > 0) inlines.Add(new Run(name[..range.Start]));
        inlines.Add(new Run(name.Substring(range.Start, range.Length)) { FontWeight = FontWeights.Bold });
        if (range.Start + range.Length < name.Length) inlines.Add(new Run(name[(range.Start + range.Length)..]));
    }

    private static (int Start, int Length) FindNormalizedRange(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return (-1, 0);
        var normalizedSource = new StringBuilder();
        var map = new List<int>();
        for (var i = 0; i < source.Length; i++)
        {
            var decomposed = source[i].ToString().Normalize(NormalizationForm.FormD);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                normalizedSource.Append(char.ToUpperInvariant(ch));
                map.Add(i);
            }
        }
        var normalizedTarget = Normalize(target).ToUpperInvariant();
        var index = normalizedSource.ToString().IndexOf(normalizedTarget, StringComparison.Ordinal);
        if (index < 0 || index >= map.Count || index + normalizedTarget.Length - 1 >= map.Count) return (-1, 0);
        var start = map[index];
        var end = map[index + normalizedTarget.Length - 1] + 1;
        return (start, Math.Max(0, end - start));
    }

    private static void CopyRichText(RichTextBox box)
    {
        try
        {
            var range = new TextRange(box.Document.ContentStart, box.Document.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, DataFormats.Rtf);
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.ASCII, true, 1024, leaveOpen: true);
            var rtf = reader.ReadToEnd();
            var plain = range.Text.TrimEnd('\r', '\n');
            var data = new DataObject();
            data.SetData(DataFormats.Rtf, rtf);
            data.SetData(DataFormats.UnicodeText, plain);
            data.SetData(DataFormats.Text, plain);
            Clipboard.SetDataObject(data, true);
        }
        catch
        {
            try
            {
                box.SelectAll();
                box.Copy();
            }
            catch { }
        }
    }

    private void SaveRichText(RichTextBox box, string extension)
    {
        var dialog = new SaveFileDialog
        {
            Filter = extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase) ? "Rich Text (*.rtf)|*.rtf" : "Texto UTF-8 (*.txt)|*.txt",
            DefaultExt = extension,
            FileName = $"auxilio_transporte_{DateTime.Now:yyyyMMdd_HHmm}{extension}"
        };
        if (dialog.ShowDialog(this) != true) return;
        var range = new TextRange(box.Document.ContentStart, box.Document.ContentEnd);
        if (extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Create(dialog.FileName);
            range.Save(stream, DataFormats.Rtf);
        }
        else File.WriteAllText(dialog.FileName, range.Text.TrimEnd(), new UTF8Encoding(true));
        Info("Arquivo salvo com sucesso.");
    }

    private void OpenTransportFolder()
    {
        var folder = Path.Combine(_paths.DataDirectory, "aux_transporte_rotas");
        Directory.CreateDirectory(folder);
        ShellService.OpenPath(folder);
    }

    private SqliteConnection OpenMainDatabase()
    {
        EnsureWritableSqliteTarget(_paths.DatabaseFile);
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 12
        }.ToString());
    }

    private SqliteConnection OpenRouteDatabase()
    {
        EnsureWritableSqliteTarget(_paths.TransportRoutesDatabaseFile);
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.TransportRoutesDatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 12
        }.ToString());
    }

    private SqliteConnection OpenMainDatabaseReadOnly()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 12
        }.ToString());
    }

    private SqliteConnection OpenRouteDatabaseReadOnly()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.TransportRoutesDatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 12
        }.ToString());
    }

    private void EnsureWritableSqliteTarget(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dataRoot = Path.GetFullPath(_paths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"O banco está fora da pasta de dados do SIGFUR: {full}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full))
            {
                var attributes = File.GetAttributes(full);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
                    _ = _log.WriteAsync($"Atributo somente leitura removido do SQLite do Auxílio-Transporte: {full}");
                }
            }
            var probe = Path.Combine(Path.GetDirectoryName(full)!, $".sigfur_write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync($"Falha ao validar escrita no SQLite do Auxílio-Transporte/DA: {path}", ex);
            throw new InvalidOperationException($"Não foi possível gravar no banco do SIGFUR.\n\nCaminho: {path}\n\nO arquivo pode estar somente leitura, em pasta protegida ou bloqueado por outro processo.", ex);
        }
    }

    private async Task ReportErrorAsync(string message, Exception ex)
    {
        _statusText.Text = message + " " + ex.Message;
        await _log.WriteAsync(message, ex);
        SigfurDialog.Show(this, message + "\n\n" + ex.Message, "SIGFUR — Auxílio-Transporte", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task RunModuleActionAsync(string errorMessage, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(errorMessage, ex);
        }
    }

    private void Info(string message) => SigfurDialog.Show(this, message, "SIGFUR — Auxílio-Transporte", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Warn(string message) => SigfurDialog.Show(this, message, "SIGFUR — Auxílio-Transporte", MessageBoxButton.OK, MessageBoxImage.Warning);
    private bool Confirm(string title, string message) => SigfurDialog.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/137.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.7");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static int ParsePositiveInt(string? value, int fallback) => int.TryParse(value?.Trim(), NumberStyles.Integer, PtBr, out var parsed) && parsed > 0 ? parsed : fallback;
    private static decimal ParseMoney(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        var raw = value
            .Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty)
            .Trim();

        // O banco antigo normalmente grava "1234.56". Tentar pt-BR primeiro
        // transforma esse valor em 123456, porque o ponto vira separador de milhar.
        // Por isso o formato é identificado antes da conversão.
        if (raw.Contains(',') && raw.Contains('.'))
        {
            var lastComma = raw.LastIndexOf(',');
            var lastDot = raw.LastIndexOf('.');
            var normalized = lastComma > lastDot
                ? raw.Replace(".", string.Empty).Replace(',', '.')
                : raw.Replace(",", string.Empty);
            return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mixed)
                ? mixed
                : 0m;
        }

        if (raw.Contains(','))
            return decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, PtBr, out var br)
                ? br
                : 0m;

        if (raw.Contains('.'))
            return decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var invariant)
                ? invariant
                : 0m;

        return decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            ? integer
            : 0m;
    }
    private static string FormatMoney(decimal value) => value.ToString("C2", PtBr);
    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
    private static string SafeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
        return Regex.Replace(value, @"\s+", "_").Trim('_', '.');
    }
    private static string NormalizeBusLine(string? value) => new((value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string CleanHtml(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
    private static string Csv(string? value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"") + '"';
    private static string CsvExcelText(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return Csv(string.Empty);
        var excelText = "=\"" + text.Replace("\"", "\"\"") + "\"";
        return Csv(excelText);
    }
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }
    private static string NormalizeCompetence(string? value)
    {
        return TryParseCompetence(value, out var date)
            ? date.ToString("MM/yyyy", CultureInfo.InvariantCulture)
            : DateTime.Today.ToString("MM/yyyy", CultureInfo.InvariantCulture);
    }
    private static string FormatCompetenceDisplay(DateTime date) => date.ToString("MMM yy", PtBr).Replace(".", string.Empty, StringComparison.Ordinal).ToUpper(PtBr);
    private static string FormatMilitaryDateShort(DateTime date) => date.ToString("dd MMM yy", PtBr).Replace(".", string.Empty, StringComparison.Ordinal).ToUpper(PtBr);
    private static string FormatCompetenceDisplay(string? value)
    {
        return TryParseCompetence(value, out var date)
            ? FormatCompetenceDisplay(date)
            : FormatCompetenceDisplay(DateTime.Today);
    }
    private static bool TryParseCompetence(string? value, out DateTime date)
    {
        date = default;
        var raw = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ").Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var compact = raw.Replace(".", string.Empty, StringComparison.Ordinal);
        var invariantFormats = new[] { "MM/yyyy", "M/yyyy", "MM/yy", "M/yy", "yyyy-MM" };
        foreach (var candidate in new[] { raw, compact })
        {
            if (DateTime.TryParseExact(candidate, invariantFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = new DateTime(parsed.Year, parsed.Month, 1);
                return true;
            }
        }

        var cultureFormats = new[] { "MMM yy", "MMM/yy", "MMM yyyy", "MMMM yy", "MMMM yyyy" };
        if (DateTime.TryParseExact(raw, cultureFormats, PtBr, DateTimeStyles.AllowWhiteSpaces, out var cultureParsed)
            || DateTime.TryParseExact(compact, cultureFormats, PtBr, DateTimeStyles.AllowWhiteSpaces, out cultureParsed))
        {
            date = new DateTime(cultureParsed.Year, cultureParsed.Month, 1);
            return true;
        }

        var named = Regex.Match(compact, @"^(?<m>\p{L}{3,12})\s*/?\s*(?<y>\d{2,4})$", RegexOptions.IgnoreCase);
        if (named.Success
            && TryParsePortugueseMonth(named.Groups["m"].Value, out var namedMonth)
            && int.TryParse(named.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var namedYear))
        {
            if (namedYear < 100) namedYear += 2000;
            date = new DateTime(namedYear, namedMonth, 1);
            return true;
        }

        var numeric = Regex.Match(compact, @"^(?<m>\d{1,2})[\s/-]+(?<y>\d{2,4})$", RegexOptions.IgnoreCase);
        if (numeric.Success
            && int.TryParse(numeric.Groups["m"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericMonth)
            && int.TryParse(numeric.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericYear)
            && numericMonth is >= 1 and <= 12)
        {
            if (numericYear < 100) numericYear += 2000;
            date = new DateTime(numericYear, numericMonth, 1);
            return true;
        }

        return false;
    }

    private static bool TryParsePortugueseMonth(string? value, out int month)
    {
        month = Normalize(value).Trim('.') switch
        {
            "jan" or "janeiro" => 1,
            "fev" or "fevereiro" => 2,
            "mar" or "marco" => 3,
            "abr" or "abril" => 4,
            "mai" or "maio" => 5,
            "jun" or "junho" => 6,
            "jul" or "julho" => 7,
            "ago" or "agosto" => 8,
            "set" or "setembro" => 9,
            "out" or "outubro" => 10,
            "nov" or "novembro" => 11,
            "dez" or "dezembro" => 12,
            _ => 0
        };
        return month > 0;
    }
    private static string NormalizeRank(string? rank)
    {
        return AtRankFormatter.CanonicalName(rank);
    }
    private static void HandleRankSorting(DataGrid grid, DataGridSortingEventArgs e, Action<ListSortDirection> applySort)
    {
        var isRankColumn = e.Column is AtRankDataGridColumn
            || e.Column.SortMemberPath.EndsWith(".Rank", StringComparison.Ordinal)
            || e.Column.SortMemberPath.Equals(nameof(MilitaryRecord.Rank), StringComparison.Ordinal);
        if (!isRankColumn) return;

        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        foreach (var column in grid.Columns)
            if (!ReferenceEquals(column, e.Column)) column.SortDirection = null;

        e.Column.SortDirection = direction;
        applySort(direction);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static readonly Dictionary<string, decimal> SalaryFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Capitão"] = 14000m, ["1º Tenente"] = 12000m, ["2º Tenente"] = 10500m, ["Subtenente"] = 9500m,
        ["1º Sargento"] = 8200m, ["2º Sargento"] = 7200m, ["3º Sargento"] = 6200m,
        ["Cabo Efetivo Profissional"] = 2869m, ["Soldado Efetivo Profissional"] = 1927m, ["Soldado Efetivo Variável"] = 1177m
    };

    private static readonly Dictionary<string, decimal> CategoryFares = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Troncal/Convencional/Estrutural"] = 6.25m,
        ["Alimentadora/Circular"] = 6.00m,
        ["Vilas e Favelas"] = 0m,
        ["Metrô"] = 5.80m
    };

    private static readonly Dictionary<string, (string Name, decimal Fare)> KnownSupplementaryLines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S10"] = ("BH SHOPPING / SÃO FRANCISCO", 6.25m), ["S19"] = ("HOSPITAL EVANGÉLICO / SHOPPING BOULEVARD", 3.00m),
        ["S20"] = ("PALMEIRAS / SERRA", 6.25m), ["S21"] = ("DOM CABRAL / BH SHOPPING", 6.25m), ["S22"] = ("BURITIS / METRÔ CALAFATE", 6.25m),
        ["S31"] = ("PETRÓPOLIS / BAIRRO DAS INDÚSTRIAS", 6.00m), ["S32"] = ("ITAIPÚ / CARDOSO", 6.00m), ["S33"] = ("LINDÉIA / MILIONÁRIOS", 6.00m),
        ["S41"] = ("CONJUNTO CALIFÓRNIA / PRADO", 6.25m), ["S53"] = ("CONFISCO / SÃO GABRIEL", 6.25m), ["S54"] = ("DOM BOSCO / SHOPPING DEL REY", 6.25m),
        ["S55"] = ("SANTA MÔNICA / MINAS SHOPPING", 6.25m), ["S56"] = ("SÃO JOSÉ / VILARINHO", 6.25m), ["S60"] = ("ETELVINA CARNEIRO / JARDIM LEBLON VIA VENDA NOVA", 6.00m),
        ["S61"] = ("LANDI / ESTAÇÃO VILARINHO", 6.00m), ["S63"] = ("CIRCULAR VENDA NOVA", 6.00m), ["S64"] = ("CIRCULAR VENDA NOVA", 6.00m),
        ["S65"] = ("SÃO BERNARDO / MINAS CAIXA", 6.00m), ["S66"] = ("TUPI / EUROPA", 6.00m), ["S70"] = ("CONJ. FELICIDADE / SHOPPING DEL REY", 6.25m),
        ["S80"] = ("JARDIM VITÓRIA / VDT CIDADE INDUSTRIAL", 6.25m), ["S84"] = ("SÃO GABRIEL / FLORESTA VIA HOSPITAL BELO HORIZONTE", 6.25m),
        ["S85"] = ("MINAS SHOPPING / SANTA INÊS", 6.00m), ["S92"] = ("ESPLANADA / BURITIS", 6.25m)
    };
}

internal static class AtGridRowResolver
{
    public static MilitaryRecord? Resolve(object? dataItem)
        => dataItem as MilitaryRecord ?? (dataItem as AtDaRow)?.Military ?? (dataItem as AtBulletinQueueItem)?.Military;
}

internal sealed class AtReceiveFlagDataGridColumn : DataGridColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => Build(dataItem);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => Build(dataItem);

    private static FrameworkElement Build(object dataItem)
    {
        var text = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        var badge = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(5, 4, 5, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text
        };
        badge.DataContextChanged += (_, args) => Update(badge, args.NewValue);
        Update(badge, dataItem);
        return badge;
    }

    private static void Update(Border badge, object? dataItem)
    {
        var military = AtGridRowResolver.Resolve(dataItem);
        var receives = military is not null && MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached;
        var foreground = receives ? Color.FromRgb(28, 135, 73) : Color.FromRgb(94, 103, 115);
        var background = receives ? Color.FromRgb(231, 248, 238) : Color.FromRgb(246, 247, 249);
        badge.Background = new SolidColorBrush(background);
        badge.BorderBrush = new SolidColorBrush(Color.FromArgb(45, foreground.R, foreground.G, foreground.B));
        badge.ToolTip = receives ? "Recebe Auxílio-Transporte" : "Não recebe Auxílio-Transporte";
        if (badge.Child is TextBlock text)
        {
            text.Text = receives ? "Sim" : "Não";
            text.Foreground = new SolidColorBrush(foreground);
        }
    }
}

internal sealed class AtTransportValueDataGridColumn : DataGridColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => Build(dataItem);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => Build(dataItem);

    private static FrameworkElement Build(object dataItem)
    {
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(4, 0, 7, 0)
        };
        text.DataContextChanged += (_, args) => Update(text, args.NewValue);
        Update(text, dataItem);
        return text;
    }

    private static void Update(TextBlock text, object? dataItem)
    {
        var military = AtGridRowResolver.Resolve(dataItem);
        decimal value = 0m;
        if (military is not null)
        {
            var raw = (military.TransportAidValue ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().Replace(" ", string.Empty);
            if (raw.Contains(',') && raw.Contains('.'))
                decimal.TryParse(raw.Replace(".", string.Empty).Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            else if (raw.Contains(','))
                decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value);
            else
                decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }
        var receives = military is not null && MilitaryRecord.IsYes(military.ReceivesTransportAid) && !military.IsAttached;
        text.Text = receives ? value.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) : "—";
        text.FontWeight = receives ? FontWeights.SemiBold : FontWeights.Normal;
        text.ToolTip = receives ? "Valor líquido mensal cadastrado" : "Sem benefício ativo";
        text.SetResourceReference(TextBlock.ForegroundProperty, receives ? "PrimaryDarkBrush" : "MutedBrush");
    }
}

internal sealed class AtReportDaValueDataGridColumn : DataGridColumn
{
    private readonly Func<MilitaryRecord, decimal> _valueSelector;
    private readonly Func<MilitaryRecord, string>? _reasonSelector;

    public AtReportDaValueDataGridColumn(Func<MilitaryRecord, decimal> valueSelector, Func<MilitaryRecord, string>? reasonSelector = null)
    {
        _valueSelector = valueSelector;
        _reasonSelector = reasonSelector;
    }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => Build(dataItem);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => Build(dataItem);

    public string GetValueText(object? dataItem)
    {
        var military = AtGridRowResolver.Resolve(dataItem);
        var value = military is null ? 0m : _valueSelector(military);
        return value.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    }

    private FrameworkElement Build(object dataItem)
    {
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(4, 0, 7, 0)
        };
        text.DataContextChanged += (_, args) => Update(text, args.NewValue);
        Update(text, dataItem);
        return text;
    }

    private void Update(TextBlock text, object? dataItem)
    {
        var military = AtGridRowResolver.Resolve(dataItem);
        var value = military is null ? 0m : _valueSelector(military);
        var reason = military is null || _reasonSelector is null ? string.Empty : _reasonSelector(military);
        text.Text = value.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
        text.FontWeight = value > 0m ? FontWeights.SemiBold : FontWeights.Normal;
        text.ToolTip = value > 0m ? (string.IsNullOrWhiteSpace(reason) || reason == "—" ? "Valor salvo/calculado para a competência de DA selecionada" : reason) : "Sem despesa a anular nesta competência";
        text.SetResourceReference(TextBlock.ForegroundProperty, value > 0m ? "PrimaryDarkBrush" : "MutedBrush");
    }
}

internal sealed class AtReportDaReasonDataGridColumn : DataGridColumn
{
    private readonly Func<MilitaryRecord, string> _reasonSelector;

    public AtReportDaReasonDataGridColumn(Func<MilitaryRecord, string> reasonSelector)
        => _reasonSelector = reasonSelector;

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => Build(dataItem);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => Build(dataItem);

    private FrameworkElement Build(object dataItem)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Padding = new Thickness(4, 0, 7, 0)
        };
        text.DataContextChanged += (_, args) => Update(text, args.NewValue);
        Update(text, dataItem);
        return text;
    }

    private void Update(TextBlock text, object? dataItem)
    {
        var military = AtGridRowResolver.Resolve(dataItem);
        var reason = military is null ? "—" : _reasonSelector(military);
        text.Text = string.IsNullOrWhiteSpace(reason) ? "—" : reason;
        text.ToolTip = text.Text;
        text.SetResourceReference(TextBlock.ForegroundProperty, text.Text == "—" ? "MutedBrush" : "PrimaryDarkBrush");
    }
}

internal readonly record struct ReportDaInfo(
    decimal Value,
    string Reason,
    bool HasVacation = false,
    string VacationPeriod = "",
    string VacationDaNoteStatus = "Não se aplica");

internal static class AtRankFormatter
{
    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(character switch
            {
                'º' or '°' => ' ',
                '.' or '-' or '/' or '_' => ' ',
                _ => char.ToLowerInvariant(character)
            });
        }
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    public static string CanonicalName(string? rank)
    {
        var original = (rank ?? string.Empty).Trim();
        var n = Fold(original);

        // A ordem das verificações é intencional: "sargento" nunca pode cair
        // em "tenente", e "tenente-coronel" precisa ser reconhecido antes de coronel.
        if (n.Contains("general de exercito") || n is "gen ex" or "general exercito") return "General de Exército";
        if (n.Contains("general de divisao") || n is "gen div" or "general divisao") return "General de Divisão";
        if (n.Contains("general de brigada") || n is "gen bda" or "general brigada") return "General de Brigada";
        if (n.Contains("tenente coronel") || n is "ten cel" or "tc") return "Tenente Coronel";
        if (n.Contains("coronel") || n == "cel") return "Coronel";
        if (n.Contains("major") || n == "maj") return "Major";
        if (n.Contains("capitao") || n == "cap") return "Capitão";

        if (Regex.IsMatch(n, @"^(1|primeiro)\s*o?\s*(sgt|sg|sargento)\b")) return "1º Sargento";
        if (Regex.IsMatch(n, @"^(2|segundo)\s*o?\s*(sgt|sg|sargento)\b")) return "2º Sargento";
        if (Regex.IsMatch(n, @"^(3|terceiro)\s*o?\s*(sgt|sg|sargento)\b")) return "3º Sargento";
        if (n.Contains("subtenente") || n is "s ten" or "sub ten" or "st") return "Subtenente";

        if (Regex.IsMatch(n, @"^(1|primeiro)\s*o?\s*(ten|tenente)\b")) return "1º Tenente";
        if (Regex.IsMatch(n, @"^(2|segundo)\s*o?\s*(ten|tenente)\b")) return "2º Tenente";
        if (n.Contains("aspirante") || n == "asp") return "Aspirante";
        if (n.Contains("cadete") || n == "cad") return "Cadete";

        if (n.Contains("cabo") || Regex.IsMatch(n, @"^cb\b")) return "Cabo Efetivo Profissional";
        if ((n.Contains("soldado") || Regex.IsMatch(n, @"^sd\b"))
            && (n.Contains("variavel") || n.Contains("vrv") || n.Contains("recruta") || Regex.IsMatch(n, @"\b(?:ev|rcr)\b")))
            return "Soldado Efetivo Variável";
        if (n.Contains("soldado") || Regex.IsMatch(n, @"^sd\b")) return "Soldado Efetivo Profissional";

        return original;
    }

    public static string CleanMilitaryName(string? name, string? rank)
    {
        var value = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var pattern = CanonicalName(rank) switch
        {
            "General de Exército" => @"^(?:gen(?:eral)?\s*(?:de\s*)?ex(?:ercito)?)\b[\s\-–—,:]*",
            "General de Divisão" => @"^(?:gen(?:eral)?\s*(?:de\s*)?div(?:isao)?)\b[\s\-–—,:]*",
            "General de Brigada" => @"^(?:gen(?:eral)?\s*(?:de\s*)?bda|general\s+de\s+brigada)\b[\s\-–—,:]*",
            "Coronel" => @"^(?:cel|coronel)\b[\s\-–—,:]*",
            "Tenente Coronel" => @"^(?:ten\s*cel|tenente\s*coronel|tc)\b[\s\-–—,:]*",
            "Major" => @"^(?:maj|major)\b[\s\-–—,:]*",
            "Capitão" => @"^(?:cap|capit[aã]o)\b[\s\-–—,:]*",
            "1º Tenente" => @"^(?:1\s*[º°o]?\s*(?:ten|tenente)|primeiro\s+tenente)\b[\s\-–—,:]*",
            "2º Tenente" => @"^(?:2\s*[º°o]?\s*(?:ten|tenente)|segundo\s+tenente)\b[\s\-–—,:]*",
            "Aspirante" => @"^(?:asp|aspirante)\b[\s\-–—,:]*",
            "Cadete" => @"^(?:cad|cadete)\b[\s\-–—,:]*",
            "Subtenente" => @"^(?:s\s*ten|sub\s*ten|subtenente|st)\b[\s\-–—,:]*",
            "1º Sargento" => @"^(?:1\s*[º°o]?\s*(?:sgt|sg|sargento)|primeiro\s+sargento)\b[\s\-–—,:]*",
            "2º Sargento" => @"^(?:2\s*[º°o]?\s*(?:sgt|sg|sargento)|segundo\s+sargento)\b[\s\-–—,:]*",
            "3º Sargento" => @"^(?:3\s*[º°o]?\s*(?:sgt|sg|sargento)|terceiro\s+sargento)\b[\s\-–—,:]*",
            "Cabo Efetivo Profissional" => @"^(?:cb|cabo)(?:\s+(?:ef(?:etivo)?\s+)?profl?|\s+ep)?\b[\s\-–—,:]*",
            "Soldado Efetivo Variável" => @"^(?:sd|soldado)(?:\s+(?:rcr|recruta|ef(?:etivo)?\s*(?:vrv|vari[aá]vel)|vrv|ev))?\b[\s\-–—,:]*",
            "Soldado Efetivo Profissional" => @"^(?:sd|soldado)(?:\s+(?:ef(?:etivo)?\s*(?:profl?|profissional)|profl?|profissional|ep))?\b[\s\-–—,:]*",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(pattern)) return value;
        var cleaned = Regex.Replace(value, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? value : cleaned;
    }

    public static string ShortName(string? rank) => CanonicalName(rank) switch
    {
        "General de Exército" => "Gen Ex",
        "General de Divisão" => "Gen Div",
        "General de Brigada" => "Gen Bda",
        "Coronel" => "Cel",
        "Tenente Coronel" => "Ten Cel",
        "Major" => "Maj",
        "Capitão" => "Cap",
        "1º Tenente" => "1º Ten",
        "2º Tenente" => "2º Ten",
        "Aspirante" => "Asp",
        "Cadete" => "Cad",
        "Subtenente" => "S Ten",
        "1º Sargento" => "1º Sgt",
        "2º Sargento" => "2º Sgt",
        "3º Sargento" => "3º Sgt",
        "Cabo Efetivo Profissional" => "Cb Ef Profl",
        "Soldado Efetivo Profissional" => "Sd Ef Profl",
        "Soldado Efetivo Variável" => "Sd Ef Vrv",
        var other => string.IsNullOrWhiteSpace(other) ? "—" : other
    };

    public static int GetOrder(string? rank) => CanonicalName(rank) switch
    {
        "General de Exército" => 0,
        "General de Divisão" => 1,
        "General de Brigada" => 2,
        "Coronel" => 3,
        "Tenente Coronel" => 4,
        "Major" => 5,
        "Capitão" => 6,
        "1º Tenente" => 7,
        "2º Tenente" => 8,
        "Aspirante" => 9,
        "Cadete" => 10,
        "Subtenente" => 11,
        "1º Sargento" => 12,
        "2º Sargento" => 13,
        "3º Sargento" => 14,
        "Cabo Efetivo Profissional" => 15,
        "Soldado Efetivo Profissional" => 16,
        "Soldado Efetivo Variável" => 17,
        _ => 999
    };
}

internal sealed class AtRankDataGridColumn : DataGridColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => Build(dataItem);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => Build(dataItem);

    private static FrameworkElement Build(object dataItem)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Padding = new Thickness(4, 0, 4, 0)
        };
        text.DataContextChanged += (_, args) => Update(text, args.NewValue);
        Update(text, dataItem);
        return text;
    }

    private static void Update(TextBlock text, object? dataItem)
    {
        var military = AtGridRowResolver.Resolve(dataItem);
        text.Text = AtRankFormatter.ShortName(military?.Rank);
        text.ToolTip = AtRankFormatter.CanonicalName(military?.Rank);
    }
}

internal sealed class WarNameDataGridColumn : DataGridColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => Build(dataItem);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => Build(dataItem);

    private static FrameworkElement Build(object dataItem)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(4, 5, 4, 5)
        };
        text.DataContextChanged += (_, args) => Update(text, args.NewValue);
        Update(text, dataItem);
        return text;
    }

    private static void Update(TextBlock text, object? dataItem)
    {
        text.Inlines.Clear();
        var military = AtGridRowResolver.Resolve(dataItem);
        if (military is null)
        {
            text.ToolTip = null;
            return;
        }

        var name = AtRankFormatter.CleanMilitaryName(military.Name, military.Rank).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        var war = (military.WarName ?? string.Empty).Trim().ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        text.ToolTip = string.IsNullOrWhiteSpace(war) ? name : $"{name} — nome de guerra: {war}";
        var range = FindRange(name, war);
        if (range.Start < 0)
        {
            text.Inlines.Add(new Run(name));
            if (!string.IsNullOrWhiteSpace(war))
            {
                text.Inlines.Add(new Run(" — "));
                text.Inlines.Add(new Run(war) { FontWeight = FontWeights.Bold });
            }
            return;
        }
        if (range.Start > 0) text.Inlines.Add(new Run(name[..range.Start]));
        text.Inlines.Add(new Run(name.Substring(range.Start, range.Length)) { FontWeight = FontWeights.Bold });
        if (range.Start + range.Length < name.Length) text.Inlines.Add(new Run(name[(range.Start + range.Length)..]));
    }

    private static (int Start, int Length) FindRange(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return (-1, 0);
        static string Fold(string value, List<int>? map = null)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                foreach (var ch in value[i].ToString().Normalize(NormalizationForm.FormD))
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                    sb.Append(char.ToUpperInvariant(ch));
                    map?.Add(i);
                }
            }
            return sb.ToString();
        }
        var indexes = new List<int>();
        var foldedSource = Fold(source, indexes);
        var foldedTarget = Fold(target);
        var position = foldedSource.IndexOf(foldedTarget, StringComparison.Ordinal);
        if (position < 0 || position + foldedTarget.Length - 1 >= indexes.Count) return (-1, 0);
        var start = indexes[position];
        var end = indexes[position + foldedTarget.Length - 1] + 1;
        return (start, end - start);
    }
}

internal sealed class AtBulletinQueueItem
{
    public string CompetenceMappingDisplay => Action.Contains("Saque", StringComparison.OrdinalIgnoreCase) ? $"{CompetencesDisplay} → {SippesReferencesDisplay}" : CompetencesDisplay;
    public string SippesReferencesDisplay
    {
        get { try { return Action.Contains("Saque", StringComparison.OrdinalIgnoreCase) ? AuxilioTransporteWindow.SaqueBenefitReferences(CompetencesText) : CompetencesDisplay; } catch { return "Competência inválida"; } }
    }

    public string Action { get; set; } = string.Empty;
    public MilitaryRecord Military { get; set; } = null!;
    public AtCalculation Calculation { get; set; } = AtCalculation.Empty;
    public IReadOnlyList<AtBusLine> Buses { get; set; } = [];
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string CompetencesText { get; set; } = string.Empty;
    public decimal ReceivedValue { get; set; }
    public decimal DifferenceValue => Math.Max(0m, decimal.Round(Calculation.Net - ReceivedValue, 2, MidpointRounding.AwayFromZero));
    public string CompetencesDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CompetencesText)) return "—";
            var normalized = Regex.Replace(CompetencesText.Trim(), @"\r\n|\n|\r|;|,", ", ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim(' ', ',', ';');
            return string.IsNullOrWhiteSpace(normalized) ? "—" : normalized;
        }
    }
    public DateTime AddedAt { get; set; }
    public int Position { get; set; }
    public string RankShort => AtRankFormatter.ShortName(Military.Rank);
    public string DaysText => Calculation.Days.ToString(CultureInfo.GetCultureInfo("pt-BR"));
    public string AddedText => AddedAt.ToString("HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
    public string DailyFormatted => Calculation.DailyGross.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string MonthGrossFormatted => Calculation.MonthGross.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string ShareFormatted => Calculation.Share.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string NetFormatted => Calculation.Net.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string ReceivedFormatted => IsDifferenceAction ? ReceivedValue.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) : "—";
    public string DifferenceFormatted => IsDifferenceAction ? DifferenceValue.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) : "—";
    private bool IsDifferenceAction => Action.Contains("Diferença", StringComparison.OrdinalIgnoreCase)
                                       || Action.Contains("Diferenca", StringComparison.OrdinalIgnoreCase);
    public string BusSummary => string.Join("; ", Buses.Select(x => x.ClearSummary).Where(x => !string.IsNullOrWhiteSpace(x)));
    public string RouteOnlySummary => string.IsNullOrWhiteSpace(Origin) && string.IsNullOrWhiteSpace(Destination)
        ? string.Empty
        : $"{(string.IsNullOrWhiteSpace(Origin) ? "Origem não informada" : Origin)} → {(string.IsNullOrWhiteSpace(Destination) ? "Destino não informado" : Destination)}";
    public string RouteSummary
    {
        get
        {
            var lines = BusSummary;
            var route = RouteOnlySummary;
            if (string.IsNullOrWhiteSpace(route)) return lines;
            return string.IsNullOrWhiteSpace(lines) ? route : $"{route} | {lines}";
        }
    }
}

internal sealed record AtBulletinSourceFile(string Path, string DisplayName);

internal sealed record DaSavedBulletinSource(
    string PdfPath,
    string FileName,
    DateTime Date,
    string DisplayNumber,
    IntelligentBulletinFile? IndexedFile)
{
    public string DisplayDate => Date.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
}

internal sealed class AtBulletinImportBlock
{
    public DateTime Date { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string Type { get; set; } = "Preta";
    public bool IsCorrection { get; set; }
    public List<int> ReplacedMilitaryIds { get; set; } = [];
    public List<string> PublishedNames { get; set; } = [];
    public List<MilitaryRecord> Matched { get; set; } = [];
    public List<MilitaryRecord> NotReceiving { get; set; } = [];
    public List<string> RankMismatch { get; set; } = [];
    public List<string> Unmatched { get; set; } = [];
    public List<string> Ambiguous { get; set; } = [];
}

internal sealed record AtBulletinPerson(string RankKey, string Name);

internal sealed class AtBusLine : INotifyPropertyChanged
{
    private int _index;
    private string _number = string.Empty;
    private string _name = string.Empty;
    private string _category = string.Empty;
    private decimal _fare;
    private string _sourceUrl = string.Empty;
    public int Index { get => _index; set { if (_index == value) return; _index = value; Changed(nameof(Index)); Changed(nameof(DisplayIndex)); } }
    public int DisplayIndex => Index + 1;
    public string DisplayNumber => string.IsNullOrWhiteSpace(Number) ? $"Ônibus {DisplayIndex}" : Number;
    public string FareFormatted => Fare.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string RoundTripFormatted => (Fare * 2m).ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string ClearSummary
    {
        get
        {
            var label = DisplayNumber;
            var description = string.IsNullOrWhiteSpace(Name) || Name.Equals($"Linha {Number}", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" — {Name.Trim()}";
            return $"{label}{description}: {FareFormatted} por passagem ({RoundTripFormatted} ida e volta)";
        }
    }
    public string Number { get => _number; set { if (_number == value) return; _number = value ?? string.Empty; Changed(nameof(Number)); Changed(nameof(DisplayNumber)); Changed(nameof(ClearSummary)); } }
    public string Name { get => _name; set { if (_name == value) return; _name = value ?? string.Empty; Changed(nameof(Name)); Changed(nameof(ClearSummary)); } }
    public string Category { get => _category; set { if (_category == value) return; _category = value ?? string.Empty; Changed(nameof(Category)); Changed(nameof(ClearSummary)); } }
    public decimal Fare { get => _fare; set { if (_fare == value) return; _fare = Math.Max(0m, value); Changed(nameof(Fare)); Changed(nameof(FareFormatted)); Changed(nameof(RoundTripFormatted)); Changed(nameof(ClearSummary)); } }
    public string SourceUrl { get => _sourceUrl; set { if (_sourceUrl == value) return; _sourceUrl = value ?? string.Empty; Changed(nameof(SourceUrl)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class AtDaRow : INotifyPropertyChanged
{
    private int _blackDays;
    private int _redDays;
    private int _calendarBlackDays;
    private int _calendarRedDays;
    private bool _manualOverride;
    public AtDaRow(MilitaryRecord military, decimal netMonthly, int workingDays, int blackDays, int redDays)
    {
        Military = military;
        NetMonthly = Math.Max(0m, netMonthly);
        WorkingDays = Math.Max(1, workingDays);
        _blackDays = Math.Max(0, blackDays);
        _redDays = Math.Max(0, redDays);
        _calendarBlackDays = _blackDays;
        _calendarRedDays = _redDays;
    }
    public MilitaryRecord Military { get; }
    public string ShortRank => AtRankFormatter.ShortName(Military.Rank);
    public decimal NetMonthly { get; }
    public int WorkingDays { get; }
    public int BlackDays { get => _blackDays; set { var v = Math.Max(0, value); if (_blackDays == v) return; _blackDays = v; Refresh(); } }
    public int RedDays { get => _redDays; set { var v = Math.Max(0, value); if (_redDays == v) return; _redDays = v; Refresh(); } }
    public int CalendarBlackDays => _calendarBlackDays;
    public int CalendarRedDays => _calendarRedDays;
    public bool ManualOverride
    {
        get => _manualOverride;
        set
        {
            if (_manualOverride == value) return;
            _manualOverride = value;
            Changed(nameof(ManualOverride));
            Changed(nameof(SourceText));
        }
    }
    public string CalendarCountsText => $"{CalendarBlackDays}P / {CalendarRedDays}V";
    public string SourceText => ManualOverride ? "PDF/boletim/manual" : "Sem lançamento";
    public int NetDays => Math.Clamp(BlackDays - RedDays, 0, Math.Max(0, WorkingDays));
    public decimal DailyNet => WorkingDays > 0 ? decimal.Round(NetMonthly / WorkingDays, 2, MidpointRounding.AwayFromZero) : 0m;
    public decimal Deduction => TransportCompetenceService.Deduction(NetMonthly, WorkingDays, BlackDays, RedDays);
    public string DailyNetFormatted => DailyNet.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string DeductionFormatted => Deduction.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public void SetCalendarBase(int blackDays, int redDays)
    {
        _calendarBlackDays = Math.Max(0, blackDays);
        _calendarRedDays = Math.Max(0, redDays);
        Changed(nameof(CalendarBlackDays));
        Changed(nameof(CalendarRedDays));
        Changed(nameof(CalendarCountsText));
        Changed(nameof(SourceText));
    }
    public void ApplyCounts(int blackDays, int redDays, bool manualOverride)
    {
        _blackDays = Math.Max(0, blackDays);
        _redDays = Math.Max(0, redDays);
        _manualOverride = manualOverride;
        Refresh();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Refresh()
    {
        foreach (var name in new[] { nameof(BlackDays), nameof(RedDays), nameof(NetDays), nameof(DailyNet), nameof(Deduction), nameof(DailyNetFormatted), nameof(DeductionFormatted), nameof(ManualOverride), nameof(SourceText), nameof(CalendarCountsText) })
            Changed(name);
    }
}

internal readonly record struct DaCalendarCount(int Black, int Red);

internal readonly record struct AtCalculation(int Days, decimal Salary, decimal DailyGross, decimal MonthGross, decimal Share, decimal Net)
{
    public static AtCalculation Empty => new(22, 0m, 0m, 0m, 0m, 0m);
    public decimal SalarySixPercent => decimal.Round(Salary * 0.06m, 2, MidpointRounding.AwayFromZero);
    public decimal SalarySixPercentDaily => decimal.Round(SalarySixPercent / 30m, 2, MidpointRounding.AwayFromZero);
}

internal sealed record AtTransportState(IReadOnlyList<AtBusLine> Buses, AtCalculation Calculation);
internal sealed record AtStoredRoute(string Origin, string Destination, IReadOnlyList<AtBusLine> Buses, string PrintPath);
internal sealed record BusLookupResult(bool Success, string Line, string Name, string Category, decimal Fare, string Url, string Message);
internal sealed class AtWindowPreferences
{
    public string Destination { get; set; } = string.Empty;
    public string DepartureTime { get; set; } = "06:00";
    public string BulletinReference { get; set; } = string.Empty;
    public string BulletinRequiredDetails { get; set; } = string.Empty;
    public string BulletinCountFromDate { get; set; } = string.Empty;
    public AtWindowPlacement Placement { get; set; } = new();
}

internal sealed class AtWindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string State { get; set; } = WindowState.Normal.ToString();
}

internal static class AtMilitaryExtensions
{
    public static string WarNameOrName(this MilitaryRecord military) => string.IsNullOrWhiteSpace(military.WarName) ? military.Name : military.WarName;
}
