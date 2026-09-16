using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Licensed;

public partial class CpexPaystubDownloadWindow : Window
{
    private readonly CpexPaystubAutomationService _service;
    private readonly IReadOnlyList<CpexPaystubPerson> _people;
    private CpexPaystubSettings _settings = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    public bool DownloadedAny { get; private set; }

    public CpexPaystubDownloadWindow(CpexPaystubAutomationService service, IReadOnlyList<CpexPaystubPerson> people)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _service = service;
        _people = people;
        PeopleGrid.ItemsSource = people.Select(x => new
        {
            x.Rank,
            x.Name,
            Cpf = MilitaryFormatting.FormatCpf(x.Cpf),
            MilitaryId = x.MilitaryId,
            PrecCp = x.PrecCp
        }).ToList();
        MonthBox.ItemsSource = Enumerable.Range(1, 12).Select(x => new MonthItem(x, CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.GetMonthName(x))).ToList();
        YearBox.ItemsSource = Enumerable.Range(DateTime.Today.Year - 6, 9).Reverse().ToList();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _service.LoadSettingsAsync();
            LoginBox.Text = _settings.Login; OutputBox.Text = _settings.OutputDirectory;
            MonthBox.SelectedValuePath = nameof(MonthItem.Number); MonthBox.DisplayMemberPath = nameof(MonthItem.Name); MonthBox.SelectedValue = _settings.Month;
            YearBox.Text = _settings.Year.ToString(CultureInfo.InvariantCulture);
            SelectCombo(SystemBox, NormalizeSystem(_settings.System));
            SelectCombo(ProcessingBox, _settings.Processing); SelectCombo(PayrollBox, _settings.PayrollType); SelectCombo(BrowserBox, _settings.Browser);
            SheetCodeBox.Text = string.IsNullOrWhiteSpace(_settings.SheetCode)
                ? CalculateSheetCode(_settings.Year, _settings.Month).ToString(CultureInfo.InvariantCulture)
                : _settings.SheetCode;
            SavePasswordBox.IsChecked = _settings.SavePassword;
            PasswordBox.Password = _service.ReadSavedPassword(_settings);
            HeadlessBox.IsChecked = _settings.Headless; OpenAfterBox.IsChecked = _settings.OpenAfterDownload;
            UpdateSystemUi();
            if (string.IsNullOrWhiteSpace(PasswordBox.Password)) PasswordBox.Focus(); else DownloadButton.Focus();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private static void SelectCombo(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }
    private static string Combo(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;

    private static string NormalizeSystem(string? value)
        => (value ?? string.Empty).Contains("SIAPPES", StringComparison.OrdinalIgnoreCase)
            ? "SIAPPES / Área UA"
            : "SIPPES";

    private static int CalculateSheetCode(int year, int month)
    {
        var deltaMonths = (year - 2026) * 12 + (month - 4);
        return 4178 + deltaMonths * 20;
    }

    private void SystemBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSystemUi();

    private void UpdateSystemUi()
    {
        if (ProcessingBox is null || PayrollBox is null || SheetCodeBox is null || SystemBox is null) return;
        var isSippes = NormalizeSystem(Combo(SystemBox)) == "SIPPES";
        SheetCodeBox.IsEnabled = isSippes;
        ProcessingBox.IsEnabled = !isSippes;
        PayrollBox.IsEnabled = !isSippes;
        DownloadButton.Content = isSippes ? "Baixar pelo SIPPES" : "Baixar pelo SIAPPES";
    }

    private void Competence_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || MonthBox.SelectedValue is not int month || !int.TryParse(YearBox.Text, out var year)) return;
        SheetCodeBox.Text = CalculateSheetCode(year, month).ToString(CultureInfo.InvariantCulture);
    }

    private void YearBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (MonthBox.SelectedValue is int month && int.TryParse(YearBox.Text, out var year))
            SheetCodeBox.Text = CalculateSheetCode(year, month).ToString(CultureInfo.InvariantCulture);
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pasta base dos contracheques", InitialDirectory = Directory.Exists(OutputBox.Text) ? OutputBox.Text : App.Paths.PaystubsDirectory };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = string.IsNullOrWhiteSpace(OutputBox.Text) ? App.Paths.PaystubsDirectory : OutputBox.Text;
        Directory.CreateDirectory(path); ShellService.OpenPath(path);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!int.TryParse(YearBox.Text, out var year)) { StatusText.Text = "Informe um ano válido."; return; }
        if (MonthBox.SelectedValue is not int month) { StatusText.Text = "Selecione o mês."; return; }
        _settings.System = NormalizeSystem(Combo(SystemBox));
        _settings.Login = LoginBox.Text.Trim(); _settings.Year = year; _settings.Month = month;
        _settings.Processing = Combo(ProcessingBox); _settings.PayrollType = Combo(PayrollBox); _settings.Browser = Combo(BrowserBox);
        _settings.SheetCode = SheetCodeBox.Text.Trim();
        _settings.SavePassword = SavePasswordBox.IsChecked == true;
        _settings.ProtectedPassword = _settings.SavePassword
            ? WindowsSecretProtector.Protect(PasswordBox.Password)
            : string.Empty;
        _settings.Headless = HeadlessBox.IsChecked == true; _settings.OpenAfterDownload = OpenAfterBox.IsChecked == true;
        _settings.OutputDirectory = string.IsNullOrWhiteSpace(OutputBox.Text) ? App.Paths.PaystubsDirectory : OutputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(PasswordBox.Password)) { StatusText.Text = "Informe a senha do sistema escolhido."; PasswordBox.Focus(); return; }

        _busy = true; DownloadButton.IsEnabled = false; LogBox.Clear(); _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                ProgressBar.Value = p.Total <= 0 ? 0 : p.Current * 100d / p.Total;
                StatusText.Text = p.Message;
                if (!string.IsNullOrWhiteSpace(p.Name)) LogBox.AppendText($"[{p.Current}/{p.Total}] {p.Name}\n");
                LogBox.ScrollToEnd();
            });
            var result = await _service.DownloadAsync(_people, _settings, PasswordBox.Password, progress, _cts.Token);
            DownloadedAny = result.Downloaded > 0;
            App.Paystubs.InvalidateCache();
            LogBox.AppendText($"\nConcluído: {result.Downloaded} baixado(s), {result.Failures.Count} falha(s).\n");
            foreach (var failure in result.Failures) LogBox.AppendText("• " + failure + "\n");
            StatusText.Text = $"Concluído: {result.Downloaded} contracheque(s) salvo(s).";
            if (_settings.OpenAfterDownload && result.Downloaded > 0) ShellService.OpenPath(_settings.OutputDirectory);
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = ex.Message; SigfurDialog.Show(this, ex.Message, "SIPPES / SIAPPES", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; DownloadButton.IsEnabled = true; _cts?.Dispose(); _cts = null; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); StatusText.Text = "Cancelando após a etapa atual..."; }
        else Close();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; _cts?.Cancel(); StatusText.Text = "Aguardando o navegador encerrar com segurança...";
    }

    private sealed record MonthItem(int Number, string Name);
}
