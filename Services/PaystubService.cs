using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using SIGFUR.Wpf.Models;

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

    private async Task<IReadOnlyList<PaystubFileRecord>> FindDocumentsForMilitaryAsync(MilitaryRecord military, bool financialStatements, CancellationToken cancellationToken)
    {
        var files = await GetPdfFilesAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<PaystubFileRecord>>(() =>
        {
            var result = new List<(int Score, PaystubFileRecord File)>();
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsFinancialStatement(path) != financialStatements) continue;
                var score = Score(path, military, null, null);
                if (score < 40) continue;
                try
                {
                    var info = new FileInfo(path);
                    result.Add((score, new PaystubFileRecord
                    {
                        Path = path,
                        ModifiedAt = info.LastWriteTime,
                        SizeBytes = info.Exists ? info.Length : 0,
                        Reference = DetectReference(path),
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
    /// Prioriza uma pasta escolhida pelo usuário e, se não encontrar nela, usa todas
    /// as raízes oficiais do SIGFUR. Isso deixa a auditoria previsível sem perder os
    /// contracheques já indexados em outras instalações.
    /// </summary>
    public async Task<string?> FindBestInDirectoryAsync(
        MilitaryRecord military,
        int month,
        int year,
        string? preferredDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(preferredDirectory) && Directory.Exists(preferredDirectory))
        {
            var direct = await Task.Run(() =>
            {
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ScanDirectory(preferredDirectory, found, maxDepth: 6, cancellationToken);
                return found.Where(path => !IsFinancialStatement(path)).ToList();
            }, cancellationToken);
            var preferred = await FindBestFromFilesAsync(direct, military, month, year, cancellationToken);
            if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        }
        return await FindBestAsync(military, month, year, cancellationToken);
    }

    public async Task<string?> FindBestOnlyInDirectoryAsync(
        MilitaryRecord military,
        int month,
        int year,
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        var files = await Task.Run(() =>
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanDirectory(directory, found, maxDepth: 1, cancellationToken);
            return found.Where(path => !IsFinancialStatement(path)).ToList();
        }, cancellationToken);
        return await FindBestFromFilesAsync(files, military, month, year, cancellationToken);
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

    public async Task<(int Exported, List<string> Failures)> ExportAsync(
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
                    exported++;
                }
                catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
            }
            return (Exported: exported, Failures: failures);
        }, cancellationToken);

        if (result.Failures.Count > 0)
        {
            var report = Path.Combine(destination, $"falhas_exportacao_contracheques_{month:00}_{year}.txt");
            await File.WriteAllLinesAsync(report,
                ["Falhas na exportação de contracheques", $"Referência: {month:00}/{year}", "", .. result.Failures],
                Encoding.UTF8,
                cancellationToken);
        }
        return (result.Exported, result.Failures);
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
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Índices das versões antigas guardavam caminhos completos para PDFs fora
        // da pasta padrão. Eles são lidos primeiro para preservar contracheques e
        // fichas financeiras já existentes sem varrer o disco inteiro.
        AddLegacyIndexedFiles(files);

        // Primeiro pesquisa somente as pastas oficiais e as que o usuário escolheu
        // nas telas de contracheque. Isso evita varrer Documentos/Downloads/Desktop
        // inteiros a cada consulta e reduz muito o uso de disco em computadores lentos.
        foreach (var directory in CandidateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanDirectory(directory, files, maxDepth: 5, cancellationToken);
            if (files.Count >= 6000) break;
        }

        // Compatibilidade com instalações antigas: só faz a pesquisa ampla quando
        // nenhuma pasta oficial/configurada possui PDFs.
        if (files.Count < 5)
        {
            foreach (var directory in BroadFallbackDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanDirectory(directory, files, maxDepth: 3, cancellationToken);
                if (files.Count >= 6000) break;
            }
        }

        return files.ToList();
    }

    private void AddLegacyIndexedFiles(HashSet<string> files)
    {
        var candidates = new[]
        {
            Path.Combine(_paths.DataDirectory, "contracheque_links.json"),
            Path.Combine(_paths.DataDirectory, "contracheque_pessoas_fora.json"),
            Path.Combine(_paths.DataDirectory, "paystub_external_people_wpf.json"),
            Path.Combine(_paths.DataDirectory, "paystub_center_wpf.json"),
            Path.Combine(_paths.DataDirectory, "lt_contracheque_settings_wpf.json"),
            Path.Combine(_paths.DataDirectory, "sippes_settings.json"),
            Path.Combine(_paths.DataDirectory, "cpex_paystub_settings.json")
        };

        foreach (var jsonPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(jsonPath)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                Visit(document.RootElement, Path.GetDirectoryName(jsonPath) ?? _paths.DataDirectory);
            }
            catch { }
        }

        void Visit(JsonElement element, string baseDirectory)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value)) return;
                value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (!value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    var full = Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(baseDirectory, value));
                    if (File.Exists(full)) files.Add(full);
                }
                catch { }
                return;
            }
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject()) Visit(property.Value, baseDirectory);
                return;
            }
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item, baseDirectory);
        }
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
                    foreach (var file in Directory.EnumerateFiles(current, "*.pdf", SearchOption.TopDirectoryOnly))
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
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Path.IsPathRooted(path)) return;
                roots.Add(Path.GetFullPath(path));
            }
            catch { }
        }

        Add(_paths.PaystubsDirectory);
        foreach (var sub in new[]
        {
            "ativos", "licenciados", "transferidos", "pessoas_fora", "pessoas_de_fora",
            "pessoas fora", "SIPPES", "SIAPPES", "CPEX", "CPEx", "lotes",
            "fichas_financeiras", "Fichas Financeiras", "ficha financeira", "financeiro", "downloads", "definitivo", "previas"
        })
            Add(Path.Combine(_paths.PaystubsDirectory, sub));

        foreach (var sub in new[]
        {
            "CPEX", "CPEx", "SIPPES", "SIAPPES", "downloads", "fichas_financeiras", "Fichas Financeiras", "ficha financeira",
            "contracheques_sippes", "contracheques_siappes", "contracheques_pessoas_fora"
        })
            Add(Path.Combine(_paths.DataDirectory, sub));

        foreach (var settingsFile in new[]
                 {
                     _paths.PaystubCenterSettingsFile,
                     _paths.CpexPaystubSettingsFile,
                     _paths.LicensedTransferredSettingsFile,
                     _paths.SippesSettingsFile
                 })
        {
            foreach (var configured in ReadConfiguredOutputDirectories(settingsFile)) Add(configured);
        }

        // Pastas legadas comuns são adicionadas de forma específica, sem pesquisar
        // o perfil inteiro. A busca ampla fica reservada ao fallback.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(profile, "Downloads");

        // Pasta oficial usada pelas versões Python e pelas máquinas do Furriel.
        // Incluímos tanto o caminho físico quanto o caminho retornado pelo Windows,
        // porque “Documentos” pode estar redirecionado para o OneDrive.
        Add(Path.Combine(profile, "Documents", "SIGFUR", "Contracheques"));
        Add(Path.Combine(documents, "SIGFUR", "Contracheques"));
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive)) Add(Path.Combine(oneDrive, "Documents", "SIGFUR", "Contracheques"));

        foreach (var baseDir in new[] { downloads, documents, desktop })
        {
            foreach (var sub in new[]
                     {
                         "contracheques", "Contracheques", "CPEX", "CPEx", "SIPPES",
                         "SIAPPES", "pdfs", "PDFs", "fichas_financeiras"
                     })
                Add(Path.Combine(baseDir, sub));
        }

        var existing = roots.Where(Directory.Exists).ToList();
        // Se uma raiz já contém outra, pesquisa apenas a mais alta para não ler a
        // mesma árvore várias vezes.
        existing = existing.Where(candidate => !existing.Any(other =>
                !candidate.Equals(other, StringComparison.OrdinalIgnoreCase)
                && IsSubPathOf(candidate, other)))
            .ToList();
        return existing
            .OrderByDescending(x => x.StartsWith(_paths.DataDirectory, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase);

        static bool IsSubPathOf(string candidate, string parent)
        {
            try
            {
                var child = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
                var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                return child.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    private static IEnumerable<string> BroadFallbackDirectories()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var values = new[]
        {
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        return values.Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadConfiguredOutputDirectories(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Visit(document.RootElement);
        }
        catch { }
        return result.ToList();

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var key = NormalizeProperty(property.Name);
                    if (property.Value.ValueKind == JsonValueKind.String && IsDirectoryProperty(key))
                    {
                        var value = property.Value.GetString();
                        if (LooksLikeLocalDirectory(value)) result.Add(value!);
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
        }

        static string NormalizeProperty(string value)
            => new(value.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                .Select(char.ToLowerInvariant).ToArray());

        static bool IsDirectoryProperty(string key)
            => key.Contains("outputdirectory", StringComparison.Ordinal)
               || key.Contains("outputfolder", StringComparison.Ordinal)
               || key.Contains("pastadestino", StringComparison.Ordinal)
               || key.Contains("pastasaida", StringComparison.Ordinal)
               || key.Contains("diretoriosaida", StringComparison.Ordinal)
               || key.Contains("destination", StringComparison.Ordinal)
               || key is "pasta" or "folder" or "directory" or "diretorio" or "caminho";

        static bool LooksLikeLocalDirectory(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile) return false;
            try { return Path.IsPathRooted(value); }
            catch { return false; }
        }
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

    private static int Score(string path, MilitaryRecord military, int? month, int? year)
    {
        var raw = $"{Path.GetFileName(path)} {Path.GetDirectoryName(path)}";
        var blob = Normalize(raw);
        var digits = MilitaryFormatting.Digits(blob);
        var score = 0;
        if (blob.Contains("contracheque") || blob.Contains("contra cheque")) score += 25;
        var cpf = MilitaryFormatting.Digits(military.Cpf);
        var prec = MilitaryFormatting.Digits(military.PrecCp);
        var idt = MilitaryFormatting.Digits(military.MilitaryId);
        if (cpf.Length >= 11 && digits.Contains(cpf)) score += 90;
        if (prec.Length >= 6 && digits.Contains(prec)) score += 70;
        if (idt.Length >= 6 && digits.Contains(idt)) score += 55;
        var name = Normalize(military.Name);
        var words = Regex.Matches(name, "[a-z0-9]+", RegexOptions.IgnoreCase).Select(x => x.Value).Where(x => x.Length >= 3).ToList();
        if (!string.IsNullOrWhiteSpace(name) && blob.Contains(name)) score += 85;
        else if (words.Count >= 2)
        {
            var hits = words.Count(blob.Contains);
            if (hits >= Math.Min(3, words.Count)) score += 55;
            else if (blob.Contains(words[0]) && blob.Contains(words[^1])) score += 45;
        }
        var refs = ExtractReferences(path);
        if (month.HasValue && year.HasValue)
        {
            if (refs.Count > 0 && !refs.Contains((month.Value, year.Value))) return -999999;
            if (refs.Contains((month.Value, year.Value))) score += 120;
            else
            {
                try { var date = File.GetLastWriteTime(path); if (date.Month == month && date.Year == year) score += 8; } catch { }
            }
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
        return text.Contains("ficha financeira")
               || text.Contains("fichas financeiras")
               || text.Contains("ficha_financeira")
               || text.Contains("fichafinanceira")
               || text.Contains("ficha-financeira")
               || text.Contains("financial statement")
               || text.Contains("fichas_financeiras")
               || (text.Contains("ficha") && text.Contains("financeira"));
    }

    private static string DetectReference(string path)
    {
        var first = ExtractReferences(path).OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).FirstOrDefault();
        return first.Year > 0 ? $"{first.Month:00}/{first.Year}" : "Não identificada";
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

    private static string UniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target)) return target;
        var name = Path.GetFileNameWithoutExtension(fileName); var ext = Path.GetExtension(fileName);
        for (var index = 2; ; index++) { target = Path.Combine(folder, $"{name} ({index}){ext}"); if (!File.Exists(target)) return target; }
    }
}
