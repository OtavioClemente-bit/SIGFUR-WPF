using Microsoft.Win32;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Personnel;

public partial class AbsenceWindow : Window
{
    private readonly AbsenceService _service = new(App.Paths);
    private List<MilitaryRecord> _military = [];
    private List<AbsenceOccurrence> _rows = [];
    private sealed class FilterOption { public int? Id { get; init; } public MilitaryRecord? Military { get; init; } public string DisplayName { get; init; } = string.Empty; }

    public AbsenceWindow()
    {
        InitializeComponent();
        App.UiState.Attach(this);
        MonthBox.ItemsSource = CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.MonthNames.Take(12)
            .Select((name, index) => new { Name = CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(name), Value = index + 1 }).ToList();
        MonthBox.DisplayMemberPath = "Name";
        MonthBox.SelectedValuePath = "Value";
        MonthBox.SelectedValue = DateTime.Today.Month;
        YearBox.ItemsSource = Enumerable.Range(DateTime.Today.Year - 5, 8).Reverse().ToList();
        YearBox.SelectedItem = DateTime.Today.Year;
        TypeFilterBox.SelectedIndex = 0;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private int SelectedMonth => MonthBox.SelectedValue is int value ? value : DateTime.Today.Month;
    private int SelectedYear => YearBox.SelectedItem is int value ? value : DateTime.Today.Year;

    private async Task InitializeAsync()
    {
        await _service.InitializeAsync();
        _military = await App.MilitaryRepository.GetAllAsync();
        var options = new List<FilterOption> { new() { DisplayName = "Todos os militares" } };
        options.AddRange(_military
            .OrderBy(x => x, Comparer<MilitaryRecord>.Create((a, b) => MilitaryRankService.Compare(a.Rank, a.Name, b.Rank, b.Name)))
            .Select(x => new FilterOption { Id = x.Id, Military = x, DisplayName = $"{x.ShortRank} {x.Name}" }));
        QuickMilitaryBox.ItemsSource = options.Where(x => x.Id is not null).ToList();
        MilitaryFilterBox.ItemsSource = options;
        MilitaryFilterBox.SelectedIndex = 0;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (MonthBox.SelectedValue is not int month || YearBox.SelectedItem is not int year) return;
        var type = (TypeFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
        var militaryId = (MilitaryFilterBox.SelectedItem as FilterOption)?.Id;
        _rows = await _service.ListAsync(year, month, militaryId, type, SearchBox.Text);
        OccurrenceGrid.ItemsSource = _rows;
        var summary = AbsenceService.Summarize(_rows);
        TotalText.Text = summary.Total.ToString("N0");
        AbsencesText.Text = summary.Absences.ToString("N0");
        DelaysText.Text = summary.Delays.ToString("N0");
        UnjustifiedText.Text = summary.Unjustified.ToString("N0");
        MinutesText.Text = summary.DelayMinutes.ToString("N0");
        var closure = await _service.GetClosureAsync(year, month);
        StatusText.Text = closure is null
            ? $"{_rows.Count:N0} ocorrência(s) no filtro atual. Mês ainda sem fechamento salvo."
            : $"{_rows.Count:N0} ocorrência(s) • fechamento salvo em {closure.ClosedAtText}" + (string.IsNullOrWhiteSpace(closure.Note) ? string.Empty : $" • {closure.Note}");
    }

    private async void Filter_Changed(object sender, EventArgs e) { if (IsLoaded) await RefreshAsync(); }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        await OpenEditorAsync(null);
    }

    private async void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        if (QuickMilitaryBox.SelectedItem is not FilterOption option || option.Id is null)
        {
            SigfurDialog.Show(this, "Pesquise e selecione o militar antes de lançar.", "Faltas & Atrasos", MessageBoxButton.OK, MessageBoxImage.Information);
            QuickMilitaryBox.Focus();
            return;
        }
        await OpenEditorAsync(option.Id);
    }

