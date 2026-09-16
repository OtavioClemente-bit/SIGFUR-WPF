using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;
using UglyToad.PdfPig;

namespace SIGFUR.Wpf.Services;

public sealed class PaystubService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private List<string> _cachedPdfs = [];
    private DateTime _cacheAt = DateTime.MinValue;

    public PaystubService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public Task<IReadOnlyList<PaystubFileRecord>> FindForMilitaryAsync(MilitaryRecord military, CancellationToken cancellationToken = default)
        => FindDocumentsForMilitaryAsync(military, financialStatements: false, cancellationToken);

    public Task<IReadOnlyList<PaystubFileRecord>> FindFinancialStatementsForMilitaryAsync(MilitaryRecord military, CancellationToken cancellationToken = default)
        => FindDocumentsForMilitaryAsync(military, financialStatements: true, cancellationToken);

    /// <summary>
    /// Retorna o contracheque da competência mais recente realmente salva para o militar.
    /// A data de alteração do arquivo serve apenas como desempate dentro da mesma competência.
    /// </summary>
    public async Task<PaystubFileRecord?> FindLatestSavedPaystubAsync(
        MilitaryRecord military,
        CancellationToken cancellationToken = default)
        => SelectLatestPaystub(await FindForMilitaryAsync(military, cancellationToken));

    /// <summary>
    /// Retorna a ficha financeira do ano mais recente realmente salvo para o militar.
    /// </summary>
    public async Task<PaystubFileRecord?> FindLatestSavedFinancialStatementAsync(
        MilitaryRecord military,
        CancellationToken cancellationToken = default)
        => SelectLatestFinancialStatement(await FindFinancialStatementsForMilitaryAsync(military, cancellationToken));

    public static PaystubFileRecord? SelectLatestPaystub(IEnumerable<PaystubFileRecord> records)
        => records
            .Select(record => (Record: record, Reference: ParseMonthReference(record.Reference)))
            .Where(item => item.Reference is not null && File.Exists(item.Record.Path))
            .OrderByDescending(item => item.Reference)
            .ThenByDescending(item => item.Record.ModifiedAt)
            .Select(item => item.Record)
            .FirstOrDefault();

    public static PaystubFileRecord? SelectLatestFinancialStatement(IEnumerable<PaystubFileRecord> records)
        => records
            .Select(record => (Record: record, Year: ParseReferenceYear(record.Reference)))
            .Where(item => item.Year is not null && File.Exists(item.Record.Path))
            .OrderByDescending(item => item.Year)
            .ThenByDescending(item => item.Record.ModifiedAt)
            .Select(item => item.Record)
            .FirstOrDefault();

    private static DateTime? ParseMonthReference(string? reference)
    {
        if (DateTime.TryParseExact(reference?.Trim(), "MM/yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return parsed;
        return null;
    }

    private static int? ParseReferenceYear(string? reference)
    {
        var match = Regex.Match(reference ?? string.Empty, @"(?<!\d)(20\d{2})(?!\d)");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    private async Task<IReadOnlyList<PaystubFileRecord>> FindDocumentsForMilitaryAsync(MilitaryRecord military, bool financialStatements, CancellationToken cancellationToken)
    {
        var files = await GetPdfFilesAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<PaystubFileRecord>>(() =>
        {
            var result = new List<(int Score, PaystubFileRecord File)>();
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Performance: na carteira a aba "Ficha Financeira" não pode abrir/ler todos os PDFs.
                // A classificação principal usa somente nome/caminho. Os arquivos salvos pelo SIGFUR
                // já recebem nome/pasta de ficha financeira, então a busca fica instantânea após baixar.
                // Leitura interna do PDF fica reservada apenas para buscas pontuais de "melhor arquivo".
                var isFinancialStatement = IsFinancialStatement(path);
                if (isFinancialStatement != financialStatements) continue;
                var score = financialStatements
                    ? ScoreFinancialStatement(path, military, year: null)
                    : Score(path, military, null, null);
                if (score < 40) continue;
                try
                {
                    var info = new FileInfo(path);
                    result.Add((score, new PaystubFileRecord
                    {
                        Path = path,
                        ModifiedAt = info.LastWriteTime,
                        SizeBytes = info.Exists ? info.Length : 0,
                        Reference = financialStatements ? DetectFinancialStatementReference(path) : DetectReference(path),
                        DocumentType = financialStatements ? "Ficha Financeira" : "Contracheque"
                    }));
                }
                catch { }
            }
            return result.OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.File.ModifiedAt)
                .Select(x => x.File)
                .Take(500)
                .ToList();
        }, cancellationToken);
    }

    public async Task<string?> FindBestAsync(MilitaryRecord military, int month, int year, CancellationToken cancellationToken = default)
    {
        var files = (await GetPdfFilesAsync(cancellationToken)).Where(path => !IsFinancialStatement(path)).ToList();
        return await FindBestFromFilesAsync(files, military, month, year, cancellationToken);
    }

    /// <summary>
    /// Usa apenas a pasta oficial do SIGFUR em AppData para evitar duplicidade
    /// entre instalações antigas e novas.
    /// </summary>
    public async Task<string?> FindBestInDirectoryAsync(
        MilitaryRecord military,
        int month,
        int year,
        string? preferredDirectory,
        CancellationToken cancellationToken = default)
        => await FindBestAsync(military, month, year, cancellationToken);

    public async Task<string?> FindBestOnlyInDirectoryAsync(
        MilitaryRecord military,
        int month,
        int year,
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (!IsUnderPaystubsDirectory(directory)) return null;
        var files = await Task.Run(() =>
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanDirectory(directory, found, maxDepth: 10, cancellationToken);
            return found.Where(path => !IsFinancialStatement(path)).ToList();
        }, cancellationToken);
        return await FindBestFromFilesAsync(files, military, month, year, cancellationToken);
    }

    public async Task<string?> FindBestFinancialStatementInDirectoryAsync(
        MilitaryRecord military,
        int year,
        string? preferredDirectory,
        CancellationToken cancellationToken = default)
    {
        var files = (await GetPdfFilesAsync(cancellationToken)).Where(IsFinancialStatementFile).ToList();
        return await FindBestFinancialStatementFromFilesAsync(files, military, year, cancellationToken);
    }

    private bool IsUnderPaystubsDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var root = Path.GetFullPath(_paths.PaystubsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static Task<string?> FindBestFromFilesAsync(
        IReadOnlyList<string> files,
        MilitaryRecord military,
        int month,
        int year,
        CancellationToken cancellationToken)
        => Task.Run(() => files
            .Select(path => (Path: path, Score: Score(path, military, month, year)))
            .Where(x => x.Score >= 110)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => SafeLastWriteTime(x.Path))
            .Select(x => x.Path)
            .FirstOrDefault(), cancellationToken);

    private static Task<string?> FindBestFinancialStatementFromFilesAsync(
        IReadOnlyList<string> files,
        MilitaryRecord military,
        int year,
        CancellationToken cancellationToken)
        => Task.Run(() => files
            .Select(path => (Path: path, Score: ScoreFinancialStatement(path, military, year)))
            .Where(x => x.Score >= 110)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => SafeLastWriteTime(x.Path))
            .Select(x => x.Path)
            .FirstOrDefault(), cancellationToken);

    public async Task<(int Exported, List<string> Failures, List<string> Files)> ExportAsync(
        IEnumerable<MilitaryRecord> military,
        int month,
        int year,
        string destination,
        IProgress<(int Current, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = military.ToList();
        var files = (await GetPdfFilesAsync(cancellationToken)).Where(path => !IsFinancialStatement(path)).ToList();
        var result = await Task.Run(() =>
        {
            Directory.CreateDirectory(destination);
            var failures = new List<string>();
            var exportedFiles = new List<string>();
            var exported = 0;
            for (var index = 0; index < list.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = list[index];
                progress?.Report((index + 1, list.Count, item.Name));
                var source = files.Select(path => (Path: path, Score: Score(path, item, month, year)))
                    .Where(x => x.Score >= 110)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => SafeLastWriteTime(x.Path))
                    .Select(x => x.Path)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(source))
                {
                    failures.Add($"{item.Name}: PDF não encontrado para {month:00}/{year}.");
                    continue;
                }
                try
                {
                    var fileName = SafeFileName($"{item.Name} - Contracheque - {month:00}-{year}.pdf");
                    var target = Path.Combine(destination, fileName);
                    if (!SamePath(source, target)) File.Copy(source, target, overwrite: true);
                    exportedFiles.Add(target);
                    exported++;
                }
                catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
            }
            return (Exported: exported, Failures: failures, Files: exportedFiles);
        }, cancellationToken);

        if (result.Failures.Count > 0)
        {
            var report = Path.Combine(destination, $"falhas_exportacao_contracheques_{month:00}_{year}.txt");
            await File.WriteAllLinesAsync(report,
                ["Falhas na exportação de contracheques", $"Referência: {month:00}/{year}", "", .. result.Failures],
                Encoding.UTF8,
                cancellationToken);
        }
        return (result.Exported, result.Failures, result.Files);
    }

    public void InvalidateCache() => _cacheAt = DateTime.MinValue;

    private async Task<IReadOnlyList<string>> GetPdfFilesAsync(CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedPdfs.Count > 0 && DateTime.Now - _cacheAt < TimeSpan.FromMinutes(10)) return _cachedPdfs;
            _cachedPdfs = await Task.Run(() => ScanPdfFiles(cancellationToken), cancellationToken);
            _cacheAt = DateTime.Now;
            return _cachedPdfs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _log.WriteAsync("Falha pesquisando contracheques salvos.", ex);
            return [];
        }
        finally { _cacheGate.Release(); }
    }

    private List<string> ScanPdfFiles(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.PaystubsDirectory);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Fonte única: AppData\Local\SIGFUR\contracheques.
        // Não lê mais Documentos, Downloads, Desktop, OneDrive nem caminhos antigos
        // gravados em JSON, evitando contracheque duplicado e resultado divergente.
        ScanDirectory(_paths.PaystubsDirectory, files, maxDepth: 10, cancellationToken);

        return files.ToList();
    }

    private static void ScanDirectory(
        string directory,
        HashSet<string> files,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootDepth = root.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0 && files.Count < 6000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                var currentDepth = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) - rootDepth;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(current, "*.pdf*", SearchOption.TopDirectoryOnly)
                                 .Where(LooksLikePdfFile))
                    {
                        files.Add(Path.GetFullPath(file));
                        if (files.Count >= 6000) break;
                    }

                    if (currentDepth >= maxDepth || files.Count >= 6000) continue;
                    foreach (var sub in Directory.EnumerateDirectories(current))
                    {
                        var name = Path.GetFileName(sub);
                        if (ShouldSkipDirectory(name)) continue;
                        pending.Push(sub);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private IEnumerable<string> CandidateDirectories()
    {
        Directory.CreateDirectory(_paths.PaystubsDirectory);
        return [_paths.PaystubsDirectory];
    }

    private static bool ShouldSkipDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return true;
        return name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AppData", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OneDriveTemp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePdfFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Contains(".pdf.", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(string path, MilitaryRecord military, int? month, int? year)
    {
        if (IsFinancialStatement(path) || LooksLikeNonPaystubSippesReport(path)) return -999999;
        var raw = $"{Path.GetFileName(path)} {Path.GetDirectoryName(path)}";
        var blob = Normalize(raw);
        var digits = MilitaryFormatting.Digits(blob);
        var score = 0;
        var identityScore = 0;
        if (blob.Contains("contracheque") || blob.Contains("contra cheque")) score += 25;
        var cpf = MilitaryFormatting.Digits(military.Cpf);
        var prec = MilitaryFormatting.Digits(military.PrecCp);
        var idt = MilitaryFormatting.Digits(military.MilitaryId);
        if (cpf.Length >= 11 && digits.Contains(cpf)) { score += 90; identityScore += 90; }
        if (prec.Length >= 6 && digits.Contains(prec)) { score += 70; identityScore += 70; }
        if (idt.Length >= 6 && digits.Contains(idt)) { score += 55; identityScore += 55; }
        var name = Normalize(military.Name);
        var words = Regex.Matches(name, "[a-z0-9]+", RegexOptions.IgnoreCase).Select(x => x.Value).Where(x => x.Length >= 3).ToList();
        if (!string.IsNullOrWhiteSpace(name) && blob.Contains(name)) { score += 85; identityScore += 85; }
        else if (words.Count >= 2)
        {
            var hits = words.Count(blob.Contains);
            if (hits >= Math.Min(3, words.Count)) { score += 55; identityScore += 55; }
            else if (blob.Contains(words[0]) && blob.Contains(words[^1])) { score += 45; identityScore += 45; }
        }
        var refs = ExtractReferences(path);
        if (month.HasValue && year.HasValue)
        {
            if (identityScore < 35) return -999999;
            if (refs.Count > 0 && !refs.Contains((month.Value, year.Value))) return -999999;
            if (refs.Contains((month.Value, year.Value))) score += 120;
            else
            {
                try { var date = File.GetLastWriteTime(path); if (date.Month == month && date.Year == year) score += 8; } catch { }
            }
        }
        return score;
    }

    private static int ScoreFinancialStatement(string path, MilitaryRecord military, int? year)
    {
        if (!IsFinancialStatementFile(path)) return -999999;

        var raw = $"{Path.GetFileName(path)} {Path.GetDirectoryName(path)}";
        var blob = Normalize(raw);
        // Não lê o conteúdo do PDF aqui. Ler preview de cada ficha/contracheque travava a carteira.
        // A vinculação é feita por nome/pasta/CPF/PREC/IDT, que é exatamente como o SIGFUR salva os arquivos.
        return ScoreFinancialStatementBlob(blob, military, year);
    }

    private static int ScoreFinancialStatementBlob(string blob, MilitaryRecord military, int? year)
    {
        var digits = MilitaryFormatting.Digits(blob);
        var score = 35;
        var identityScore = 0;
        if ((blob.Contains("ficha") && blob.Contains("financeira")) || blob.Contains("financial statement")) score += 20;

        var cpf = MilitaryFormatting.Digits(military.Cpf);
        var prec = MilitaryFormatting.Digits(military.PrecCp);
        var idt = MilitaryFormatting.Digits(military.MilitaryId);
        if (cpf.Length >= 11 && digits.Contains(cpf)) { score += 95; identityScore += 95; }
        if (prec.Length >= 6 && digits.Contains(prec)) { score += 70; identityScore += 70; }
        if (idt.Length >= 6 && digits.Contains(idt)) { score += 55; identityScore += 55; }

        var name = Normalize(military.Name);
        var words = Regex.Matches(name, "[a-z0-9]+", RegexOptions.IgnoreCase).Select(x => x.Value).Where(x => x.Length >= 3).ToList();
        if (!string.IsNullOrWhiteSpace(name) && blob.Contains(name)) { score += 85; identityScore += 85; }
        else if (words.Count >= 2)
        {
            var hits = words.Count(blob.Contains);
            if (hits >= Math.Min(3, words.Count)) { score += 55; identityScore += 55; }
            else if (blob.Contains(words[0]) && blob.Contains(words[^1])) { score += 45; identityScore += 45; }
        }

        if (identityScore < 35) return -999999;

        if (year.HasValue)
        {
            var fullYear = year.Value.ToString(CultureInfo.InvariantCulture);
            var hasYear = Regex.IsMatch(blob, $@"(?<!\d){Regex.Escape(fullYear)}(?!\d)");
            if (!hasYear) return -999999;
            score += 120;
        }
        else if (Regex.IsMatch(blob, @"(?<!\d)20\d{2}(?!\d)"))
        {
            score += 10;
        }

        return score;
    }

    private static HashSet<(int Month, int Year)> ExtractReferences(string path)
    {
        var result = new HashSet<(int, int)>();
        var text = Normalize(Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match match in Regex.Matches(text, @"(?<!\d)(0?[1-9]|1[0-2])\s*[-_/\.]\s*((?:20)?\d{2})(?!\d)"))
        {
            var month = int.Parse(match.Groups[1].Value); var year = NormalizeYear(match.Groups[2].Value); if (year > 0) result.Add((month, year));
        }
        foreach (Match match in Regex.Matches(text, @"(?<!\d)((?:20)?\d{2})\s*[-_/\.]\s*(0?[1-9]|1[0-2])(?!\d)"))
        {
            var year = NormalizeYear(match.Groups[1].Value); var month = int.Parse(match.Groups[2].Value); if (year > 0) result.Add((month, year));
        }

        // Reconhece nomes como "JUNHO - 2026.pdf", além dos formatos numéricos.
        var monthPattern = "janeiro|jan|fevereiro|fev|marco|mar|abril|abr|maio|mai|junho|jun|julho|jul|agosto|ago|setembro|set|outubro|out|novembro|nov|dezembro|dez";
        foreach (Match match in Regex.Matches(text, $@"\b({monthPattern})\b\s*(?:[-_/\.]|de)?\s*((?:20)?\d{{2}})\b", RegexOptions.IgnoreCase))
        {
            var month = MonthNumber(match.Groups[1].Value);
            var year = NormalizeYear(match.Groups[2].Value);
            if (month > 0 && year > 0) result.Add((month, year));
        }
        foreach (Match match in Regex.Matches(text, $@"\b((?:20)?\d{{2}})\b\s*(?:[-_/\.]|de)?\s*\b({monthPattern})\b", RegexOptions.IgnoreCase))
        {
            var year = NormalizeYear(match.Groups[1].Value);
            var month = MonthNumber(match.Groups[2].Value);
            if (month > 0 && year > 0) result.Add((month, year));
        }
        return result;
    }

    private static int MonthNumber(string value)
    {
        var key = Normalize(value).Trim();
        return key switch
        {
            "janeiro" or "jan" => 1,
            "fevereiro" or "fev" => 2,
            "marco" or "mar" => 3,
            "abril" or "abr" => 4,
            "maio" or "mai" => 5,
            "junho" or "jun" => 6,
            "julho" or "jul" => 7,
            "agosto" or "ago" => 8,
            "setembro" or "set" => 9,
            "outubro" or "out" => 10,
            "novembro" or "nov" => 11,
            "dezembro" or "dez" => 12,
            _ => 0
        };
    }

    private static int NormalizeYear(string value)
    {
        if (!int.TryParse(value, out var year)) return 0;
        if (year < 100) year += 2000;
        return year is >= 2000 and <= 2100 ? year : 0;
    }

    private static bool IsFinancialStatement(string path)
    {
        var text = Normalize($"{Path.GetFileName(path)} {Path.GetDirectoryName(path)}");
        return LooksLikeFinancialStatementText(text);
    }

    private static bool IsFinancialStatementFile(string path)
    {
        // Rápido por padrão: não abre PDF durante carregamento de listas.
        return IsFinancialStatement(path);
    }

    private static bool LooksLikeFinancialStatementText(string text)
    {
        return text.Contains("ficha financeira")
               || text.Contains("fichas financeiras")
               || text.Contains("ficha_financeira")
               || text.Contains("fichafinanceira")
               || text.Contains("ficha-financeira")
               || text.Contains("financial statement")
               || text.Contains("fichas_financeiras")
               || (text.Contains("ficha") && text.Contains("financeira"));
    }

    private static bool LooksLikeNonPaystubSippesReport(string path)
    {
        var text = Normalize($"{Path.GetFileName(path)} {Path.GetDirectoryName(path)}");
        return text.Contains("dados militar ativa")
               || text.Contains("dados_militar_ativa")
               || text.Contains("dados ma")
               || text.Contains("dados sippes")
               || text.Contains("relatorio dados")
               || text.Contains("espelho contracheque om")
               || text.Contains("espelho_contracheque_om")
               || text.Contains("espelho de contracheque")
               || text.Contains("conferencia espelho");
    }

    private static string DetectReference(string path)
    {
        var first = ExtractReferences(path).OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).FirstOrDefault();
        return first.Year > 0 ? $"{first.Month:00}/{first.Year}" : "Não identificada";
    }

    private static string DetectFinancialStatementReference(string path)
    {
        var monthly = DetectReference(path);
        if (!monthly.Equals("Não identificada", StringComparison.OrdinalIgnoreCase)) return monthly;

        var text = Normalize($"{Path.GetFileName(path)} {Path.GetDirectoryName(path)}");
        var years = Regex.Matches(text, @"(?<!\d)(20\d{2})(?!\d)")
            .Select(match => int.TryParse(match.Groups[1].Value, out var year) ? year : 0)
            .Where(year => year is >= 2000 and <= 2200)
            .Distinct()
            .OrderByDescending(year => year)
            .ToList();

        return years.Count > 0 ? years[0].ToString(CultureInfo.InvariantCulture) : "Não identificada";
    }

    private static string ReadPdfTextPreview(string path)
    {
        try
        {
            var builder = new StringBuilder();
            using var pdf = PdfDocument.Open(path);
            foreach (var page in pdf.GetPages().Take(2))
            {
                builder.AppendLine(page.Text ?? string.Empty);
                if (builder.Length >= 20000) break;
            }
            return builder.ToString();
        }
        catch { return string.Empty; }
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(clean) ? "Contracheque.pdf" : clean;
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool IsSubPathOf(string candidate, string parent)
    {
        try
        {
            var child = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string UniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target)) return target;
        var name = Path.GetFileNameWithoutExtension(fileName); var ext = Path.GetExtension(fileName);
        for (var index = 2; ; index++) { target = Path.Combine(folder, $"{name} ({index}){ext}"); if (!File.Exists(target)) return target; }
    }
}
