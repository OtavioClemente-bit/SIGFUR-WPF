using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class IntelligentBulletinService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly SettingsService _settings;
    private readonly MilitaryRepository _military;
    private readonly LicensedTransferredRepository _licensed;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private const int CurrentParserVersion = 13;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly Regex FurrielWordRegex = new(@"\bfurriel\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BulletinNumberRegex = new(@"BOLETIM\s+INTERNO\s+N[ºO°]?\s*([0-9]{1,4}/[0-9]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BulletinDateRegex = new(@"Belo\s+Horizonte/MG,\s*(.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ServiceDayRegex = new(@"Dia\s+(\d{1,2})\s+([A-Za-zÀ-ÿ]{3,12})\s+(\d{2,4})\s+\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RankLineRegex = new(@"^(?:S\s*Ten|Sub\s*Ten|[1-3]º?\s*Sgt|[1-2]º?\s*Ten|Asp(?:\s*Of)?|Cap|Maj|Ten(?:\s*Cel)?|Cel|Cb(?:\s*Ef\s*(?:Profl|Vrv))?|Sd(?:\s*(?:EV|Ef\s*(?:Profl|Vrv)))?|Ex[- ]?militar)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PaymentRegex = new(@"\b(?:seja\s+sacado|saque|sacado|atrasad[oa]?|pagament[oa]|adicional\s+de\s+f[eé]rias|pecuni[áa]ri\w*|exerc[íi]cio\s+anterior|sal[áa]rio-fam[íi]lia)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VacationRegex = new(@"\bf[eé]rias\b|Altero\s+o\s+plano\s+de\s+f[eé]rias|Seja\s+inclu[ií]do\s+no\s+Plano\s+de\s+F[eé]rias", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConvalescenceRegex = new(@"\bconval(?:e|esc)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly (string Type, Regex Pattern)[] AdminPatterns =
    [
        ("Apresentação", new Regex(@"\bapresent(?:ou-se|ou)\b|\bapresenta[çc][ãa]o\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Licenciamento", new Regex(@"\blicenciament[oa]\b|\blicenciad[oa]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Desligamento", new Regex(@"\bdesligament[oa]\b|\bdesligad[oa]\b|\befetuar\s+o\s+desligamento\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Transferência", new Regex(@"\btransfer[êe]ncia\b|\btransferid[oa]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Atualização de dados", new Regex(@"atualiza[çc][ãa]o\s+de\s+dados|nome\s+de\s+guerra|domic[íi]lio\s+banc[áa]rio", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Inspeção de saúde", new Regex(@"inspe[çc][ãa]o\s+de\s+sa[úu]de", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];
    private static readonly (string Type, Regex Pattern)[] OtherPatterns =
    [
        ("Designação", new Regex(@"designa[çc][ãa]o|designad[oa]", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Apuração disciplinar", new Regex(@"apura[çc][ãa]o\s+de\s+transgress|transgress[ãa]o\s+disciplinar", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Reunião", new Regex(@"\breuni[ãa]o\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Comissão", new Regex(@"\bcomiss[ãa]o\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Sindicância", new Regex(@"\bsindic[âa]ncia\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Furriel", new Regex(@"\bfurriel\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    public IntelligentBulletinService(AppPaths paths, JsonFileService json, SettingsService settings, MilitaryRepository military, LicensedTransferredRepository licensed, LogService log)
    {
        _paths = paths; _json = json; _settings = settings; _military = military; _licensed = licensed; _log = log;
        Directory.CreateDirectory(_paths.IntelligentBulletinLibraryDirectory);
        Directory.CreateDirectory(_paths.IntelligentBulletinTextDirectory);
        Directory.CreateDirectory(_paths.IntelligentBulletinParseDirectory);
        Directory.CreateDirectory(_paths.IntelligentBulletinTempDirectory);
    }

    public async Task<IntelligentBulletinStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var reviews = await ReadReviewsUnsafeAsync(cancellationToken);
            if (await UpgradeStaleParserItemsUnsafeAsync(store, reviews, cancellationToken))
            {
                await WriteStoreUnsafeAsync(store, cancellationToken);
                await WriteReviewsUnsafeAsync(reviews, cancellationToken);
            }
            ApplyReviews(store, reviews);
            return store;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<IntelligentBulletinMilitaryOption>> LoadMilitaryOptionsAsync(string source, CancellationToken cancellationToken = default)
    {
        var result = new List<IntelligentBulletinMilitaryOption>();
        if (!source.Equals("Só licenciados/transferidos", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in await _military.GetAllAsync(cancellationToken))
                result.Add(new IntelligentBulletinMilitaryOption { Id = item.Id, Rank = item.Rank, FullName = item.Name, WarName = item.WarName, Cpf = item.Cpf, Identity = item.MilitaryId, PrecCp = item.PrecCp, Source = "Ativos" });
        }
        if (!source.Equals("Ativos", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in await _licensed.GetAllAsync(false, null, cancellationToken))
                result.Add(new IntelligentBulletinMilitaryOption { Id = item.Id, Rank = item.Rank, FullName = item.Name, WarName = item.WarName, Cpf = item.Cpf, Identity = item.MilitaryId, PrecCp = item.PrecCp, Source = "Lic./Transf." });
        }
        return result
            .GroupBy(x => $"{x.Source}|{(!string.IsNullOrWhiteSpace(Digits(x.Cpf)) ? Digits(x.Cpf) : Normalize(x.FullName))}")
            .Select(x => x.First())
            .OrderBy(x => x.Source == "Ativos" ? 0 : 1)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IntelligentBulletinImportResult> ImportAsync(IEnumerable<string> sources, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new IntelligentBulletinImportResult();
        var tempDirectories = new List<string>();
        var files = await ExpandSourcesAsync(sources, tempDirectories, result.Errors, cancellationToken);
        if (files.Count == 0) return result;
        var people = (await LoadMilitaryOptionsAsync("Ativos + licenciados/transferidos", cancellationToken)).ToList();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var reviews = await ReadReviewsUnsafeAsync(cancellationToken);
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = files[index];
                progress?.Report($"Lendo {index + 1}/{files.Count}: {Path.GetFileName(source)}");
                try
                {
                    var hash = await HashFileAsync(source, cancellationToken);
                    var sameHash = store.Items.FirstOrDefault(x => x.HashSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    var requiresParserUpgrade = store.Version < CurrentParserVersion || sameHash?.ParserVersion < CurrentParserVersion;
                    if (sameHash is not null && !requiresParserUpgrade) { result.Duplicates++; continue; }

                    var pages = await ExtractPdfPagesAsync(source, cancellationToken);
                    var parsed = ParsePdf(pages, source, people);
                    parsed.HashSha256 = hash;
                    parsed.Id = hash[..Math.Min(20, hash.Length)];
                    parsed.SourceFolder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? string.Empty;
                    parsed.SourceFolderLabel = Path.GetFileName(parsed.SourceFolder);
                    parsed.OriginalFileName = Path.GetFileName(source);
                    parsed.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr);
                    parsed.SizeBytes = new FileInfo(source).Length;

                    var existing = sameHash ?? store.Items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(parsed.BulletinNumber) && parsed.BulletinNumber != "—" && Normalize(x.BulletinNumber) == Normalize(parsed.BulletinNumber));
                    if (existing is not null)
                    {
                        foreach (var old in existing.Findings) reviews.Remove(old.Id);
                        var destination = string.IsNullOrWhiteSpace(existing.PdfPath) ? CreateLibraryPath(parsed) : existing.PdfPath;
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, true);
                        BindPaths(parsed, destination);
                        var position = store.Items.IndexOf(existing);
                        store.Items[position] = parsed;
                        result.Updated++;
                    }
                    else
                    {
                        var destination = CreateLibraryPath(parsed);
                        File.Copy(source, destination, false);
                        BindPaths(parsed, destination);
                        store.Items.Add(parsed);
                        result.Imported++;
                    }
                    await WriteCachesUnsafeAsync(parsed, pages, cancellationToken);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                    await _log.WriteAsync("Falha ao importar boletim inteligente: " + source, ex);
                }
            }
            store.Items = store.Items.OrderByDescending(x => ParseIsoDate(x.DateIso) ?? DateTime.MinValue).ThenByDescending(x => BulletinNumeric(x.BulletinNumber)).ToList();
            store.Version = CurrentParserVersion;
            await WriteStoreUnsafeAsync(store, cancellationToken);
            await WriteReviewsUnsafeAsync(reviews, cancellationToken);
        }
        finally
        {
            _gate.Release();
            foreach (var directory in tempDirectories) { try { Directory.Delete(directory, true); } catch { } }
        }
        return result;
    }

    public async Task ReindexAsync(string fileId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var people = (await LoadMilitaryOptionsAsync("Ativos + licenciados/transferidos", cancellationToken)).ToList();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var item = store.Items.FirstOrDefault(x => x.Id == fileId) ?? throw new InvalidOperationException("Boletim não encontrado.");
            if (!File.Exists(item.PdfPath)) throw new FileNotFoundException("PDF salvo não encontrado.", item.PdfPath);
            progress?.Report("Extraindo e analisando novamente o PDF...");
            var pages = await ExtractPdfPagesAsync(item.PdfPath, cancellationToken);
            var reparsed = ParsePdf(pages, item.PdfPath, people);
            reparsed.Id = item.Id; reparsed.HashSha256 = item.HashSha256; reparsed.PdfPath = item.PdfPath;
            reparsed.FileName = item.FileName; reparsed.OriginalFileName = item.OriginalFileName; reparsed.SourceFolder = item.SourceFolder;
            reparsed.SourceFolderLabel = item.SourceFolderLabel; reparsed.SavedAt = item.SavedAt; reparsed.SizeBytes = item.SizeBytes;
            BindPaths(reparsed, item.PdfPath);
            var reviews = await ReadReviewsUnsafeAsync(cancellationToken);
            foreach (var old in item.Findings) reviews.Remove(old.Id);
            store.Items[store.Items.IndexOf(item)] = reparsed;
            await WriteCachesUnsafeAsync(reparsed, pages, cancellationToken);
            await WriteStoreUnsafeAsync(store, cancellationToken);
            await WriteReviewsUnsafeAsync(reviews, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ReindexAllAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var people = (await LoadMilitaryOptionsAsync("Ativos + licenciados/transferidos", cancellationToken)).ToList();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var reviews = await ReadReviewsUnsafeAsync(cancellationToken);
            for (var index = 0; index < store.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = store.Items[index];
                progress?.Report($"Reindexando {index + 1}/{store.Items.Count}: {item.FileName}");
                if (!File.Exists(item.PdfPath))
                {
                    errors.Add($"{item.FileName}: PDF salvo não encontrado.");
                    continue;
                }

                try
                {
                    var pages = await ExtractPdfPagesAsync(item.PdfPath, cancellationToken);
                    var reparsed = ParsePdf(pages, item.PdfPath, people);
                    reparsed.Id = item.Id;
                    reparsed.HashSha256 = item.HashSha256;
                    reparsed.PdfPath = item.PdfPath;
                    reparsed.FileName = item.FileName;
                    reparsed.OriginalFileName = item.OriginalFileName;
                    reparsed.SourceFolder = item.SourceFolder;
                    reparsed.SourceFolderLabel = item.SourceFolderLabel;
                    reparsed.SavedAt = item.SavedAt;
                    reparsed.SizeBytes = item.SizeBytes;
                    BindPaths(reparsed, item.PdfPath);
                    foreach (var old in item.Findings) reviews.Remove(old.Id);
                    store.Items[index] = reparsed;
                    await WriteCachesUnsafeAsync(reparsed, pages, cancellationToken);
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.FileName}: {ex.Message}");
                    await _log.WriteAsync("Falha ao reindexar boletim inteligente: " + item.PdfPath, ex);
                }
            }

            store.Version = CurrentParserVersion;
            await WriteStoreUnsafeAsync(store, cancellationToken);
            await WriteReviewsUnsafeAsync(reviews, cancellationToken);
        }
        finally { _gate.Release(); }
        return errors;
    }

    public async Task RemoveAsync(string fileId, bool deletePdf, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var item = store.Items.FirstOrDefault(x => x.Id == fileId);
            if (item is null) return;
            store.Items.Remove(item);
            var reviews = await ReadReviewsUnsafeAsync(cancellationToken);
            foreach (var finding in item.Findings) reviews.Remove(finding.Id);
            await WriteStoreUnsafeAsync(store, cancellationToken);
            await WriteReviewsUnsafeAsync(reviews, cancellationToken);
            if (deletePdf) foreach (var path in new[] { item.PdfPath, item.TextCachePath, item.ParseCachePath }) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }
        finally { _gate.Release(); }
    }

    public async Task SetReviewedAsync(IntelligentBulletinFinding finding, bool reviewed, string note = "", CancellationToken cancellationToken = default)
    {
        if (!finding.Reviewable) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var reviews = await ReadReviewsUnsafeAsync(cancellationToken);
            if (reviewed)
                reviews[finding.Id] = new IntelligentBulletinReviewEntry { Ok = true, Category = finding.Category, Bulletin = finding.Bulletin, File = finding.FileName, Military = finding.DisplayMilitary, Summary = finding.Detail, Note = note, ReviewedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr) };
            else reviews.Remove(finding.Id);
            await WriteReviewsUnsafeAsync(reviews, cancellationToken);
            finding.Reviewed = reviewed;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ReadCachedTextAsync(IntelligentBulletinFile file, CancellationToken cancellationToken = default)
    {
        var path = ResolveDataPath(file.TextCachePath);
        if (!File.Exists(path)) return string.Empty;
        try { return await File.ReadAllTextAsync(path, cancellationToken); }
        catch { return string.Empty; }
    }

    public async Task ExportCsvAsync(string path, IEnumerable<IntelligentBulletinFinding> findings, CancellationToken cancellationToken = default)
    {
        static string Csv(string? value) { var text = value ?? string.Empty; return text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? '"' + text.Replace("\"", "\"\"") + '"' : text; }
        var lines = new List<string> { "Tipo;Boletim;Data;Assunto_Nota;P/G;Militar;CPF;PREC_CP;Consequencia;Pagina;Texto_Nota;Arquivo" };
        lines.AddRange(findings.Select(x => string.Join(";", new[]
        {
            x.Type, x.Bulletin, x.BulletinDate, x.DisplaySubject, x.Rank, x.DisplayMilitary,
            x.MentionedCpf, x.MentionedPrecCp, x.ConsequenceDisplay, x.Page.ToString(PtBr),
            x.NoteText, x.FileName
        }.Select(Csv))));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), cancellationToken);
    }

    public static string BuildSummary(IEnumerable<IntelligentBulletinFinding> findings)
    {
        var rows = findings.ToList();
        var builder = new StringBuilder();
        builder.AppendLine("RESUMO DE BOLETINS — SIGFUR");
        builder.AppendLine($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}");
        builder.AppendLine();
        foreach (var group in rows.GroupBy(x => x.Category).OrderBy(x => CategoryOrder(x.Key)))
        {
            builder.AppendLine($"{group.Key.ToUpperInvariant()} — {group.Count()} item(ns)");
            foreach (var item in group.OrderBy(x => x.Bulletin).ThenBy(x => x.DisplayMilitary))
                builder.AppendLine($"• BI {item.Bulletin} | {item.DisplayMilitary} | {item.Type}: {OneLine(item.Detail)}");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    public void OpenPdf(IntelligentBulletinFinding finding)
        => OpenPdf(finding.PdfPath, string.IsNullOrWhiteSpace(finding.PdfSearchTerm) ? finding.DisplayMilitary : finding.PdfSearchTerm, finding.Page);

    public void OpenPdf(string path, string searchText = "", int page = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("PDF não encontrado.", path);
        ShellService.OpenPath(path);
        if (string.IsNullOrWhiteSpace(searchText)) return;
        try { Clipboard.SetText(searchText.Trim()); } catch { }
        _ = Task.Run(async () =>
        {
            await Task.Delay(1800);
            SendCtrlFAndPaste();
            await Task.Delay(650);
            PressKey(VkEscape);
        });
    }

    private IntelligentBulletinFile ParsePdf(IReadOnlyList<string> pages, string sourcePath, IReadOnlyList<IntelligentBulletinMilitaryOption> people)
    {
        var fullText = string.Join("\n", pages);
        var number = BulletinNumberRegex.Match(fullText).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(number)) number = InferBulletinNumberFromFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(number)) number = "—";
        var rawDate = BulletinDateRegex.Match(fullText).Groups[1].Value.Trim();
        var parsedDate = ParseBulletinDate(rawDate, number);
        var dateText = parsedDate?.ToString("dd/MM/yyyy") ?? (string.IsNullOrWhiteSpace(rawDate) ? "—" : OneLine(rawDate));
        var identities = people.Select(x => new BulletinMilitaryIdentity
        {
            Id = x.Id, Rank = x.Rank, FullName = x.FullName, WarName = x.WarName,
            Cpf = x.Cpf, Identity = x.Identity, PrecCp = x.PrecCp
        }).ToList();
        var mentions = ProfessionalBulletinParser.Parse(pages, "BI", number, parsedDate, sourcePath, identities)
            .Where(mention => ContainsFurrielWord(string.Join(" ", mention.NoteText, mention.ConsequenceText)))
            .ToList();
        var file = new IntelligentBulletinFile
        {
            BulletinType = "BI", BulletinNumber = number, BulletinDate = dateText,
            DateIso = parsedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            Period = parsedDate?.ToString("MMMM/yyyy", PtBr) ?? string.Empty,
            FileName = Path.GetFileName(sourcePath), Pages = pages.Count, Mentions = mentions, ParserVersion = CurrentParserVersion
        };

        // Regra profissional do Boletim Inteligente: a leitura direta do PDF só serve
        // para localizar ocorrências reais da palavra "Furriel" no BI. As notas por
        // militar vêm do Índice por Pessoa do SisBol, evitando associar nomes à nota errada.
        file.Findings = BuildFurrielFindings(pages, number, dateText, parsedDate, sourcePath, people)
            .Concat(BuildConvalescenceHomeFindings(pages, number, dateText, parsedDate, sourcePath, people))
            .OrderBy(x => CategoryOrder(x.Category))
            .ThenBy(x => x.Page)
            .ThenBy(x => x.DisplayMilitary, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.DisplaySubject, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return file;
    }


    private static List<IntelligentBulletinFinding> BuildFurrielFindings(
        IReadOnlyList<string> pages,
        string bulletinNumber,
        string bulletinDateText,
        DateTime? bulletinDate,
        string sourcePath,
        IReadOnlyList<IntelligentBulletinMilitaryOption> people)
    {
        var results = new List<IntelligentBulletinFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var lines = CleanFurrielPageLines(pages[pageIndex]);
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (!ContainsFurrielWord(lines[lineIndex])) continue;

                var start = FindFurrielContextStart(lines, lineIndex);
                var end = FindFurrielContextEnd(lines, lineIndex);
                var contextLines = lines.Skip(start).Take(end - start + 1).ToList();
                var context = OneLine(string.Join(" ", contextLines));
                if (!ContainsFurrielWord(context)) continue;

                var subject = DetectFurrielSubject(lines, lineIndex, contextLines);
                var consequenceText = ExtractFurrielContextText(lines, lineIndex);
                var pageNumber = pageIndex + 1;
                var displaySubject = subject.Equals("Furriel", StringComparison.OrdinalIgnoreCase)
                    ? $"Furriel — BI {bulletinNumber}"
                    : $"{subject} — Furriel";
                var matches = ResolveStrongMilitaryMatches(context, people).ToList();
                if (matches.Count == 0)
                    matches.Add(new IntelligentBulletinMilitaryOption { FullName = string.Empty, WarName = string.Empty, Rank = string.Empty });

                foreach (var person in matches)
                {
                    var fullName = person.FullName ?? string.Empty;
                    var warName = person.WarName ?? string.Empty;
                    var rank = person.Rank ?? string.Empty;
                    var militaryDisplay = string.IsNullOrWhiteSpace(fullName)
                        ? "Furriel"
                        : $"{MilitaryRankService.ShortName(rank)} {fullName}".Trim();
                    var pdfSearch = string.IsNullOrWhiteSpace(fullName) ? "Furriel" : fullName;
                    var key = string.Join('|', "furriel", bulletinNumber, pageNumber.ToString(CultureInfo.InvariantCulture), start.ToString(CultureInfo.InvariantCulture), end.ToString(CultureInfo.InvariantCulture), Normalize(fullName), Normalize(context));
                    if (!seen.Add(key)) continue;

                    results.Add(new IntelligentBulletinFinding
                    {
                        Id = ShortHash(key),
                        Category = "Furriel",
                        Type = "Furriel",
                        Bulletin = bulletinNumber,
                        BulletinDate = bulletinDate?.ToString("dd/MM/yyyy") ?? bulletinDateText,
                        Military = militaryDisplay,
                        FullName = fullName,
                        WarName = warName,
                        Rank = rank,
                        Subject = subject,
                        NoteTitle = "Furriel",
                        SubjectNoteDisplay = displaySubject,
                        Preview = string.Empty,
                        Detail = consequenceText,
                        Context = context,
                        NoteText = context,
                        HasConsequence = ContainsConsequenceWord(context),
                        IsFurrielConsequence = true,
                        ConsequenceText = consequenceText,
                        Page = pageNumber,
                        FileName = Path.GetFileName(sourcePath),
                        PdfPath = sourcePath,
                        PdfSearchTerm = pdfSearch
                    });
                }
            }
        }

        return results
            .GroupBy(item => string.Join('|', item.Bulletin, item.Page.ToString(CultureInfo.InvariantCulture), Normalize(item.FullName), Normalize(item.Context)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static List<IntelligentBulletinFinding> BuildConvalescenceHomeFindings(
        IReadOnlyList<string> pages,
        string bulletinNumber,
        string bulletinDateText,
        DateTime? bulletinDate,
        string sourcePath,
        IReadOnlyList<IntelligentBulletinMilitaryOption> people)
    {
        var results = new List<IntelligentBulletinFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var lines = CleanFurrielPageLines(pages[pageIndex]);
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (!IsConvalescenceHomeAnchor(lines, lineIndex)) continue;

                var end = FindConvalescenceBlockEnd(lines, lineIndex);
                var blockLines = lines.Skip(lineIndex).Take(end - lineIndex + 1).ToList();
                var context = OneLine(string.Join(" ", blockLines));
                if (!IsConvalescenceHomeText(context)) continue;

                var pageNumber = pageIndex + 1;
                var matches = ResolveStrongMilitaryMatches(context, people).ToList();
                if (matches.Count == 0)
                {
                    var extractedName = ExtractProbableName(blockLines);
                    var extractedRank = ExtractRank(blockLines);
                    if (!string.IsNullOrWhiteSpace(extractedName))
                        matches.Add(new IntelligentBulletinMilitaryOption { FullName = extractedName, Rank = extractedRank });
                }
                if (matches.Count == 0)
                    matches.Add(new IntelligentBulletinMilitaryOption { FullName = string.Empty, Rank = string.Empty });

                foreach (var person in matches)
                {
                    var fullName = person.FullName ?? string.Empty;
                    var rank = person.Rank ?? string.Empty;
                    var warName = person.WarName ?? string.Empty;
                    var militaryDisplay = string.IsNullOrWhiteSpace(fullName) ? "—" : $"{MilitaryRankService.ShortName(rank)} {fullName}".Trim();
                    var pdfSearch = string.IsNullOrWhiteSpace(fullName) ? "convalescer em residência" : fullName;
                    var key = string.Join('|', "convalescenca-casa", bulletinNumber, pageNumber.ToString(CultureInfo.InvariantCulture), lineIndex.ToString(CultureInfo.InvariantCulture), Normalize(fullName), Normalize(context));
                    if (!seen.Add(key)) continue;
                    results.Add(new IntelligentBulletinFinding
                    {
                        Id = ShortHash(key),
                        Category = "Convalescença",
                        Type = "Convalescença em casa",
                        Bulletin = bulletinNumber,
                        BulletinDate = bulletinDate?.ToString("dd/MM/yyyy") ?? bulletinDateText,
                        Military = militaryDisplay,
                        FullName = fullName,
                        WarName = warName,
                        Rank = rank,
                        Subject = "Serviço de Saúde",
                        NoteTitle = "Convalescença em casa",
                        SubjectNoteDisplay = "Serviço de Saúde — Convalescença em casa",
                        Preview = TruncateText(context, 260),
                        Detail = TruncateText(context, 420),
                        Context = context,
                        NoteText = context,
                        HasConsequence = ContainsConsequenceWord(context),
                        ConsequenceText = context,
                        Page = pageNumber,
                        FileName = Path.GetFileName(sourcePath),
                        PdfPath = sourcePath,
                        PdfSearchTerm = pdfSearch
                    });
                }
            }
        }
        return results
            .GroupBy(item => string.Join('|', item.Bulletin, item.Page.ToString(CultureInfo.InvariantCulture), Normalize(item.FullName), Normalize(item.Context)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsConvalescenceHomeAnchor(IReadOnlyList<string> lines, int lineIndex)
    {
        // Convalescença em casa é assunto próprio do BI: detectar pelo bloco médico,
        // não por consequência de Furriel nem por texto solto de outra nota.
        if (lineIndex < 0 || lineIndex >= lines.Count) return false;
        var current = Normalize(lines[lineIndex]);
        if (!current.Contains("convale", StringComparison.Ordinal) &&
            !Regex.IsMatch(current, @"\bconvem\b.*\bmilitar\b", RegexOptions.IgnoreCase)) return false;
        var window = OneLine(string.Join(" ", lines.Skip(lineIndex).Take(Math.Min(3, lines.Count - lineIndex))));
        return IsConvalescenceHomeText(window);
    }

    private static int FindConvalescenceBlockEnd(IReadOnlyList<string> lines, int lineIndex)
    {
        var limit = Math.Min(lines.Count - 1, lineIndex + 14);
        var end = limit;
        for (var index = lineIndex + 1; index <= limit; index++)
        {
            if (index > lineIndex + 1 && IsConvalescenceHomeAnchor(lines, index)) return index - 1;
            if (index > lineIndex + 1 && IsProbableHeading(lines[index]) && !ConvalescenceRegex.IsMatch(lines[index])) return index - 1;
            if (!IsConsequenceLine(lines[index])) continue;

            end = index;
            for (var after = index + 1; after <= Math.Min(lines.Count - 1, index + 3); after++)
            {
                if (IsConvalescenceHomeAnchor(lines, after) || IsProbableHeading(lines[after])) break;
                end = after;
                if (Regex.IsMatch(lines[after], @"provid[êe]ncias\s+decorrentes|medidas\s+decorrentes", RegexOptions.IgnoreCase)) break;
            }
            return end;
        }
        return end;
    }

    private static string ExtractProbableName(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Select(OneLine))
        {
            if (!LooksLikeNameLine(line)) continue;
            var value = RankLineRegex.Replace(line, string.Empty).Trim(' ', '-', '–', '—', ':', ';', ',');
            value = Regex.Split(value, @"\b(?:CPF|Prec[- ]?CP|IDT|por|para|referente|conforme)\b", RegexOptions.IgnoreCase)[0].Trim();
            if (value.Length >= 4) return value.Length > 100 ? value[..100] : value;
        }
        return string.Empty;
    }

    private static string ExtractRank(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Select(OneLine))
        {
            var match = RankLineRegex.Match(line);
            if (match.Success && LooksLikeNameLine(line)) return match.Value;
        }
        return string.Empty;
    }

    private static List<string> CleanFurrielPageLines(string page)
        => (page ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(OneLine)
            .Where(line => line.Length > 0 && !IsFurrielStructuralLine(line))
            .ToList();

    private static bool IsFurrielStructuralLine(string line)
        => Regex.IsMatch(line, @"^(?:MINIST[ÉE]RIO DA DEFESA|EX[ÉE]RCITO BRASILEIRO|CML\s*-\s*4ª\s*RM|4ª\s+COMPANHIA|BOLETIM\s+INTERNO|P[áa]g(?:ina|\.)|\(?Continua[çc][ãa]o|Confere\s+com\s+o\s+original|Quartel\b)", RegexOptions.IgnoreCase);

    private static int FindFurrielContextStart(IReadOnlyList<string> lines, int lineIndex)
    {
        var lowerLimit = Math.Max(0, lineIndex - 16);
        for (var i = lineIndex - 1; i >= lowerLimit; i--)
        {
            if (IsFurrielBlockBoundary(lines[i])) return i;
            if (IsProbableFurrielSubject(lines[i])) return i;
        }
        return lowerLimit;
    }

    private static int FindFurrielContextEnd(IReadOnlyList<string> lines, int lineIndex)
    {
        var upperLimit = Math.Min(lines.Count - 1, lineIndex + 10);
        for (var i = lineIndex + 1; i <= upperLimit; i++)
        {
            if (IsFurrielBlockBoundary(lines[i]) || (i > lineIndex + 2 && IsProbableFurrielSubject(lines[i])))
                return i - 1;
        }
        return upperLimit;
    }

    private static bool IsFurrielBlockBoundary(string line)
        => Regex.IsMatch(line, @"^(?:\d+[ªa]?\s+PARTE\b|[A-Z]\)|[a-z]\)|\d+[.)-]\s+)", RegexOptions.IgnoreCase)
           || Regex.IsMatch(line, @"^Nota\s+(?:n[ºo°]?|nr\.?)?\s*[:#-]?\s*\d{2,8}\b", RegexOptions.IgnoreCase);

    private static string DetectFurrielSubject(IReadOnlyList<string> lines, int lineIndex, IReadOnlyList<string> contextLines)
    {
        for (var i = lineIndex; i >= Math.Max(0, lineIndex - 16); i--)
            if (IsProbableFurrielSubject(lines[i])) return CleanFurrielHeading(lines[i]);

        foreach (var line in contextLines)
            if (IsProbableFurrielSubject(line)) return CleanFurrielHeading(line);

        return "Furriel";
    }

    private static bool IsProbableFurrielSubject(string line)
    {
        var text = CleanFurrielHeading(line);
        if (text.Length is < 4 or > 120) return false;
        if (LooksLikeNameLine(text) || RankLineRegex.IsMatch(text)) return false;
        if (ContainsFurrielWord(text) || ContainsConsequenceWord(text)) return false;
        if (Regex.IsMatch(text, @"\b(?:CPF|Prec[- ]?CP|IDT|identidade|filho\(a\)|banco|ag[êe]ncia|conta|valor)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(?:seja|conforme|solicito|providencie|tomem|tome|dever[áaã]|militar|interessad[oa]s?)\b", RegexOptions.IgnoreCase)) return false;
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 4) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= 12 && letters.Count(char.IsUpper) >= letters.Count * 0.65;
    }

    private static string CleanFurrielHeading(string line)
    {
        var text = Regex.Replace(OneLine(line), @"^(?:[a-zA-Z]\)|[a-zA-Z]\.|\d+[.)-]\s+|[-–—•]\s*)", string.Empty).Trim(' ', '-', ':', ';');
        return text.Length > 120 ? text[..120].TrimEnd() : text;
    }

    private static string ExtractFurrielContextText(IReadOnlyList<string> lines, int lineIndex)
    {
        var start = Math.Max(0, lineIndex - 2);
        var end = Math.Min(lines.Count - 1, lineIndex + 3);
        var selected = lines.Skip(start).Take(end - start + 1).Where(line => !IsFurrielStructuralLine(line)).ToList();
        var text = OneLine(string.Join(" ", selected));
        if (!ContainsFurrielWord(text)) text = OneLine(lines[lineIndex]);
        return text;
    }

    private static bool ContainsFurrielWord(string? text)
        => !string.IsNullOrWhiteSpace(text) && FurrielWordRegex.IsMatch(text);

    private static bool IsConvalescenceHomeText(string? text)
    {
        var normalized = Normalize(text);
        return normalized.Contains("convale", StringComparison.Ordinal) &&
               (normalized.Contains("casa", StringComparison.Ordinal) ||
                normalized.Contains("residencia", StringComparison.Ordinal) ||
                normalized.Contains("residencial", StringComparison.Ordinal) ||
                normalized.Contains("domicilio", StringComparison.Ordinal) ||
                normalized.Contains("domiciliar", StringComparison.Ordinal) ||
                normalized.Contains("lar", StringComparison.Ordinal));
    }

    private static IEnumerable<IntelligentBulletinMilitaryOption> ResolveStrongMilitaryMatches(string context, IReadOnlyList<IntelligentBulletinMilitaryOption> people)
    {
        // No BI, quando o Furriel cita militar, só vinculamos pessoa por nome completo
        // ou documento forte. Nome de guerra isolado não é usado para evitar misturar homônimos.
        var normalized = Normalize(context);
        var digits = Digits(context);
        foreach (var person in people)
        {
            var documents = new[] { person.Cpf, person.Identity, person.PrecCp }
                .Select(Digits)
                .Where(x => x.Length >= 5)
                .ToList();
            if (documents.Any(document => digits.Contains(document, StringComparison.Ordinal)))
            {
                yield return person;
                continue;
            }

            var full = Normalize(person.FullName);
            if (full.Length >= 8 && ContainsWholeNormalizedPhrase(normalized, full))
            {
                yield return person;
                continue;
            }

            var tokens = NameTokens(person.FullName).ToList();
            if (tokens.Count >= 3 && tokens.All(token => Regex.IsMatch(normalized, $@"(?:^|\s){Regex.Escape(token)}(?:$|\s)")))
                yield return person;
        }
    }

    private static bool ContainsWholeNormalizedPhrase(string normalizedText, string normalizedPhrase)
        => !string.IsNullOrWhiteSpace(normalizedText) && !string.IsNullOrWhiteSpace(normalizedPhrase) &&
           Regex.IsMatch(normalizedText, $@"(?:^|\s){Regex.Escape(normalizedPhrase)}(?:$|\s)");

    private static bool ContainsConsequenceWord(string? text)
        => Regex.IsMatch(text ?? string.Empty, @"\bEm\s+consequ[êe]ncia\b", RegexOptions.IgnoreCase);

    private static string JoinMilitary(string rank, string name)
        => string.Join(' ', new[] { MilitaryRankService.ShortName(rank), name }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

    private IntelligentBulletinFile ParsePdfLegacy(IReadOnlyList<string> pages, string sourcePath, IReadOnlyList<IntelligentBulletinMilitaryOption> people)
    {
        var fullText = string.Join("\n", pages);
        var number = BulletinNumberRegex.Match(fullText).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(number)) number = InferBulletinNumberFromFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(number)) number = "—";
        var rawDate = BulletinDateRegex.Match(fullText).Groups[1].Value.Trim();
        var parsedDate = ParseBulletinDate(rawDate, number);
        var dateText = parsedDate?.ToString("dd/MM/yyyy") ?? (string.IsNullOrWhiteSpace(rawDate) ? "—" : OneLine(rawDate));
        var file = new IntelligentBulletinFile
        {
            BulletinNumber = number,
            BulletinDate = dateText,
            DateIso = parsedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            Period = parsedDate?.ToString("MMMM/yyyy", PtBr) ?? string.Empty,
            FileName = Path.GetFileName(sourcePath),
            Pages = pages.Count
        };

        var findings = new List<IntelligentBulletinFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var cleanPage = SanitizeText(pages[pageIndex]);
            var lines = cleanPage.Split('\n').Select(OneLine).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var pageNumber = pageIndex + 1;

            // Escala de serviço: limita a leitura ao bloco de cada dia, remove Apoios/Perito
            // e informa a cor sugerida (fim de semana em vermelho), como no módulo Python.
            var serviceMatches = ServiceDayRegex.Matches(cleanPage).Cast<Match>().ToList();
            for (var serviceIndex = 0; serviceIndex < serviceMatches.Count; serviceIndex++)
            {
                var match = serviceMatches[serviceIndex];
                var blockEnd = serviceIndex + 1 < serviceMatches.Count ? serviceMatches[serviceIndex + 1].Index : cleanPage.Length;
                var block = cleanPage.Substring(match.Index + match.Length, Math.Max(0, blockEnd - match.Index - match.Length));
                block = Regex.Split(block, @"\bApoios\b", RegexOptions.IgnoreCase)[0];
                var serviceLines = block.Split('\n').Select(OneLine)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !Regex.IsMatch(x, @"\bPerito\b", RegexOptions.IgnoreCase))
                    .Where(x => Normalize(x) is not "servicos externos" and not "servicos internos" and not "escala de servico")
                    .ToList();
                var weekday = match.Groups[4].Value.Trim().ToUpperInvariant();
                var red = Normalize(weekday) is "sabado" or "domingo";
                var dateLabel = $"{int.Parse(match.Groups[1].Value, PtBr):00}/{MonthNumber(match.Groups[2].Value):00}/{NormalizeYear(match.Groups[3].Value)}";
                var detail = $"{dateLabel} · {weekday} · cor sugerida: {(red ? "VERMELHA" : "PRETA")} · {serviceLines.Count} item(ns) detectado(s)";
                AddFinding("Serviço", "Escala de serviço", detail, string.Join(" ", serviceLines), pageNumber);
            }

            // Férias: trata separadamente alteração (Do/Para) e inclusão por militar.
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                if (Regex.IsMatch(line, @"Altero\s+o\s+plano\s+de\s+f[eé]rias", RegexOptions.IgnoreCase))
                {
                    var block = new List<string>();
                    var end = lineIndex;
                    while (end < lines.Count)
                    {
                        block.Add(lines[end]);
                        if (IsConsequenceLine(lines[end])) break;
                        end++;
                    }
                    var from = new List<string>(); var to = new List<string>(); var military = string.Empty; var mode = string.Empty;
                    foreach (var value in block.Skip(1))
                    {
                        if (Regex.IsMatch(value, @"^Do:$", RegexOptions.IgnoreCase)) { mode = "from"; continue; }
                        if (Regex.IsMatch(value, @"^Para:$", RegexOptions.IgnoreCase)) { mode = "to"; continue; }
                        if (LooksLikeNameLine(value)) { military = value; continue; }
                        if (IsConsequenceLine(value)) break;
                        if (mode == "from") from.Add(value); else if (mode == "to") to.Add(value);
                    }
                    if (!string.IsNullOrWhiteSpace(military))
                    {
                        var context = string.Join(" ", block);
                        var detail = FormatVacationChange(string.Join(" ", from), string.Join(" ", to), context);
                        AddFinding("Férias", "Alteração", detail, context, pageNumber, lines, lineIndex);
                    }
                    lineIndex = Math.Max(lineIndex, end);
                    continue;
                }

                if (Regex.IsMatch(line, @"Seja\s+inclu[ií]do\s+no\s+Plano\s+de\s+F[eé]rias", RegexOptions.IgnoreCase))
                {
                    var end = lineIndex + 1;
                    var currentMilitary = string.Empty;
                    var periods = new List<string>();
                    while (end < lines.Count)
                    {
                        var value = lines[end];
                        if (IsConsequenceLine(value))
                        {
                            FlushVacationInclusion(currentMilitary, periods, lineIndex, end);
                            break;
                        }
                        if (LooksLikeNameLine(value))
                        {
                            FlushVacationInclusion(currentMilitary, periods, lineIndex, end);
                            currentMilitary = value; periods = [];
                        }
                        else if (LooksLikePeriodLine(value) || periods.Count > 0) periods.Add(value);
                        else if (IsProbableHeading(value)) break;
                        end++;
                    }
                    lineIndex = Math.Max(lineIndex, end);
                }
            }

            // Pagamentos e saques: começa no militar mais próximo e termina antes do próximo bloco.
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (!PaymentRegex.IsMatch(lines[lineIndex])) continue;
                var start = lineIndex;
                for (var index = lineIndex; index >= Math.Max(0, lineIndex - 8); index--)
                    if (LooksLikeNameLine(lines[index])) { start = index; break; }
                var end = Math.Min(lines.Count, lineIndex + 10);
                for (var index = lineIndex + 1; index < Math.Min(lines.Count, lineIndex + 12); index++)
                {
                    if (IsConsequenceLine(lines[index])) { end = index + 1; break; }
                    if (index > lineIndex + 1 && LooksLikeNameLine(lines[index]) && start < lineIndex) { end = index; break; }
                    if (index > lineIndex && IsProbableHeading(lines[index]) && !PaymentRegex.IsMatch(lines[index])) { end = index; break; }
                }
                var block = lines.Skip(start).Take(Math.Max(1, end - start)).ToList();
                var context = string.Join(" ", block);
                AddFinding("Pagamento", ClassifyPayment(context), lines[lineIndex], context, pageNumber, lines, lineIndex);
            }

            // Convalescença: somente quando o texto informa residência ou casa.
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var normalizedLine = Normalize(lines[lineIndex]);
                if (!normalizedLine.Contains("convale", StringComparison.Ordinal) ||
                    (!normalizedLine.Contains("residencia", StringComparison.Ordinal) && !normalizedLine.Contains("casa", StringComparison.Ordinal))) continue;
                var start = Math.Max(0, lineIndex - 7);
                var end = Math.Min(lines.Count, lineIndex + 10);
                var block = new List<string>();
                for (var index = start; index < end; index++)
                {
                    if (index > lineIndex && IsConsequenceLine(lines[index])) break;
                    if (index > lineIndex && IsProbableHeading(lines[index]) && !ConvalescenceRegex.IsMatch(lines[index])) break;
                    block.Add(lines[index]);
                }
                AddFinding("Convalescença", "Convalescença em residência/casa", lines[lineIndex], string.Join(" ", block), pageNumber, lines, lineIndex);
            }

            // Alertas administrativos: somente a consequência e o contexto imediatamente anterior.
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (!IsConsequenceLine(lines[lineIndex])) continue;
                var consequence = new List<string> { lines[lineIndex] };
                for (var index = lineIndex + 1; index < lines.Count && consequence.Count < 5; index++)
                {
                    if (IsProbableHeading(lines[index]) || LooksLikeNameLine(lines[index])) break;
                    consequence.Add(lines[index]);
                }
                var contextStart = Math.Max(0, lineIndex - 18);
                var previous = lines.Skip(contextStart).Take(lineIndex - contextStart).ToList();
                var lastHeading = previous.FindLastIndex(IsProbableHeading);
                if (lastHeading >= 0) previous = previous.Skip(lastHeading + 1).ToList();
                var context = string.Join(" ", previous);
                var consequenceText = string.Join(" ", consequence);
                var furriel = Regex.IsMatch(consequenceText, @"\bfurriel\b", RegexOptions.IgnoreCase);
                var type = ClassifyAdministrative(context, consequenceText, furriel);
                if (string.IsNullOrWhiteSpace(type)) continue;
                AddFinding("Alertas Adm", type, consequenceText, context + " " + consequenceText, pageNumber, lines, lineIndex);
            }

            // Demais assuntos relevantes, limitados pela desduplicação global.
            foreach (var (type, pattern) in OtherPatterns.Where(x => x.Type != "Furriel"))
            {
                var added = 0;
                for (var lineIndex = 0; lineIndex < lines.Count && added < 3; lineIndex++)
                {
                    if (!pattern.IsMatch(lines[lineIndex])) continue;
                    AddFinding("Outros", type, lines[lineIndex], WindowContext(lines, lineIndex, 3), pageNumber, lines, lineIndex);
                    added++;
                }
            }

            void FlushVacationInclusion(string military, List<string> periods, int start, int end)
            {
                if (string.IsNullOrWhiteSpace(military)) return;
                var context = string.Join(" ", lines.Skip(start).Take(Math.Max(1, Math.Min(lines.Count, end + 1) - start)));
                var periodText = FormatVacationInclusion(periods.Count == 0 ? context : string.Join(" ", periods));
                AddFinding("Férias", "Inclusão", periodText, context, pageNumber, lines, start);
            }
        }

        // Furriel tem prioridade sobre Férias para a mesma pessoa e publicação.
        var furrielRows = findings.Where(x => x.Category == "Alertas Adm" && x.Type.Contains("Furriel", StringComparison.OrdinalIgnoreCase)).ToList();
        if (furrielRows.Count > 0)
        {
            findings.RemoveAll(vacation => vacation.Category == "Férias" && furrielRows.Any(furriel =>
                vacation.Page == furriel.Page && SameMilitary(vacation, furriel)));
        }
        file.Findings = findings.OrderBy(x => CategoryOrder(x.Category)).ThenBy(x => x.Page).ThenBy(x => x.DisplayMilitary).ToList();
        return file;

        void AddFinding(string category, string type, string detail, string context, int page, IReadOnlyList<string>? pageLines = null, int lineIndex = -1)
        {
            context = OneLine(context);
            detail = OneLine(detail);
            var note = pageLines is not null && lineIndex >= 0
                ? ResolveFindingNote(pageLines, lineIndex, context)
                : new BulletinNote { Context = context, Preview = TruncateText(context, 260) };
            if (!string.IsNullOrWhiteSpace(note.Context)) context = note.Context;
            var subject = category.Equals("Serviço", StringComparison.OrdinalIgnoreCase) ? type : FirstNonEmpty(note.Subject, type);
            var preview = FirstNonEmpty(note.Preview, TruncateText(context, 260), detail);
            if (detail.Length > 420) detail = detail[..420].TrimEnd() + "…";
            var person = ResolveMilitary(context, people);
            var extracted = person is null ? ExtractProbableName(context) : string.Empty;
            var fullName = person?.FullName ?? extracted;
            var warName = person?.WarName ?? string.Empty;
            var rank = person?.Rank ?? ExtractRank(context);
            var search = BestPdfSearchTerm(fullName, warName);
            var idSeed = string.Join("|", category, type, number, page.ToString(CultureInfo.InvariantCulture), Normalize(fullName), Normalize(detail));
            var id = ShortHash(idSeed);
            if (!seen.Add(id)) return;
            findings.Add(new IntelligentBulletinFinding
            {
                Id = id, Category = category, Type = type, Bulletin = number, BulletinDate = dateText,
                Military = string.IsNullOrWhiteSpace(fullName) ? "—" : $"{MilitaryRankService.ShortName(rank)} {fullName}".Trim(),
                FullName = fullName, WarName = warName, Rank = rank, Subject = subject, Preview = preview, Detail = detail, Context = context,
                Page = page, FileName = Path.GetFileName(sourcePath), PdfPath = sourcePath, PdfSearchTerm = search
            });
        }
    }

    private static IntelligentBulletinMilitaryOption? ResolveMilitary(string context, IReadOnlyList<IntelligentBulletinMilitaryOption> people)
    {
        var normalized = Normalize(context);
        var digits = Digits(context);
        IntelligentBulletinMilitaryOption? best = null;
        var bestScore = 0;
        foreach (var person in people)
        {
            var score = 0;
            foreach (var document in new[] { person.Cpf, person.Identity, person.PrecCp }.Select(Digits).Where(x => x.Length >= 5)) if (digits.Contains(document, StringComparison.Ordinal)) score += 1200;
            var full = Normalize(person.FullName);
            var war = Normalize(person.WarName);
            if (!string.IsNullOrWhiteSpace(full) && normalized.Contains(full, StringComparison.Ordinal)) score += 1000;
            if (!string.IsNullOrWhiteSpace(war) && war.Length >= 3 && Regex.IsMatch(normalized, $@"(?:^|\s){Regex.Escape(war)}(?:$|\s)")) score += 360;
            var tokens = NameTokens(person.FullName).ToList();
            var hits = tokens.Count(token => Regex.IsMatch(normalized, $@"(?:^|\s){Regex.Escape(token)}(?:$|\s)"));
            if (tokens.Count >= 3 && hits == tokens.Count) score += 700;
            else if (hits >= Math.Min(3, tokens.Count) && hits >= 2) score += hits * 120;
            if (score > bestScore) { bestScore = score; best = person; }
        }
        return bestScore >= 360 ? best : null;
    }

    private static string ExtractProbableName(string context)
    {
        foreach (var segment in Regex.Split(context, @"[;\n]").Select(OneLine))
        {
            if (!RankLineRegex.IsMatch(segment)) continue;
            var value = RankLineRegex.Replace(segment, string.Empty).Trim(' ', '-', '–', '—', ':', ';', ',');
            value = Regex.Split(value, @"\b(?:CPF|Prec[- ]?CP|IDT|por|para|referente|conforme)\b", RegexOptions.IgnoreCase)[0].Trim();
            if (value.Length >= 4) return value.Length > 100 ? value[..100] : value;
        }
        return string.Empty;
    }

    private static string ExtractRank(string context)
    {
        foreach (var segment in Regex.Split(context, @"[;\n]").Select(OneLine))
        {
            var match = RankLineRegex.Match(segment);
            if (match.Success) return match.Value;
        }
        return string.Empty;
    }

    private async Task<List<string>> ExtractPdfPagesAsync(string path, CancellationToken cancellationToken)
    {
        // Extração nativa em C#: não depende de Python, pypdf, PyPDF2 ou Poppler.
        var pages = await App.PdfText.ExtractPagesAsync(path, cancellationToken);
        var result = pages.Select(x => x ?? string.Empty).ToList();
        while (result.Count > 1 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        if (result.Any(x => !string.IsNullOrWhiteSpace(x))) return result;
        throw new InvalidOperationException("O PDF não possui texto pesquisável. Use o arquivo original exportado pelo sistema ou aplique OCR antes de importar.");
    }

    private async Task<List<string>> ExpandSourcesAsync(IEnumerable<string> sources, List<string> tempDirectories, List<string> errors, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Where(x => !string.IsNullOrWhiteSpace(x))) await ExpandOneAsync(Path.GetFullPath(source));
        return result;

        async Task ExpandOneAsync(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(x => x.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) await ExpandOneAsync(file);
                return;
            }
            if (!File.Exists(path)) { errors.Add("Arquivo não encontrado: " + path); return; }
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { if (seen.Add(path)) result.Add(path); return; }
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
            var directory = Path.Combine(_paths.IntelligentBulletinTempDirectory, "zip_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); tempDirectories.Add(directory);
            try
            {
                using var archive = ZipFile.OpenRead(path);
                var counter = 0;
                foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    var destination = Path.Combine(directory, $"{++counter:0000}_{SafeFileName(Path.GetFileName(entry.FullName))}");
                    await using var input = entry.Open(); await using var output = File.Create(destination); await input.CopyToAsync(output, cancellationToken);
                    if (seen.Add(destination)) result.Add(destination);
                }
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
    }

    private static List<IntelligentBulletinFinding> ReadLegacyFindings(string json, IntelligentBulletinFile item)
    {
        var findings = new List<IntelligentBulletinFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return findings;

        var bulletin = FirstNonEmpty(JsonText(root, "numero_bi", "boletim"), item.BulletinNumber, "—");
        var bulletinDate = FirstNonEmpty(JsonText(root, "data_bi", "data"), item.BulletinDate, "—");
        if (string.IsNullOrWhiteSpace(item.BulletinNumber) || item.BulletinNumber == "—") item.BulletinNumber = bulletin;
        if (string.IsNullOrWhiteSpace(item.BulletinDate) || item.BulletinDate == "—") item.BulletinDate = bulletinDate;

        AddArray("servicos", "Serviço", "Serviço / escala");
        AddArray("ferias", "Férias", "Férias");
        AddArray("convalescencas", "Convalescença", "Convalescença");
        AddArray("pagamentos", "Pagamento", "Pagamento");
        AddArray("alertas_admin", "Alertas Adm", "Alerta administrativo");
        AddArray("outros", "Outros", "Menção");
        return findings.OrderBy(x => CategoryOrder(x.Category)).ThenBy(x => x.Page).ThenBy(x => x.DisplayMilitary).ToList();

        void AddArray(string propertyName, string category, string defaultType)
        {
            if (!TryProperty(root, propertyName, out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var type = FirstNonEmpty(JsonText(element, "tipo", "categoria"), defaultType);
                var militaryRaw = FirstNonEmpty(JsonText(element, "militar", "militares", "nome", "nome_completo"), "—");
                var rank = FirstNonEmpty(JsonText(element, "posto", "pg", "graduacao"), ExtractRank(militaryRaw));
                var fullName = JsonText(element, "nome_completo", "nome");
                if (string.IsNullOrWhiteSpace(fullName)) fullName = StripRankPrefix(militaryRaw);
                var warName = JsonText(element, "nome_guerra", "guerra");
                var context = FirstNonEmpty(JsonText(element, "contexto", "texto", "consequencia"), militaryRaw);
                var detail = BuildLegacyDetail(element, category);
                if (string.IsNullOrWhiteSpace(detail)) detail = context;
                var page = JsonInt(element, "pagina", "page");
                if (page <= 0) page = 1;
                var id = FirstNonEmpty(JsonText(element, "_review_id", "review_id", "id"));
                if (string.IsNullOrWhiteSpace(id))
                    id = ShortHash(string.Join("|", category, type, bulletin, page.ToString(CultureInfo.InvariantCulture), Normalize(fullName), Normalize(detail)));
                if (!seen.Add(id)) continue;
                findings.Add(new IntelligentBulletinFinding
                {
                    Id = id,
                    Category = category,
                    Type = type,
                    Bulletin = bulletin,
                    BulletinDate = bulletinDate,
                    Military = militaryRaw,
                    FullName = fullName,
                    WarName = warName,
                    Rank = rank,
                    Detail = OneLine(detail),
                    Context = OneLine(context),
                    Page = page,
                    FileName = item.FileName,
                    PdfPath = item.PdfPath,
                    PdfSearchTerm = BestPdfSearchTerm(fullName, warName)
                });
            }
        }
    }

    private static string BuildLegacyDetail(JsonElement element, string category)
    {
        var detail = JsonText(element, "detalhe", "resumo", "texto", "consequencia", "contexto");
        if (category == "Serviço")
        {
            var date = FirstNonEmpty(JsonText(element, "data", "dia"));
            var weekday = JsonText(element, "dia_semana", "semana");
            var items = JsonText(element, "itens", "servicos", "escala");
            return OneLine(string.Join(" · ", new[] { date, weekday, items, detail }.Where(x => !string.IsNullOrWhiteSpace(x))));
        }
        if (category == "Férias")
        {
            var from = JsonText(element, "de", "periodo_anterior", "origem");
            var to = JsonText(element, "para", "periodo_novo", "destino");
            var movement = string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to) ? string.Empty : $"De {from} para {to}".Trim();
            return OneLine(string.Join(" · ", new[] { movement, detail }.Where(x => !string.IsNullOrWhiteSpace(x))));
        }
        return OneLine(detail);
    }

    private static string StripRankPrefix(string value)
    {
        var text = OneLine(value);
        var match = RankLineRegex.Match(text);
        return match.Success ? text[match.Length..].Trim(' ', '-', '–', '—', ':') : text;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string JsonText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.ToString();
                case JsonValueKind.Array:
                    var parts = value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x));
                    var joined = string.Join(", ", parts);
                    if (!string.IsNullOrWhiteSpace(joined)) return joined;
                    break;
            }
        }
        return string.Empty;
    }

    private static int JsonInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        return 0;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private async Task<IntelligentBulletinStore> ReadStoreUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.BulletinIndexFile)) return new IntelligentBulletinStore();
        try
        {
            var indexJson = await File.ReadAllTextAsync(_paths.BulletinIndexFile, cancellationToken);
            IntelligentBulletinStore store;
            using (var document = JsonDocument.Parse(indexJson))
            {
                store = document.RootElement.ValueKind == JsonValueKind.Array
                    ? new IntelligentBulletinStore { Items = JsonSerializer.Deserialize<List<IntelligentBulletinFile>>(indexJson, JsonOptions) ?? [] }
                    : JsonSerializer.Deserialize<IntelligentBulletinStore>(indexJson, JsonOptions) ?? new IntelligentBulletinStore();
            }
            store.Items ??= [];
            foreach (var item in store.Items)
            {
                item.Findings ??= [];
                item.Mentions ??= [];
                foreach (var finding in item.Findings)
                {
                    finding.FullName = UpperPersonName(finding.FullName);
                    finding.WarName = UpperPersonName(finding.WarName);
                    finding.Military = UpperPersonName(finding.Military);
                }
                item.PdfPath = ResolveDataPath(item.PdfPath);
                item.TextCachePath = ResolveDataPath(item.TextCachePath);
                item.ParseCachePath = ResolveDataPath(item.ParseCachePath);
                if (string.IsNullOrWhiteSpace(item.Period))
                    item.Period = InferStoredPeriod(item);
                if (item.Findings.Count == 0 && File.Exists(item.ParseCachePath))
                {
                    try
                    {
                        var cacheJson = await File.ReadAllTextAsync(item.ParseCachePath, cancellationToken);
                        var cached = JsonSerializer.Deserialize<IntelligentBulletinFile>(cacheJson, JsonOptions);
                        if (cached?.Findings.Count > 0)
                        {
                            item.Findings = cached.Findings;
                            item.Mentions = cached.Mentions ?? [];
                            item.ParserVersion = Math.Max(item.ParserVersion, cached.ParserVersion);
                        }
                        else
                        {
                            item.Findings = ReadLegacyFindings(cacheJson, item);
                        }
                    }
                    catch { }
                }
                BindPaths(item, item.PdfPath);
            }
            return store;
        }
        catch (Exception ex) { await _log.WriteAsync("Falha ao carregar índice do Boletim Inteligente.", ex); return new IntelligentBulletinStore(); }
    }

    private static string UpperPersonName(string? value)
        => string.Join(" ", (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpper(PtBr);

    private async Task<bool> UpgradeStaleParserItemsUnsafeAsync(IntelligentBulletinStore store, Dictionary<string, IntelligentBulletinReviewEntry> reviews, CancellationToken cancellationToken)
    {
        store.Items ??= [];
        var staleItems = store.Items
            .Where(item => (store.Version < CurrentParserVersion || item.ParserVersion < CurrentParserVersion) && File.Exists(item.PdfPath))
            .ToList();

        if (staleItems.Count == 0)
        {
            if (store.Version >= CurrentParserVersion) return false;
            store.Version = CurrentParserVersion;
            return true;
        }

        // Reprocessa automaticamente índices antigos para novas regras do BI.
        // Assim a aba Convalescença em casa aparece ao clicar Atualizar lista, sem depender de reimportar PDF.
        var people = (await LoadMilitaryOptionsAsync("Ativos + licenciados/transferidos", cancellationToken)).ToList();
        var changed = false;
        foreach (var item in staleItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = store.Items.IndexOf(item);
            if (position < 0 || !File.Exists(item.PdfPath)) continue;

            try
            {
                var pages = await ExtractPdfPagesAsync(item.PdfPath, cancellationToken);
                var reparsed = ParsePdf(pages, item.PdfPath, people);
                reparsed.Id = item.Id;
                reparsed.HashSha256 = item.HashSha256;
                reparsed.PdfPath = item.PdfPath;
                reparsed.FileName = item.FileName;
                reparsed.OriginalFileName = item.OriginalFileName;
                reparsed.SourceFolder = item.SourceFolder;
                reparsed.SourceFolderLabel = item.SourceFolderLabel;
                reparsed.SavedAt = item.SavedAt;
                reparsed.SizeBytes = item.SizeBytes;
                BindPaths(reparsed, item.PdfPath);

                foreach (var old in item.Findings) reviews.Remove(old.Id);
                store.Items[position] = reparsed;
                await WriteCachesUnsafeAsync(reparsed, pages, cancellationToken);
                changed = true;
            }
            catch (Exception ex)
            {
                await _log.WriteAsync("Falha ao atualizar parser do Boletim Inteligente: " + item.PdfPath, ex);
            }
        }

        store.Version = CurrentParserVersion;
        return changed || staleItems.Count > 0;
    }

    private static string InferStoredPeriod(IntelligentBulletinFile item)
    {
        DateTime date;
        if (DateTime.TryParseExact(item.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParse(item.BulletinDate, PtBr, DateTimeStyles.AllowWhiteSpaces, out date))
            return date.ToString("MMMM/yyyy", PtBr);

        var source = string.Join(" ", item.SourceFolder, item.SourceFolderLabel, item.PdfPath, item.FileName);
        var monthNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["JANEIRO"] = 1, ["JAN"] = 1, ["FEVEREIRO"] = 2, ["FEV"] = 2,
            ["MARCO"] = 3, ["MAR"] = 3, ["ABRIL"] = 4, ["ABR"] = 4,
            ["MAIO"] = 5, ["MAI"] = 5, ["JUNHO"] = 6, ["JUN"] = 6,
            ["JULHO"] = 7, ["JUL"] = 7, ["AGOSTO"] = 8, ["AGO"] = 8,
            ["SETEMBRO"] = 9, ["SET"] = 9, ["OUTUBRO"] = 10, ["OUT"] = 10,
            ["NOVEMBRO"] = 11, ["NOV"] = 11, ["DEZEMBRO"] = 12, ["DEZ"] = 12
        };
        var normalized = Normalize(source).ToUpperInvariant();
        var yearMatch = Regex.Match(normalized, @"\b(20\d{2})\b");
        var year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        foreach (var pair in monthNames)
        {
            if (year > 0 && Regex.IsMatch(normalized, $@"\b{Regex.Escape(pair.Key)}\b"))
                return new DateTime(year, pair.Value, 1).ToString("MMMM/yyyy", PtBr);
        }
        var numeric = Regex.Match(normalized, @"(?<!\d)(0?[1-9]|1[0-2])[-_ /](20\d{2})(?!\d)");
        if (numeric.Success)
            return new DateTime(int.Parse(numeric.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture), 1).ToString("MMMM/yyyy", PtBr);
        return string.Empty;
    }

    private async Task WriteStoreUnsafeAsync(IntelligentBulletinStore store, CancellationToken cancellationToken)
    {
        var temp = _paths.BulletinIndexFile + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.BulletinIndexFile)!);
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
        File.Move(temp, _paths.BulletinIndexFile, true);
    }

    private async Task<Dictionary<string, IntelligentBulletinReviewEntry>> ReadReviewsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.IntelligentBulletinReviewFile)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var stream = File.OpenRead(_paths.IntelligentBulletinReviewFile);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, IntelligentBulletinReviewEntry>>(stream, JsonOptions, cancellationToken) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private async Task WriteReviewsUnsafeAsync(Dictionary<string, IntelligentBulletinReviewEntry> reviews, CancellationToken cancellationToken)
    {
        var temp = _paths.IntelligentBulletinReviewFile + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, reviews, JsonOptions, cancellationToken);
        File.Move(temp, _paths.IntelligentBulletinReviewFile, true);
    }

    private static void ApplyReviews(IntelligentBulletinStore store, IReadOnlyDictionary<string, IntelligentBulletinReviewEntry> reviews)
    {
        foreach (var item in store.Items)
        foreach (var finding in item.Findings)
            finding.Reviewed = reviews.TryGetValue(finding.Id, out var review) && review.Ok;
    }

    private async Task WriteCachesUnsafeAsync(IntelligentBulletinFile item, IReadOnlyList<string> pages, CancellationToken cancellationToken)
    {
        item.TextCachePath = Path.Combine(_paths.IntelligentBulletinTextDirectory, Path.GetFileNameWithoutExtension(item.FileName) + "_" + item.Id + ".txt");
        item.ParseCachePath = Path.Combine(_paths.IntelligentBulletinParseDirectory, Path.GetFileNameWithoutExtension(item.FileName) + "_" + item.Id + ".json");
        await File.WriteAllTextAsync(item.TextCachePath, string.Join("\f", pages), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(item.ParseCachePath, JsonSerializer.Serialize(item, JsonOptions), new UTF8Encoding(false), cancellationToken);
    }

    private string CreateLibraryPath(IntelligentBulletinFile item)
    {
        var number = SafeFileName(item.BulletinNumber.Replace('/', '-'));
        var baseName = SafeFileName(Path.GetFileNameWithoutExtension(item.OriginalFileName));
        var name = $"BI_{number}_{baseName}.pdf";
        var path = Path.Combine(_paths.IntelligentBulletinLibraryDirectory, name);
        var index = 2;
        while (File.Exists(path)) path = Path.Combine(_paths.IntelligentBulletinLibraryDirectory, $"BI_{number}_{baseName}_{index++}.pdf");
        return path;
    }

    private static void BindPaths(IntelligentBulletinFile item, string pdfPath)
    {
        item.PdfPath = pdfPath;
        if (string.IsNullOrWhiteSpace(item.FileName)) item.FileName = Path.GetFileName(pdfPath);
        foreach (var finding in item.Findings) { finding.PdfPath = pdfPath; finding.FileName = item.FileName; finding.Bulletin = item.BulletinNumber; finding.BulletinDate = item.BulletinDate; }
        foreach (var mention in item.Mentions) mention.SourceFilePath = pdfPath;
    }

    private string ResolveDataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.IsPathRooted(path) ? path : Path.Combine(_paths.DataDirectory, path);
    }

    private static bool LooksLikeNameLine(string value)
    {
        var text = OneLine(value);
        var match = RankLineRegex.Match(text);
        if (!match.Success) return false;
        if (text.Length > match.Length && (text[match.Length] == '/' || text[match.Length] == ',')) return false;
        if (Regex.IsMatch(text, @"\b(?:situad[oa]|bairro|apartamento|apto|rua|avenida|valor|conta|ag[êe]ncia)\b", RegexOptions.IgnoreCase)) return false;
        var rest = text[match.Length..].Trim(' ', '-', ':');
        return Regex.Matches(rest, @"\b[A-ZÁÀÂÃÉÊÍÓÔÕÚÇ]{2,}\b").Count >= 1;
    }
    private static bool IsConsequenceLine(string value) => Regex.IsMatch(OneLine(value), @"^Em\s+consequ", RegexOptions.IgnoreCase);
    private static bool LooksLikePeriodLine(string value)
        => Regex.IsMatch(OneLine(value),
            @"(?:\d{1,2}º?\s+Per[ií]odo|Per[ií]odo\s+Especial|\b\d{1,2}/\d{1,2}/\d{2,4}\b|\b\d{1,2}\s+[A-ZÇ]{3,12}\.?\s+\d{2,4}\b)",
            RegexOptions.IgnoreCase);

    private static string FormatVacationChange(string from, string to, string context)
    {
        var fromPeriod = ExtractVacationRanges(from).FirstOrDefault();
        var toPeriod = ExtractVacationRanges(to).FirstOrDefault();
        var all = ExtractVacationRanges(context);

        if (string.IsNullOrWhiteSpace(fromPeriod) && all.Count > 0) fromPeriod = all[0];
        if (string.IsNullOrWhiteSpace(toPeriod) && all.Count > 1) toPeriod = all[1];

        var fromText = string.IsNullOrWhiteSpace(fromPeriod) ? CleanVacationText(from) : fromPeriod;
        var toText = string.IsNullOrWhiteSpace(toPeriod) ? CleanVacationText(to) : toPeriod;
        if (string.IsNullOrWhiteSpace(fromText) && string.IsNullOrWhiteSpace(toText))
            return "Período não identificado no PDF";
        return $"Do: {FirstNonEmpty(fromText, "não identificado")} · Para: {FirstNonEmpty(toText, "não identificado")}";
    }

    private static string FormatVacationInclusion(string value)
    {
        var ranges = ExtractVacationRanges(value);
        if (ranges.Count > 0) return string.Join(" | ", ranges);
        var cleaned = CleanVacationText(value);
        return string.IsNullOrWhiteSpace(cleaned) ? "Período não identificado no PDF" : cleaned;
    }

    private static List<string> ExtractVacationRanges(string value)
    {
        var text = OneLine(value);
        var datePattern = @"(?:\d{1,2}/\d{1,2}/\d{2,4}|\d{1,2}\s+[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{3,12}\.?\s+\d{2,4})";
        var rangeRegex = new Regex($@"(?<a>{datePattern})\s*(?:a|até|ao|-|–|—)\s*(?<b>{datePattern})", RegexOptions.IgnoreCase);
        var result = rangeRegex.Matches(text).Cast<Match>()
            .Select(match => $"{NormalizeVacationDate(match.Groups["a"].Value)} a {NormalizeVacationDate(match.Groups["b"].Value)}")
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (result.Count > 0) return result;
        var dates = Regex.Matches(text, datePattern, RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => NormalizeVacationDate(match.Value)).ToList();
        for (var index = 0; index + 1 < dates.Count; index += 2)
            result.Add($"{dates[index]} a {dates[index + 1]}");
        return result.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string NormalizeVacationDate(string value)
    {
        var text = OneLine(value).Trim('.', ',', ';');
        foreach (var format in new[] { "d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "dd/MM/yy" })
            if (DateTime.TryParseExact(text, format, PtBr, DateTimeStyles.None, out var numeric))
                return numeric.ToString("dd/MM/yyyy", PtBr);

        var match = Regex.Match(text, @"^(?<d>\d{1,2})\s+(?<m>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{3,12})\.?\s+(?<y>\d{2,4})$", RegexOptions.IgnoreCase);
        if (!match.Success) return text.ToUpperInvariant();
        var month = MonthNumber(match.Groups["m"].Value);
        var year = int.TryParse(match.Groups["y"].Value, out var parsedYear) ? parsedYear : DateTime.Today.Year;
        if (year < 100) year += 2000;
        return int.TryParse(match.Groups["d"].Value, out var day) &&
               DateTime.TryParse($"{day:00}/{month:00}/{year:0000}", PtBr, DateTimeStyles.None, out var date)
            ? date.ToString("dd/MM/yyyy", PtBr)
            : text.ToUpperInvariant();
    }

    private static string CleanVacationText(string value)
    {
        var text = OneLine(value);
        text = Regex.Replace(text, @"(?:^|\s)(?:Do|Para)\s*:\s*", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '–', '—', '.', ':');
        return text is "--" or "- - " or "--.--" ? string.Empty : text;
    }

    private static BulletinNote ResolveFindingNote(IReadOnlyList<string> lines, int lineIndex, string context)
    {
        if (lines.Count == 0) return new BulletinNote { Context = context, Preview = TruncateText(context, 260) };
        lineIndex = Math.Clamp(lineIndex, 0, lines.Count - 1);
        var titleIndex = FindFindingNoteTitleIndex(lines, lineIndex);
        var subject = titleIndex >= 0 ? FormatNoteTitle(lines[titleIndex]) : string.Empty;
        var start = titleIndex >= 0 ? titleIndex : Math.Max(0, lineIndex - 4);
        var end = FindFindingNoteEndIndex(lines, start, lineIndex);
        var rows = CleanFindingRows(lines.Skip(start).Take(Math.Max(1, end - start + 1))).ToList();
        if (rows.Count == 0) rows = CleanFindingRows(lines.Skip(Math.Max(0, lineIndex - 3)).Take(7)).ToList();

        var full = OneLine(string.Join(' ', rows));
        var preview = TruncateText(OneLine(string.Join(' ', rows.Take(7))), 260);
        var review = BuildFindingReviewContext(rows, lines, lineIndex);
        return new BulletinNote
        {
            Subject = subject,
            Preview = string.IsNullOrWhiteSpace(preview) ? TruncateText(full, 260) : preview,
            Context = string.IsNullOrWhiteSpace(review) ? context : review
        };
    }

    private static int FindFindingNoteTitleIndex(IReadOnlyList<string> lines, int lineIndex)
    {
        var first = Math.Max(0, lineIndex - 120);
        for (var candidate = lineIndex; candidate >= first; candidate--)
        {
            var text = OneLine(lines[candidate]);
            if (IsLikelyFindingNoteTitle(text)) return candidate;
            if (candidate < lineIndex && IsFindingHardBoundary(text) && lineIndex - candidate > 8) break;
        }
        return -1;
    }

    private static int FindFindingNoteEndIndex(IReadOnlyList<string> lines, int start, int anchor)
    {
        var end = Math.Clamp(anchor, start, lines.Count - 1);
        for (var candidate = Math.Max(start + 1, anchor + 1); candidate < lines.Count; candidate++)
        {
            var text = OneLine(lines[candidate]);
            if (candidate > anchor && (IsLikelyFindingNoteTitle(text) || IsFindingHardBoundary(text))) break;
            end = candidate;
            if (candidate - start >= 70) break;
        }
        return end;
    }

    private static IEnumerable<string> CleanFindingRows(IEnumerable<string> rows)
    {
        foreach (var row in rows)
        {
            var text = OneLine(row);
            if (text.Length == 0 || IsStructuralFindingLine(text)) continue;
            yield return IsLikelyFindingNoteTitle(text)
                ? FormatNoteTitle(text)
                : text.Replace('–', '-').Replace('—', '-');
        }
    }

    private static string BuildFindingReviewContext(IReadOnlyList<string> rows, IReadOnlyList<string> lines, int lineIndex)
    {
        var full = OneLine(string.Join(' ', rows));
        if (full.Length <= 1000) return full;
        var head = TruncateText(OneLine(string.Join(' ', rows.Take(7))), 560);
        var occurrence = TruncateText(OneLine(string.Join(' ', CleanFindingRows(lines.Skip(Math.Max(0, lineIndex - 2)).Take(7)))), 390);
        return string.IsNullOrWhiteSpace(occurrence) ? TruncateText(full, 1000) : TruncateText($"{head} ... Ocorrência: {occurrence}", 1000);
    }

    private static bool IsLikelyFindingNoteTitle(string value)
    {
        var text = OneLine(value);
        if (text.Length < 4 || text.Length > 180) return false;
        if (IsStructuralFindingLine(text) || LooksLikeNameLine(text)) return false;
        if (Regex.IsMatch(text, @"\b(?:Prec[- ]?CP|CPF|IDT|R\$|Valor\s+(?:Di[aá]rio|Total)|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"^(?:Em consequ|Seja\s+|No requerimento|Tendo em vista|Referente\b)", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"^[a-z]\.\s+\S", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, @"\s[-–—]\s") && LooksLikeStandaloneNoteTitle(text)) return true;
        return false;
    }

    private static bool LooksLikeStandaloneNoteTitle(string value)
    {
        var text = OneLine(value);
        if (text.Contains(',')) return false;
        if (Regex.IsMatch(text, @"\b(?:conforme|c[oó]digo|conclu[ií]do|referente|valor|m[eê]s|dias?|militar(?:es)?|publicado|favor|abaixo|relacionad[oa]s?|correspondente)\b", RegexOptions.IgnoreCase)) return false;
        var words = Regex.Matches(text, @"[A-Za-zÁÀÂÃÉÊÍÓÔÕÚÇáàâãéêíóôõúç0-9]+").Count;
        return words is >= 2 and <= 10;
    }

    private static bool IsFindingHardBoundary(string value)
    {
        var text = OneLine(value);
        if (Regex.IsMatch(text, @"^\d+[ªa]?\s+Parte\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, @"^\d+\.\s+", RegexOptions.IgnoreCase)) return true;
        var normalized = Normalize(text);
        return normalized is "servicos diarios" or "instrucao" or "assuntos gerais e administrativos" or "justica" or "disciplina";
    }

    private static bool IsStructuralFindingLine(string value)
    {
        var text = OneLine(value);
        if (text.Length == 0) return true;
        if (Regex.IsMatch(text, @"^(?:Pag\s+n[ºo°]\s*\d+|MINIST[ÉE]RIO DA DEFESA|EX[ÉE]RCITO BRASILEIRO|CML\s*-\s*4ª\s*RM|4ª\s+COMPANHIA DE POL[ÍI]CIA|Pel\s+Pol\s+QGR|Quartel\s+Rua)", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, @"^\([^)]+-feira\)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, @"^(?:BOLETIM\s+INTERNO|ADITAMENTO\s+DO\s+FURRIEL)\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, @"^Para conhecimento deste aquartelamento", RegexOptions.IgnoreCase)) return true;
        return Normalize(text) is "sem alteracao" or "sem alteracoes";
    }

    private static string FormatNoteTitle(string value)
    {
        var text = OneLine(value).Replace('–', '-').Replace('—', '-');
        text = Regex.Replace(text, @"^[a-z]\.\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+-\s+", " - ").Trim();
        if (text.Length == 0) return string.Empty;
        if (IsMostlyUppercase(text))
        {
            text = PtBr.TextInfo.ToTitleCase(text.ToLower(PtBr));
            text = LowerSmallWords(text);
            text = RestoreKnownAcronyms(text);
        }
        return text[..Math.Min(text.Length, 180)];
    }

    private static bool IsMostlyUppercase(string value)
    {
        var letters = value.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.Count(char.IsUpper) >= letters.Count * 0.75;
    }

    private static string LowerSmallWords(string value)
    {
        foreach (var word in new[] { "De", "Da", "Do", "Das", "Dos", "E", "Ao", "Aos", "Em", "No", "Na" })
            value = Regex.Replace(value, $@"(?<!^)\b{word}\b", word.ToLower(PtBr), RegexOptions.CultureInvariant);
        return value;
    }

    private static string RestoreKnownAcronyms(string value)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cpf"] = "CPF",
            ["Prec-Cp"] = "Prec-CP",
            ["Prec Cp"] = "Prec-CP",
            ["Idt"] = "IDT",
            ["Bi"] = "BI",
            ["Nr"] = "Nr",
            ["Pnr"] = "PNR",
            ["Ompe"] = "OMPE",
            ["Sippes"] = "SIPPES",
            ["Cpex"] = "CPEx"
        };
        foreach (var pair in replacements)
            value = Regex.Replace(value, $@"\b{Regex.Escape(pair.Key)}\b", pair.Value, RegexOptions.IgnoreCase);
        return value;
    }

    private static string TruncateText(string value, int max)
    {
        value = OneLine(value);
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    private static bool IsProbableHeading(string value)
    {
        var text = OneLine(value);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120) return false;
        if (Regex.IsMatch(text, @"^(?:[1-4][ªa]?\s+Parte|[a-z]\.|\d+\.)", RegexOptions.IgnoreCase)) return true;
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 5 && letters.All(char.IsUpper);
    }
    private static int MonthNumber(string month)
    {
        var normalized = Normalize(month);
        var months = new[] { "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez" };
        var index = Array.FindIndex(months, value => normalized.StartsWith(value, StringComparison.Ordinal));
        return index < 0 ? 1 : index + 1;
    }
    private static string ClassifyAdministrative(string context, string consequence, bool furriel)
    {
        var text = context + " " + consequence;
        var categories = new List<string>();
        foreach (var (type, pattern) in AdminPatterns)
            if (pattern.IsMatch(text) && !categories.Contains(type, StringComparer.OrdinalIgnoreCase)) categories.Add(type);
        if (PaymentRegex.IsMatch(text) && !categories.Contains("Pagamento", StringComparer.OrdinalIgnoreCase)) categories.Add("Pagamento");
        if (furriel && VacationRegex.IsMatch(text) && categories.Count == 0) categories.Add("Férias");
        if (furriel) categories.Insert(0, "Furriel");
        return string.Join(" / ", categories.Distinct(StringComparer.OrdinalIgnoreCase).Take(2));
    }
    private static bool SameMilitary(IntelligentBulletinFinding left, IntelligentBulletinFinding right)
    {
        var a = Normalize(FirstNonEmpty(left.FullName, left.Military));
        var b = Normalize(FirstNonEmpty(right.FullName, right.Military));
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    private static string ClassifyPayment(string context)
    {
        var normalized = Normalize(context);
        if (normalized.Contains("exercicio anterior")) return "Exercício anterior";
        if (normalized.Contains("adicional de ferias")) return "Adicional de férias";
        if (normalized.Contains("pecuniaria")) return "Compensação pecuniária";
        if (normalized.Contains("salario familia")) return "Salário-família";
        if (normalized.Contains("atrasad")) return "Saque de atrasados";
        return "Pagamento";
    }

    private static DateTime? ParseBulletinDate(string raw, string number)
    {
        var normalized = Normalize(raw);
        var monthMap = new Dictionary<string, int> { ["jan"] = 1, ["janeiro"] = 1, ["fev"] = 2, ["fevereiro"] = 2, ["mar"] = 3, ["marco"] = 3, ["abr"] = 4, ["abril"] = 4, ["mai"] = 5, ["maio"] = 5, ["jun"] = 6, ["junho"] = 6, ["jul"] = 7, ["julho"] = 7, ["ago"] = 8, ["agosto"] = 8, ["set"] = 9, ["setembro"] = 9, ["out"] = 10, ["outubro"] = 10, ["nov"] = 11, ["novembro"] = 11, ["dez"] = 12, ["dezembro"] = 12 };
        var slash = Regex.Match(normalized, @"\b(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})\b");
        if (slash.Success)
        {
            var year = int.Parse(slash.Groups[3].Value, PtBr); if (year < 100) year += 2000;
            try { return new DateTime(year, int.Parse(slash.Groups[2].Value, PtBr), int.Parse(slash.Groups[1].Value, PtBr)); } catch { }
        }
        var textual = Regex.Match(normalized, @"\b(\d{1,2})\s*(?:de\s*)?([a-z]{3,12})\s*(?:de\s*)?(\d{2,4})\b");
        if (textual.Success && monthMap.TryGetValue(textual.Groups[2].Value, out var month))
        {
            var year = int.Parse(textual.Groups[3].Value, PtBr); if (year < 100) year += 2000;
            try { return new DateTime(year, month, int.Parse(textual.Groups[1].Value, PtBr)); } catch { }
        }
        return null;
    }

    private static string InferBulletinNumberFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(name, @"(?<!\d)(\d{1,4})\s*[/_. -]\s*(20\d{2})(?!\d)");
        if (match.Success) return $"{int.Parse(match.Groups[1].Value, PtBr)}/{match.Groups[2].Value}";
        var year = Regex.Match(name, @"\b(20\d{2})\b").Groups[1].Value;
        var number = Regex.Match(name, @"(?:\bBI\b|BOLETIM)[^0-9]{0,15}0*(\d{1,4})", RegexOptions.IgnoreCase).Groups[1].Value;
        return string.IsNullOrWhiteSpace(number) ? string.Empty : $"{int.Parse(number, PtBr)}/{(string.IsNullOrWhiteSpace(year) ? DateTime.Today.Year : year)}";
    }

    private static string WindowContext(IReadOnlyList<string> lines, int index, int radius)
        => string.Join(" ", lines.Skip(Math.Max(0, index - radius)).Take(radius * 2 + 1));
    private static string ContextAround(string text, int index, int length, int radius)
        => OneLine(text.Substring(Math.Max(0, index - radius), Math.Min(text.Length - Math.Max(0, index - radius), length + radius * 2)));
    private static IEnumerable<string> NameTokens(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3 && x is not "dos" and not "das" and not "de" and not "da" and not "do");
    private static string BestPdfSearchTerm(string fullName, string warName)
    {
        // O leitor de PDF recebe o nome completo uma única vez. Isso evita que o
        // Ctrl+F mude para sobrenomes parciais e perca a ocorrência correta.
        var complete = OneLine(fullName);
        return string.IsNullOrWhiteSpace(complete) ? OneLine(warName) : complete;
    }
    private static string NormalizeYear(string year) => year.Length == 2 ? "20" + year : year;
    private static int CategoryOrder(string category) => category switch { "Serviço" => 0, "Férias" => 1, "Convalescença" => 2, "Pagamento" => 3, "Alertas Adm" => 4, _ => 5 };
    private static int BulletinNumeric(string number) => int.TryParse(Regex.Match(number ?? string.Empty, @"\d+").Value, out var value) ? value : 0;
    private static DateTime? ParseIsoDate(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static string OneLine(string? text) => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
    private static string Digits(string? text) => Regex.Replace(text ?? string.Empty, @"\D+", string.Empty);
    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var chars = text.Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).Select(x => char.IsLetterOrDigit(x) ? char.ToLowerInvariant(x) : ' ').ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }
    private static string ShortHash(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant(); }
    private static string SafeFileName(string value) { var cleaned = new string((value ?? string.Empty).Normalize(NormalizationForm.FormD).Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).Select(x => Path.GetInvalidFileNameChars().Contains(x) || char.IsWhiteSpace(x) ? '_' : x).ToArray()); return Regex.Replace(cleaned, "_+", "_").Trim('_', '.', ' '); }
    private static string SanitizeText(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(OneLine).Where(x => !string.IsNullOrWhiteSpace(x) && !Regex.IsMatch(x, @"^(?:MINIST[ÉE]RIO DA DEFESA|EX[ÉE]RCITO BRASILEIRO|CML\s*-\s*4ª\s*RM|4ª\s+COMPANHIA DE POL[ÍI]CIA|Pag\s+n[ºo°]|\(Continua[çc][ãa]o do BI)", RegexOptions.IgnoreCase));
        return string.Join("\n", lines);
    }
    private static List<string> SplitPages(string output) { var pages = output.Split('\f').ToList(); while (pages.Count > 1 && string.IsNullOrWhiteSpace(pages[^1])) pages.RemoveAt(pages.Count - 1); return pages.Count == 0 ? [output] : pages; }

    private static string? FindExecutable(string name)
    {
        try
        {
            if (Path.IsPathRooted(name) && File.Exists(name)) return name;
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    private static string ExtractPdfTextBestEffort(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var ascii = Encoding.Latin1.GetString(bytes);
            var matches = Regex.Matches(ascii, @"\((?<text>(?:\\.|[^\\)]){4,})\)\s*T[Jj]");
            var lines = matches.Cast<Match>().Select(x => Regex.Unescape(x.Groups["text"].Value.Replace("\\(", "(").Replace("\\)", ")"))).Where(x => x.Any(char.IsLetter)).ToList();
            return string.Join("\n", lines);
        }
        catch { return string.Empty; }
    }

    private sealed class BulletinNote
    {
        public string Subject { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    private const byte VkControl = 0x11, VkF = 0x46, VkV = 0x56, VkEscape = 0x1B;
    private const uint KeyUp = 0x0002;
    private static void SendCtrlFAndPaste()
    {
        try
        {
            HotKey(VkF); Thread.Sleep(180); HotKey(VkV);
        }
        catch { }
        static void HotKey(byte key) { keybd_event(VkControl, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, KeyUp, UIntPtr.Zero); keybd_event(VkControl, 0, KeyUp, UIntPtr.Zero); }
    }

    private static void PressKey(byte key)
    {
        try
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        }
        catch { }
    }
}
