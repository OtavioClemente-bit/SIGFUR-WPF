using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class BankInconsistencyWindow : Window
{
    private readonly BankInconsistencyService _service;
    private BankInconsistencySettings _cpex = new();
    private SinfoppesCriticizedSettings _sinf = new();
    private CancellationTokenSource? _operation;
    private static readonly string[] Months = ["TODOS", "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"];
    private readonly List<SavedBankReport> _savedReports = [];

    public BankInconsistencyWindow(BankInconsistencyService service)
    {
        _service = service;
        InitializeComponent();
        App.UiState.Attach(this);
        Loaded += OnLoaded;
        Closing += async (_, _) => await SaveAllQuietlyAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        for (var year = DateTime.Today.Year + 1; year >= 2013; year--) { CpexYearBox.Items.Add(year); SinfYearBox.Items.Add(year); }
        foreach (var month in Months) { CpexMonthBox.Items.Add(month); if (month != "TODOS") SinfMonthBox.Items.Add(month); }
        foreach (var rm in new[] { "TODAS", "1ª RM", "2ª RM", "3ª RM", "4ª RM", "5ª RM", "6ª RM", "7ª RM", "8ª RM", "9ª RM", "10ª RM", "11ª RM", "12ª RM" }) CpexRegionBox.Items.Add(rm);
        try
        {
            _cpex = await _service.LoadCpexSettingsAsync();
            _sinf = await _service.LoadSinfoppesSettingsAsync();
            ApplyCpex(); ApplySinf();
            RefreshSavedReports();
            StatusText.Text = "Pronto.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ApplyCpex()
    {
        CpexSystemBox.Text = _cpex.SystemName; CpexYearBox.Text = _cpex.Year.ToString(CultureInfo.InvariantCulture); CpexMonthBox.SelectedItem = _cpex.Month; CpexMonthBox.Text = _cpex.Month; CpexRegionBox.Text = _cpex.MilitaryRegion; CpexCodomBox.Text = _cpex.Codom; CpexCodomBox.ItemsSource = _cpex.CodomHistory; CpexReportTypeBox.Text = _cpex.ReportType; CpexOutputBox.Text = _cpex.OutputDirectory; CpexSpreadsheetCheck.IsChecked = _cpex.DownloadSpreadsheet; CpexOpenPdfCheck.IsChecked = _cpex.OpenPdf; CpexHeadlessCheck.IsChecked = _cpex.Headless; CpexKeepOpenCheck.IsChecked = _cpex.KeepBrowserOpen;
    }
    private void ApplySinf()
    {
        SinfCpfBox.Text = _sinf.Cpf; SinfPasswordBox.Password = _sinf.GetPassword(); SinfYearBox.Text = _sinf.Year.ToString(CultureInfo.InvariantCulture); SinfMonthBox.Text = _sinf.Month; SinfRunBox.Text = _sinf.Run; SinfOutputBox.Text = _sinf.OutputDirectory; SinfSaveCpfCheck.IsChecked = _sinf.SaveCpf; SinfSavePasswordCheck.IsChecked = _sinf.SavePassword; SinfOpenPdfCheck.IsChecked = _sinf.OpenPdf; SinfHeadlessCheck.IsChecked = _sinf.Headless; SinfKeepOpenCheck.IsChecked = _sinf.KeepBrowserOpen;
    }

    private BankInconsistencySettings ReadCpex()
    {
        _cpex.SystemName = CpexSystemBox.Text.Trim(); _cpex.Year = int.TryParse(CpexYearBox.Text, out var y) ? y : DateTime.Today.Year; _cpex.Month = CpexMonthBox.Text.Trim(); _cpex.MilitaryRegion = CpexRegionBox.Text.Trim(); _cpex.Codom = MilitaryFormatting.Digits(CpexCodomBox.Text); _cpex.ReportType = CpexReportTypeBox.Text.Trim(); _cpex.OutputDirectory = CpexOutputBox.Text.Trim(); _cpex.DownloadSpreadsheet = CpexSpreadsheetCheck.IsChecked == true; _cpex.OpenPdf = CpexOpenPdfCheck.IsChecked == true; _cpex.Headless = CpexHeadlessCheck.IsChecked == true; _cpex.KeepBrowserOpen = CpexKeepOpenCheck.IsChecked == true; return _cpex;
    }
    private SinfoppesCriticizedSettings ReadSinf()
    {
        _sinf.Cpf = MilitaryFormatting.Digits(SinfCpfBox.Text); _sinf.SaveCpf = SinfSaveCpfCheck.IsChecked == true; _sinf.SavePassword = SinfSavePasswordCheck.IsChecked == true; _sinf.Year = int.TryParse(SinfYearBox.Text, out var y) ? y : DateTime.Today.Year; _sinf.Month = SinfMonthBox.Text.Trim(); _sinf.Run = SinfRunBox.Text.Trim(); _sinf.OutputDirectory = SinfOutputBox.Text.Trim(); _sinf.OpenPdf = SinfOpenPdfCheck.IsChecked == true; _sinf.Headless = SinfHeadlessCheck.IsChecked == true; _sinf.KeepBrowserOpen = SinfKeepOpenCheck.IsChecked == true; _sinf.SetPassword(SinfPasswordBox.Password); return _sinf;
    }

    private async void RunCpex_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadCpex();
        if (string.IsNullOrWhiteSpace(settings.Codom)) { SigfurDialog.Show(this, "Informe o CODOM.", "Inconsistência Bancária", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        await RunAsync(async (progress, ct) => await _service.RunCpexAsync(settings, progress, ct), CpexLogBox);
    }
    private async void RunSinfoppes_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadSinf();
        if (string.IsNullOrWhiteSpace(settings.Cpf) || string.IsNullOrWhiteSpace(SinfPasswordBox.Password)) { SigfurDialog.Show(this, "Informe CPF e senha.", "Lançamentos Criticados", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        await RunAsync(async (progress, ct) => await _service.RunSinfoppesAsync(settings, SinfPasswordBox.Password, progress, ct), SinfLogBox);
    }

    private async Task RunAsync(Func<IProgress<AutomationProgress>, CancellationToken, Task<BankAutomationResult>> operation, System.Windows.Controls.TextBox logBox)
    {
        if (_operation is not null) return;
        _operation = new CancellationTokenSource(); CancelButton.IsEnabled = true; Progress.Value = 0; logBox.Clear(); Cursor = System.Windows.Input.Cursors.Wait;
        var progress = new Progress<AutomationProgress>(p => { Progress.Value = p.Percent; StatusText.Text = p.Message; AppendLog(logBox, p.Message); });
        try
        {
            var result = await operation(progress, _operation.Token);
            AppendLog(logBox, result.Message);
            StatusText.Text = result.Message;
            if (!result.Success && !result.Message.Equals("Operação cancelada.", StringComparison.OrdinalIgnoreCase)) SigfurDialog.Show(this, result.Message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (result.NoRecords) SigfurDialog.Show(this, result.Message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (result.Success)
            {
                RefreshSavedReports();
                SigfurDialog.Show(this, $"{result.Message}\n\n{result.PdfPath}", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _operation.Dispose(); _operation = null; CancelButton.IsEnabled = false; Cursor = System.Windows.Input.Cursors.Arrow; await SaveAllQuietlyAsync(); }
    }

    private static void AppendLog(System.Windows.Controls.TextBox box, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        box.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}"); box.ScrollToEnd();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void CloseBrowser_Click(object sender, RoutedEventArgs e) { await _service.CloseBrowserAsync(); StatusText.Text = "Navegador encerrado."; }
    private void OpenCpexSite_Click(object sender, RoutedEventArgs e) => _service.OpenCpexSite();
    private void OpenSinfSite_Click(object sender, RoutedEventArgs e) => _service.OpenSinfoppesSite();
    private void OpenLastCpexPdf_Click(object sender, RoutedEventArgs e) => OpenExisting(_cpex.LastPdf);
    private void OpenLastSinfPdf_Click(object sender, RoutedEventArgs e) => OpenExisting(_sinf.LastPdf);
    private void OpenExisting(string path) { if (File.Exists(path)) ShellService.OpenPath(path); else SigfurDialog.Show(this, "Arquivo não localizado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = MainTabs.SelectedIndex == 0 ? CpexOutputBox.Text : SinfOutputBox.Text;
        if (Directory.Exists(folder)) ShellService.OpenPath(folder); else ShellService.OpenPath(App.Paths.DataDirectory);
    }
    private void BrowseCpexOutput_Click(object sender, RoutedEventArgs e) { var path = Browse(CpexOutputBox.Text); if (path is not null) CpexOutputBox.Text = path; }
    private void BrowseSinfOutput_Click(object sender, RoutedEventArgs e) { var path = Browse(SinfOutputBox.Text); if (path is not null) SinfOutputBox.Text = path; }
    private string? Browse(string initial) { var dialog = new OpenFolderDialog { Title = "Selecionar pasta", InitialDirectory = Directory.Exists(initial) ? initial : App.Paths.DataDirectory }; return dialog.ShowDialog(this) == true ? dialog.FolderName : null; }

    private void RefreshReports_Click(object sender, RoutedEventArgs e) => RefreshSavedReports();

    private void RefreshSavedReports()
    {
        _savedReports.Clear();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDirectory(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
                if (Directory.Exists(full)) directories.Add(full);
            }
            catch { }
        }

        AddDirectory(CpexOutputBox.Text);
        AddDirectory(SinfOutputBox.Text);
        AddDirectory(_cpex.OutputDirectory);
        AddDirectory(_sinf.OutputDirectory);
        AddDirectory(App.Paths.BankInconsistencyOutputDirectory);
        AddDirectory(App.Paths.SinfoppesCriticizedOutputDirectory);

        foreach (var directory in directories)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        var source = path.StartsWith(App.Paths.SinfoppesCriticizedOutputDirectory, StringComparison.OrdinalIgnoreCase)
                                     || directory.Equals(_sinf.OutputDirectory, StringComparison.OrdinalIgnoreCase)
                            ? "SINFOPPES"
                            : "CPEX";
                        _savedReports.Add(new SavedBankReport
                        {
                            Source = source,
                            Name = info.Name,
                            FullPath = info.FullName,
                            Folder = info.DirectoryName ?? string.Empty,
                            Modified = info.LastWriteTime,
                            Competence = InferCompetence(info.Name),
                            SizeBytes = info.Length
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        var ordered = _savedReports
            .GroupBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => x.Modified)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SavedReportsGrid.ItemsSource = ordered;
        SavedReportsSummaryText.Text = $"{ordered.Count} PDF(s) localizado(s). Selecione vários com Ctrl/Shift para comparar.";
    }

    private static string InferCompetence(string name)
    {
        var match = Regex.Match(name, @"(?<!\d)(20\d{2})[-_ ]?(0[1-9]|1[0-2])(?!\d)");
        if (match.Success) return $"{match.Groups[2].Value}/{match.Groups[1].Value}";
        match = Regex.Match(name, @"(?<!\d)(0[1-9]|1[0-2])[-_ ]?(20\d{2})(?!\d)");
        return match.Success ? $"{match.Groups[1].Value}/{match.Groups[2].Value}" : "—";
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        var selected = SavedReportsGrid.SelectedItems.Cast<SavedBankReport>().ToList();
        if (selected.Count == 0 && SavedReportsGrid.SelectedItem is SavedBankReport one) selected.Add(one);
        if (selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione pelo menos um PDF.", "Relatórios salvos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var item in selected.Where(x => File.Exists(x.FullPath))) ShellService.OpenPath(item.FullPath);
        StatusText.Text = selected.Count == 1 ? "PDF aberto." : $"{selected.Count} PDFs abertos para comparação.";
    }

    private void SavedReportsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenReports_Click(sender, new RoutedEventArgs());

    private void OpenReportsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SavedReportsGrid.SelectedItem is SavedBankReport item && Directory.Exists(item.Folder))
        {
            ShellService.OpenPath(item.Folder);
            return;
        }
        OpenFolder_Click(sender, e);
    }

    private sealed class SavedBankReport
    {
        public string Source { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;
        public string Competence { get; init; } = "—";
        public DateTime Modified { get; init; }
        public long SizeBytes { get; init; }
        public string ModifiedText => Modified.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
        public string SizeText => SizeBytes < 1024 * 1024
            ? $"{Math.Max(1, SizeBytes / 1024d):0} KB"
            : $"{SizeBytes / 1024d / 1024d:0.0} MB";
    }

    private async Task SaveAllQuietlyAsync()
    {
        try { await Task.WhenAll(_service.SaveCpexSettingsAsync(ReadCpex()), _service.SaveSinfoppesSettingsAsync(ReadSinf())); } catch { }
    }
    private void ShowError(Exception ex) { _ = App.Log.WriteAsync("Falha na Inconsistência Bancária.", ex); SigfurDialog.Show(this, ex.Message, "SIGFUR — Inconsistência Bancária", MessageBoxButton.OK, MessageBoxImage.Error); }
}
