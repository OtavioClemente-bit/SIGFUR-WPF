using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SisbolPersonIndexImportService
{
    private readonly AppPaths _paths;
    private readonly PdfTextService _pdfText;
    private readonly MilitaryRepository _militaryRepository;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private List<SisbolPersonIndexItem>? _cachedRows;
    private Dictionary<int, MilitaryRecord>? _cachedMilitaryById;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly Regex PeriodRegex = new(@"ÍNDICE\s+REMISSIVO\s+DOS\s+BI\s+de\s+(\d{1,2}/\d{1,2}/\d{4})\s+a\s+(\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BiLineRegex = new(@"\bBI\s*N[º°O]?\s*(\d{1,4})\s+de\s+(\d{1,2}/\d{1,2}/\d{4})\s+pag\.?\s*(\d{1,5})\s*-\s*Nota:\s*([0-9A-Za-z./-]*)\s*-\s*Usu[aá]rio:\s*([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RankRegex = new(@"^(?:Gen(?:\s+Ex|\s+Bda|\s+Div)?|Cel|Ten\s*Cel|Maj|Cap(?:\s+R/1)?|1º\s*Ten|2º\s*Ten|Asp\s*Of|Sub\s*Ten|S Ten|1º\s*Sgt|2º\s*Sgt|3º\s*Sgt|Cb(?:\s+EF\s+PROFL)?|Sd(?:\s+EV|\s+Ef\s+Vrv|\s+EF\s+VRV|\s+Ef\s+Profl|\s+EF\s+PROFL)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SisbolPersonIndexImportService(AppPaths paths, PdfTextService pdfText, MilitaryRepository militaryRepository, LogService log)
    {
        _paths = paths;
        _pdfText = pdfText;
        _militaryRepository = militaryRepository;
        _log = log;
    }

    public async Task EnsureDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sisbol_person_index (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_pdf_path TEXT NOT NULL,
                source_pdf_hash TEXT NOT NULL,
                index_start_date TEXT NULL,
                index_end_date TEXT NULL,
                rank TEXT NOT NULL DEFAULT '',
                person_name TEXT NOT NULL DEFAULT '',
                military_id INTEGER NULL,
                main_subject TEXT NOT NULL DEFAULT '',
                sub_subject TEXT NOT NULL DEFAULT '',
                subject_note TEXT NOT NULL DEFAULT '',
                bulletin_type TEXT NOT NULL DEFAULT 'BI',
                bulletin_number TEXT NOT NULL DEFAULT '',
                bulletin_date TEXT NULL,
                bulletin_page INTEGER NULL,
                note_number TEXT NOT NULL DEFAULT '',
                sisbol_user TEXT NOT NULL DEFAULT '',
                search_text TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_military_id ON sisbol_person_index(military_id);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_person_name ON sisbol_person_index(person_name);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_bulletin_number ON sisbol_person_index(bulletin_number);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_bulletin_date ON sisbol_person_index(bulletin_date);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_note_number ON sisbol_person_index(note_number);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_main_subject ON sisbol_person_index(main_subject);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_user ON sisbol_person_index(sisbol_user);
            CREATE INDEX IF NOT EXISTS ix_sisbol_person_index_hash ON sisbol_person_index(source_pdf_hash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SisbolPersonIndexImportResult> ImportAsync(string sourcePdfPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePdfPath) || !File.Exists(sourcePdfPath))
            throw new FileNotFoundException("PDF do Índice por Pessoa não encontrado.", sourcePdfPath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureDatabaseAsync(cancellationToken);
            Directory.CreateDirectory(_paths.SisbolPersonIndexDirectory);
            progress?.Report("Calculando hash do Índice por Pessoa...");
            var hash = await ComputeHashAsync(sourcePdfPath, cancellationToken);
            var storedPath = await CopyToIndexLibraryAsync(sourcePdfPath, hash, cancellationToken);

            progress?.Report("Extraindo texto do PDF do Índice por Pessoa...");
            var pages = await ExtractIndexPagesAsync(storedPath, cancellationToken);
            var allMilitary = await _militaryRepository.GetAllAsync(cancellationToken);
            var lookup = allMilitary.Select(MilitaryCandidate.FromRecord).ToList();

            progress?.Report($"Processando {pages.Count} página(s) do índice...");
            var parsed = await Task.Run(() => ParsePages(pages, storedPath, hash, lookup, progress, cancellationToken), cancellationToken);
            if (parsed.Count == 0)
                throw new InvalidOperationException("Nenhum registro de BI/Nota foi identificado no Índice por Pessoa. Confira se o PDF é o índice remissivo gerado pelo SisBol.");

            progress?.Report("Gravando índice no banco de dados...");
            await SaveImportedRowsAsync(storedPath, hash, parsed, cancellationToken);

            var result = new SisbolPersonIndexImportResult
            {
                SourcePdfPath = storedPath,
                SourcePdfHash = hash,
                IndexStartDate = parsed.Select(x => x.IndexStartDate).FirstOrDefault(x => x is not null),
                IndexEndDate = parsed.Select(x => x.IndexEndDate).FirstOrDefault(x => x is not null),
                Imported = parsed.Count,
                Linked = parsed.Count(x => x.MilitaryId.HasValue)
            };
            progress?.Report($"Índice importado: {result.Imported} registro(s), {result.Linked} vinculado(s) ao banco.");
            return result;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao importar Índice por Pessoa do SisBol.", ex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SisbolPersonIndexItem>> SearchAsync(SisbolPersonIndexQuery query, int limit = 10000, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        query ??= new SisbolPersonIndexQuery();
        var rows = await LoadCachedRowsAsync(cancellationToken);
        return await Task.Run(() => GroupNoteRows(FilterCachedRows(rows, query, limit * 4, cancellationToken), query, limit, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ExtractIndexPagesAsync(string pdfPath, CancellationToken cancellationToken)
    {
        // O Índice por Pessoa depende da hierarquia visual:
        // 1º nível: assunto em negrito; 2º nível: nota/tipo sublinhada; depois vem o BI.
        // O PdfTextService genérico remove o recuo esquerdo e faz assunto/nota se inverterem.
        // Por isso esta importação usa um extrator próprio por coordenadas, preservando espaços
        // iniciais antes do traço. Assim o parser consegue distinguir assunto de nota sem ler o BI comum.
        var nativeLayout = await TryPdfPigVisualIndexPagesAsync(pdfPath, cancellationToken);
        if (nativeLayout.Any(page => !string.IsNullOrWhiteSpace(page))) return nativeLayout;

        var external = await TryPdftotextLayoutPagesAsync(pdfPath, cancellationToken);
        if (external.Any(page => !string.IsNullOrWhiteSpace(page))) return external;

        // Último recurso: ainda importa por sequência, mas sem o recuo visual pode haver perda de precisão.
        return await _pdfText.ExtractPagesAsync(pdfPath, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> TryPdfPigVisualIndexPagesAsync(string pdfPath, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                var result = new List<string>();
                using var document = PdfDocument.Open(pdfPath);
                foreach (var page in document.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(BuildIndexLayoutText(page));
                }
                return (IReadOnlyList<string>)result;
            }, cancellationToken);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string BuildIndexLayoutText(Page page)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2d)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();
        if (words.Count == 0) return string.Empty;

        var rows = new List<List<Word>>();
        foreach (var word in words)
        {
            var center = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2d;
            var row = rows.FirstOrDefault(existing =>
            {
                var sample = existing[0];
                var sampleCenter = (sample.BoundingBox.Top + sample.BoundingBox.Bottom) / 2d;
                var tolerance = Math.Max(2.0, Math.Min(6.5, sample.BoundingBox.Height * 0.58));
                return Math.Abs(sampleCenter - center) <= tolerance;
            });
            if (row is null) rows.Add([word]); else row.Add(word);
        }

        var orderedRows = rows
            .Select(row => row.OrderBy(word => word.BoundingBox.Left).ToList())
            .OrderByDescending(row => row.Average(word => (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2d))
            .ToList();

        var leftReference = words.Min(word => word.BoundingBox.Left);
        var charWidths = words
            .Select(word => word.Text.Length > 0 ? word.BoundingBox.Width / Math.Max(1, word.Text.Length) : 4d)
            .Where(width => width is > 0 and < 30)
            .OrderBy(width => width)
            .ToList();
        var medianCharWidth = charWidths.Count == 0 ? 4d : charWidths[charWidths.Count / 2];
        var unit = Math.Max(2.6, Math.Min(6.0, medianCharWidth));

        var sb = new StringBuilder();
        foreach (var row in orderedRows)
        {
            var line = BuildIndexLayoutLine(row, leftReference, unit);
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string BuildIndexLayoutLine(IReadOnlyList<Word> row, double leftReference, double unit)
    {
        if (row.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        var leadingSpaces = Math.Clamp((int)Math.Round((row[0].BoundingBox.Left - leftReference) / unit), 0, 80);
        if (leadingSpaces > 0) sb.Append(' ', leadingSpaces);

        double right = row[0].BoundingBox.Left;
        for (var i = 0; i < row.Count; i++)
        {
            var word = row[i];
            if (i > 0)
            {
                var gap = word.BoundingBox.Left - right;
                var spaces = gap <= unit * 0.8 ? 1 : Math.Clamp((int)Math.Round(gap / Math.Max(3.2, unit * 1.7)), 1, 20);
                sb.Append(' ', spaces);
            }
            sb.Append(word.Text);
            right = word.BoundingBox.Right;
        }
        return sb.ToString().TrimEnd();
    }

    private static async Task<IReadOnlyList<string>> TryPdftotextLayoutPagesAsync(string pdfPath, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "SIGFUR", "sisbol_index_text");
        Directory.CreateDirectory(work);
        var output = Path.Combine(work, $"{Guid.NewGuid():N}.txt");
        try
        {
            foreach (var command in new[] { "pdftotext.exe", "pdftotext" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = command,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    foreach (var argument in new[] { "-layout", "-enc", "UTF-8", pdfPath, output })
                        psi.ArgumentList.Add(argument);
                    using var process = Process.Start(psi);
                    if (process is null) continue;
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode != 0 || !File.Exists(output)) continue;
                    var text = await File.ReadAllTextAsync(output, Encoding.UTF8, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\f').ToList();
                }
                catch { }
            }
            return Array.Empty<string>();
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
        }
    }

    private async Task<IReadOnlyList<SisbolPersonIndexItem>> LoadCachedRowsAsync(CancellationToken cancellationToken)
    {
        if (_cachedRows is not null) return _cachedRows;
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedRows is not null) return _cachedRows;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM sisbol_person_index ORDER BY bulletin_date DESC, CAST(bulletin_number AS INTEGER) DESC, person_name COLLATE NOCASE, main_subject COLLATE NOCASE;";
            var list = new List<SisbolPersonIndexItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadItem(reader));
            await ApplyLinkedNamesAsync(list, cancellationToken);
            _cachedRows = list;
            return list;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static IReadOnlyList<SisbolPersonIndexItem> GroupNoteRows(
        IReadOnlyList<SisbolPersonIndexItem> rows,
        SisbolPersonIndexQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 10000);
        var groups = rows
            .GroupBy(row => BuildNoteGroupKey(row), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildAggregatedNoteRow(group, query))
            .OrderByDescending(row => row.BulletinDate ?? DateTime.MinValue)
            .ThenByDescending(row => SafeNumber(row.BulletinNumber))
            .ThenBy(row => row.BulletinPage ?? int.MaxValue)
            .ThenBy(row => row.MainSubject, StringComparer.CurrentCultureIgnoreCase)
            .Take(safeLimit)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return groups;
    }

    private static SisbolPersonIndexItem BuildAggregatedNoteRow(IGrouping<string, SisbolPersonIndexItem> group, SisbolPersonIndexQuery query)
    {
        var first = group.First();
        var people = group
            .Select(row => new PersonDisplay
            {
                Name = FirstNonEmpty(row.LinkedFullName, row.PersonName),
                War = row.LinkedWarName,
                Rank = row.Rank,
                Linked = row.MilitaryId.HasValue,
                Sort = NormalizeForSearch(FirstNonEmpty(row.LinkedFullName, row.PersonName))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => NormalizeForSearch(x.Name), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.Sort, StringComparer.Ordinal)
            .ToList();

        var requestedPerson = NormalizeForSearch(StripCountSuffix(query.Person));
        var preferred = people.FirstOrDefault(person => !string.IsNullOrWhiteSpace(requestedPerson)
                                                        && !requestedPerson.Equals("todos", StringComparison.Ordinal)
                                                        && NormalizeForSearch(string.Join(' ', person.Rank, person.Name, person.War)).Contains(requestedPerson, StringComparison.Ordinal))
                        ?? people.FirstOrDefault();

        var clone = new SisbolPersonIndexItem
        {
            Id = first.Id,
            SourcePdfPath = first.SourcePdfPath,
            SourcePdfHash = first.SourcePdfHash,
            IndexStartDate = first.IndexStartDate,
            IndexEndDate = first.IndexEndDate,
            Rank = first.Rank,
            PersonName = preferred?.Name ?? first.PersonName,
            MilitaryId = people.Count == 1 ? first.MilitaryId : null,
            LinkedFullName = preferred?.Name ?? first.LinkedFullName,
            LinkedWarName = BuildWarNamesForHighlight(people, preferred?.War ?? first.LinkedWarName),
            MainSubject = first.MainSubject,
            SubSubject = first.SubSubject,
            SubjectNote = first.SubjectNote,
            BulletinType = first.BulletinType,
            BulletinNumber = first.BulletinNumber,
            BulletinDate = first.BulletinDate,
            BulletinPage = first.BulletinPage,
            NoteNumber = first.NoteNumber,
            SisbolUser = first.SisbolUser,
            SearchText = string.Join(' ', group.Select(row => row.SearchText)),
            CreatedAt = first.CreatedAt,
            UpdatedAt = group.Max(row => row.UpdatedAt),
            AggregatedPersonCount = people.Count,
            AggregatedLinkedCount = people.Count(x => x.Linked),
            AggregatedSearchPerson = preferred?.Name ?? first.PersonName,
            AggregatedPeople = BuildPeopleDisplay(people)
        };
        return clone;
    }

    private static string BuildPeopleDisplay(IReadOnlyList<PersonDisplay> people)
    {
        if (people.Count == 0) return string.Empty;
        var labels = people.Select(person =>
        {
            var prefix = string.IsNullOrWhiteSpace(person.Rank) ? string.Empty : MilitaryRankService.ShortName(person.Rank) + " ";
            return (prefix + person.Name).Trim();
        }).ToList();
        if (labels.Count <= 8) return string.Join("; ", labels);
        return string.Join("; ", labels.Take(8)) + $"; +{labels.Count - 8} pessoa(s)";
    }

    private static string BuildWarNamesForHighlight(IReadOnlyList<PersonDisplay> people, string fallback)
    {
        var wars = people
            .Select(x => x.War)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .ToList();
        if (wars.Count == 0 && !string.IsNullOrWhiteSpace(fallback)) wars.Add(fallback.Trim());
        return string.Join(";", wars);
    }

    private sealed class PersonDisplay
    {
        public string Name { get; init; } = string.Empty;
        public string War { get; init; } = string.Empty;
        public string Rank { get; init; } = string.Empty;
        public bool Linked { get; init; }
        public string Sort { get; init; } = string.Empty;
    }

    private static string BuildNoteGroupKey(SisbolPersonIndexItem row)
        => string.Join('|', row.BulletinNumber, row.BulletinDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            row.BulletinPage?.ToString(CultureInfo.InvariantCulture), row.NoteNumber, NormalizeForSearch(row.MainSubject), NormalizeForSearch(row.SubSubject));

    private static int SafeNumber(string value)
    {
        var first = (value ?? string.Empty).Split('/').FirstOrDefault() ?? string.Empty;
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static IReadOnlyList<SisbolPersonIndexItem> FilterCachedRows(
        IReadOnlyList<SisbolPersonIndexItem> rows,
        SisbolPersonIndexQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 10000);
        var searchTerms = SearchTerms(query.Search);
        var searchDigits = Digits(query.Search);
        var personTerms = SearchTerms(StripCountSuffix(query.Person));
        var subjectTerms = SearchTerms(StripCountSuffix(query.Subject));
        var noteTerms = SearchTerms(StripCountSuffix(query.Note));
        var subjectOrNoteTerms = SearchTerms(query.SubjectOrNote);
        var user = query.User?.Trim() ?? string.Empty;
        var linkFilter = query.LinkFilter?.Trim() ?? string.Empty;
        var year = 0;
        if (!string.IsNullOrWhiteSpace(query.Year) && !query.Year.Equals("Todos", StringComparison.OrdinalIgnoreCase))
            _ = int.TryParse(query.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
        var month = 0;
        if (!string.IsNullOrWhiteSpace(query.Month) && !query.Month.Equals("Todos", StringComparison.OrdinalIgnoreCase))
            month = MonthNumber(query.Month);

        var result = new List<SisbolPersonIndexItem>(Math.Min(safeLimit, rows.Count));
        for (var index = 0; index < rows.Count; index++)
        {
            if ((index & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            if (year > 0 && row.BulletinDate?.Year != year) continue;
            if (month > 0 && row.BulletinDate?.Month != month) continue;
            if (linkFilter.Equals("Vinculados", StringComparison.OrdinalIgnoreCase) && row.MilitaryId is null) continue;
            if (linkFilter.Equals("Não vinculados", StringComparison.OrdinalIgnoreCase) && row.MilitaryId is not null) continue;
            if (!string.IsNullOrWhiteSpace(user) && !user.Equals("Todos", StringComparison.OrdinalIgnoreCase) && !row.SisbolUser.Equals(user, StringComparison.OrdinalIgnoreCase)) continue;

            var rowSearch = string.IsNullOrWhiteSpace(row.SearchText) ? BuildRowSearchText(row) : row.SearchText;
            if (!MatchesTerms(rowSearch, searchTerms)) continue;
            if (searchDigits.Length >= 3 && !rowSearch.Contains(searchDigits, StringComparison.Ordinal)) continue;

            if (!MatchesTerms(NormalizeForSearch(string.Join(' ', row.PersonName, row.LinkedFullName, row.LinkedWarName, row.Rank)), personTerms)) continue;
            var subjectText = NormalizeForSearch(string.Join(' ', row.MainSubject, row.SubjectNote));
            if (!MatchesTerms(subjectText, subjectTerms)) continue;
            var noteText = NormalizeForSearch(string.Join(' ', row.SubSubject, row.SubjectNote));
            if (!MatchesTerms(noteText, noteTerms)) continue;
            var subjectNoteText = NormalizeForSearch(string.Join(' ', row.MainSubject, row.SubSubject, row.SubjectNote));
            if (!MatchesTerms(subjectNoteText, subjectOrNoteTerms)) continue;

            result.Add(row);
            if (result.Count >= safeLimit) break;
        }
        return result;
    }

    private static string[] SearchTerms(string? value)
    {
        var normalized = NormalizeForSearch(value);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("todos", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MatchesTerms(string normalizedText, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return true;
        return terms.All(term => normalizedText.Contains(term, StringComparison.Ordinal));
    }

    private static string BuildRowSearchText(SisbolPersonIndexItem row)
        => NormalizeForSearch(string.Join(' ', row.Rank, row.PersonName, row.LinkedFullName, row.LinkedWarName, row.MainSubject,
            row.SubSubject, row.SubjectNote, row.BulletinType, row.BulletinNumber, row.BulletinDate?.ToString("dd/MM/yyyy"),
            row.BulletinPage, row.NoteNumber, row.SisbolUser));

    public async Task<IReadOnlyList<SisbolPersonIndexItem>> FindForMilitaryAsync(MilitaryRecord military, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        var name = NormalizeForSearch(military.Name);
        var war = NormalizeForSearch(military.WarName);
        var cpf = Digits(military.Cpf);
        var prec = Digits(military.PrecCp);
        var idt = Digits(military.MilitaryId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new List<string> { "military_id = $id" };
        command.Parameters.AddWithValue("$id", military.Id);
        if (!string.IsNullOrWhiteSpace(name)) { where.Add("search_text LIKE $name"); command.Parameters.AddWithValue("$name", "%" + name + "%"); }
        if (war.Length >= 3) { where.Add("search_text LIKE $war"); command.Parameters.AddWithValue("$war", "%" + war + "%"); }
        if (cpf.Length >= 5) { where.Add("REPLACE(REPLACE(REPLACE(search_text, '.', ''), '-', ''), '/', '') LIKE $cpf"); command.Parameters.AddWithValue("$cpf", "%" + cpf + "%"); }
        if (prec.Length >= 5) { where.Add("REPLACE(REPLACE(REPLACE(search_text, '.', ''), '-', ''), '/', '') LIKE $prec"); command.Parameters.AddWithValue("$prec", "%" + prec + "%"); }
        if (idt.Length >= 5) { where.Add("REPLACE(REPLACE(REPLACE(search_text, '.', ''), '-', ''), '/', '') LIKE $idt"); command.Parameters.AddWithValue("$idt", "%" + idt + "%"); }

        command.CommandText = $"""
            SELECT * FROM sisbol_person_index
            WHERE {string.Join(" OR ", where)}
            ORDER BY bulletin_date DESC, CAST(bulletin_number AS INTEGER) DESC, main_subject COLLATE NOCASE;
            """;
        var list = new List<SisbolPersonIndexItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadItem(reader));
        var distinct = list
            .DistinctBy(x => $"{x.PersonName}|{x.BulletinNumber}|{x.BulletinDate:yyyyMMdd}|{x.NoteNumber}|{x.MainSubject}|{x.SubSubject}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        await ApplyLinkedNamesAsync(distinct, cancellationToken);
        return distinct;
    }

    private async Task ApplyLinkedNamesAsync(IReadOnlyCollection<SisbolPersonIndexItem> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(x => x.MilitaryId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToHashSet();
        if (ids.Count == 0) return;
        var byId = await GetMilitaryByIdCacheAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (row.MilitaryId is not int id || !byId.TryGetValue(id, out var military)) continue;
            row.LinkedFullName = military.Name;
            row.LinkedWarName = military.WarName;
        }
    }

    private async Task<Dictionary<int, MilitaryRecord>> GetMilitaryByIdCacheAsync(CancellationToken cancellationToken)
    {
        if (_cachedMilitaryById is not null) return _cachedMilitaryById;
        var people = await _militaryRepository.GetAllAsync(cancellationToken);
        _cachedMilitaryById = people.GroupBy(x => x.Id).ToDictionary(group => group.Key, group => group.First());
        return _cachedMilitaryById;
    }

    public async Task<SisbolPersonIndexSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) AS total,
                   COUNT(DISTINCT person_name) AS people,
                   SUM(CASE WHEN military_id IS NOT NULL THEN 1 ELSE 0 END) AS linked,
                   SUM(CASE WHEN military_id IS NULL THEN 1 ELSE 0 END) AS unlinked,
                   MIN(index_start_date) AS start_date,
                   MAX(index_end_date) AS end_date
            FROM sisbol_person_index;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new SisbolPersonIndexSummary();
        return new SisbolPersonIndexSummary
        {
            TotalRecords = SafeInt(reader, "total"),
            PeopleCount = SafeInt(reader, "people"),
            LinkedCount = SafeInt(reader, "linked"),
            UnlinkedCount = SafeInt(reader, "unlinked"),
            StartDate = ParseIsoDate(reader["start_date"]?.ToString()),
            EndDate = ParseIsoDate(reader["end_date"]?.ToString())
        };
    }


    public async Task<string> GetMissingBulletinsTextAsync(string? yearFilter = "Todos", CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var year = 0;
        if (!string.IsNullOrWhiteSpace(yearFilter) && !yearFilter.Equals("Todos", StringComparison.OrdinalIgnoreCase))
            _ = int.TryParse(yearFilter, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);

        if (year > 0)
        {
            command.CommandText = "SELECT DISTINCT bulletin_number, bulletin_date FROM sisbol_person_index WHERE bulletin_date IS NOT NULL AND strftime('%Y', bulletin_date) = $year;";
            command.Parameters.AddWithValue("$year", year.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            command.CommandText = "SELECT DISTINCT bulletin_number, bulletin_date FROM sisbol_person_index WHERE bulletin_date IS NOT NULL;";
        }

        var byYear = new Dictionary<int, HashSet<int>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var number = SafeNumber(reader["bulletin_number"]?.ToString() ?? string.Empty);
            var date = ParseIsoDate(reader["bulletin_date"]?.ToString());
            if (number <= 0 || date is null) continue;
            if (!byYear.TryGetValue(date.Value.Year, out var numbers))
            {
                numbers = [];
                byYear[date.Value.Year] = numbers;
            }
            numbers.Add(number);
        }

        if (byYear.Count == 0) return "BIs faltantes no índice/período: sem BI importado.";
        var selectedYear = year > 0 ? year : byYear.Keys.OrderByDescending(x => x).First();
        if (!byYear.TryGetValue(selectedYear, out var selected) || selected.Count == 0)
            return $"BIs faltantes no índice/período: sem BI em {selectedYear}.";

        var min = selected.Min();
        var max = selected.Max();
        var missing = Enumerable.Range(min, max - min + 1).Where(value => !selected.Contains(value)).ToList();
        var missingText = missing.Count == 0
            ? "nenhum número intermediário faltando"
            : string.Join(", ", missing.Take(60)) + (missing.Count > 60 ? $", +{missing.Count - 60}" : string.Empty);
        return $"BIs faltantes no índice/período — {selectedYear}: encontrados {min} a {max}; faltando: {missingText}.";
    }

    public async Task<IReadOnlyList<string>> LoadYearsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT strftime('%Y', bulletin_date) FROM sisbol_person_index WHERE bulletin_date IS NOT NULL ORDER BY 1 DESC;";
        var values = new List<string> { "Todos" };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values;
    }

    public async Task<IReadOnlyList<string>> LoadUsersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT sisbol_user FROM sisbol_person_index WHERE sisbol_user <> '' ORDER BY sisbol_user COLLATE NOCASE;";
        var values = new List<string> { "Todos" };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    public async Task<IReadOnlyList<string>> LoadSubjectsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT main_subject FROM sisbol_person_index WHERE main_subject <> '' ORDER BY main_subject COLLATE NOCASE;";
        var values = new List<string> { "Todos" };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }


    public async Task<IReadOnlyList<string>> LoadNotesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT sub_subject FROM sisbol_person_index WHERE sub_subject <> '' ORDER BY sub_subject COLLATE NOCASE;";
        var values = new List<string> { "Todos" };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    public async Task<IReadOnlyList<string>> LoadPeopleAsync(CancellationToken cancellationToken = default)
    {
        var options = await LoadPersonOptionsAsync(cancellationToken);
        return options.Select(x => x.Display).Prepend("Todos").ToList();
    }

    public async Task<IReadOnlyList<SisbolPersonIndexPersonOption>> LoadPersonOptionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        var rows = await LoadCachedRowsAsync(cancellationToken);
        var options = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.PersonName))
            .GroupBy(row => NormalizeForSearch(FirstNonEmpty(row.LinkedFullName, row.PersonName)), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new SisbolPersonIndexPersonOption
                {
                    Rank = first.Rank,
                    Name = first.PersonName,
                    LinkedFullName = FirstNonEmpty(first.LinkedFullName, first.PersonName),
                    WarName = first.LinkedWarName,
                    Count = group.Count()
                };
            })
            .OrderBy(x => NormalizeForSearch(x.FullName), StringComparer.Ordinal)
            .ToList();
        return options;
    }

    public async Task ExportCsvAsync(string destination, IEnumerable<SisbolPersonIndexItem> rows, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("Data;BI;Nota SisBol;Pessoas da nota;Qtde Pessoas;P/G;Assunto;Nota;Assunto/Nota;Página;Usuário;Vinculado;PDF Índice");
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                row.DateText, row.BulletinNumber, row.NoteNumber, row.PeopleDisplay, row.PeopleCountText, row.Rank,
                row.MainSubjectDisplay, row.NoteDisplay, row.DisplaySubjectNote, row.PageText, row.SisbolUser, row.LinkedText, row.SourcePdfPath
            };
            await writer.WriteLineAsync(string.Join(';', values.Select(EscapeCsv)));
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DatabaseFile)!);
        var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task SaveImportedRowsAsync(string sourcePath, string hash, IReadOnlyList<SisbolPersonIndexItem> rows, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var startDate = ToIsoDate(rows.Select(x => x.IndexStartDate).FirstOrDefault(x => x is not null));
        var endDate = ToIsoDate(rows.Select(x => x.IndexEndDate).FirstOrDefault(x => x is not null));
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            // O Índice por Pessoa é a fonte oficial e deve ser regravado limpo.
            // Remove toda importação anterior para eliminar cache/lixo do parser antigo
            // (assunto/nota invertidos, texto varado e linhas em branco).
            delete.CommandText = """
                DELETE FROM sisbol_person_index;
                """;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO sisbol_person_index
                (source_pdf_path, source_pdf_hash, index_start_date, index_end_date, rank, person_name, military_id,
                 main_subject, sub_subject, subject_note, bulletin_type, bulletin_number, bulletin_date, bulletin_page,
                 note_number, sisbol_user, search_text, created_at, updated_at)
                VALUES
                ($source_pdf_path, $source_pdf_hash, $index_start_date, $index_end_date, $rank, $person_name, $military_id,
                 $main_subject, $sub_subject, $subject_note, $bulletin_type, $bulletin_number, $bulletin_date, $bulletin_page,
                 $note_number, $sisbol_user, $search_text, $created_at, $updated_at);
                """;
            AddParameters(insert, item);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        _cachedRows = null;
    }

    private static void AddParameters(SqliteCommand command, SisbolPersonIndexItem item)
    {
        command.Parameters.AddWithValue("$source_pdf_path", item.SourcePdfPath);
        command.Parameters.AddWithValue("$source_pdf_hash", item.SourcePdfHash);
        command.Parameters.AddWithValue("$index_start_date", ToIsoDate(item.IndexStartDate));
        command.Parameters.AddWithValue("$index_end_date", ToIsoDate(item.IndexEndDate));
        command.Parameters.AddWithValue("$rank", item.Rank);
        command.Parameters.AddWithValue("$person_name", item.PersonName);
        command.Parameters.AddWithValue("$military_id", item.MilitaryId.HasValue ? item.MilitaryId.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$main_subject", item.MainSubject);
        command.Parameters.AddWithValue("$sub_subject", item.SubSubject);
        command.Parameters.AddWithValue("$subject_note", item.SubjectNote);
        command.Parameters.AddWithValue("$bulletin_type", item.BulletinType);
        command.Parameters.AddWithValue("$bulletin_number", item.BulletinNumber);
        command.Parameters.AddWithValue("$bulletin_date", ToIsoDate(item.BulletinDate));
        command.Parameters.AddWithValue("$bulletin_page", item.BulletinPage.HasValue ? item.BulletinPage.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$note_number", item.NoteNumber);
        command.Parameters.AddWithValue("$sisbol_user", item.SisbolUser);
        command.Parameters.AddWithValue("$search_text", item.SearchText);
        command.Parameters.AddWithValue("$created_at", item.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private List<SisbolPersonIndexItem> ParsePages(
        IReadOnlyList<string> pages,
        string sourcePath,
        string hash,
        IReadOnlyList<MilitaryCandidate> military,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var allText = string.Join("\n", pages);
        var period = PeriodRegex.Match(allText);
        DateTime? startDate = null;
        DateTime? endDate = null;
        if (period.Success)
        {
            startDate = ParseBrazilianDate(period.Groups[1].Value);
            endDate = ParseBrazilianDate(period.Groups[2].Value);
        }

        var rows = new List<SisbolPersonIndexItem>();
        var currentRank = string.Empty;
        var currentPerson = string.Empty;
        var currentMainSubject = string.Empty;
        var currentNoteType = string.Empty;
        var lastSubjectPart = string.Empty;

        var lines = pages
            .SelectMany((page, pageIndex) => NormalizeIndexLines(page).Select(line => line with { PageIndex = pageIndex }))
            .Where(line => !ShouldSkipLine(line.Text))
            .ToList();

        // Regra definitiva do Índice por Pessoa SisBol:
        // o assunto é o traço de 1º nível; a nota/tipo é o traço recuado de 2º nível; o BI apenas vincula.
        // Quando o extrator visual preserva recuo, usamos essa hierarquia. Se por algum motivo vier texto
        // sem recuo, caímos na sequência ASSUNTO -> NOTA/TIPO -> BI.
        var useBulletLevels = ShouldUseBulletLevels(lines);

        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 400 == 0) progress?.Report($"Lendo Índice por Pessoa: linha {index + 1}/{lines.Count}...");

            var currentLine = lines[index];
            var line = currentLine.Text;

            if (IsRankLine(line))
            {
                currentRank = NormalizeRankLabel(line);
                continue;
            }

            if (line.StartsWith('»'))
            {
                currentPerson = Regex.Replace(line.TrimStart('»').Trim(), @"\s+", " ").Trim();
                currentMainSubject = string.Empty;
                currentNoteType = string.Empty;
                lastSubjectPart = string.Empty;
                continue;
            }

            var biMatch = BiLineRegex.Match(line);
            if (biMatch.Success && !string.IsNullOrWhiteSpace(currentPerson))
            {
                AddIndexRow(rows, sourcePath, hash, startDate, endDate, currentRank, currentPerson, currentMainSubject, currentNoteType, biMatch, military);
                // O BI comum não é lido para descobrir assunto/nota. Ele apenas fica vinculado ao par assunto + nota/tipo já aberto no índice.
                // Mantém currentMainSubject/currentNoteType para permitir vários BI em sequência na mesma nota.
                lastSubjectPart = string.Empty;
                continue;
            }

            if (currentLine.IsBullet && !string.IsNullOrWhiteSpace(currentPerson))
            {
                ApplyIndexBullet(lines, index, currentLine, useBulletLevels, ref currentMainSubject, ref currentNoteType, ref lastSubjectPart);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(currentPerson) && !string.IsNullOrWhiteSpace(lastSubjectPart) && IsContinuationLine(line))
            {
                if (lastSubjectPart == "main") currentMainSubject = AppendContinuation(currentMainSubject, line);
                else currentNoteType = AppendContinuation(currentNoteType, line);
            }
        }

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.PersonName) && !string.IsNullOrWhiteSpace(x.BulletinNumber))
            .DistinctBy(x => $"{NormalizeForSearch(x.PersonName)}|{x.Rank}|{x.BulletinNumber}|{x.BulletinDate:yyyyMMdd}|{x.BulletinPage}|{x.NoteNumber}|{NormalizeForSearch(x.MainSubject)}|{NormalizeForSearch(x.SubSubject)}", StringComparer.Ordinal)
            .ToList();
    }

    private static void ApplyIndexBullet(
        IReadOnlyList<IndexLine> lines,
        int index,
        IndexLine currentLine,
        bool useBulletLevels,
        ref string currentMainSubject,
        ref string currentNoteType,
        ref string lastSubjectPart)
    {
        var bullet = NormalizeSubjectValue(currentLine.CleanBullet);
        if (string.IsNullOrWhiteSpace(bullet)) return;

        if (useBulletLevels)
        {
            // Mantido apenas como recurso técnico, mas a importação oficial força false.
            // O recuo do PDF não é confiável em todas as máquinas.
            if (currentLine.BulletLevel <= 1)
            {
                currentMainSubject = bullet;
                currentNoteType = string.Empty;
                lastSubjectPart = "main";
                return;
            }

            if (string.IsNullOrWhiteSpace(currentMainSubject))
            {
                currentMainSubject = bullet;
                currentNoteType = string.Empty;
                lastSubjectPart = "main";
                return;
            }

            currentNoteType = bullet;
            lastSubjectPart = "note";
            return;
        }

        // Interpretação oficial por sequência do índice, sem ler BI comum:
        // 1) se ainda não existe assunto aberto, o traço é ASSUNTO;
        // 2) se já existe assunto e ainda não existe nota, o traço é NOTA/TIPO;
        // 3) se já existe assunto+nota e o próximo item também é traço, começou novo ASSUNTO;
        // 4) se já existe assunto+nota e o próximo item é BI, é nova NOTA/TIPO do mesmo assunto.
        var nextKind = FindNextSignificantKind(lines, index + 1);
        if (string.IsNullOrWhiteSpace(currentMainSubject))
        {
            currentMainSubject = bullet;
            currentNoteType = string.Empty;
            lastSubjectPart = "main";
            return;
        }

        if (string.IsNullOrWhiteSpace(currentNoteType))
        {
            currentNoteType = bullet;
            lastSubjectPart = "note";
            return;
        }

        if (nextKind == NextIndexLineKind.Bullet)
        {
            currentMainSubject = bullet;
            currentNoteType = string.Empty;
            lastSubjectPart = "main";
        }
        else
        {
            currentNoteType = bullet;
            lastSubjectPart = "note";
        }
    }

    private static bool ShouldUseBulletLevels(IReadOnlyList<IndexLine> lines)
    {
        var bullets = lines.Where(line => line.IsBullet).Take(250).ToList();
        if (bullets.Count < 4) return false;
        var secondLevel = bullets.Count(line => line.BulletLevel >= 2);
        var firstLevel = bullets.Count(line => line.BulletLevel <= 1);
        return firstLevel > 0 && secondLevel >= Math.Min(4, Math.Max(1, bullets.Count / 8));
    }

    private static void AddIndexRow(
        ICollection<SisbolPersonIndexItem> rows,
        string sourcePath,
        string hash,
        DateTime? startDate,
        DateTime? endDate,
        string currentRank,
        string currentPerson,
        string currentMainSubject,
        string currentNoteType,
        Match biMatch,
        IReadOnlyList<MilitaryCandidate> military)
    {
        var bulletinDate = ParseBrazilianDate(biMatch.Groups[2].Value);
        var note = biMatch.Groups[4].Value.Trim();
        var user = biMatch.Groups[5].Value.Trim();
        _ = int.TryParse(biMatch.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page);
        var linked = FindMilitary(currentPerson, currentRank, military);
        var main = NormalizeSubjectValue(currentMainSubject);
        var noteType = NormalizeSubjectValue(currentNoteType);
        var subjectNote = BuildSubjectNote(main, noteType);
        var created = DateTime.Now;
        var row = new SisbolPersonIndexItem
        {
            SourcePdfPath = sourcePath,
            SourcePdfHash = hash,
            IndexStartDate = startDate,
            IndexEndDate = endDate,
            Rank = currentRank,
            PersonName = currentPerson,
            MilitaryId = linked?.Id,
            MainSubject = main,
            SubSubject = noteType,
            SubjectNote = subjectNote,
            BulletinType = "BI",
            BulletinNumber = biMatch.Groups[1].Value.Trim() + "/" + (bulletinDate?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            BulletinDate = bulletinDate,
            BulletinPage = page > 0 ? page : null,
            NoteNumber = note,
            SisbolUser = user,
            CreatedAt = created,
            UpdatedAt = created
        };
        row.SearchText = BuildSearchText(row, linked);
        rows.Add(row);
    }

    private static NextIndexLineKind FindNextSignificantKind(IReadOnlyList<IndexLine> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var line = lines[i].Text;
            if (ShouldSkipLine(line)) continue;
            if (IsRankLine(line) || line.StartsWith('»')) return NextIndexLineKind.Scope;
            if (BiLineRegex.IsMatch(line)) return NextIndexLineKind.Bi;
            if (lines[i].IsBullet) return NextIndexLineKind.Bullet;
            if (IsContinuationLine(line)) continue;
            return NextIndexLineKind.Other;
        }
        return NextIndexLineKind.End;
    }

    private enum NextIndexLineKind
    {
        End,
        Bi,
        Bullet,
        Scope,
        Other
    }

    private static IReadOnlyList<IndexLine> NormalizeIndexLines(string page)
    {
        var parsed = (page ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(ToIndexLine)
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToList();

        var indents = parsed
            .Where(line => line.IsBullet)
            .Select(line => line.BulletLevel)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (indents.Count < 2) return parsed;

        var firstIndent = indents[0];
        var secondIndent = indents.FirstOrDefault(value => value > firstIndent);
        if (secondIndent <= firstIndent) return parsed;

        var threshold = firstIndent + Math.Max(1, (secondIndent - firstIndent) / 2);
        return parsed
            .Select(line => !line.IsBullet
                ? line
                : line with { BulletLevel = line.BulletLevel <= threshold ? 1 : 2 })
            .ToList();
    }

    private static IndexLine ToIndexLine(string raw)
    {
        raw ??= string.Empty;
        var rightTrimmed = raw.TrimEnd();
        var trimmed = Regex.Replace(rightTrimmed.Trim(), @"\s+", " ");
        var match = Regex.Match(rightTrimmed, @"^(?<indent>\s*)-\s*(?<text>\S.*)$");
        if (!match.Success) return new IndexLine(trimmed, false, 0, string.Empty);
        var indent = match.Groups["indent"].Value.Length;
        var clean = Regex.Replace(match.Groups["text"].Value.Trim(), @"\s+", " ");
        // Aqui BulletLevel guarda temporariamente o recuo bruto. NormalizeIndexLines converte
        // para nível lógico 1/2 comparando com os recuos existentes na própria página.
        return new IndexLine(trimmed, true, indent, clean);
    }

    private sealed record IndexLine(string Text, bool IsBullet, int BulletLevel, string CleanBullet, int PageIndex = 0);

    private static IReadOnlyList<string> NormalizeLines(string page)
        => (page ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => Regex.Replace(line ?? string.Empty, @"\s+", " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private static bool ShouldSkipLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        if (line.StartsWith("MINISTÉRIO", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("EXÉRCITO", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("CML -", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("COMPANHIA DE POLÍCIA DO EXÉRCITO", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("PEL POL", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("ÍNDICE REMISSIVO", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("POSTO/GRADUAÇÃO", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("NOME DA PESSOA", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("(Continuação do Índice", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(line, @"^-\s*Página\s+\d+\s*-$", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static bool IsRankLine(string line)
        => line.Length <= 35 && RankRegex.IsMatch(Regex.Replace(line, @"\s+", " ").Trim());

    private static string NormalizeRankLabel(string line)
        => Regex.Replace(line.Trim(), @"\s+", " ");

    private static bool IsBulletLine(string line)
        => Regex.IsMatch(line, @"^-\s*\S");

    private static string CleanBulletLine(string line)
        => Regex.Replace(line, @"^-\s*", string.Empty).Trim();

    private static bool IsContinuationLine(string line)
    {
        if (line.StartsWith("BI ", StringComparison.OrdinalIgnoreCase)) return false;
        if (line.StartsWith('»')) return false;
        if (IsRankLine(line)) return false;
        if (IsBulletLine(line)) return false;
        if (ShouldSkipLine(line)) return false;
        return line.Length >= 2;
    }

    private static string AppendContinuation(string value, string continuation)
    {
        var clean = Regex.Replace(continuation ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(clean)) return value;
        return Regex.Replace((value + " " + clean).Trim(), @"\s+", " ").Trim();
    }

    private static bool IsLikelyMainSubject(string value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length == 0) return false;
        if (text.Length >= 30) return true;
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return false;
        var upper = letters.Count(char.IsUpper);
        var ratio = upper / (double)letters.Length;
        if (ratio >= 0.72) return true;
        var normalized = NormalizeForSearch(text);
        var mainKeywords = new[]
        {
            "ferias", "adicional", "auxilio", "pensao", "gratificacao", "despesa", "implantacao", "saque",
            "licenciamento", "transferencia", "instrucao", "formatura", "sindicancia", "servico de saude", "atualizacao", "designacao"
        };
        return mainKeywords.Any(keyword => normalized.StartsWith(keyword, StringComparison.Ordinal));
    }

    private static bool TrySplitInlineSubjectNote(string value, out string main, out string sub)
    {
        main = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        sub = string.Empty;
        if (string.IsNullOrWhiteSpace(main)) return false;

        var parts = Regex.Split(main, @"\s+-\s+")
            .Select(part => Regex.Replace(part, @"\s+", " ").Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length < 2) return false;

        var first = parts[0];
        var rest = string.Join(" - ", parts.Skip(1));
        if (!IsLikelyMainSubject(first) && first.Length < 14) return false;

        main = first;
        sub = rest;
        return true;
    }

    private static string NormalizeSubjectValue(string value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '–', '—', ':', ';');
        text = Regex.Replace(text, @"^(?:[-–—•]+\s*)+", string.Empty).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 180) text = text[..180].TrimEnd() + "…";
        return text;
    }

    private static string BuildSubjectNote(string main, string sub)
    {
        main = Regex.Replace(main ?? string.Empty, @"\s+", " ").Trim();
        sub = Regex.Replace(sub ?? string.Empty, @"\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(main) && !string.IsNullOrWhiteSpace(sub)) return $"{main} — {sub}";
        return string.IsNullOrWhiteSpace(main) ? "Assunto não identificado" : main;
    }

    private static MilitaryCandidate? FindMilitary(string personName, string rank, IReadOnlyList<MilitaryCandidate> military)
    {
        var normalized = NormalizeName(personName);
        if (normalized.Length == 0) return null;
        var exact = military.FirstOrDefault(x => x.NormalizedName == normalized);
        if (exact is not null) return exact;

        var tokens = Tokenize(normalized).Where(x => x.Length > 1).ToArray();
        if (tokens.Length == 0) return null;
        var first = tokens.First();
        var last = tokens.Last();
        var sameRank = MilitaryRankService.Normalize(rank);
        var strong = military
            .Select(candidate => new { Candidate = candidate, Score = NameScore(tokens, candidate.NameTokens) })
            .Where(x => x.Score >= 0.78)
            .OrderByDescending(x => x.Score)
            .ToList();
        if (strong.Count == 0)
        {
            strong = military
                .Where(candidate => candidate.NameTokens.Contains(first) && candidate.NameTokens.Contains(last))
                .Select(candidate => new { Candidate = candidate, Score = 0.76 })
                .ToList();
        }
        if (strong.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(sameRank))
        {
            var ranked = strong.FirstOrDefault(x => MilitaryRankService.Normalize(x.Candidate.Rank).Equals(sameRank, StringComparison.OrdinalIgnoreCase));
            if (ranked is not null) return ranked.Candidate;
        }
        return strong[0].Candidate;
    }

    private static double NameScore(IReadOnlyCollection<string> tokens, IReadOnlyCollection<string> candidateTokens)
    {
        if (tokens.Count == 0 || candidateTokens.Count == 0) return 0;
        var hit = tokens.Count(candidateTokens.Contains);
        var reverseHit = candidateTokens.Count(tokens.Contains);
        return (hit / (double)tokens.Count + reverseHit / (double)candidateTokens.Count) / 2d;
    }

    private static string[] Tokenize(string normalizedName)
        => normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildSearchText(SisbolPersonIndexItem item, MilitaryCandidate? linked)
    {
        var raw = string.Join(' ',
            item.Rank, item.PersonName, item.MainSubject, item.SubSubject, item.SubjectNote, item.BulletinType,
            item.BulletinNumber, item.BulletinDate?.ToString("dd/MM/yyyy"), item.BulletinPage, item.NoteNumber,
            item.SisbolUser, linked?.Name, linked?.WarName, linked?.Cpf, linked?.PrecCp, linked?.Identity);
        return NormalizeForSearch(raw + " " + Digits(raw));
    }

    private async Task<string> CopyToIndexLibraryAsync(string sourcePdfPath, string hash, CancellationToken cancellationToken)
    {
        var original = Path.GetFileNameWithoutExtension(sourcePdfPath);
        var safe = SafeFileName(original);
        if (!safe.Contains("indice", StringComparison.OrdinalIgnoreCase)) safe += "_indice_por_pessoa";
        var destination = Path.Combine(_paths.SisbolPersonIndexDirectory, safe + ".pdf");
        if (File.Exists(destination))
        {
            var existingHash = await ComputeHashAsync(destination, cancellationToken);
            if (existingHash.Equals(hash, StringComparison.OrdinalIgnoreCase)) return destination;
        }
        if (Path.GetFullPath(sourcePdfPath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return destination;
        File.Copy(sourcePdfPath, destination, true);
        return destination;
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static SisbolPersonIndexItem ReadItem(SqliteDataReader reader)
        => new()
        {
            Id = SafeInt(reader, "id"),
            SourcePdfPath = SafeString(reader, "source_pdf_path"),
            SourcePdfHash = SafeString(reader, "source_pdf_hash"),
            IndexStartDate = ParseIsoDate(SafeString(reader, "index_start_date")),
            IndexEndDate = ParseIsoDate(SafeString(reader, "index_end_date")),
            Rank = SafeString(reader, "rank"),
            PersonName = SafeString(reader, "person_name"),
            MilitaryId = SafeNullableInt(reader, "military_id"),
            MainSubject = SafeString(reader, "main_subject"),
            SubSubject = SafeString(reader, "sub_subject"),
            SubjectNote = SafeString(reader, "subject_note"),
            BulletinType = SafeString(reader, "bulletin_type"),
            BulletinNumber = SafeString(reader, "bulletin_number"),
            BulletinDate = ParseIsoDate(SafeString(reader, "bulletin_date")),
            BulletinPage = SafeNullableInt(reader, "bulletin_page"),
            NoteNumber = SafeString(reader, "note_number"),
            SisbolUser = SafeString(reader, "sisbol_user"),
            SearchText = SafeString(reader, "search_text"),
            CreatedAt = ParseDateTime(SafeString(reader, "created_at")) ?? DateTime.MinValue,
            UpdatedAt = ParseDateTime(SafeString(reader, "updated_at")) ?? DateTime.MinValue
        };

    private static string SafeString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetValue(ordinal)?.ToString() ?? string.Empty;
    }

    private static int SafeInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return 0;
        var value = reader.GetValue(ordinal);
        return value is long l ? (int)l : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static int? SafeNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value is long l ? (int)l : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseBrazilianDate(string value)
        => DateTime.TryParseExact(value.Trim(), new[] { "dd/MM/yyyy", "d/M/yyyy" }, PtBr, DateTimeStyles.None, out var date) ? date.Date : null;

    private static DateTime? ParseIsoDate(string? value)
        => DateTime.TryParseExact(value ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : null;

    private static DateTime? ParseDateTime(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    private static string ToIsoDate(DateTime? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static int MonthNumber(string value)
    {
        var normalized = NormalizeForSearch(value);
        var months = new Dictionary<string, int>
        {
            ["janeiro"] = 1, ["fevereiro"] = 2, ["marco"] = 3, ["março"] = 3, ["abril"] = 4,
            ["maio"] = 5, ["junho"] = 6, ["julho"] = 7, ["agosto"] = 8, ["setembro"] = 9,
            ["outubro"] = 10, ["novembro"] = 11, ["dezembro"] = 12
        };
        if (int.TryParse(normalized, out var month) && month is >= 1 and <= 12) return month;
        return months.TryGetValue(normalized, out month) ? month : 0;
    }

    private static string StripCountSuffix(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s*\(\d+\)\s*$", string.Empty).Trim();

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n')) return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private static string NormalizeForSearch(string? value)
    {
        var characters = (value ?? string.Empty).Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')
            .ToArray();
        return string.Join(' ', new string(characters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeName(string? value)
    {
        var characters = (value ?? string.Empty).Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ')
            .ToArray();
        return string.Join(' ', new string(characters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", "_").Trim('_');
        cleaned = Regex.Replace(cleaned, "_+", "_");
        return string.IsNullOrWhiteSpace(cleaned) ? $"indice_por_pessoa_{DateTime.Now:yyyyMMdd_HHmmss}" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    private sealed class MilitaryCandidate
    {
        public int Id { get; init; }
        public string Rank { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string WarName { get; init; } = string.Empty;
        public string Cpf { get; init; } = string.Empty;
        public string PrecCp { get; init; } = string.Empty;
        public string Identity { get; init; } = string.Empty;
        public string NormalizedName { get; init; } = string.Empty;
        public string[] NameTokens { get; init; } = [];

        public static MilitaryCandidate FromRecord(MilitaryRecord record)
        {
            var normalized = NormalizeName(record.Name);
            return new MilitaryCandidate
            {
                Id = record.Id,
                Rank = record.Rank,
                Name = record.Name,
                WarName = record.WarName,
                Cpf = record.Cpf,
                PrecCp = record.PrecCp,
                Identity = record.MilitaryId,
                NormalizedName = normalized,
                NameTokens = Tokenize(normalized).Where(x => x.Length > 1).ToArray()
            };
        }
    }
}
