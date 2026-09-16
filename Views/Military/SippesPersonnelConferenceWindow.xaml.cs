using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class SippesPersonnelConferenceWindow : Window
{
    private readonly MilitaryRepository _repository;
    private readonly ObservableCollection<SippesPersonnelConferenceRow> _rows = [];
    private readonly ObservableCollection<SippesOmPaystubMirrorConferenceRow> _mirrorRows = [];
    private readonly ICollectionView _view;
    private readonly ICollectionView _mirrorView;
    private SippesPersonnelConferenceSummary? _currentSummary;
    private SippesOmPaystubMirrorSummary? _currentMirrorSummary;
    private CancellationTokenSource? _operationCts;
    private bool _busy;
    private bool _openingMirrorDocument;

    public SippesPersonnelConferenceWindow(MilitaryRepository repository)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterConferenceRow;
        ResultsGrid.ItemsSource = _view;
        _mirrorView = CollectionViewSource.GetDefaultView(_mirrorRows);
        _mirrorView.Filter = FilterMirrorRow;
        MirrorGridCompact.ItemsSource = _mirrorView;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeMirrorSelectors();
        await RefreshCredentialsAsync();
    }


    private void InitializeMirrorSelectors()
    {
        // O SIPPES normalmente só libera a folha processada do mês já fechado.
        // Em início de mês, deixar o mês atual como padrão leva a “Nenhum registro foi encontrado”.
        var reference = DateTime.Today.AddMonths(-1);
        MirrorYearBox.Text = reference.Year.ToString(CultureInfo.InvariantCulture);
        MirrorMonthBox.SelectedIndex = Math.Clamp(reference.Month - 1, 0, 11);
    }

    private (int Year, int Month) ReadMirrorReference()
    {
        var year = int.TryParse(MirrorYearBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear)
            ? parsedYear
            : DateTime.Today.Year;
        if (year is < 2000 or > 2200)
            throw new InvalidOperationException("Informe um ano válido para gerar o Espelho da OM.");
        var month = 1;
        if (MirrorMonthBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var parsedMonth)) month = parsedMonth;
        month = Math.Clamp(month, 1, 12);
        return (year, month);
    }

    private void ResetMirrorLiveLog(string message)
    {
        MirrorLiveLogBox.Text = $"[{DateTime.Now:HH:mm:ss}] {message}" + Environment.NewLine;
        MirrorLiveLogBox.ScrollToEnd();
    }

    private void AppendMirrorLiveLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        MirrorLiveLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message.Trim()}" + Environment.NewLine);
        MirrorLiveLogBox.ScrollToEnd();
    }

    private async Task RefreshCredentialsAsync()
    {
        try
        {
            var settings = await App.CpexPaystubs.LoadSettingsAsync();
            settings.System = "SIPPES";
            await App.CpexPaystubs.SaveSettingsAsync(settings);
            var password = App.CpexPaystubs.ReadSavedPassword(settings);
            UserBox.Text = settings.Login;
            PasswordBox.Password = password;
            SessionStatusText.Text = !string.IsNullOrWhiteSpace(settings.Login) && !string.IsNullOrWhiteSpace(password)
                ? $"Login salvo para {settings.Login}"
                : "Informe login e senha do SIPPES";
            StatusText.Text = App.CpexPaystubs.HasPreparedSession
                ? $"Sessão SIPPES já preparada às {App.CpexPaystubs.PreparedAt:HH:mm}. PDFs Dados MA: {App.Paths.SippesDadosMaDirectory}"
                : $"Pronto para preparar sessão ou conferir direto. PDFs Dados MA: {App.Paths.SippesDadosMaDirectory}";
        }
        catch (Exception ex)
        {
            SessionStatusText.Text = "Credencial não carregada";
            StatusText.Text = ex.Message;
        }
    }

    private async Task SaveCredentialsFromFieldsAsync()
    {
        var login = UserBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Informe o usuário/CPF e a senha do SIPPES.");

        await App.CpexPaystubs.SaveCredentialsAsync(login, password, savePassword: true);
        var settings = await App.CpexPaystubs.LoadSettingsAsync();
        settings.System = "SIPPES";
        settings.Browser = string.IsNullOrWhiteSpace(settings.Browser) ? "Edge" : settings.Browser;
        settings.OutputDirectory = PersonDocumentStorageService.DefaultRoot(App.Paths);
        await App.CpexPaystubs.SaveSettingsAsync(settings);
        SessionStatusText.Text = $"Login salvo para {login}";
    }

    private async void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync("Salvando login protegido pelo Windows…", async () =>
        {
            await SaveCredentialsFromFieldsAsync();
            StatusText.Text = "Login salvo. A senha fica protegida pelo Windows no perfil do usuário.";
        });
    }

    private async void PrepareSession_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync("Preparando sessão do SIPPES…", async ct =>
        {
            await SaveCredentialsFromFieldsAsync();
            var settings = await App.CpexPaystubs.LoadSettingsAsync();
            settings.System = "SIPPES";
            settings.OutputDirectory = PersonDocumentStorageService.DefaultRoot(App.Paths);
            var password = App.CpexPaystubs.ReadSavedPassword(settings);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = p.Message;
            });
            await App.CpexPaystubs.PrepareHiddenSessionAsync(settings, password, progress, ct);
            StatusText.Text = $"Sessão SIPPES preparada em segundo plano às {App.CpexPaystubs.PreparedAt:HH:mm}.";
            SessionStatusText.Text = $"Sessão SIPPES pronta — {settings.Login}";
        });
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        await RunConferenceAsync(downloadReports: true);
    }

    private async void RunTableOnly_Click(object sender, RoutedEventArgs e)
    {
        await RunConferenceAsync(downloadReports: false);
    }

    private async Task RunConferenceAsync(bool downloadReports)
    {
        await RunUiAsync(downloadReports ? "Conferindo efetivo e baixando PDFs do SIPPES…" : "Conferindo efetivo no SIPPES…", async ct =>
        {
            await SaveCredentialsFromFieldsAsync();
            _rows.Clear();
            UpdateSummary(null);

            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                var prefix = p.Total > 0 ? $"{p.Current}/{p.Total} — " : string.Empty;
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = prefix + p.Message;
            });

            var sippesRows = await App.CpexPaystubs.ReadSippesActivePersonnelAsync(progress, downloadReports, ct);
            await ApplyConferenceRowsAsync(sippesRows, cancellationToken: ct);
        });
    }

    private async void CaptureOpen_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync("Lendo tabela aberta no navegador…", async ct =>
        {
            _rows.Clear();
            UpdateSummary(null);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                var prefix = p.Total > 0 ? $"{p.Current}/{p.Total} — " : string.Empty;
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = prefix + p.Message;
            });

            var sippesRows = await App.CpexPaystubs.ReadCurrentVisibleSippesActivePersonnelAsync(progress, ct);
            await ApplyConferenceRowsAsync(sippesRows, cancellationToken: ct);
        });
    }

    private async void LoadSavedPdfs_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync("Lendo PDFs já salvos do SIPPES…", async ct =>
        {
            _rows.Clear();
            UpdateSummary(null);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                var prefix = p.Total > 0 ? $"{p.Current}/{p.Total} — " : string.Empty;
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = prefix + p.Message;
            });

            var sippesRows = await App.CpexPaystubs.ReadSavedSippesActiveDataReportsAsync(progress, ct);
            await ApplyConferenceRowsAsync(sippesRows, "Conferência montada usando PDFs salvos já encontrados pelo SIGFUR. Nenhum acesso novo ao SIPPES foi feito.", ct);
        });
    }

    private async void SelectPdfs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar PDFs Dados MA do SIPPES",
            Filter = "PDF do SIPPES|*.pdf",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;

        await RunUiAsync("Lendo PDFs selecionados…", async ct =>
        {
            _rows.Clear();
            UpdateSummary(null);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                var prefix = p.Total > 0 ? $"{p.Current}/{p.Total} — " : string.Empty;
                if (!string.IsNullOrWhiteSpace(p.Message)) StatusText.Text = prefix + p.Message;
            });

            var sippesRows = await App.CpexPaystubs.ReadSippesActiveDataReportsFromFilesAsync(dialog.FileNames, progress, ct);
            await ApplyConferenceRowsAsync(sippesRows, $"Conferência montada com {dialog.FileNames.Length} PDF(s) selecionado(s) manualmente. Nenhum acesso novo ao SIPPES foi feito.", ct);
        });
    }

    private async void LoadLastConference_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync("Carregando última conferência salva…", async () =>
        {
            var cache = await App.Json.LoadAsync<SippesPersonnelConferenceCache>(LastConferenceFile);
            if (cache is null || cache.Rows.Count == 0)
                throw new InvalidOperationException("Ainda não existe conferência salva para carregar.");

            ReplaceRowsFast(ResultsGrid, _view, _rows, cache.Rows);
            _currentSummary = cache.Summary;
            UpdateSummary(cache.Summary);
            _view.Refresh();
            StatusText.Text = $"Última conferência carregada: {cache.Rows.Count} linha(s), salva em {cache.SavedAt:dd/MM/yyyy HH:mm}.";
            CountText.Text = $"{cache.Rows.Count} linha(s) — salvo em {cache.SavedAt:dd/MM/yyyy HH:mm}";
        });
    }

    private async Task ApplyConferenceRowsAsync(IReadOnlyList<SippesPersonnelRow> sippesRows, string? completionMessage = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sippesRows.Count == 0)
            throw new InvalidOperationException(completionMessage is null
                ? "O SIPPES abriu a consulta, mas o SIGFUR não conseguiu ler nenhuma linha da tabela. A conferência foi interrompida para não gerar falso alerta de que todos os ativos estão fora da folha."
                : "Nenhum PDF de Dados MA válido foi localizado para montar a conferência. Use o botão 'Selecionar PDFs…' e escolha os PDFs baixados, ou confirme se eles estão em AppData\\Local\\SIGFUR\\SIPPES, Downloads, Documentos ou Área de Trabalho.");

        var active = await _repository.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await App.MilitaryPreferences.ApplyAsync(active);
        var licensedTransferred = await App.LicensedTransferred.GetAllAsync(includeHidden: true);
        var (rows, summary) = await Task.Run(() => SippesPersonnelConferenceService.Build(sippesRows, active, licensedTransferred), cancellationToken);

        ReplaceRowsFast(ResultsGrid, _view, _rows, rows, cancellationToken);
        _currentSummary = summary;
        UpdateSummary(summary);
        _view.Refresh();
        await SaveLastConferenceAsync(summary);
        var pdfCount = sippesRows.Count(x => !string.IsNullOrWhiteSpace(x.ReportPdfPath));
        StatusText.Text = completionMessage ?? $"Conferência concluída: {sippesRows.Count} registro(s) lidos do SIPPES, {pdfCount} PDF(s) vinculado(s) e {_rows.Count} linha(s) de análise. Pasta: {App.Paths.SippesDadosMaDirectory}";
        CountText.Text = $"{_rows.Count} linha(s) — gerado às {summary.GeneratedAt:dd/MM/yyyy HH:mm}";
    }

    private static void ReplaceRowsFast<T>(DataGrid grid, ICollectionView view, ObservableCollection<T> target, IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        grid.ItemsSource = null;
        try
        {
            target.Clear();
            foreach (var row in rows)
                target.Add(row);
        }
        finally
        {
            grid.ItemsSource = view;
        }
    }

    private void UpdateSummary(SippesPersonnelConferenceSummary? summary)
    {
        if (summary is null)
        {
            _currentSummary = null;
            SummaryText.Text = "Conferência em preparação. O SIGFUR só lê/compara e, se solicitado, baixa PDFs para leitura; não altera cadastro e não mexe no SIPPES.";
            SippesCountPill.Text = "SIPPES: 0";
            OkCountPill.Text = "OK: 0";
            LicensedCountPill.Text = "Lic./Transf.: 0";
            OutsideCountPill.Text = "Fora: 0";
            MissingCountPill.Text = "Ativo sem SIPPES: 0";
            CountText.Text = "Nenhuma conferência executada";
            return;
        }

        SummaryText.Text = summary.Display
                           + $" | Nome sem CPF/IDT: {summary.NameOnlyMatchCount}"
                           + $" | PDF Normal: {summary.PaymentNormalCount}"
                           + $" | PDF Suspenso: {summary.PaymentSuspendedCount}"
                           + $" | PDF Outro: {summary.PaymentOtherCount}"
                           + $" | Gerado às {summary.GeneratedAt:dd/MM/yyyy HH:mm}";
        SippesCountPill.Text = $"SIPPES: {summary.SippesCount}";
        OkCountPill.Text = $"OK: {summary.ActiveOkCount}";
        LicensedCountPill.Text = $"Lic./Transf.: {summary.ReceivingButLicensedTransferredCount}";
        OutsideCountPill.Text = $"Fora: {summary.ReceivingOutsideSigfurCount}";
        MissingCountPill.Text = $"Ativo sem SIPPES: {summary.ActiveMissingFromSippesCount}";
    }

    private string LastConferenceFile => App.Paths.SippesLastConferenceFile;

    private async Task SaveLastConferenceAsync(SippesPersonnelConferenceSummary summary)
    {
        var cache = new SippesPersonnelConferenceCache
        {
            SavedAt = DateTime.Now,
            Summary = summary,
            Rows = _rows.ToList()
        };
        await App.Json.SaveAsync(LastConferenceFile, cache);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        CountText.Text = _currentSummary is null
            ? $"{_view.Cast<object>().Count()} linha(s) filtrada(s)"
            : $"{_view.Cast<object>().Count()} de {_rows.Count} linha(s) — gerado às {_currentSummary.GeneratedAt:dd/MM/yyyy HH:mm}";
    }

    private bool FilterConferenceRow(object item)
    {
        if (item is not SippesPersonnelConferenceRow row) return false;
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var haystack = string.Join(' ', row.Status, row.Severity, row.DisplayRank, row.DisplayName, row.DisplayCpf,
                row.DisplayMilitaryId, row.SippesOm, row.DisplayPaymentStatus, row.PaymentAlert, row.SigfurSource, row.MatchKind);
            if (!NormalizeForFilter(haystack).Contains(NormalizeForFilter(query), StringComparison.OrdinalIgnoreCase)) return false;
        }

        var selected = (FilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
        return selected switch
        {
            "CRITICAL" => row.Severity.Contains("CRÍTICO", StringComparison.OrdinalIgnoreCase),
            "ALERT" => row.Severity.Contains("ALERTA", StringComparison.OrdinalIgnoreCase) || row.Severity.Contains("ATENÇÃO", StringComparison.OrdinalIgnoreCase),
            "ACTIVE_OK" => row.Status.StartsWith("OK", StringComparison.OrdinalIgnoreCase),
            "LICENSED" => row.Status.Contains("Lic./Transf.", StringComparison.OrdinalIgnoreCase),
            "OUTSIDE" => row.Status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase),
            "MISSING" => row.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase),
            "PAY_NORMAL" => NormalizeForFilter(row.DisplayPaymentStatus).Contains("PAGAMENTO NORMAL", StringComparison.OrdinalIgnoreCase),
            "PAY_SUSPENDED" => NormalizeForFilter(row.DisplayPaymentStatus).Contains("PAGAMENTO SUSPENSO", StringComparison.OrdinalIgnoreCase),
            "PAY_OTHER" => !string.IsNullOrWhiteSpace(row.SippesPaymentStatus)
                           && !row.DisplayPaymentStatus.Contains("Não baixado", StringComparison.OrdinalIgnoreCase)
                           && !row.DisplayPaymentStatus.Contains("Situação não localizada", StringComparison.OrdinalIgnoreCase)
                           && !NormalizeForFilter(row.DisplayPaymentStatus).Contains("PAGAMENTO NORMAL", StringComparison.OrdinalIgnoreCase)
                           && !NormalizeForFilter(row.DisplayPaymentStatus).Contains("PAGAMENTO SUSPENSO", StringComparison.OrdinalIgnoreCase),
            "PAY_NOT_FOUND" => row.DisplayPaymentStatus.Contains("Situação não localizada", StringComparison.OrdinalIgnoreCase) || row.DisplayPaymentStatus.Contains("Não lido", StringComparison.OrdinalIgnoreCase),
            "POSSIBLE_BAD" => IsCriticalNormalPaymentRow(row),
            "NOT_ACTIVE_PAY_NORMAL" => IsCriticalNormalPaymentRow(row),
            _ => true
        };
    }

    private static string NormalizeForFilter(string? value)
    {
        var text = (value ?? string.Empty).ToUpperInvariant();
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            SigfurDialog.Show(this, "Execute a conferência antes de exportar.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar conferência de efetivo SIPPES",
            Filter = "CSV|*.csv",
            FileName = $"Conferencia_Efetivo_SIPPES_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        var lines = new List<string> { "SITUACAO;GRAVIDADE;PG;NOME;CPF;IDT_CADASTRO;OM_SIPPES;SITUACAO_PDF;ALERTA_PAGAMENTO;PDF;CADASTRO_SIGFUR;CONFERENCIA" };
        lines.AddRange(_rows.Select(x => string.Join(';', new[]
        {
            SippesPersonnelConferenceService.CsvCell(x.Status),
            SippesPersonnelConferenceService.CsvCell(x.Severity),
            SippesPersonnelConferenceService.CsvCell(x.DisplayRank),
            SippesPersonnelConferenceService.CsvCell(x.DisplayName),
            SippesPersonnelConferenceService.CsvCell(x.DisplayCpf),
            SippesPersonnelConferenceService.CsvCell(x.DisplayMilitaryId),
            SippesPersonnelConferenceService.CsvCell(x.SippesOm),
            SippesPersonnelConferenceService.CsvCell(x.DisplayPaymentStatus),
            SippesPersonnelConferenceService.CsvCell(x.PaymentAlert),
            SippesPersonnelConferenceService.CsvCell(x.ReportPdfPath),
            SippesPersonnelConferenceService.CsvCell(x.SigfurSource),
            SippesPersonnelConferenceService.CsvCell(x.MatchKind)
        })));
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true));
        StatusText.Text = $"Conferência exportada: {dialog.FileName}";
    }


    private async void ExportCriticalReport_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            SigfurDialog.Show(this, "Execute uma conferência ou carregue a última conferência antes de gerar o relatório.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var candidates = _rows
            .Where(IsCriticalNormalPaymentRow)
            .OrderBy(x => x.Status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => MilitaryRankService.GetOrder(x.DisplayRank))
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            SigfurDialog.Show(this,
                "Não há militar fora da relação de ativos com Pagamento Normal.\n\nO relatório crítico só inclui quem está em Lic./Transf. ou fora do SIGFUR e, ao mesmo tempo, consta no PDF do SIPPES com Pagamento Normal.",
                "SIGFUR — Relatório crítico", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Salvar relatório crítico SIPPES",
            Filter = "Relatório HTML pronto para imprimir|*.html",
            FileName = $"Relatorio_Critico_SIPPES_Pagamento_Normal_{DateTime.Now:yyyyMMdd_HHmm}.html"
        };
        if (dialog.ShowDialog(this) != true) return;

        var html = BuildCriticalReportHtml(candidates);
        await File.WriteAllTextAsync(dialog.FileName, html, new UTF8Encoding(true));
        StatusText.Text = $"Relatório crítico gerado: {dialog.FileName}";
        ShellService.OpenPath(dialog.FileName);
    }

    private static bool IsCriticalNormalPaymentRow(SippesPersonnelConferenceRow row)
    {
        if (!IsPaymentNormalForReport(row.DisplayPaymentStatus)) return false;
        return IsNotActiveForCriticalReport(row);
    }

    private static bool IsNotActiveForCriticalReport(SippesPersonnelConferenceRow row)
    {
        var status = NormalizeForFilter(row.Status);
        var source = NormalizeForFilter(row.SigfurSource);
        if (source.StartsWith("ATIVOS", StringComparison.OrdinalIgnoreCase)) return false;
        return status.Contains("LIC./TRANSF.", StringComparison.OrdinalIgnoreCase)
               || status.Contains("LIC TRANSF", StringComparison.OrdinalIgnoreCase)
               || status.Contains("FORA DO SIGFUR", StringComparison.OrdinalIgnoreCase)
               || source.Contains("LICENCIADOS", StringComparison.OrdinalIgnoreCase)
               || source.Contains("TRANSFERIDOS", StringComparison.OrdinalIgnoreCase)
               || source.Contains("NAO LOCALIZADO", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPaymentNormalForReport(string? value)
    {
        var text = NormalizeForFilter(value);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("SUSPENSO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("BLOQUEADO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TRANSFERIDO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CANCELADO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NAO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LOCALIZADA", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LIDO", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Contains("PAGAMENTO NORMAL", StringComparison.OrdinalIgnoreCase)
               || string.Equals(text.Trim(), "NORMAL", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(text, @"(^|\s)NORMAL(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildCriticalReportHtml(IReadOnlyList<SippesPersonnelConferenceRow> rows)
    {
        var generatedAt = DateTime.Now;
        var outsideCount = rows.Count(x => x.Status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase));
        var licensedCount = rows.Count(x => x.Status.Contains("Lic./Transf.", StringComparison.OrdinalIgnoreCase)
                                           || x.SigfurSource.Contains("Licenciados", StringComparison.OrdinalIgnoreCase));
        var withPdfCount = rows.Count(x => !string.IsNullOrWhiteSpace(x.ReportPdfPath) && File.Exists(x.ReportPdfPath));
        var sb = new StringBuilder();
        sb.AppendLine("""
<!doctype html>
<html lang="pt-br">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Relatório Crítico SIPPES — Pagamento Normal fora da Ativa</title>
<style>
:root{--ink:#172033;--muted:#64748b;--line:#d7dde8;--soft:#f4f7fb;--danger:#9f1239;--dangerSoft:#fff1f2;--warn:#92400e;--primary:#1d4f91;--ok:#166534;}
*{box-sizing:border-box} body{font-family:Segoe UI,Arial,sans-serif;color:var(--ink);margin:0;background:#eef2f7;font-size:13px;line-height:1.45}.page{max-width:1180px;margin:24px auto;background:white;padding:34px 40px;border:1px solid var(--line);box-shadow:0 12px 35px rgba(15,23,42,.10)}
.header{display:flex;justify-content:space-between;gap:20px;border-bottom:3px solid var(--primary);padding-bottom:18px}.brand h1{margin:0;font-size:24px;letter-spacing:.04em}.brand p{margin:6px 0 0;color:var(--muted)}.stamp{text-align:right;color:var(--muted);font-size:12px}.stamp strong{display:block;color:var(--ink);font-size:14px}.alert{margin:22px 0;padding:14px 16px;border:1px solid #fecdd3;background:var(--dangerSoft);border-left:6px solid var(--danger);border-radius:10px}.alert strong{color:var(--danger);font-size:14px}.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:18px 0 22px}.card{border:1px solid var(--line);border-radius:12px;padding:14px;background:var(--soft)}.card .num{font-size:28px;font-weight:800;color:var(--primary)}.card .label{color:var(--muted);font-size:12px}.section{margin-top:24px}.section h2{font-size:18px;margin:0 0 10px;border-bottom:1px solid var(--line);padding-bottom:8px}table{width:100%;border-collapse:collapse;margin-top:10px}th,td{border:1px solid var(--line);padding:8px 9px;vertical-align:top}th{background:#eaf1f8;text-align:left;font-size:12px}.tag{display:inline-block;padding:3px 7px;border-radius:999px;font-weight:700;font-size:11px}.tag-danger{background:var(--dangerSoft);color:var(--danger);border:1px solid #fecdd3}.tag-warn{background:#fffbeb;color:var(--warn);border:1px solid #fde68a}.tag-ok{background:#f0fdf4;color:var(--ok);border:1px solid #bbf7d0}.person{border:1px solid var(--line);border-radius:14px;margin:14px 0;padding:14px 16px;page-break-inside:avoid}.person h3{margin:0 0 8px;font-size:16px}.grid{display:grid;grid-template-columns:170px 1fr 170px 1fr;gap:6px 12px}.k{font-size:11px;color:var(--muted);font-weight:700;text-transform:uppercase}.v{font-weight:600}.note{margin-top:10px;color:var(--muted);font-size:12px}.footer{margin-top:26px;border-top:1px solid var(--line);padding-top:12px;color:var(--muted);font-size:12px}.actions{margin-top:10px;padding:12px;background:#f8fafc;border:1px solid var(--line);border-radius:10px}.actions li{margin:4px 0}a{color:#1d4ed8;text-decoration:none}@media print{body{background:white}.page{margin:0;max-width:none;border:0;box-shadow:none}.no-print{display:none}.person{break-inside:avoid}.cards{grid-template-columns:repeat(4,1fr)}}
</style>
</head>
<body>
<div class="page">
""");
        sb.AppendLine("<div class=\"header\"><div class=\"brand\">");
        sb.AppendLine("<h1>RELATÓRIO CRÍTICO — SIPPES</h1>");
        sb.AppendLine("<p>Militares que <strong>não estão na relação de ativos</strong> do SIGFUR e constam nos Dados de Militar da Ativa do SIPPES com <strong>Pagamento Normal</strong>.</p>");
        sb.AppendLine("</div><div class=\"stamp\">");
        sb.AppendLine("<strong>SIGFUR — Conferência de Efetivo SIPPES</strong>");
        sb.AppendLine($"Gerado em {Html(generatedAt.ToString("dd/MM/yyyy HH:mm"))}<br>Fonte: tabela SIPPES + PDFs Dados MA salvos");
        sb.AppendLine("</div></div>");
        sb.AppendLine("<div class=\"alert\"><strong>Atenção operacional:</strong> este relatório não afirma irregularidade nem recebimento indevido. Ele apenas separa para conferência manual quem está fora da relação de ativos do SIGFUR, ou em Licenciados/Transferidos, com PDF Dados MA indicando Situação de Pagamento Normal.</div>");
        sb.AppendLine("<div class=\"cards\">");
        AppendCard(sb, rows.Count.ToString(CultureInfo.InvariantCulture), "Total crítico");
        AppendCard(sb, outsideCount.ToString(CultureInfo.InvariantCulture), "Fora do SIGFUR");
        AppendCard(sb, licensedCount.ToString(CultureInfo.InvariantCulture), "Lic./Transf.");
        AppendCard(sb, withPdfCount.ToString(CultureInfo.InvariantCulture), "Com PDF vinculado");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"section\"><h2>Resumo executivo</h2>");
        sb.AppendLine("<p>Critério aplicado: <strong>Cadastro SIGFUR diferente de Ativos</strong> + <strong>Situação no PDF Dados MA = Pagamento Normal</strong>. Casos com pagamento suspenso, não localizado ou sem PDF não entram neste relatório.</p>");
        sb.AppendLine("<div class=\"actions\"><strong>Providências sugeridas para conferência:</strong><ol><li>abrir o PDF de Dados MA do militar;</li><li>conferir se há BI/aditamento de licenciamento, transferência ou agregação;</li><li>confirmar no SIPPES/folha se o pagamento permanece normal no mês atual;</li><li>registrar a medida tomada antes de qualquer alteração administrativa.</li></ol></div></div>");
        sb.AppendLine("<div class=\"section\"><h2>Relação nominal consolidada</h2>");
        sb.AppendLine("<table><thead><tr><th>#</th><th>Situação</th><th>P/G</th><th>Nome</th><th>CPF</th><th>Idt/Cadastro</th><th>Cadastro SIGFUR</th><th>Situação PDF</th><th>PDF</th></tr></thead><tbody>");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            sb.AppendLine("<tr>"
                          + $"<td>{i + 1}</td>"
                          + $"<td>{Html(r.Status)}</td>"
                          + $"<td>{Html(r.DisplayRank)}</td>"
                          + $"<td><strong>{Html(r.DisplayName)}</strong></td>"
                          + $"<td>{Html(r.DisplayCpf)}</td>"
                          + $"<td>{Html(r.DisplayMilitaryId)}</td>"
                          + $"<td>{Html(r.SigfurSource)}</td>"
                          + $"<td><span class=\"tag tag-danger\">{Html(r.DisplayPaymentStatus)}</span></td>"
                          + $"<td>{BuildPdfLink(r)}</td>"
                          + "</tr>");
        }
        sb.AppendLine("</tbody></table></div>");
        sb.AppendLine("<div class=\"section\"><h2>Fichas individuais para conferência</h2>");
        for (var i = 0; i < rows.Count; i++) AppendPersonCard(sb, rows[i], i + 1);
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"footer\">Relatório gerado automaticamente pelo SIGFUR. Use como instrumento de conferência; a decisão administrativa deve ser baseada nos documentos oficiais, boletins/aditamentos e validação manual no SIPPES.</div>");
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static void AppendCard(StringBuilder sb, string number, string label)
        => sb.AppendLine($"<div class=\"card\"><div class=\"num\">{Html(number)}</div><div class=\"label\">{Html(label)}</div></div>");

    private static void AppendPersonCard(StringBuilder sb, SippesPersonnelConferenceRow row, int index)
    {
        var badgeClass = row.Status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase) ? "tag-danger" : "tag-warn";
        sb.AppendLine("<div class=\"person\">");
        sb.AppendLine($"<h3>{index:00}. {Html(row.DisplayRank)} {Html(row.DisplayName)} <span class=\"tag {badgeClass}\">{Html(row.Status)}</span></h3>");
        sb.AppendLine("<div class=\"grid\">");
        AppendKv(sb, "CPF", row.DisplayCpf);
        AppendKv(sb, "Idt/Cadastro", row.DisplayMilitaryId);
        AppendKv(sb, "OM SIPPES", row.SippesOm);
        AppendKv(sb, "Cadastro SIGFUR", row.SigfurSource);
        AppendKv(sb, "Situação PDF", row.DisplayPaymentStatus);
        AppendKv(sb, "Conferência", row.MatchKind);
        AppendKv(sb, "PDF", string.IsNullOrWhiteSpace(row.ReportPdfPath) ? row.ReportDownloadStatus : row.ReportPdfPath);
        sb.AppendLine("</div>");
        sb.AppendLine($"<div class=\"note\"><strong>Arquivo:</strong> {BuildPdfLink(row)}</div>");
        sb.AppendLine("</div>");
    }

    private static void AppendKv(StringBuilder sb, string key, string value)
        => sb.AppendLine($"<div class=\"k\">{Html(key)}</div><div class=\"v\">{Html(value)}</div>");

    private static string BuildPdfLink(SippesPersonnelConferenceRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ReportPdfPath) || !File.Exists(row.ReportPdfPath))
            return Html(string.IsNullOrWhiteSpace(row.ReportDownloadStatus) ? "Sem PDF vinculado" : row.ReportDownloadStatus);
        var uri = new Uri(row.ReportPdfPath).AbsoluteUri;
        return $"<a href=\"{Html(uri)}\">Abrir PDF</a>";
    }

    private static string Html(string? value)
    {
        return (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private async void SendOutside_Click(object sender, RoutedEventArgs e)
    {
        var candidates = _rows
            .Where(x => x.Status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase))
            .Where(x => MilitaryFormatting.Digits(x.SippesCpf).Length >= 10)
            .ToList();
        if (candidates.Count == 0)
        {
            SigfurDialog.Show(this, "Não há pessoa fora do SIGFUR com CPF lido do SIPPES.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = await App.Json.LoadAsync<List<PaystubCenterWindow.ExternalPaystubPerson>>(App.Paths.ExternalPaystubPeopleFile) ?? [];
        var added = 0;
        foreach (var item in candidates)
        {
            var cpf = MilitaryFormatting.Digits(item.SippesCpf);
            if (existing.Any(x => MilitaryFormatting.Digits(x.Cpf) == cpf)) continue;
            existing.Add(new PaystubCenterWindow.ExternalPaystubPerson { Name = item.SippesName, Cpf = cpf });
            added++;
        }
        await App.Json.SaveAsync(App.Paths.ExternalPaystubPeopleFile, existing);
        StatusText.Text = added == 0
            ? "As pessoas fora do SIGFUR já estavam na lista de Pessoas de fora."
            : $"{added} pessoa(s) enviada(s) para Pessoas de fora.";
    }



    private async void GenerateMirror_Click(object sender, RoutedEventArgs e)
    {
        var (year, month) = ReadMirrorReference();
        ResetMirrorLiveLog($"Modo manual assistido iniciado para Espelho OM {month:00}/{year}. O SIGFUR vai abrir o SIPPES e aguardar seu OK.");
        await RunUiAsync($"Abrindo SIPPES em modo manual para Espelho OM {month:00}/{year}…", async ct =>
        {
            await SaveCredentialsFromFieldsAsync();
            _mirrorRows.Clear();
            UpdateMirrorSummary(null);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                var prefix = p.Total > 0 ? $"{p.Current}/{p.Total} — " : string.Empty;
                if (!string.IsNullOrWhiteSpace(p.Message))
                {
                    var message = prefix + p.Message;
                    StatusText.Text = message;
                    AppendMirrorLiveLog(message);
                }
            });

            await App.CpexPaystubs.OpenSippesOmPaystubMirrorManualAsync(year, month, progress, ct);
            StatusText.Text = "SIPPES aberto. Faça o fluxo manualmente até o relatório detalhado do Espelho OM ficar carregado.";
            AppendMirrorLiveLog("SIPPES aberto em modo manual. Faça o fluxo no navegador: folha do mês, pesquisar, selecionar todos, gerar relatório. Não feche esta mensagem até a tela final estar pronta.");

            var confirmation = SigfurDialog.Show(
                this,
                "O SIPPES foi aberto para você fazer manualmente.\n\n" +
                "Faça o fluxo completo no navegador até chegar na tela FINAL do relatório, com os favorecidos, dados e valores carregados.\n\n" +
                "Quando a tela final estiver pronta, clique em OK aqui. O SIGFUR vai ocultar o navegador, salvar o HTML/TXT e cruzar com os ativos e contracheques salvos.\n\n" +
                "Se ainda não carregou, não clique OK agora.",
                "SIGFUR — Espelho OM manual",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information,
                MessageBoxResult.Cancel);

            if (confirmation != MessageBoxResult.OK)
            {
                StatusText.Text = "Leitura manual cancelada. O navegador SIPPES ficou aberto para você continuar ou fechar.";
                AppendMirrorLiveLog("Leitura cancelada pelo usuário. O navegador permaneceu visível.");
                return;
            }

            AppendMirrorLiveLog("OK recebido. Lendo a tela final aberta no SIPPES...");
            ct.ThrowIfCancellationRequested();
            var result = await App.CpexPaystubs.ReadCurrentVisibleSippesOmPaystubMirrorAsync(year, month, progress, ct);
            App.CpexPaystubs.HidePreparedSessionWindows();
            AppendMirrorLiveLog("Tela lida. Navegador ocultado. Cruzando com ativos e contracheques salvos...");
            await ApplyMirrorRowsAsync(result, ct);
            AppendMirrorLiveLog("Conferência finalizada e tabela atualizada no SIGFUR.");
        });
    }

    private async void ReadOpenMirror_Click(object sender, RoutedEventArgs e)
    {
        var (year, month) = ReadMirrorReference();
        ResetMirrorLiveLog($"Lendo o relatório que já está aberto no navegador para {month:00}/{year}.");
        await RunUiAsync("Lendo relatório de Espelho da OM já aberto no navegador…", async ct =>
        {
            _mirrorRows.Clear();
            UpdateMirrorSummary(null);
            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message))
                {
                    StatusText.Text = p.Message;
                    AppendMirrorLiveLog(p.Message);
                }
            });
            ct.ThrowIfCancellationRequested();
            var result = await App.CpexPaystubs.ReadCurrentVisibleSippesOmPaystubMirrorAsync(year, month, progress, ct);
            App.CpexPaystubs.HidePreparedSessionWindows();
            await ApplyMirrorRowsAsync(result, ct);
            AppendMirrorLiveLog("Relatório aberto lido, navegador ocultado e conferência atualizada.");
        });
    }

    private async Task ApplyMirrorRowsAsync(SippesOmPaystubMirrorResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.People.Count == 0)
            throw new InvalidOperationException("O Espelho da OM foi lido, mas nenhum favorecido foi identificado.");

        var active = await _repository.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await App.MilitaryPreferences.ApplyAsync(active);
        var (rows, summary) = await BuildMirrorConferenceRowsAsync(result, active, cancellationToken);

        ReplaceRowsFast(MirrorGridCompact, _mirrorView, _mirrorRows, rows, cancellationToken);
        _currentMirrorSummary = summary;
        UpdateMirrorSummary(summary);
        _mirrorView.Refresh();
        await SaveLastMirrorConferenceAsync(result.Year, result.Month, summary);
        StatusText.Text = $"Espelho OM conferido: {summary.SippesCount} pessoa(s), {summary.OutsideActiveCount} fora dos ativos e {summary.SavedPaystubMissingCount} sem contracheque salvo em {result.Month:00}/{result.Year}. Arquivo: {result.HtmlPath}";
        CountText.Text = $"{_mirrorRows.Count} linha(s) — Espelho OM {result.Month:00}/{result.Year}";
    }

    private async Task<(List<SippesOmPaystubMirrorConferenceRow> Rows, SippesOmPaystubMirrorSummary Summary)> BuildMirrorConferenceRowsAsync(
        SippesOmPaystubMirrorResult result,
        IReadOnlyList<MilitaryRecord> active,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<SippesOmPaystubMirrorConferenceRow>();
        var matchedActiveIds = new HashSet<int>();
        var dadosMaIndex = await BuildSippesDadosMaPdfIndexAsync(cancellationToken);
        var processed = 0;
        var totalToBuild = result.People.Count + active.Count;

        foreach (var person in result.People)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (processed == 1 || processed % 40 == 0)
                AppendMirrorLiveLog($"Montando conferência do Espelho OM: {processed}/{totalToBuild} — {person.Name}");
            var match = FindActiveMatchForMirror(person, active);
            string paystubPath = string.Empty;
            string paystubStatus;
            string status;
            string severity;
            string matchKind;
            int priority;

            MilitaryRecord fichaProbe;
            if (match is null)
            {
                fichaProbe = new MilitaryRecord
                {
                    Rank = person.Rank,
                    Name = person.Name,
                    Cpf = person.Cpf,
                    MilitaryId = person.MilitaryId,
                    PrecCp = person.PrecCp
                };
                paystubPath = await App.Paystubs.FindBestAsync(fichaProbe, result.Month, result.Year, cancellationToken) ?? string.Empty;
                if (!IsExpectedMonthlyPaystubPath(paystubPath, result.Month, result.Year)) paystubPath = string.Empty;
                var hasOutsidePaystub = !string.IsNullOrWhiteSpace(paystubPath) && File.Exists(paystubPath);
                status = "Consta no Espelho OM — fora dos ativos do SIGFUR";
                severity = "CRÍTICO";
                matchKind = "Nome/CPF/Idt do espelho não localizado na relação de ativos do SIGFUR";
                paystubStatus = hasOutsidePaystub ? $"Salvo — {Path.GetFileName(paystubPath)}" : $"Não localizado em {result.Month:00}/{result.Year}";
                priority = 0;
            }
            else
            {
                fichaProbe = match;
                matchedActiveIds.Add(match.Id);
                paystubPath = await App.Paystubs.FindBestAsync(match, result.Month, result.Year, cancellationToken) ?? string.Empty;
                if (!IsExpectedMonthlyPaystubPath(paystubPath, result.Month, result.Year)) paystubPath = string.Empty;
                var hasPaystub = !string.IsNullOrWhiteSpace(paystubPath) && File.Exists(paystubPath);
                status = hasPaystub
                    ? "OK — ativo no SIGFUR, consta no Espelho OM e tem contracheque salvo"
                    : "Ativo no Espelho OM — contracheque salvo não localizado";
                severity = hasPaystub ? "OK" : "ATENÇÃO";
                matchKind = !string.IsNullOrWhiteSpace(person.Cpf) && MilitaryFormatting.Digits(person.Cpf) == MilitaryFormatting.Digits(match.Cpf)
                    ? "Conferido por CPF"
                    : !string.IsNullOrWhiteSpace(person.MilitaryId) && MilitaryFormatting.Digits(person.MilitaryId) == MilitaryFormatting.Digits(match.MilitaryId)
                        ? "Conferido por Idt/Cadastro"
                        : "Conferido por nome completo; revisar CPF/Idt";
                paystubStatus = hasPaystub ? $"Salvo — {Path.GetFileName(paystubPath)}" : $"Não localizado em {result.Month:00}/{result.Year}";
                priority = hasPaystub ? 50 : 20;
            }
            var sippesDadosMaPath = FindSippesDadosMaPdfFromIndex(dadosMaIndex, fichaProbe, person);

            rows.Add(new SippesOmPaystubMirrorConferenceRow
            {
                Status = status,
                Severity = severity,
                SippesRank = MilitaryRankService.ShortName(person.Rank),
                SippesName = person.Name,
                SippesCpf = person.Cpf,
                SippesMilitaryId = person.MilitaryId,
                SippesPrecCp = person.PrecCp,
                SippesOm = person.Om,
                PaymentStatus = person.PaymentStatus,
                NetValue = person.DisplayNetValue,
                SigfurRank = match?.ShortRank ?? string.Empty,
                SigfurName = match?.Name ?? string.Empty,
                SigfurWarName = match?.WarName ?? string.Empty,
                SigfurCpf = match?.Cpf ?? string.Empty,
                SigfurMilitaryId = match?.MilitaryId ?? string.Empty,
                MatchKind = matchKind,
                SavedPaystubPath = paystubPath,
                SavedPaystubStatus = paystubStatus,
                SippesDadosMaPath = sippesDadosMaPath,
                SourcePath = result.HtmlPath,
                SourceText = person.SourceText,
                MirrorRubricsText = SummarizeRubrics(person.Rubrics),
                ValueCheckStatus = InitialValueCheckStatus(paystubPath, result.Month, result.Year),
                SortPriority = priority
            });
        }

        var mirrorKeys = new HashSet<string>(result.People.SelectMany(x => new[]
        {
            MilitaryFormatting.Digits(x.Cpf),
            MilitaryFormatting.Digits(x.MilitaryId),
            NormalizeForFilter(x.Name)
        }).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);

        foreach (var military in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (processed % 40 == 0)
                AppendMirrorLiveLog($"Montando conferência do Espelho OM: {processed}/{totalToBuild} — {military.Name}");
            if (matchedActiveIds.Contains(military.Id)) continue;
            var keys = new[]
            {
                MilitaryFormatting.Digits(military.Cpf),
                MilitaryFormatting.Digits(military.MilitaryId),
                NormalizeForFilter(military.Name)
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (keys.Any(mirrorKeys.Contains)) continue;

            var paystubPath = await App.Paystubs.FindBestAsync(military, result.Month, result.Year, cancellationToken) ?? string.Empty;
            if (!IsExpectedMonthlyPaystubPath(paystubPath, result.Month, result.Year)) paystubPath = string.Empty;
            var hasPaystub = !string.IsNullOrWhiteSpace(paystubPath) && File.Exists(paystubPath);
            var sippesDadosMaPath = FindSippesDadosMaPdfFromIndex(dadosMaIndex, military, null);
            rows.Add(new SippesOmPaystubMirrorConferenceRow
            {
                Status = "Ativo SIGFUR não apareceu no Espelho OM",
                Severity = "ALERTA",
                SigfurRank = military.ShortRank,
                SigfurName = military.Name,
                SigfurWarName = military.WarName,
                SigfurCpf = military.Cpf,
                SigfurMilitaryId = military.MilitaryId,
                MatchKind = "Militar ativo no SIGFUR, mas não foi identificado no relatório do Espelho OM",
                SavedPaystubPath = paystubPath,
                SavedPaystubStatus = hasPaystub ? $"Salvo — {Path.GetFileName(paystubPath)}" : $"Não localizado em {result.Month:00}/{result.Year}",
                SippesDadosMaPath = sippesDadosMaPath,
                SourcePath = result.HtmlPath,
                ValueCheckStatus = "Não se aplica — não apareceu no Espelho OM",
                SortPriority = 30
            });
        }

        var ordered = rows
            .OrderBy(x => x.SortPriority)
            .ThenBy(x => MilitaryRankService.GetOrder(x.DisplayRank))
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var summary = new SippesOmPaystubMirrorSummary
        {
            GeneratedAt = result.GeneratedAt,
            SippesCount = result.People.Count,
            ActiveMatchedCount = ordered.Count(x => x.Status.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || x.Status.StartsWith("Ativo no Espelho", StringComparison.OrdinalIgnoreCase)),
            OutsideActiveCount = ordered.Count(x => x.Status.Contains("fora dos ativos", StringComparison.OrdinalIgnoreCase)),
            SavedPaystubFoundCount = ordered.Count(x => !string.IsNullOrWhiteSpace(x.SavedPaystubPath) && File.Exists(x.SavedPaystubPath)),
            SavedPaystubMissingCount = ordered.Count(x => !x.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(x.SavedPaystubPath)),
            ActiveMissingFromMirrorCount = ordered.Count(x => x.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase)),
            HtmlPath = result.HtmlPath
        };
        return (ordered, summary);
    }


    private static string InitialValueCheckStatus(string paystubPath, int month, int year)
        => IsExpectedMonthlyPaystubPath(paystubPath, month, year)
            ? "Aguardando batimento de rubricas/valores"
            : $"Sem contracheque salvo para {month:00}/{year}";

    private static async Task<string> ResolveMirrorMonthlyPaystubPathAsync(SippesOmPaystubMirrorConferenceRow row, int month, int year, CancellationToken cancellationToken = default)
    {
        if (IsExpectedMonthlyPaystubPath(row.SavedPaystubPath, month, year)) return row.SavedPaystubPath;
        var found = await App.Paystubs.FindBestAsync(BuildMilitaryRecordForMirrorRow(row), month, year, cancellationToken) ?? string.Empty;
        return IsExpectedMonthlyPaystubPath(found, month, year) ? found : string.Empty;
    }

    private static bool IsExpectedMonthlyPaystubPath(string? path, int month, int year)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var name = Path.GetFileName(path);
        var full = NormalizeForFilter($"{name} {Path.GetDirectoryName(path)}");
        if (full.Contains("FICHA FINANCEIRA", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("DADOS MILITAR ATIVA", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("DADOS_MILITAR_ATIVA", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("DADOS MA", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("ESPELHO CONTRACHEQUE OM", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("ESPELHO_CONTRACHEQUE_OM", StringComparison.OrdinalIgnoreCase))
            return false;

        var file = NormalizeForFilter(name);
        if (!file.Contains(year.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(file, $@"(?<!\d){month:00}\s*[-_/\.]\s*{year}(?!\d)|(?<!\d){year}\s*[-_/\.]\s*{month:00}(?!\d)", RegexOptions.CultureInvariant)) return true;
        return file.Contains(MonthNameForFile(month), StringComparison.OrdinalIgnoreCase) || file.Contains(MonthShortNameForFile(month), StringComparison.OrdinalIgnoreCase);
    }

    private static string MonthNameForFile(int month) => month switch
    {
        1 => "JANEIRO", 2 => "FEVEREIRO", 3 => "MARCO", 4 => "ABRIL",
        5 => "MAIO", 6 => "JUNHO", 7 => "JULHO", 8 => "AGOSTO",
        9 => "SETEMBRO", 10 => "OUTUBRO", 11 => "NOVEMBRO", 12 => "DEZEMBRO",
        _ => string.Empty
    };

    private static string MonthShortNameForFile(int month) => month switch
    {
        1 => "JAN", 2 => "FEV", 3 => "MAR", 4 => "ABR", 5 => "MAI", 6 => "JUN",
        7 => "JUL", 8 => "AGO", 9 => "SET", 10 => "OUT", 11 => "NOV", 12 => "DEZ",
        _ => string.Empty
    };

    private static string SummarizeRubrics(IEnumerable<SippesOmPaystubRubricValue>? rubrics)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var list = (rubrics ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Description, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (list.Count == 0) return string.Empty;

        return string.Join(Environment.NewLine, list.Select(x =>
        {
            var description = Regex.Replace(x.Description ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            return string.IsNullOrWhiteSpace(description)
                ? $"{x.Code}: {x.Value.ToString("C", culture)}"
                : $"{x.Code} — {description}: {x.Value.ToString("C", culture)}";
        }));
    }

    private async Task CompareMirrorRowsWithSavedPaystubsAsync(List<SippesOmPaystubMirrorConferenceRow> rows, int month, int year, CancellationToken cancellationToken = default)
    {
        var total = rows.Count(x => !string.IsNullOrWhiteSpace(x.SavedPaystubPath) && File.Exists(x.SavedPaystubPath) && !string.IsNullOrWhiteSpace(x.SourceText));
        var current = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Status.StartsWith("Ativo SIGFUR não apareceu", StringComparison.OrdinalIgnoreCase))
            {
                row.ValueCheckStatus = "Não se aplica — não apareceu no Espelho OM";
                continue;
            }

            row.SavedPaystubPath = await ResolveMirrorMonthlyPaystubPathAsync(row, month, year, cancellationToken);
            row.SavedPaystubStatus = !string.IsNullOrWhiteSpace(row.SavedPaystubPath) && File.Exists(row.SavedPaystubPath)
                ? $"Salvo — {Path.GetFileName(row.SavedPaystubPath)}"
                : $"Não localizado em {month:00}/{year}";

            if (string.IsNullOrWhiteSpace(row.SavedPaystubPath) || !File.Exists(row.SavedPaystubPath))
            {
                row.ValueCheckStatus = $"Sem contracheque salvo para {month:00}/{year}";
                if (!row.Status.Contains("fora dos ativos", StringComparison.OrdinalIgnoreCase))
                {
                    row.Status = "PENDENTE — contracheque salvo não localizado";
                    row.Severity = "ATENÇÃO";
                    row.SortPriority = Math.Min(row.SortPriority, 20);
                }
                continue;
            }

            var mirrorRubrics = ExtractRubricsFromMirrorSection(row.SourceText);
            row.MirrorRubricsText = SummarizeRubrics(mirrorRubrics);
            if (mirrorRubrics.Count == 0)
            {
                row.ValueCheckStatus = "Não consegui ler as rubricas do Espelho OM salvo";
                row.Severity = "ATENÇÃO";
                continue;
            }

            current++;
            if (current == 1 || current % 10 == 0)
                AppendMirrorLiveLog($"Lendo contracheques salvos: {current}/{total} — {row.DisplayName}");

            try
            {
                var paystubText = await App.PdfText.ExtractAsync(row.SavedPaystubPath);
                var paystubRubrics = ExtractRubricsFromPaystubText(paystubText);
                row.PaystubRubricsText = SummarizeRubrics(paystubRubrics);
                var check = CompareRubricValues(mirrorRubrics, paystubRubrics);
                row.ValueCheckStatus = check;
                if (check.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                {
                    row.Status = row.Status.Contains("fora dos ativos", StringComparison.OrdinalIgnoreCase)
                        ? row.Status
                        : "OK — ativo e valores conferidos";
                    row.Severity = row.Status.Contains("fora dos ativos", StringComparison.OrdinalIgnoreCase) ? row.Severity : "OK";
                    row.SortPriority = Math.Max(row.SortPriority, 60);
                }
                else
                {
                    row.Status = "DIVERGENTE — rubrica/valor diferente do Espelho OM";
                    row.Severity = "CRÍTICO";
                    row.SortPriority = 5;
                }
            }
            catch (Exception ex)
            {
                row.ValueCheckStatus = "Erro ao ler contracheque salvo: " + ex.Message;
                row.Severity = "CRÍTICO";
                row.Status = "ERRO — não consegui conferir valores no PDF salvo";
                row.SortPriority = 6;
            }
        }

        rows.Sort((a, b) =>
        {
            var pr = a.SortPriority.CompareTo(b.SortPriority);
            if (pr != 0) return pr;
            var rank = MilitaryRankService.GetOrder(a.DisplayRank).CompareTo(MilitaryRankService.GetOrder(b.DisplayRank));
            return rank != 0 ? rank : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private static List<SippesOmPaystubRubricValue> ExtractRubricsFromMirrorSection(string text)
    {
        var lineResult = ExtractMirrorRubricsFromLines(text);
        return lineResult.Count > 0 ? lineResult : ExtractRubricsFromCompactMirrorText(text);
    }

    private static List<SippesOmPaystubRubricValue> ExtractRubricsFromPaystubText(string text)
    {
        var lineResult = ExtractPaystubRubricsFromLines(text);
        return lineResult.Count > 0 ? lineResult : ExtractRubricsFromCompactMirrorText(text);
    }

    private static List<SippesOmPaystubRubricValue> ExtractPaystubRubricsFromLines(string text)
    {
        var result = new List<SippesOmPaystubRubricValue>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = Regex.Split(text.Replace('\u00a0', ' '), @"\r\n|\n|\r")
            .Select(x => Regex.Replace(x, @"\s+", " ", RegexOptions.CultureInvariant).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        // PdfPig/pdftotext costuma montar o contracheque do CPEx em linhas separadas:
        // código em uma linha, descrição na próxima, valor na seguinte e só depois %, R/D e IR.
        // O parser antigo tentava ler tudo na mesma linha e acabava perdendo rubricas corretas.
        for (var i = 0; i < lines.Count; i++)
        {
            var codeOnly = Regex.Match(lines[i], @"^(?<code>[A-Z]{1,3}\d{3,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (codeOnly.Success)
            {
                var descriptionParts = new List<string>();
                string? valueText = null;
                for (var j = i + 1; j < Math.Min(lines.Count, i + 9); j++)
                {
                    var candidate = lines[j];
                    if (Regex.IsMatch(candidate, @"^[A-Z]{1,3}\d{3,5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) break;
                    if (IsRubricNoiseLine(candidate)) continue;
                    var valueMatch = Regex.Match(candidate, @"(?<value>\d{1,3}(?:\.\d{3})*,\d{2})", RegexOptions.CultureInvariant);
                    if (valueMatch.Success)
                    {
                        valueText = valueMatch.Groups["value"].Value;
                        break;
                    }
                    if (descriptionParts.Count < 3) descriptionParts.Add(candidate);
                }

                if (string.IsNullOrWhiteSpace(valueText))
                    valueText = FindPreviousLooseMoneyValue(lines, i);

                if (!string.IsNullOrWhiteSpace(valueText) && descriptionParts.Count > 0)
                    AddRubricIfValid(result, seen, codeOnly.Groups["code"].Value, string.Join(" ", descriptionParts), valueText);

                continue;
            }

            // Reserva para PDFs em que o extrator consiga manter código/descrição/valor na mesma linha.
            var inline = Regex.Match(lines[i],
                @"^(?<code>[A-Z]{1,3}\d{3,5})\s+(?<description>.+?)\s+(?<value>\d{1,3}(?:\.\d{3})*,\d{2})(?:\s+\d{1,3}(?:,\d{2})?)?\s+[RD]\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (inline.Success)
                AddRubricIfValid(result, seen, inline.Groups["code"].Value, inline.Groups["description"].Value, inline.Groups["value"].Value);
        }
        return result;
    }

    private static string? FindPreviousLooseMoneyValue(IReadOnlyList<string> lines, int codeIndex)
    {
        var values = new List<string>();
        for (var i = codeIndex - 1; i >= Math.Max(0, codeIndex - 10); i--)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"^[A-Z]{1,3}\d{3,5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) break;
            if (line.Contains("RUBRICA", StringComparison.OrdinalIgnoreCase) || line.Contains("DESCRI", StringComparison.OrdinalIgnoreCase)) break;
            var valueMatch = Regex.Match(line, @"(?<value>\d{1,3}(?:\.\d{3})*,\d{2})", RegexOptions.CultureInvariant);
            if (valueMatch.Success) values.Add(valueMatch.Groups["value"].Value);
        }

        // Quando o extrator joga o valor da primeira rubrica antes do código, costuma aparecer
        // valor e percentual antes do código. O valor financeiro é o primeiro em ordem visual.
        return values.Count == 0 ? null : values.Last();
    }

    private static bool IsRubricNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        if (line.Equals("R", StringComparison.OrdinalIgnoreCase) || line.Equals("D", StringComparison.OrdinalIgnoreCase) || line.Equals("+", StringComparison.OrdinalIgnoreCase) || line.Equals("-", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(line, @"^\d+[/\\]\d+$", RegexOptions.CultureInvariant)) return true;
        if (Regex.IsMatch(line, @"^\d{1,3}(?:,\d{2})?$", RegexOptions.CultureInvariant)) return false;
        return line.Contains("RUBRICA", StringComparison.OrdinalIgnoreCase)
            || line.Contains("DESCRI", StringComparison.OrdinalIgnoreCase)
            || line.Equals("VALOR", StringComparison.OrdinalIgnoreCase)
            || line.Equals("%", StringComparison.OrdinalIgnoreCase)
            || line.Equals("R/D", StringComparison.OrdinalIgnoreCase)
            || line.Equals("IR", StringComparison.OrdinalIgnoreCase)
            || line.Equals("PARC", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SippesOmPaystubRubricValue> ExtractMirrorRubricsFromLines(string text)
    {
        var result = new List<SippesOmPaystubRubricValue>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in Regex.Split(text.Replace('\u00a0', ' '), @"\r\n|\n|\r"))
        {
            var line = Regex.Replace(rawLine, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            if (line.Length == 0 || line.Contains("Total receitas", StringComparison.OrdinalIgnoreCase) || line.Contains("Total despesas", StringComparison.OrdinalIgnoreCase) || line.Contains("Total líquido", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Match match in Regex.Matches(line,
                         @"\b(?<code>[A-Z]{1,3}\d{3,5})\b\s+(?<description>.*?)(?<value>\d{1,3}(?:\.\d{3})*,\d{2})(?=\s+\b[A-Z]{1,3}\d{3,5}\b|\s*$)",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                AddRubricIfValid(result, seen, match.Groups["code"].Value, match.Groups["description"].Value, match.Groups["value"].Value);
        }
        return result;
    }

    private static List<SippesOmPaystubRubricValue> ExtractRubricsFromCompactMirrorText(string text)
    {
        var result = new List<SippesOmPaystubRubricValue>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var normalized = Regex.Replace(text.Replace('\u00a0', ' '), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(normalized,
                     @"\b(?<code>[A-Z]{1,3}\d{3,5})\b\s*(?<description>.*?)(?<value>\d{1,3}(?:\.\d{3})*,\d{2})(?=\s+\b[A-Z]{1,3}\d{3,5}\b|\s+Total\s+(?:receitas|despesas|l[ií]quido)\b|$)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline))
            AddRubricIfValid(result, seen, match.Groups["code"].Value, match.Groups["description"].Value, match.Groups["value"].Value);
        return result;
    }

    private static void AddRubricIfValid(List<SippesOmPaystubRubricValue> result, HashSet<string> seen, string rawCode, string rawDescription, string rawValue)
    {
        var code = NormalizeRubricCode(rawCode);
        var description = Regex.Replace(rawDescription ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(description)) return;
        if (description.Contains("Total receitas", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Total despesas", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Total líquido", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("RUBRICA", StringComparison.OrdinalIgnoreCase))
            return;
        if (!TryParseBrazilianMoney(rawValue, out var value) || value <= 0) return;
        var key = $"{code}|{value:0.00}|{description}";
        if (!seen.Add(key)) return;
        result.Add(new SippesOmPaystubRubricValue { Code = code, Description = description, Value = value });
    }

    private static string CompareRubricValues(IReadOnlyList<SippesOmPaystubRubricValue> mirrorRubrics, IReadOnlyList<SippesOmPaystubRubricValue> paystubRubrics)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var expected = NormalizeAndGroupRubrics(mirrorRubrics);
        var actual = NormalizeAndGroupRubrics(paystubRubrics);
        var matchedActual = new HashSet<int>();
        var issues = new List<string>();

        foreach (var item in expected.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Description, StringComparer.CurrentCultureIgnoreCase))
        {
            var exactIndex = FindMatchingRubric(actual, matchedActual, item, strictCode: true, allowEquivalentCode: false, allowDescription: false);
            if (exactIndex >= 0)
            {
                matchedActual.Add(exactIndex);
                continue;
            }

            var equivalentIndex = FindMatchingRubric(actual, matchedActual, item, strictCode: false, allowEquivalentCode: true, allowDescription: true);
            if (equivalentIndex >= 0)
            {
                matchedActual.Add(equivalentIndex);
                continue;
            }

            var sameCode = actual
                .Where((x, index) => !matchedActual.Contains(index) && string.Equals(x.Code, item.Code, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .ToList();
            if (sameCode.Count > 0)
                issues.Add($"{item.Code}: Espelho {item.Value.ToString("C", culture)} / PDF {sameCode.Sum().ToString("C", culture)}");
            else
                issues.Add($"faltando {item.Code} {item.Value.ToString("C", culture)}");
        }

        for (var i = 0; i < actual.Count; i++)
        {
            if (matchedActual.Contains(i)) continue;
            var pdf = actual[i];
            var existsEquivalent = expected.Any(x => IsEquivalentRubric(x, pdf, allowEquivalentCode: true, allowDescription: true));
            if (!existsEquivalent) issues.Add($"extra no PDF {pdf.Code} {pdf.Value.ToString("C", culture)}");
        }

        if (issues.Count == 0) return $"OK — {expected.Count} rubrica(s) e valores iguais";
        return "DIVERGENTE — " + string.Join("; ", issues.Take(8)) + (issues.Count > 8 ? $"; +{issues.Count - 8} diferença(s)" : string.Empty);
    }

    private static List<SippesOmPaystubRubricValue> NormalizeAndGroupRubrics(IEnumerable<SippesOmPaystubRubricValue> rubrics)
        => rubrics
            .Where(x => !string.IsNullOrWhiteSpace(x.Code) && x.Value > 0)
            .Select(x => new SippesOmPaystubRubricValue
            {
                Code = NormalizeRubricCode(x.Code),
                Description = Regex.Replace(x.Description ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant).Trim(),
                Value = x.Value
            })
            .GroupBy(x => $"{x.Code}|{NormalizeRubricDescriptionForCompare(x.Description)}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new SippesOmPaystubRubricValue
            {
                Code = g.First().Code,
                Description = g.First().Description,
                Value = g.Sum(x => x.Value)
            })
            .ToList();

    private static int FindMatchingRubric(IReadOnlyList<SippesOmPaystubRubricValue> candidates, HashSet<int> used, SippesOmPaystubRubricValue expected, bool strictCode, bool allowEquivalentCode, bool allowDescription)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i)) continue;
            if (IsEquivalentRubric(expected, candidates[i], allowEquivalentCode, allowDescription) && (!strictCode || string.Equals(NormalizeRubricCode(expected.Code), NormalizeRubricCode(candidates[i].Code), StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return -1;
    }

    private static bool IsEquivalentRubric(SippesOmPaystubRubricValue left, SippesOmPaystubRubricValue right, bool allowEquivalentCode, bool allowDescription)
    {
        if (Math.Abs(left.Value - right.Value) > 0.01m) return false;
        var leftCode = NormalizeRubricCode(left.Code);
        var rightCode = NormalizeRubricCode(right.Code);
        if (string.Equals(leftCode, rightCode, StringComparison.OrdinalIgnoreCase)) return true;

        var leftDescription = NormalizeRubricDescriptionForCompare(left.Description);
        var rightDescription = NormalizeRubricDescriptionForCompare(right.Description);

        // O Espelho da OM e o contracheque às vezes trocam só o prefixo (ER/NR/AR/FR/DR),
        // mantendo o mesmo número, descrição e valor. Isso não deve virar divergência falsa.
        if (allowEquivalentCode
            && string.Equals(RubricNumberKey(leftCode), RubricNumberKey(rightCode), StringComparison.OrdinalIgnoreCase)
            && AreRubricDescriptionsCompatible(leftDescription, rightDescription))
            return true;

        if (allowDescription && AreRubricDescriptionsCompatible(leftDescription, rightDescription)) return true;

        return false;
    }

    private static bool AreRubricDescriptionsCompatible(string leftDescription, string rightDescription)
    {
        if (string.IsNullOrWhiteSpace(leftDescription) || string.IsNullOrWhiteSpace(rightDescription)) return false;
        if (leftDescription == rightDescription) return true;
        if (leftDescription.Length >= 8 && rightDescription.Contains(leftDescription, StringComparison.OrdinalIgnoreCase)) return true;
        if (rightDescription.Length >= 8 && leftDescription.Contains(rightDescription, StringComparison.OrdinalIgnoreCase)) return true;

        var leftTokens = leftDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3 && !IsWeakRubricToken(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightTokens = rightDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3 && !IsWeakRubricToken(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return false;
        var common = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        return common >= Math.Min(2, Math.Min(leftTokens.Count, rightTokens.Count));
    }

    private static bool IsWeakRubricToken(string token)
        => token is "MIL" or "DISP" or "AD" or "C" or "DE" or "DA" or "DO" or "DAS" or "DOS" or "PARC" or "EX";

    private static string NormalizeRubricCode(string? rawCode)
    {
        var code = Regex.Replace(rawCode ?? string.Empty, @"[^A-Za-z0-9]", string.Empty, RegexOptions.CultureInvariant).ToUpperInvariant();
        var match = Regex.Match(code, @"^(?<prefix>[A-Z]{1,3})(?<digits>\d{1,5})$", RegexOptions.CultureInvariant);
        if (!match.Success) return code;
        var digits = match.Groups["digits"].Value;
        if (digits.Length < 4) digits = digits.PadLeft(4, '0');
        return match.Groups["prefix"].Value + digits;
    }

    private static string RubricNumberKey(string? code)
    {
        var digits = Regex.Match(NormalizeRubricCode(code), @"\d+", RegexOptions.CultureInvariant).Value.TrimStart('0');
        return string.IsNullOrWhiteSpace(digits) ? NormalizeRubricCode(code) : digits;
    }

    private static string NormalizeRubricDescriptionForCompare(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        var text = sb.ToString().ToUpperInvariant();
        text = Regex.Replace(text, @"[^A-Z0-9]+", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool TryParseBrazilianMoney(string value, out decimal result)
        => decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out result);

    private static MilitaryRecord? FindActiveMatchForMirror(SippesOmPaystubMirrorPerson person, IReadOnlyList<MilitaryRecord> active)
    {
        var cpf = MilitaryFormatting.Digits(person.Cpf);
        if (cpf.Length >= 10)
        {
            var match = active.FirstOrDefault(x => MilitaryFormatting.Digits(x.Cpf) == cpf);
            if (match is not null) return match;
        }

        var idt = MilitaryFormatting.Digits(person.MilitaryId);
        if (idt.Length >= 5)
        {
            var match = active.FirstOrDefault(x => MilitaryFormatting.Digits(x.MilitaryId) == idt || MilitaryFormatting.Digits(x.PrecCp) == idt);
            if (match is not null) return match;
        }

        var name = NormalizeForFilter(person.Name);
        if (name.Length >= 10)
        {
            var exact = active.FirstOrDefault(x => NormalizeForFilter(x.Name) == name);
            if (exact is not null) return exact;

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3).ToList();
            if (parts.Count >= 3)
            {
                return active.FirstOrDefault(x =>
                {
                    var n = NormalizeForFilter(x.Name);
                    return parts.Count(part => n.Contains(part, StringComparison.OrdinalIgnoreCase)) >= Math.Min(4, parts.Count);
                });
            }
        }

        return null;
    }

    private void UpdateMirrorSummary(SippesOmPaystubMirrorSummary? summary)
    {
        if (summary is null)
        {
            _currentMirrorSummary = null;
            MirrorSummaryText.Text = "Escolha mês/ano. Use Abrir manual + aguardar OK para salvar o Espelho OM do mês, ou Carregar mês para abrir uma conferência mensal já salva.";
            MirrorCountPill.Text = "Espelho: 0";
            MirrorOutsidePill.Text = "Fora dos ativos: 0";
            MirrorSavedPill.Text = "Contracheque salvo OK: 0";
            MirrorMissingPaystubPill.Text = "Sem salvo: 0";
            return;
        }

        MirrorSummaryText.Text = summary.Display + $" | Referência mensal salva | Gerado às {summary.GeneratedAt:dd/MM/yyyy HH:mm}" + (string.IsNullOrWhiteSpace(summary.HtmlPath) ? string.Empty : $" | Arquivo: {summary.HtmlPath}");
        MirrorCountPill.Text = $"Espelho: {summary.SippesCount}";
        MirrorOutsidePill.Text = $"Fora dos ativos: {summary.OutsideActiveCount}";
        MirrorSavedPill.Text = $"Contracheque salvo OK: {summary.SavedPaystubFoundCount}";
        MirrorMissingPaystubPill.Text = $"Sem salvo: {summary.SavedPaystubMissingCount}";
    }

    private async Task SaveLastMirrorConferenceAsync(int year, int month, SippesOmPaystubMirrorSummary summary)
    {
        var cache = new SippesOmPaystubMirrorCache
        {
            SavedAt = DateTime.Now,
            Year = year,
            Month = month,
            Summary = summary,
            Rows = _mirrorRows.ToList()
        };

        // Sempre salva em dois lugares:
        // 1) arquivo "última conferência", para abrir rápido;
        // 2) arquivo fixo por mês/ano, para manter histórico e fazer o batimento certo do mês depois.
        await App.Json.SaveAsync(App.Paths.SippesLastMirrorConferenceFile, cache);
        await App.Json.SaveAsync(GetMirrorMonthCachePath(year, month), cache);
    }

    private static string GetMirrorMonthDirectory(int year, int month)
    {
        var dir = Path.Combine(App.Paths.SippesEspelhoContrachequeOmDirectory, $"{year}_{month:00}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetMirrorMonthCachePath(int year, int month)
        => Path.Combine(GetMirrorMonthDirectory(year, month), $"conferencia_espelho_contracheque_om_{year}_{month:00}.json");

    private static string? FindLatestMirrorHtmlForMonth(int year, int month)
    {
        var dir = GetMirrorMonthDirectory(year, month);
        return Directory.EnumerateFiles(dir, $"Espelho_Contracheque_OM_{year}_{month:00}_*.html", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }

    private Task LoadMirrorCacheIntoGridAsync(SippesOmPaystubMirrorCache cache)
    {
        ReplaceRowsFast(MirrorGridCompact, _mirrorView, _mirrorRows, cache.Rows);
        _currentMirrorSummary = cache.Summary;
        UpdateMirrorSummary(cache.Summary);
        _mirrorView.Refresh();
        MirrorYearBox.Text = cache.Year.ToString(CultureInfo.InvariantCulture);
        MirrorMonthBox.SelectedIndex = Math.Clamp(cache.Month - 1, 0, 11);
        StatusText.Text = $"Espelho OM {cache.Month:00}/{cache.Year} carregado: {cache.Rows.Count} linha(s), salvo em {cache.SavedAt:dd/MM/yyyy HH:mm}.";
        CountText.Text = $"{cache.Rows.Count} linha(s) — Espelho OM {cache.Month:00}/{cache.Year}";
        AppendMirrorLiveLog(StatusText.Text);
        return Task.CompletedTask;
    }

    private bool FilterMirrorRow(object item)
    {
        if (item is not SippesOmPaystubMirrorConferenceRow row) return false;
        var query = MirrorSearchBox?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var haystack = string.Join(' ', row.Status, row.Severity, row.DisplayRank, row.DisplayName, row.DisplayCpf,
                row.ValueCheckStatus, row.SavedPaystubStatus, row.MirrorRubricsText, row.PaystubRubricsText);
            if (!NormalizeForFilter(haystack).Contains(NormalizeForFilter(query), StringComparison.OrdinalIgnoreCase)) return false;
        }

        var selected = (MirrorFilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
        return selected switch
        {
            "OUTSIDE" => row.Status.Contains("fora dos ativos", StringComparison.OrdinalIgnoreCase),
            "ACTIVE" => row.Status.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || row.Status.StartsWith("Ativo no Espelho", StringComparison.OrdinalIgnoreCase),
            "MISSING_PAYSTUB" => !row.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(row.SavedPaystubPath),
            "PAYSTUB_OK" => !string.IsNullOrWhiteSpace(row.SavedPaystubPath) && File.Exists(row.SavedPaystubPath),
            "ACTIVE_MISSING" => row.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private void MirrorFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_mirrorView is null) return;
        _mirrorView.Refresh();
        CountText.Text = _currentMirrorSummary is null
            ? $"{_mirrorView.Cast<object>().Count()} linha(s) filtrada(s) no Espelho OM"
            : $"{_mirrorView.Cast<object>().Count()} de {_mirrorRows.Count} linha(s) — Espelho OM gerado às {_currentMirrorSummary.GeneratedAt:dd/MM/yyyy HH:mm}";
    }

    private async void LoadLastMirror_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync("Carregando última conferência do Espelho OM…", async () =>
        {
            var cache = await App.Json.LoadAsync<SippesOmPaystubMirrorCache>(App.Paths.SippesLastMirrorConferenceFile);
            if (cache is null || cache.Rows.Count == 0)
                throw new InvalidOperationException("Ainda não existe conferência de Espelho OM salva para carregar.");
            await LoadMirrorCacheIntoGridAsync(cache);
        });
    }

    private async void LoadMirrorMonth_Click(object sender, RoutedEventArgs e)
    {
        var (year, month) = ReadMirrorReference();
        await RunUiAsync($"Carregando conferência salva do Espelho OM {month:00}/{year}…", async ct =>
        {
            var monthCachePath = GetMirrorMonthCachePath(year, month);
            var cache = await App.Json.LoadAsync<SippesOmPaystubMirrorCache>(monthCachePath);
            if (cache is not null && cache.Rows.Count > 0)
            {
                await LoadMirrorCacheIntoGridAsync(cache);
                return;
            }

            var latestHtml = FindLatestMirrorHtmlForMonth(year, month);
            if (string.IsNullOrWhiteSpace(latestHtml) || !File.Exists(latestHtml))
                throw new InvalidOperationException($"Ainda não existe conferência salva nem HTML do Espelho OM para {month:00}/{year}.");

            AppendMirrorLiveLog($"Cache mensal não encontrado. Relendo HTML salvo do mês: {Path.GetFileName(latestHtml)}");
            var result = await App.CpexPaystubs.ReadSavedSippesOmPaystubMirrorFileAsync(latestHtml, year, month, ct);
            await ApplyMirrorRowsAsync(result, ct);
        });
    }

    private void OpenMirrorFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.Paths.SippesEspelhoContrachequeOmDirectory);
        ShellService.OpenPath(App.Paths.SippesEspelhoContrachequeOmDirectory);
        StatusText.Text = $"Pasta do Espelho OM aberta: {App.Paths.SippesEspelhoContrachequeOmDirectory}";
    }

    private async void ExportMirrorCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_mirrorRows.Count == 0)
        {
            SigfurDialog.Show(this, "Gere ou carregue uma conferência do Espelho OM antes de exportar.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar conferência do Espelho de Contracheque da OM",
            Filter = "CSV|*.csv",
            FileName = $"Conferencia_Espelho_Contracheque_OM_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        var lines = new List<string> { "SITUACAO;GRAVIDADE;PG;NOME;CPF;VERIFICACAO_VALORES;RUBRICAS_ESPELHO;RUBRICAS_PDF;CONTRACHEQUE_SALVO" };
        lines.AddRange(_mirrorRows.Select(x => string.Join(';', new[]
        {
            SippesPersonnelConferenceService.CsvCell(x.Status),
            SippesPersonnelConferenceService.CsvCell(x.Severity),
            SippesPersonnelConferenceService.CsvCell(x.DisplayRank),
            SippesPersonnelConferenceService.CsvCell(x.DisplayName),
            SippesPersonnelConferenceService.CsvCell(x.DisplayCpf),
            SippesPersonnelConferenceService.CsvCell(x.ValueCheckStatus),
            SippesPersonnelConferenceService.CsvCell(x.DisplayMirrorRubricsText),
            SippesPersonnelConferenceService.CsvCell(x.DisplayPaystubRubricsText),
            SippesPersonnelConferenceService.CsvCell(string.IsNullOrWhiteSpace(x.SavedPaystubPath) ? x.SavedPaystubStatus : x.SavedPaystubPath)
        })));
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true));
        StatusText.Text = $"Conferência do Espelho OM exportada: {dialog.FileName}";
    }


    private async void CompareMirrorValues_Click(object sender, RoutedEventArgs e)
    {
        if (_mirrorRows.Count == 0)
        {
            SigfurDialog.Show(this, "Leia ou carregue o Espelho OM antes de bater rubricas e valores.", "SIGFUR — Espelho OM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunUiAsync("Batendo rubricas e valores do Espelho OM com os contracheques salvos do mês…", async ct =>
        {
            var (year, month) = ReadMirrorReference();
            var htmlPath = _currentMirrorSummary?.HtmlPath;
            if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath))
                htmlPath = _mirrorRows.Select(x => x.SourcePath).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
            if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath))
                throw new InvalidOperationException("Não localizei o HTML salvo do Espelho OM para reler as rubricas. Use 'Ler tela aberta agora' novamente ou carregue a última conferência salva.");

            AppendMirrorLiveLog($"Relendo Espelho OM salvo: {Path.GetFileName(htmlPath)}");
            var result = await App.CpexPaystubs.ReadSavedSippesOmPaystubMirrorFileAsync(htmlPath, year, month, ct);
            var active = await _repository.GetAllAsync();
            ct.ThrowIfCancellationRequested();
            await App.MilitaryPreferences.ApplyAsync(active);
            var (rows, summary) = await BuildMirrorConferenceRowsAsync(result, active, ct);
            await CompareMirrorRowsWithSavedPaystubsAsync(rows, month, year, ct);
            RecalculateMirrorSummary(summary, rows);

            ReplaceRowsFast(MirrorGridCompact, _mirrorView, _mirrorRows, rows, ct);
            _currentMirrorSummary = summary;
            UpdateMirrorSummary(summary);
            _mirrorView.Refresh();
            await SaveLastMirrorConferenceAsync(year, month, summary);

            var divergent = rows.Count(x => x.Severity == "CRÍTICO" || x.ValueCheckStatus.StartsWith("DIVERGENTE", StringComparison.OrdinalIgnoreCase));
            var ok = rows.Count(x => x.ValueCheckStatus.StartsWith("OK", StringComparison.OrdinalIgnoreCase));
            StatusText.Text = $"Batimento concluído: {ok} OK, {divergent} com divergência/pendência de valor, {summary.SavedPaystubMissingCount} sem contracheque salvo.";
            CountText.Text = $"{_mirrorRows.Count} linha(s) — rubricas e valores batidos com contracheques salvos {month:00}/{year}";
            AppendMirrorLiveLog(StatusText.Text);
        });
    }

    private static void RecalculateMirrorSummary(SippesOmPaystubMirrorSummary summary, IReadOnlyCollection<SippesOmPaystubMirrorConferenceRow> rows)
    {
        summary.ActiveMatchedCount = rows.Count(x => x.Status.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || x.Status.StartsWith("Ativo no Espelho", StringComparison.OrdinalIgnoreCase));
        summary.OutsideActiveCount = rows.Count(x => x.Status.Contains("fora dos ativos", StringComparison.OrdinalIgnoreCase));
        summary.SavedPaystubFoundCount = rows.Count(x => !string.IsNullOrWhiteSpace(x.SavedPaystubPath) && File.Exists(x.SavedPaystubPath));
        summary.SavedPaystubMissingCount = rows.Count(x => !x.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(x.SavedPaystubPath) || !File.Exists(x.SavedPaystubPath)));
        summary.ActiveMissingFromMirrorCount = rows.Count(x => x.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase));
    }

    private void ClearMirror_Click(object sender, RoutedEventArgs e)
    {
        _mirrorRows.Clear();
        UpdateMirrorSummary(null);
        _mirrorView.Refresh();
        StatusText.Text = "Resultado do Espelho OM limpo.";
    }

    private async void MirrorGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (VisualTreeUtilities.FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (MirrorGridCompact.SelectedItem is not SippesOmPaystubMirrorConferenceRow row) return;
        await OpenMirrorPaystubAsync(row);
    }

    private async void OpenMirrorPaystub_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: SippesOmPaystubMirrorConferenceRow row })
            await OpenMirrorPaystubAsync(row);
    }

    private async Task OpenMirrorPaystubAsync(SippesOmPaystubMirrorConferenceRow row)
    {
        if (_openingMirrorDocument) return;
        _openingMirrorDocument = true;
        try
        {
            await OpenMirrorPaystubCoreAsync(row);
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao abrir contracheque pelo Espelho OM.", ex);
            SigfurDialog.Show(this, $"Não foi possível abrir este contracheque.\n\n{ex.Message}", "SIGFUR — Espelho OM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _openingMirrorDocument = false;
        }
    }

    private async Task OpenMirrorPaystubCoreAsync(SippesOmPaystubMirrorConferenceRow row)
    {
        var (year, month) = ReadMirrorReference();
        row.SavedPaystubPath = await ResolveMirrorMonthlyPaystubPathAsync(row, month, year);
        _mirrorView.Refresh();
        if (string.IsNullOrWhiteSpace(row.SavedPaystubPath) || !File.Exists(row.SavedPaystubPath))
        {
            SigfurDialog.Show(this, "Não há contracheque PDF salvo do mês selecionado vinculado a esta linha.", "SIGFUR — Espelho OM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!FileOpenService.TryOpenFile(row.SavedPaystubPath, out var error))
        {
            SigfurDialog.Show(this, $"O PDF foi localizado, mas o Windows não conseguiu abri-lo.\n\n{error}\n\nArquivo:\n{row.SavedPaystubPath}", "SIGFUR — Abrir contracheque", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Não foi possível abrir o contracheque. Verifique o leitor de PDF padrão.";
            return;
        }
        StatusText.Text = $"Contracheque aberto: {Path.GetFileName(row.SavedPaystubPath)}";
    }

    private async void OpenMirrorSippesFicha_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: SippesOmPaystubMirrorConferenceRow row }) return;

        var path = row.SippesDadosMaPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = await FindSippesDadosMaPdfAsync(BuildMilitaryRecordForMirrorRow(row), null);
            row.SippesDadosMaPath = path;
            _mirrorView.Refresh();
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SigfurDialog.Show(this, "Não localizei ficha/dados do militar da ativa do SIPPES para esta linha.", "SIGFUR — Espelho OM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!FileOpenService.TryOpenFile(path, out var error))
        {
            SigfurDialog.Show(this, $"A ficha foi localizada, mas o Windows não conseguiu abri-la.\n\n{error}\n\nArquivo:\n{path}", "SIGFUR — Abrir ficha SIPPES", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StatusText.Text = $"Ficha SIPPES aberta: {Path.GetFileName(path)}";
    }

    private static MilitaryRecord BuildMilitaryRecordForMirrorRow(SippesOmPaystubMirrorConferenceRow row)
        => new()
        {
            Rank = !string.IsNullOrWhiteSpace(row.SigfurRank) ? row.SigfurRank : row.SippesRank,
            Name = !string.IsNullOrWhiteSpace(row.SigfurName) ? row.SigfurName : row.SippesName,
            WarName = row.SigfurWarName,
            Cpf = !string.IsNullOrWhiteSpace(row.SigfurCpf) ? row.SigfurCpf : row.SippesCpf,
            MilitaryId = !string.IsNullOrWhiteSpace(row.SigfurMilitaryId) ? row.SigfurMilitaryId : row.SippesMilitaryId,
            PrecCp = row.SippesPrecCp
        };

    private sealed record SippesDadosMaPdfIndexItem(string Path, string NormalizedPath, string Digits, DateTime LastWriteTime);

    private static Task<List<SippesDadosMaPdfIndexItem>> BuildSippesDadosMaPdfIndexAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var roots = new[] { App.Paths.SippesDadosMaDirectory, App.Paths.SippesDirectory }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (roots.Count == 0) return [];

            var candidates = new Dictionary<string, SippesDadosMaPdfIndexItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*.pdf*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var full = Path.GetFullPath(file);
                        if (!FileLooksLikePdf(full)) continue;
                        var normalized = NormalizeForFilter(full);
                        if (normalized.Contains("ESPELHO CONTRACHEQUE OM", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("ESPELHO DE CONTRACHEQUE", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("CONTRACHEQUE", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("FICHA FINANCEIRA", StringComparison.OrdinalIgnoreCase))
                            continue;
                        candidates[full] = new SippesDadosMaPdfIndexItem(full, normalized, MilitaryFormatting.Digits(normalized), SafeLastWriteTime(full));
                    }
                }
                catch
                {
                    // Pasta sem permissão ou arquivo bloqueado. A conferência continua com os demais caminhos.
                }
            }

            return candidates.Values.ToList();
        }, cancellationToken);

    private static async Task<string> FindSippesDadosMaPdfAsync(MilitaryRecord military, SippesOmPaystubMirrorPerson? person, CancellationToken cancellationToken = default)
    {
        var index = await BuildSippesDadosMaPdfIndexAsync(cancellationToken);
        return FindSippesDadosMaPdfFromIndex(index, military, person);
    }

    private static string FindSippesDadosMaPdfFromIndex(IReadOnlyList<SippesDadosMaPdfIndexItem> index, MilitaryRecord military, SippesOmPaystubMirrorPerson? person)
    {
        if (index.Count == 0) return string.Empty;
        var scored = index.Select(item => (item.Path, Score: ScoreSippesDadosMaPdf(item, military, person), item.LastWriteTime))
            .Where(x => x.Score >= 55)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.LastWriteTime)
            .FirstOrDefault();
        return scored.Path ?? string.Empty;
    }

    private static bool FileLooksLikePdf(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
               (name.Contains(".pdf.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    private static int ScoreSippesDadosMaPdf(SippesDadosMaPdfIndexItem item, MilitaryRecord military, SippesOmPaystubMirrorPerson? person)
    {
        var blob = item.NormalizedPath;
        var digits = item.Digits;
        var score = 0;
        if (blob.Contains("DADOS MILITAR ATIVA", StringComparison.OrdinalIgnoreCase) || blob.Contains("DADOS MA", StringComparison.OrdinalIgnoreCase)) score += 45;
        var cpfs = new[] { military.Cpf, person?.Cpf }.Select(MilitaryFormatting.Digits).Where(x => x.Length >= 10).Distinct().ToList();
        var idts = new[] { military.MilitaryId, person?.MilitaryId }.Select(MilitaryFormatting.Digits).Where(x => x.Length >= 5).Distinct().ToList();
        var precs = new[] { military.PrecCp, person?.PrecCp }.Select(MilitaryFormatting.Digits).Where(x => x.Length >= 5).Distinct().ToList();
        if (cpfs.Any(digits.Contains)) score += 110;
        if (idts.Any(digits.Contains)) score += 80;
        if (precs.Any(digits.Contains)) score += 65;
        var names = new[] { military.Name, person?.Name }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeForFilter).Distinct().ToList();
        foreach (var name in names)
        {
            if (blob.Contains(name, StringComparison.OrdinalIgnoreCase)) { score += 90; break; }
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3).ToList();
            if (parts.Count >= 2 && parts.Count(part => blob.Contains(part, StringComparison.OrdinalIgnoreCase)) >= Math.Min(3, parts.Count))
            {
                score += 60;
                break;
            }
        }
        return score;
    }

    private void OpenPdfFromName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SippesPersonnelConferenceRow row })
        {
            OpenPdfForRow(row);
            e.Handled = true;
        }
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is SippesPersonnelConferenceRow row)
        {
            OpenPdfForRow(row);
            e.Handled = true;
        }
    }

    private void OpenPdfForRow(SippesPersonnelConferenceRow row)
    {
        var pdfPath = ResolvePdfPathForRow(row);
        if (!string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath))
        {
            row.ReportPdfPath = pdfPath;
            if (string.IsNullOrWhiteSpace(row.ReportDownloadStatus) || row.ReportDownloadStatus.Contains("não", StringComparison.OrdinalIgnoreCase))
                row.ReportDownloadStatus = "PDF salvo localizado";
            _view.Refresh();

            try
            {
                ShellService.OpenPath(pdfPath);
                StatusText.Text = $"PDF aberto: {row.DisplayName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"PDF localizado, mas o Windows não conseguiu abrir automaticamente: {Path.GetFileName(pdfPath)}";
                SigfurDialog.Show(this,
                    "O PDF foi localizado, mas o Windows não conseguiu abrir automaticamente.\n\n" +
                    $"Arquivo: {pdfPath}\n\nErro: {ex.Message}",
                    "SIGFUR — PDF Dados MA", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShellService.RevealInExplorer(pdfPath);
            }
            return;
        }

        var message = string.IsNullOrWhiteSpace(row.ReportDownloadStatus)
            ? "Não há PDF vinculado para este militar. Baixe os PDFs novamente ou use 'Ler PDFs salvos' / 'Selecionar PDFs…'."
            : $"Não localizei o PDF deste militar. Status do PDF: {row.ReportDownloadStatus}";
        SigfurDialog.Show(this, message, "SIGFUR — PDF Dados MA", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string ResolvePdfPathForRow(SippesPersonnelConferenceRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ReportPdfPath) && File.Exists(row.ReportPdfPath))
            return row.ReportPdfPath;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            App.Paths.SippesDadosMaDirectory,
            App.Paths.SippesDirectory,
            App.Paths.PaystubsDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Downloads", "SIPPES"),
            Path.Combine(userProfile, "Downloads", "SIGFUR")
        }
        .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var cpf = MilitaryFormatting.Digits(row.DisplayCpf);
        var idt = MilitaryFormatting.Digits(row.DisplayMilitaryId);
        var nameKey = NormalizeForFilter(row.DisplayName);

        foreach (var root in roots)
        {
            try
            {
                var match = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
                    .Select(path => new { Path = path, Score = ScorePdfForRow(path, cpf, idt, nameKey) })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => File.GetLastWriteTime(x.Path))
                    .Select(x => x.Path)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match) && File.Exists(match)) return match;
            }
            catch
            {
                // Algumas pastas podem estar sem permissão ou com arquivo bloqueado. Ignora e tenta a próxima.
            }
        }

        return string.Empty;
    }

    private static int ScorePdfForRow(string path, string cpf, string idt, string nameKey)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var normalizedFile = NormalizeForFilter(fileName);
        var compactFile = Regex.Replace(normalizedFile, @"[^A-Z0-9]", string.Empty, RegexOptions.CultureInvariant);
        var looksLikeDadosMa = compactFile.Contains("DADOSMA", StringComparison.OrdinalIgnoreCase)
                              || (compactFile.Contains("DADOS", StringComparison.OrdinalIgnoreCase) && compactFile.Contains("SIPPES", StringComparison.OrdinalIgnoreCase))
                              || (compactFile.Contains("DADOS", StringComparison.OrdinalIgnoreCase) && compactFile.Contains("MILITAR", StringComparison.OrdinalIgnoreCase));
        var fileDigits = MilitaryFormatting.Digits(fileName);
        var score = looksLikeDadosMa ? 20 : 0;

        if (!string.IsNullOrWhiteSpace(cpf) && cpf.Length >= 9 && fileDigits.Contains(cpf, StringComparison.OrdinalIgnoreCase)) return score + 100;
        if (!string.IsNullOrWhiteSpace(idt) && idt.Length >= 5 && fileDigits.Contains(idt, StringComparison.OrdinalIgnoreCase)) return score + 80;

        if (!string.IsNullOrWhiteSpace(nameKey) && nameKey.Length >= 10 && normalizedFile.Contains(nameKey, StringComparison.OrdinalIgnoreCase)) return score + 70;

        var nameParts = nameKey.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3)
            .Take(5)
            .ToList();
        var hits = nameParts.Count(part => normalizedFile.Contains(part, StringComparison.OrdinalIgnoreCase));
        if (nameParts.Count >= 2 && hits >= 2) return score + (hits * 10);

        return 0;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        UpdateSummary(null);
        StatusText.Text = "Resultado limpo.";
    }

    private void OpenPdfFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.Paths.SippesDadosMaDirectory);
        ShellService.OpenPath(App.Paths.SippesDadosMaDirectory);
        StatusText.Text = $"Pasta oficial dos PDFs Dados MA aberta: {App.Paths.SippesDadosMaDirectory}";
    }

    private void OpenPortal_Click(object sender, RoutedEventArgs e)
    {
        ShellService.OpenPath(CpexPaystubAutomationService.SippesLoginUrl);
        StatusText.Text = "Portal SIPPES aberto no navegador padrão.";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts is null || _operationCts.IsCancellationRequested) return;
        _operationCts.Cancel();
        StatusText.Text = "Cancelando operação com segurança…";
        AppendMirrorLiveLog("Cancelamento solicitado. O SIGFUR vai parar no próximo ponto seguro.");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _operationCts?.Cancel();
            StatusText.Text = "Cancelando antes de fechar…";
            return;
        }
        Close();
    }

    private Task RunUiAsync(string message, Func<Task> action)
        => RunUiAsync(message, _ => action());

    private async Task RunUiAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true, message);
        try
        {
            await action(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operação cancelada. Nada foi alterado no cadastro.";
            AppendMirrorLiveLog("Operação cancelada pelo usuário.");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            AppendMirrorLiveLog("ERRO: " + ex.Message);
            try { await App.Log.WriteAsync("Falha inesperada na Conferência SIPPES.", ex); } catch { }
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Conferência SIPPES", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            _operationCts?.Dispose();
            _operationCts = null;
            _busy = false;
        }
    }

    private void SetBusy(bool value, string message)
    {
        BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RunButton.IsEnabled = !value;
        MirrorRunButton.IsEnabled = !value;
        StatusText.Text = message;
    }
}
