using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Biblioteca permanente dos boletins externos da 4ª RM e do CML.
/// No Boletim Regional a 1ª Parte (Serviços Diários) é descartada antes da pesquisa.
/// No Aditamento CML o leitor destaca o bloco de autorização/liberação de pagamento.
/// </summary>
public sealed class ExternalBulletinService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly PdfTextService _pdfText;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly Regex RegionalNumberRegex = new(@"BOLETIM\s+REGIONAL\s+N[º°O]?\s*([0-9]{1,4}/[0-9]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CmlHeaderRegex = new(@"ADITAMENTO\s+(?<secao>.+?)\s+N[º°O]?\s*(?<adt>[0-9]{1,4}/[0-9]{4})\s+BI\s+N[º°O]?\s*(?<bi>[0-9]{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex NumericDateRegex = new(@"\b(?<d>\d{1,2})[/-](?<m>\d{1,2})[/-](?<y>20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex LongDateRegex = new(@"\b(?<d>\d{1,2})\s+de\s+(?<m>[A-Za-zÀ-ÿ]+)\s+de\s+(?<y>20\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"R\$\s*[0-9.]+(?:,[0-9]{2})?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EventRegex = new(@"Descri[cç][aã]o\s+do\s+Evento\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DurationRegex = new(@"Dura[cç][aã]o\s+em\s+dias\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PersonnelRegex = new(@"Efetivo\s+autorizado\s+por\s+posto\s+e\s+gradua[cç][aã]o\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ExternalBulletinService(AppPaths paths, JsonFileService json, PdfTextService pdfText, LogService log)
    {
        _paths = paths;
        _json = json;
        _pdfText = pdfText;
        _log = log;
        Directory.CreateDirectory(_paths.ExternalBulletinDirectory);
        Directory.CreateDirectory(_paths.ExternalBulletinRegionDirectory);
        Directory.CreateDirectory(_paths.ExternalBulletinCmlDirectory);
    }

    public async Task<ExternalBulletinStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await _json.LoadAsync<ExternalBulletinStore>(_paths.ExternalBulletinIndexFile) ?? new ExternalBulletinStore();
            store.Items ??= [];
            foreach (var item in store.Items)
            {
                item.Mentions ??= [];
                item.StoredPath = ResolvePath(item.StoredPath);
                foreach (var mention in item.Mentions)
                {
                    mention.PdfPath = item.StoredPath;
                    mention.FileId = item.Id;
                    mention.Kind = item.Kind;
                    mention.Bulletin = item.DisplayNumber;
                    mention.BulletinDate = item.DisplayDate;
                }
            }
            return store;
        }
        finally { _gate.Release(); }
    }

    public async Task<ExternalBulletinSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        => await _json.LoadAsync<ExternalBulletinSettings>(_paths.ExternalBulletinSettingsFile) ?? new ExternalBulletinSettings();

    public Task SaveSettingsAsync(ExternalBulletinSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(_paths.ExternalBulletinSettingsFile, settings);

    public async Task<ExternalBulletinImportResult> ImportAsync(
        string kind,
        IEnumerable<string> sources,
        string searchTerm,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        searchTerm = CleanSearchTerm(searchTerm);
        if (string.IsNullOrWhiteSpace(searchTerm)) throw new InvalidOperationException("Informe o nome ou a OM que deve ser pesquisada.");

        var result = new ExternalBulletinImportResult();
        var pdfs = ExpandPdfSources(sources).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pdfs.Count == 0) return result;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            for (var index = 0; index < pdfs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = pdfs[index];
                progress?.Report($"Lendo {index + 1}/{pdfs.Count}: {Path.GetFileName(source)}");
                try
                {
                    var hash = await HashFileAsync(source, cancellationToken);
                    var sameHash = store.Items.FirstOrDefault(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && x.HashSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    if (sameHash is not null)
                    {
                        if (!Normalize(sameHash.IndexedSearchTerm).Equals(Normalize(searchTerm), StringComparison.Ordinal))
                        {
                            var pagesForTerm = await _pdfText.ExtractPagesAsync(sameHash.StoredPath, cancellationToken);
                            ApplyParse(sameHash, pagesForTerm, searchTerm);
                            result.Updated++;
                        }
                        else result.Duplicates++;
                        continue;
                    }

                    var pages = await _pdfText.ExtractPagesAsync(source, cancellationToken);
                    var parsed = ParseFile(kind, source, pages, searchTerm, hash);
                    parsed.SourceFolder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? string.Empty;
                    parsed.OriginalFileName = Path.GetFileName(source);
                    parsed.ImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr);
                    parsed.SizeBytes = new FileInfo(source).Length;

                    var existing = store.Items.FirstOrDefault(x =>
                        x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                        x.DisplayNumber != "—" &&
                        Normalize(x.DisplayNumber).Equals(Normalize(parsed.DisplayNumber), StringComparison.Ordinal) &&
                        Normalize(x.DisplayDate).Equals(Normalize(parsed.DisplayDate), StringComparison.Ordinal));

                    var destination = existing is null ? CreateDestination(parsed) : existing.StoredPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                        File.Copy(source, destination, true);
                    parsed.StoredPath = destination;
                    BindMentionPaths(parsed);

                    if (existing is null)
                    {
                        store.Items.Add(parsed);
                        result.Imported++;
                    }
                    else
                    {
                        parsed.Id = existing.Id;
                        BindMentionPaths(parsed);
                        store.Items[store.Items.IndexOf(existing)] = parsed;
                        result.Updated++;
                    }
                    if (parsed.MentionCount == 0) result.WithoutMention++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                    await _log.WriteAsync($"Falha ao importar boletim externo: {source}", ex);
                }
            }

            SortStore(store);
            await WriteStoreUnsafeAsync(store, cancellationToken);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ReindexAllAsync(string searchTerm, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        searchTerm = CleanSearchTerm(searchTerm);
        if (string.IsNullOrWhiteSpace(searchTerm)) throw new InvalidOperationException("Informe o nome ou a OM que deve ser pesquisada.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            for (var index = 0; index < store.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = store.Items[index];
                progress?.Report($"Relendo {index + 1}/{store.Items.Count}: {item.OriginalFileName}");
                if (!File.Exists(item.StoredPath)) continue;
                try
                {
                    var pages = await _pdfText.ExtractPagesAsync(item.StoredPath, cancellationToken);
                    ApplyParse(item, pages, searchTerm);
                }
                catch (Exception ex)
                {
                    await _log.WriteAsync($"Falha ao reindexar boletim externo: {item.StoredPath}", ex);
                }
            }
            SortStore(store);
            await WriteStoreUnsafeAsync(store, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string id, bool deleteStoredPdf, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var item = store.Items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            store.Items.Remove(item);
            await WriteStoreUnsafeAsync(store, cancellationToken);
            if (deleteStoredPdf)
            {
                try { if (File.Exists(item.StoredPath)) File.Delete(item.StoredPath); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    public void OpenPdf(ExternalBulletinMention mention)
    {
        if (mention is null) return;
        OpenPdf(mention.PdfPath, mention.PdfSearchTerm, mention.DocumentOccurrence);
    }

    public void OpenPdf(ExternalBulletinFile file, string searchTerm = "")
        => OpenPdf(file.StoredPath, searchTerm, 1);

    public void OpenPdf(string path, string searchTerm, int occurrence)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("O PDF do boletim não foi encontrado.", path);

        ShellService.OpenPath(path);
        var term = CleanSearchTerm(searchTerm);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(term)) return;
        try { Clipboard.SetText(term); } catch { }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1850);
            SendCtrlFAndPaste();
            await Task.Delay(650);
            PressKey(VkEscape);
            await Task.Delay(120);
            for (var index = 1; index < Math.Max(1, occurrence); index++)
            {
                PressKey(VkF3);
                await Task.Delay(105);
            }
        });
    }

    private ExternalBulletinFile ParseFile(string kind, string source, IReadOnlyList<string> pages, string searchTerm, string hash)
    {
        var item = new ExternalBulletinFile
        {
            Id = hash[..Math.Min(20, hash.Length)],
            HashSha256 = hash,
            Kind = kind,
            BulletinType = ExternalBulletinKinds.DisplayName(kind),
            OriginalFileName = Path.GetFileName(source),
            Pages = pages.Count
        };
        ApplyMetadata(item, pages, source);
        ApplyParse(item, pages, searchTerm);
        return item;
    }

    private void ApplyParse(ExternalBulletinFile item, IReadOnlyList<string> pages, string searchTerm)
    {
        item.Pages = pages.Count;
        item.IndexedSearchTerm = searchTerm;
        item.Mentions = ParseMentions(item, pages, searchTerm, out var ignored);
        item.IgnoredFirstPartMentions = ignored;
        BindMentionPaths(item);
    }

    private List<ExternalBulletinMention> ParseMentions(ExternalBulletinFile file, IReadOnlyList<string> pages, string searchTerm, out int ignoredFirstPart)
    {
        var result = new List<ExternalBulletinMention>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = Normalize(searchTerm);
        var regionContentStarted = file.Kind != ExternalBulletinKinds.Region;
        var documentOccurrence = 0;
        ignoredFirstPart = 0;

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = CleanPage(pages[pageIndex]);
            var lines = page.Split('\n').Select(OneLine).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var searchableFrom = 0;

            if (file.Kind == ExternalBulletinKinds.Region && !regionContentStarted)
            {
                var markerIndex = lines.FindIndex(IsAfterDailyServicesMarker);
                if (markerIndex >= 0)
                {
                    regionContentStarted = true;
                    searchableFrom = markerIndex + 1;
                }
                else searchableFrom = lines.Count;
            }

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                var occurrencesInLine = CountOccurrences(Normalize(line), target);
                if (occurrencesInLine == 0) continue;

                for (var occurrenceInLine = 0; occurrenceInLine < occurrencesInLine; occurrenceInLine++)
                {
                    documentOccurrence++;
                    if (file.Kind == ExternalBulletinKinds.Region && (!regionContentStarted || lineIndex < searchableFrom))
                    {
                        ignoredFirstPart++;
                        continue;
                    }

                    var mention = BuildMention(file, lines, lineIndex, pageIndex + 1, searchTerm, documentOccurrence);
                    var key = $"{mention.Page}|{mention.DocumentOccurrence}|{Normalize(mention.MatchLine)}";
                    if (seen.Add(key)) result.Add(mention);
                }
            }
        }
        return result;
    }

    private ExternalBulletinMention BuildMention(ExternalBulletinFile file, IReadOnlyList<string> lines, int lineIndex, int page, string searchTerm, int occurrence)
    {
        var isCml = file.Kind == ExternalBulletinKinds.Cml;
        var before = isCml ? 22 : 9;
        var after = isCml ? 5 : 8;
        var contextLines = lines.Skip(Math.Max(0, lineIndex - before)).Take(Math.Min(lines.Count, lineIndex + after + 1) - Math.Max(0, lineIndex - before)).ToList();
        var context = string.Join("\n", contextLines);
        var line = lines[lineIndex];
        var section = FindSection(lines, lineIndex, isCml ? 70 : 45);

        var mention = new ExternalBulletinMention
        {
            Id = $"{file.Id}:{page}:{occurrence}",
            FileId = file.Id,
            Kind = file.Kind,
            Bulletin = file.DisplayNumber,
            BulletinDate = file.DisplayDate,
            Page = page,
            DocumentOccurrence = occurrence,
            PdfSearchTerm = searchTerm,
            Section = section,
            MatchLine = line,
            Context = context,
            PdfPath = file.StoredPath
        };

        if (isCml) PopulateCmlDetails(mention, context);
        else PopulateRegionDetails(mention, lines, lineIndex, searchTerm);
        mention.Id = StableMentionId(file, mention);
        return mention;
    }

    private static void PopulateRegionDetails(ExternalBulletinMention mention, IReadOnlyList<string> lines, int index, string searchTerm)
    {
        var normalizedLine = Normalize(lines[index]);
        var target = Normalize(searchTerm);
        if (normalizedLine == target)
        {
            mention.Type = "Bloco da OM";
            var members = lines.Skip(index + 1).Take(4).Where(x => !LooksLikeHeading(x)).ToList();
            mention.Summary = members.Count == 0 ? lines[index] : $"{lines[index]} · {string.Join(" · ", members)}";
        }
        else if (normalizedLine.StartsWith("- o cmt", StringComparison.Ordinal) || normalizedLine.Contains("providencias decorrentes", StringComparison.Ordinal))
        {
            mention.Type = "Providência / consequência";
            mention.Summary = lines[index];
        }
        else if (Regex.IsMatch(lines[index], @"\b(?:S\s*Ten|[1-3][º°]\s*Sgt|[1-2][º°]\s*Ten|Cap|Maj|Ten\s*Cel|Cel|Cb|Sd)\b", RegexOptions.IgnoreCase))
        {
            mention.Type = "Militar / ato publicado";
            mention.Summary = lines[index];
        }
        else
        {
            mention.Type = "Menção administrativa";
            mention.Summary = lines[index];
        }
    }

    private static void PopulateCmlDetails(ExternalBulletinMention mention, string context)
    {
        var normalized = Normalize(context);
        mention.Type = normalized.Contains("exercicios anteriores", StringComparison.Ordinal)
            ? "Liberação para pagamento - Exercícios anteriores"
            : normalized.Contains("autorizo o saque", StringComparison.Ordinal)
                ? "Autorização de saque"
                : "Menção em aditamento do CML";

        mention.Amount = AmountRegex.Matches(context).Cast<Match>().Select(x => x.Value.Trim()).LastOrDefault() ?? string.Empty;
        mention.Event = MatchLine(EventRegex, context);
        mention.Duration = MatchLine(DurationRegex, context);
        mention.Personnel = MatchLine(PersonnelRegex, context);

        var parts = new List<string> { mention.Type };
        if (!string.IsNullOrWhiteSpace(mention.Event)) parts.Add(mention.Event);
        if (!string.IsNullOrWhiteSpace(mention.Amount)) parts.Add(mention.Amount);
        mention.Summary = string.Join(" · ", parts);
    }

    private static string MatchLine(Regex regex, string context)
    {
        var match = regex.Match(context);
        return match.Success ? OneLine(match.Groups[1].Value) : string.Empty;
    }

    private static string FindSection(IReadOnlyList<string> lines, int index, int lookBehind)
    {
        var headings = new List<string>();
        for (var i = index; i >= Math.Max(0, index - lookBehind); i--)
        {
            var line = lines[i];
            if (IsPageNoise(line)) continue;
            var normalized = Normalize(line);
            if (Regex.IsMatch(normalized, @"^[234]a\s+parte\b") || LooksLikeHeading(line))
            {
                var clean = OneLine(line);
                if (!headings.Any(x => Normalize(x) == Normalize(clean))) headings.Add(clean);
                if (headings.Count >= 3) break;
            }
        }
        headings.Reverse();
        return string.Join(" › ", headings);
    }

    private static bool LooksLikeHeading(string value)
    {
        var line = OneLine(value);
        if (line.Length is < 4 or > 125 || IsPageNoise(line)) return false;
        var letters = line.Where(char.IsLetter).ToArray();
        if (letters.Length < 4) return false;
        var upper = letters.Count(char.IsUpper);
        return upper >= Math.Max(4, (int)Math.Ceiling(letters.Length * 0.72)) ||
               Regex.IsMatch(line, @"^[a-z0-9]+[.)]\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ]", RegexOptions.IgnoreCase);
    }

    private static bool IsPageNoise(string value)
    {
        var normalized = Normalize(value);
        return normalized.StartsWith("continuacao do ", StringComparison.Ordinal) ||
               normalized.StartsWith("pag n", StringComparison.Ordinal) ||
               normalized.Contains("original assinado", StringComparison.Ordinal);
    }

    private static bool IsAfterDailyServicesMarker(string line)
    {
        var normalized = Normalize(line);
        return Regex.IsMatch(normalized, @"^[23]a\s+parte\b") ||
               normalized is "instrucao" or "assuntos gerais e administrativos";
    }

    private static int CountOccurrences(string text, string target)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target)) return 0;
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(target, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += Math.Max(1, target.Length);
        }
        return count;
    }

    private static string StableMentionId(ExternalBulletinFile file, ExternalBulletinMention mention)
    {
        var raw = $"{file.Kind}|{file.HashSha256}|{mention.Page}|{mention.DocumentOccurrence}|{Normalize(mention.MatchLine)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }

    private void ApplyMetadata(ExternalBulletinFile item, IReadOnlyList<string> pages, string source)
    {
        var fullText = string.Join("\n", pages.Take(4));
        if (item.Kind == ExternalBulletinKinds.Region)
        {
            item.BulletinType = "Boletim Regional";
            var match = RegionalNumberRegex.Match(fullText);
            item.BulletinNumber = match.Success ? match.Groups[1].Value.Trim() : InferNumberFromFileName(source, "Bol Rg");
        }
        else
        {
            item.BulletinType = "Aditamento CML";
            var match = CmlHeaderRegex.Match(fullText);
            if (match.Success)
            {
                var section = OneLine(match.Groups["secao"].Value);
                item.BulletinNumber = $"Adt {section} Nr {match.Groups["adt"].Value} · BI {match.Groups["bi"].Value}";
            }
            else item.BulletinNumber = InferNumberFromFileName(source, "Adt CML");
        }

        var date = ParseDate(fullText);
        item.BulletinDate = date?.ToString("dd/MM/yyyy", PtBr) ?? "—";
        item.DateIso = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTime? ParseDate(string text)
    {
        var longDate = LongDateRegex.Match(text);
        if (longDate.Success && TryMonth(longDate.Groups["m"].Value, out var month) &&
            int.TryParse(longDate.Groups["d"].Value, out var day) && int.TryParse(longDate.Groups["y"].Value, out var year))
        {
            try { return new DateTime(year, month, day); } catch { }
        }
        var numeric = NumericDateRegex.Match(text);
        if (numeric.Success && int.TryParse(numeric.Groups["d"].Value, out var d) &&
            int.TryParse(numeric.Groups["m"].Value, out var m) && int.TryParse(numeric.Groups["y"].Value, out var y))
        {
            try { return new DateTime(y, m, d); } catch { }
        }
        return null;
    }

    private static bool TryMonth(string value, out int month)
    {
        month = Normalize(value) switch
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
        return month > 0;
    }

    private string CreateDestination(ExternalBulletinFile item)
    {
        var root = item.Kind == ExternalBulletinKinds.Cml ? _paths.ExternalBulletinCmlDirectory : _paths.ExternalBulletinRegionDirectory;
        var date = DateTime.TryParseExact(item.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
        var folder = date is null ? Path.Combine(root, "Sem_Data") : Path.Combine(root, date.Value.ToString("yyyy"), date.Value.ToString("MM"));
        Directory.CreateDirectory(folder);
        var clean = SafeFileName(Path.GetFileNameWithoutExtension(item.OriginalFileName));
        var suffix = item.HashSha256.Length >= 8 ? item.HashSha256[..8] : Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(folder, $"{clean}_{suffix}.pdf");
    }

    private static string InferNumberFromFileName(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var number = Regex.Match(name, @"\b(?:Nr\s*)?0*([0-9]{1,4})(?:[/_-](20\d{2}))?\b", RegexOptions.IgnoreCase);
        if (!number.Success) return prefix;
        return number.Groups[2].Success ? $"{number.Groups[1].Value}/{number.Groups[2].Value}" : number.Groups[1].Value;
    }

    private static IEnumerable<string> ExpandPdfSources(IEnumerable<string> sources)
    {
        foreach (var source in sources.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (File.Exists(source) && Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(source);
                continue;
            }
            if (!Directory.Exists(source)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(source, "*.pdf", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files) yield return Path.GetFullPath(file);
        }
    }

    private async Task<ExternalBulletinStore> ReadStoreUnsafeAsync(CancellationToken cancellationToken)
    {
        var store = await _json.LoadAsync<ExternalBulletinStore>(_paths.ExternalBulletinIndexFile) ?? new ExternalBulletinStore();
        store.Items ??= [];
        foreach (var item in store.Items)
        {
            item.Mentions ??= [];
            item.StoredPath = ResolvePath(item.StoredPath);
            BindMentionPaths(item);
        }
        return store;
    }

    private async Task WriteStoreUnsafeAsync(ExternalBulletinStore store, CancellationToken cancellationToken)
    {
        foreach (var item in store.Items)
        {
            item.StoredPath = MakeRelative(item.StoredPath);
            foreach (var mention in item.Mentions) mention.PdfPath = item.StoredPath;
        }
        await _json.SaveAsync(_paths.ExternalBulletinIndexFile, store);
        foreach (var item in store.Items)
        {
            item.StoredPath = ResolvePath(item.StoredPath);
            BindMentionPaths(item);
        }
    }

    private void BindMentionPaths(ExternalBulletinFile item)
    {
        foreach (var mention in item.Mentions)
        {
            mention.FileId = item.Id;
            mention.Kind = item.Kind;
            mention.Bulletin = item.DisplayNumber;
            mention.BulletinDate = item.DisplayDate;
            mention.PdfPath = item.StoredPath;
        }
    }

    private string MakeRelative(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_paths.DataDirectory) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(_paths.DataDirectory, full) : full;
        }
        catch { return path; }
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_paths.DataDirectory, path)); }
        catch { return path; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void SortStore(ExternalBulletinStore store)
    {
        store.Items = store.Items
            .OrderBy(x => x.Kind == ExternalBulletinKinds.Region ? 0 : 1)
            .ThenByDescending(x => x.DateIso)
            .ThenByDescending(x => x.DisplayNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.OriginalFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string CleanPage(string value)
        => (value ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string OneLine(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string CleanSearchTerm(string? value)
        => OneLine(value).Trim('"', '\'', ' ');

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Replace('ª', 'a').Replace('º', 'o').Replace('°', 'o');
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"[^a-z0-9]+", " ").Trim();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? "boletim").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', '_');
        return clean.Length > 100 ? clean[..100].Trim() : (string.IsNullOrWhiteSpace(clean) ? "boletim" : clean);
    }

    private static void ValidateKind(string kind)
    {
        if (kind != ExternalBulletinKinds.Region && kind != ExternalBulletinKinds.Cml)
            throw new ArgumentOutOfRangeException(nameof(kind), "Origem de boletim externo inválida.");
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    private const byte VkControl = 0x11, VkF = 0x46, VkV = 0x56, VkEscape = 0x1B, VkF3 = 0x72;
    private const uint KeyUp = 0x0002;

    private static void SendCtrlFAndPaste()
    {
        try
        {
            HotKey(VkF);
            Thread.Sleep(180);
            HotKey(VkV);
        }
        catch { }
    }

    private static void HotKey(byte key)
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyUp, UIntPtr.Zero);
    }

    private static void PressKey(byte key)
    {
        try
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        }
        catch { }
    }
}