    private async Task OpenEditorAsync(int? preselectedMilitaryId)
    {
        var window = new AbsenceEditorWindow(_military, null, preselectedMilitaryId) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await _service.SaveAsync(window.Value);
            await RefreshAsync();
        }
    }

    private void OccurrenceGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Edit_Click(sender, new RoutedEventArgs());

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (OccurrenceGrid.SelectedItem is not AbsenceOccurrence selected) return;
        var window = new AbsenceEditorWindow(_military, selected) { Owner = this };
        if (window.ShowDialog() == true) { await _service.SaveAsync(window.Value); await RefreshAsync(); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (OccurrenceGrid.SelectedItem is not AbsenceOccurrence selected) return;
        if (SigfurDialog.Show(this, $"Excluir a ocorrência de {selected.ShortRank} {selected.Name} em {selected.DateText}?", "Faltas & Atrasos", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _service.DeleteAsync(selected.Id);
        await RefreshAsync();
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (OccurrenceGrid.SelectedItem is not AbsenceOccurrence item) return;
        Clipboard.SetText($"{item.DateText} {item.Time} — {item.ShortRank} {NameHighlightHelper.PlainDisplay(item.Name, item.WarName)} — {item.Type} — {item.JustifiedText}. Motivo: {item.Reason}. Providência: {item.Measure}.");
        StatusText.Text = "Resumo copiado.";
    }

    private async void SaveClosure_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Fechamento do mês", "Observação opcional para identificar a conferência ou o responsável.") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var summary = AbsenceService.Summarize(_rows);
        await _service.SaveClosureAsync(SelectedYear, SelectedMonth, summary, prompt.Value);
        await RefreshAsync();
        SigfurDialog.Show(this, "O retrato deste mês foi salvo. Um novo fechamento do mesmo mês substitui o anterior sem apagar as ocorrências.", "Faltas & Atrasos", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        Directory.CreateDirectory(App.Paths.AbsenceReportsDirectory);
        var dialog = new SaveFileDialog
        {
            Filter = "CSV UTF-8|*.csv",
            FileName = $"faltas_atrasos_{SelectedYear}_{SelectedMonth:00}.csv",
            InitialDirectory = App.Paths.AbsenceReportsDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        var builder = new StringBuilder();
        builder.AppendLine("Data;Hora;Posto/Graduação;Nome completo;Tipo;Minutos;Justificada;Motivo;Providência;Observações");
        foreach (var item in _rows)
            builder.AppendLine(string.Join(';', new[]
            {
                item.DateText, item.Time, item.ShortRank, item.Name, item.Type, item.Minutes.ToString(CultureInfo.InvariantCulture),
                item.Justified ? "Sim" : "Não", item.Reason, item.Measure, item.Notes
            }.Select(Csv)));
        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
        StatusText.Text = "CSV gerado: " + dialog.FileName;
    }

    private void ExportHtml_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        Directory.CreateDirectory(App.Paths.AbsenceReportsDirectory);
        var file = UniquePath(Path.Combine(App.Paths.AbsenceReportsDirectory, $"RELATORIO_FALTAS_ATRASOS_{SelectedYear}_{SelectedMonth:00}_{DateTime.Now:yyyyMMdd_HHmmss}.html"));
        File.WriteAllText(file, BuildReportHtml(), new UTF8Encoding(false));
        ShellService.OpenPath(file);
        StatusText.Text = "Relatório HTML gerado: " + file;
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        Directory.CreateDirectory(App.Paths.AbsenceReportsDirectory);
        var dialog = new SaveFileDialog
        {
            Filter = "Documento PDF|*.pdf",
            FileName = $"RELATORIO_FALTAS_ATRASOS_{SelectedYear}_{SelectedMonth:00}.pdf",
            InitialDirectory = App.Paths.AbsenceReportsDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        var pdfPath = dialog.FileName;
        var htmlPath = Path.Combine(Path.GetTempPath(), $"SIGFUR_FALTAS_{Guid.NewGuid():N}.html");
        File.WriteAllText(htmlPath, BuildReportHtml(), new UTF8Encoding(false));
        try
        {
            StatusText.Text = "Gerando PDF profissional…";
            await PrintHtmlToPdfAsync(htmlPath, pdfPath);
            if (File.Exists(pdfPath))
            {
                try { File.Delete(htmlPath); } catch { }
                ShellService.OpenPath(pdfPath);
                StatusText.Text = "PDF profissional gerado: " + pdfPath;
            }
        }
        catch (Exception ex)
        {
            ShellService.OpenPath(htmlPath);
            SigfurDialog.Show(this, "Não foi possível converter automaticamente para PDF. O relatório HTML foi preservado e aberto para impressão manual.\n\n" + ex.Message, "Faltas & Atrasos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildReportHtml()
    {
        var summary = AbsenceService.Summarize(_rows);
        var tableRows = string.Join("\n", _rows.Select(item =>
            $"<tr><td>{H(item.DateText)}</td><td>{H(item.Time)}</td><td>{H(item.ShortRank)}</td><td>{BoldNameHtml(item.Name, item.WarName)}</td><td>{H(item.Type)}</td><td>{H(item.DurationText)}</td><td>{H(item.JustifiedText)}</td><td>{H(item.Reason)}</td><td>{H(item.Measure)}</td><td>{H(item.Notes)}</td></tr>"));
        var filter = MilitaryFilterBox.Text;
        var militarySummaryRows = string.Join("\n", _rows
            .GroupBy(x => new { x.MilitaryId, x.ShortRank, x.Name, x.WarName })
            .Select(group => new
            {
                group.Key,
                Total = group.Count(),
                Absences = group.Count(x => x.Type.Equals("FALTA", StringComparison.OrdinalIgnoreCase)),
                Delays = group.Count(x => x.Type.Equals("ATRASO", StringComparison.OrdinalIgnoreCase)),
                Minutes = group.Sum(x => x.Minutes),
                Unjustified = group.Count(x => !x.Justified)
            })
            .OrderByDescending(x => x.Total)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Key.ShortRank))
            .ThenBy(x => x.Key.Name)
            .Select(x => $"<tr><td>{H(x.Key.ShortRank)}</td><td>{BoldNameHtml(x.Key.Name, x.Key.WarName)}</td><td>{x.Total}</td><td>{x.Absences}</td><td>{x.Delays}</td><td>{x.Minutes}</td><td>{x.Unjustified}</td></tr>"));

        var html = new StringBuilder(20_000);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"pt-BR\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>Faltas e Atrasos</title>");
        html.AppendLine("<style>");
        html.AppendLine("@page{size:A4 landscape;margin:10mm}");
        html.AppendLine("*{box-sizing:border-box}");
        html.AppendLine("body{font-family:'Segoe UI',Arial,sans-serif;margin:28px;color:#172033;background:#fff}");
        html.AppendLine("header{border-bottom:4px solid #163d70;padding-bottom:12px}");
        html.AppendLine("h1{color:#163d70;margin:0;font-size:25px}");
        html.AppendLine(".sub{color:#526174;margin-top:5px}");
        html.AppendLine(".cards{display:flex;gap:10px;margin:18px 0}");
        html.AppendLine(".card{flex:1;border:1px solid #d8dee9;border-radius:10px;padding:12px 16px;background:#f8fafc}");
        html.AppendLine(".card b{font-size:11px;color:#526174}");
        html.AppendLine(".value{font-size:23px;font-weight:700;color:#163d70;margin-top:3px}");
        html.AppendLine("h2{color:#163d70;font-size:15px;margin:20px 0 8px;border-left:4px solid #ff9f66;padding-left:9px}");
        html.AppendLine("table{border-collapse:collapse;width:100%;font-size:10.5px;page-break-inside:auto}");
        html.AppendLine("th,td{border:1px solid #cfd8e3;padding:6px;vertical-align:top}");
        html.AppendLine("th{background:#163d70;color:#fff;text-align:left}");
        html.AppendLine("tr:nth-child(even){background:#f8fafc}");
        html.AppendLine("thead{display:table-header-group} tr{page-break-inside:avoid;page-break-after:auto}");
        html.AppendLine("footer{margin-top:12px;color:#64748b;font-size:10px}");
        html.AppendLine("@media print{body{margin:0}}");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<header>");
        html.AppendLine("<h1>Faltas &amp; Atrasos</h1>");
        html.Append("<div class=\"sub\">Período: ")
            .Append(H(MonthBox.Text))
            .Append('/')
            .Append(SelectedYear)
            .Append(" • Filtro: ")
            .Append(H(filter))
            .Append(" • Gerado em ")
            .Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")))
            .AppendLine("</div>");
        html.AppendLine("</header>");
        html.AppendLine("<div class=\"cards\">");
        html.Append("<div class=\"card\"><b>TOTAL</b><div class=\"value\">").Append(summary.Total).AppendLine("</div></div>");
        html.Append("<div class=\"card\"><b>FALTAS</b><div class=\"value\">").Append(summary.Absences).AppendLine("</div></div>");
        html.Append("<div class=\"card\"><b>ATRASOS</b><div class=\"value\">").Append(summary.Delays).AppendLine("</div></div>");
        html.Append("<div class=\"card\"><b>NÃO JUSTIFICADAS</b><div class=\"value\">").Append(summary.Unjustified).AppendLine("</div></div>");
        html.Append("<div class=\"card\"><b>MINUTOS DE ATRASO</b><div class=\"value\">").Append(summary.DelayMinutes).AppendLine("</div></div>");
        html.AppendLine("</div>");
        html.AppendLine("<h2>Resumo por militar</h2>");
        html.AppendLine("<table>");
        html.AppendLine("<thead><tr><th>P/G</th><th>Nome completo</th><th>Total</th><th>Faltas</th><th>Atrasos</th><th>Minutos</th><th>Não justificadas</th></tr></thead>");
        html.Append("<tbody>").Append(militarySummaryRows).AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("<h2>Relação detalhada das ocorrências</h2>");
        html.AppendLine("<table>");
        html.AppendLine("<thead><tr><th>Data</th><th>Hora</th><th>P/G</th><th>Nome completo</th><th>Tipo</th><th>Duração</th><th>Situação</th><th>Motivo</th><th>Providência</th><th>Observações</th></tr></thead>");
        html.Append("<tbody>").Append(tableRows).AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("<footer>Documento gerado pelo SIGFUR. O nome de guerra é destacado em negrito dentro do nome completo.</footer>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static async Task PrintHtmlToPdfAsync(string htmlPath, string pdfPath)
    {
        var browser = FindHeadlessBrowser();
        if (string.IsNullOrWhiteSpace(browser)) throw new InvalidOperationException("Microsoft Edge ou Google Chrome não foi localizado.");
        var info = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--headless");
        info.ArgumentList.Add("--disable-gpu");
        info.ArgumentList.Add("--no-pdf-header-footer");
        info.ArgumentList.Add("--print-to-pdf=" + pdfPath);
        info.ArgumentList.Add(new Uri(htmlPath).AbsoluteUri);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o navegador para gerar o PDF.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || !File.Exists(pdfPath))
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "O navegador não produziu o arquivo PDF." : error.Trim());
        }
    }

    private static string FindHeadlessBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string BoldNameHtml(string name, string war) => string.Concat(NameHighlightHelper.BuildSegments(name, war).Select(segment => segment.IsBold ? $"<b>{H(segment.Text)}</b>" : H(segment.Text)));
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{index:00}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMddHHmmss}{extension}");
    }
}
