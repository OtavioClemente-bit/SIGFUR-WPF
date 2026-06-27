using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class MeasuresTakenService
{
    private const string DefaultOm = "4ª Cia PE";
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly MilitaryRepository _military;
    private readonly LicensedTransferredRepository _licensed;
    private readonly PdfTextService _pdf;
    private readonly LogService _log;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public MeasuresTakenService(AppPaths paths, JsonFileService json, MilitaryRepository military, LicensedTransferredRepository licensed, PdfTextService pdf, LogService log)
    {
        _paths = paths;
        _json = json;
        _military = military;
        _licensed = licensed;
        _pdf = pdf;
        _log = log;
        Directory.CreateDirectory(_paths.MeasuresTakenOutputDirectory);
    }

    public async Task<MeasuresTakenSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = (await _json.LoadAsync<MeasuresTakenSettings>(_paths.MeasuresTakenSettingsFile)) ?? new MeasuresTakenSettings();
        settings.Organization = string.IsNullOrWhiteSpace(settings.Organization) ? DefaultOm : settings.Organization;
        settings.DefaultMeasure = string.IsNullOrWhiteSpace(settings.DefaultMeasure) ? "Em análise." : settings.DefaultMeasure;
        settings.PaymentDefaultMeasure = string.IsNullOrWhiteSpace(settings.PaymentDefaultMeasure) ? settings.DefaultMeasure : settings.PaymentDefaultMeasure;
        settings.PaystubDefaultMeasure = string.IsNullOrWhiteSpace(settings.PaystubDefaultMeasure) ? settings.DefaultMeasure : settings.PaystubDefaultMeasure;
        settings.OutputDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : _paths.MeasuresTakenOutputDirectory;
        return settings;
    }

    public Task SaveSettingsAsync(MeasuresTakenSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(_paths.MeasuresTakenSettingsFile, settings);

    public async Task<List<MeasuresMilitaryItem>> LoadPeopleAsync(string source, CancellationToken cancellationToken = default)
    {
        var result = new List<MeasuresMilitaryItem>();
        if (source.Equals("Transferidos", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await _licensed.GetAllAsync(true, cancellationToken: cancellationToken);
            result.AddRange(rows.Select(x => new MeasuresMilitaryItem { Military = x.ToMilitaryRecord(), Source = "Transferidos", IsTransferred = true }));
        }
        else if (source.Equals("Todos", StringComparison.OrdinalIgnoreCase))
        {
            var activeTask = _military.GetAllAsync(cancellationToken);
            var licensedTask = _licensed.GetAllAsync(true, cancellationToken: cancellationToken);
            await Task.WhenAll(activeTask, licensedTask);
            result.AddRange(activeTask.Result.Select(x => new MeasuresMilitaryItem { Military = x, Source = "Banco principal" }));
            result.AddRange(licensedTask.Result.Select(x => new MeasuresMilitaryItem { Military = x.ToMilitaryRecord(), Source = "Transferidos", IsTransferred = true }));
        }
        else
        {
            result.AddRange((await _military.GetAllAsync(cancellationToken)).Select(x => new MeasuresMilitaryItem { Military = x, Source = "Banco principal" }));
        }

        return result
            .GroupBy(x => $"{x.IsTransferred}:{x.Military.Id}")
            .Select(x => x.First())
            .OrderBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IEnumerable<MeasuresMilitaryItem> FilterPeople(IEnumerable<MeasuresMilitaryItem> source, string search)
    {
        var query = Normalize(search);
        var digits = MilitaryFormatting.Digits(search);
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(digits)) return source;
        return source.Where(x => Normalize(x.SearchText).Contains(query, StringComparison.Ordinal) || (!string.IsNullOrWhiteSpace(digits) && MilitaryFormatting.Digits(x.SearchText).Contains(digits, StringComparison.Ordinal)));
    }

    public async Task<MeasuresPdfImportResult> ImportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await _pdf.ExtractAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Não foi possível extrair texto do PDF.");
        var result = new MeasuresPdfImportResult { ExtractedText = text, SuggestedOrigin = SuggestOrigin(text, path) };
        var lines = text.Replace("\u00ad", string.Empty, StringComparison.Ordinal)
            .Replace("\ufeff", string.Empty, StringComparison.Ordinal)
            .Replace("\ufffd", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanLine)
            .Where(x => x.Length > 0)
            .ToList();

        var section = string.Empty;
        var insideFindings = false;
        for (var index = 0; index < lines.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            var normalized = Normalize(line);
            if (Regex.IsMatch(normalized, @"\b4\s+a equipe constatou")) insideFindings = true;
            if (insideFindings && Regex.IsMatch(normalized, @"\b5\s+as falhas constantes")) break;
            if (insideFindings && Regex.IsMatch(normalized, @"\ba\s+exame de pagamento")) { section = MeasuresSections.PaymentExam; index++; continue; }
            if (insideFindings && Regex.IsMatch(normalized, @"\bb\s+exame de contracheque")) { section = MeasuresSections.PaystubExam; index++; continue; }
            if (insideFindings && !string.IsNullOrWhiteSpace(section) && IsRowNumber(line))
            {
                var parsed = ParsePdfRow(lines, index, section);
                if (parsed.Entry is not null && IsValidImportedName(parsed.Entry.Name)) result.Entries.Add(parsed.Entry);
                if (parsed.NextIndex > index) { index = parsed.NextIndex; continue; }
            }
            index++;
        }

        result.Entries = result.Entries
            .GroupBy(x => $"{x.Section}|{Normalize(x.Name)}|{MilitaryFormatting.Digits(x.Code)}")
            .Select(x => x.First())
            .ToList();
        return result;
    }

    private static string CleanLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static bool IsRowNumber(string value) => Regex.IsMatch(CleanLine(value), @"^\d{1,3}$");
    private static bool IsCodeLine(string value)
    {
        var digits = MilitaryFormatting.Digits(value);
        return digits.Length is >= 6 and <= 12 && Regex.IsMatch(CleanLine(value), @"^[\d.\-/ ]+$");
    }

    private static readonly Regex ImportedRankPattern = new(
        @"^(?<rank>Gen\s+Ex|Gen\s+Div|Gen\s+Bda|Ten\s+Cel|S\s*Ten|ST|Asp\s+Of|[12][º°]?\s*Ten|[123][º°]?\s*Sgt|Sd\s+EngE|Sd\s+Rcr|Sd\s+EV|Sd\s+Ef\s+(?:Profl|Vrv)|Sd|Cb\s+Ef\s+Profl|Cb|Cap|Maj|Cel)(?:\s+(?<name>.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsChangeMarker(string value)
    {
        var compact = Regex.Replace(CleanLine(value), @"\s+", string.Empty).Replace(";", string.Empty, StringComparison.Ordinal);
        return Regex.IsMatch(compact, @"^(?:[IVX]+,)?(?:\(\d{1,3}\),?)+\.?[,]?$", RegexOptions.IgnoreCase)
               || Regex.IsMatch(compact, @"^[IVX]+,?$", RegexOptions.IgnoreCase);
    }

    private static bool IsPdfNoise(string value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text)) return true;
        return text.StartsWith("dadic", StringComparison.Ordinal)
               || text.StartsWith("assinado com senha", StringComparison.Ordinal)
               || text.StartsWith("autenticado com senha", StringComparison.Ordinal)
               || text.StartsWith("documento n", StringComparison.Ordinal)
               || text.StartsWith("consulta a autenticidade", StringComparison.Ordinal)
               || text.Contains("svpdigital", StringComparison.Ordinal)
               || text.StartsWith("pag n", StringComparison.Ordinal)
               || text.StartsWith("continuacao", StringComparison.Ordinal);
    }

    private static (MeasuresPdfEntry? Entry, int NextIndex) ParsePdfRow(IReadOnlyList<string> lines, int index, string section)
    {
        if (index + 2 >= lines.Count || !IsRowNumber(lines[index]) || !IsCodeLine(lines[index + 1])) return (null, index + 1);
        var cursor = index + 2;
        while (cursor < lines.Count && IsPdfNoise(lines[cursor])) cursor++;
        if (cursor >= lines.Count) return (null, cursor);
        var rankMatch = ImportedRankPattern.Match(CleanLine(lines[cursor]));
        if (!rankMatch.Success) return (null, index + 1);
        var rank = MilitaryRankService.ShortName(rankMatch.Groups["rank"].Value);
        var nameParts = new List<string>();
        if (rankMatch.Groups["name"].Success) nameParts.Add(rankMatch.Groups["name"].Value);
        cursor++;
        var changes = string.Empty;
        while (cursor < lines.Count)
        {
            var current = CleanLine(lines[cursor]);
            var normalized = Normalize(current);
            if (IsChangeMarker(current)) { changes = current; cursor++; break; }
            if (Regex.IsMatch(normalized, @"\b5\s+as falhas constantes") || Regex.IsMatch(normalized, @"\b[abc]\s+exame de")) break;
            if (new[] { "nº", "nr", "ord", "prec cp", "idt mil", "p grad", "nome", "alterações", "providência" }.Any(x => string.Equals(Normalize(x), normalized, StringComparison.Ordinal))) { cursor++; continue; }
            if (IsPdfNoise(current)) { cursor++; continue; }
            if (IsRowNumber(current) && cursor + 1 < lines.Count && IsCodeLine(lines[cursor + 1])) break;
            nameParts.Add(current);
            cursor++;
        }
        var name = JoinNameParts(nameParts);
        if (string.IsNullOrWhiteSpace(name)) return (null, cursor);
        return (new MeasuresPdfEntry
        {
            Section = section,
            Order = int.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : 0,
            Code = MilitaryFormatting.Digits(lines[index + 1]),
            Rank = rank,
            Name = name,
            Changes = changes
        }, cursor);
    }

    private static string JoinNameParts(IEnumerable<string> parts)
    {
        var result = string.Empty;
        foreach (var raw in parts)
        {
            var part = CleanLine(raw);
            if (part.Length == 0) continue;
            result = result.EndsWith("-", StringComparison.Ordinal) ? result[..^1] + part : (result + " " + part).Trim();
        }
        return Regex.Replace(result, @"\s+", " ").Trim(' ', ',', '.', ';');
    }

    private static bool IsValidImportedName(string name)
    {
        var cleaned = CleanLine(name);
        if (cleaned.Length < 5) return false;
        var normalized = Normalize(cleaned);
        if (new[] { "legenda", "alteracoes", "providencia", "relatorio", "pagamento", "divergencias" }.Any(marker => normalized.Contains(marker, StringComparison.Ordinal))) return false;
        return cleaned.Count(char.IsLetter) >= 5;
    }

    public static List<(MeasuresPdfEntry Entry, MeasuresMilitaryItem? Match)> MatchImported(IEnumerable<MeasuresPdfEntry> entries, IReadOnlyList<MeasuresMilitaryItem> people)
    {
        var result = new List<(MeasuresPdfEntry, MeasuresMilitaryItem?)>();
        foreach (var entry in entries)
        {
            MeasuresMilitaryItem? match = null;
            var code = MilitaryFormatting.Digits(entry.Code);
            if (!string.IsNullOrWhiteSpace(code))
            {
                match = people.FirstOrDefault(x =>
                {
                    var personCode = MilitaryFormatting.Digits(x.PrecCp);
                    return !string.IsNullOrWhiteSpace(personCode)
                           && (personCode.EndsWith(code, StringComparison.Ordinal) || code.EndsWith(personCode, StringComparison.Ordinal));
                });
            }
            if (match is null)
            {
                var target = Normalize(entry.Name);
                match = people
                    .Select(x => (Item: x, Score: Similarity(target, Normalize(x.Name))))
                    .Where(x => x.Score >= 0.66)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Item)
                    .FirstOrDefault();
            }
            result.Add((entry, match));
        }
        return result;
    }

    public static string BuildPreview(MeasuresDocumentData data)
    {
        var sb = new StringBuilder();
        foreach (var line in HeaderLines) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine($"Medidas tomadas conforme {CleanOrigin(data.OriginText)}:");
        sb.AppendLine();
        sb.AppendLine("a. RELATÓRIO DO EXAME DE PAGAMENTO DE PESSOAL");
        sb.AppendLine();

        AppendPreviewSection(sb, 1, "Exame de Pagamento", data.Organization, SectionItems(data, MeasuresSections.PaymentExam), DefaultMeasureFor(data, MeasuresSections.PaymentExam));
        AppendPreviewSection(sb, 2, "Exame de Contracheque", data.Organization, SectionItems(data, MeasuresSections.PaystubExam), DefaultMeasureFor(data, MeasuresSections.PaystubExam));

        sb.AppendLine();
        sb.AppendLine(data.CommanderName?.Trim() ?? string.Empty);
        sb.AppendLine(MilitaryRankService.ShortName(data.CommanderRank));
        sb.AppendLine(data.SignatureRole?.Trim() ?? string.Empty);
        return sb.ToString().TrimEnd();
    }

    private static readonly string[] HeaderLines =
    [
        "MINISTÉRIO DA DEFESA",
        "EXÉRCITO BRASILEIRO",
        "C M L - 4ª RM",
        "4ª COMPANHIA DE POLÍCIA DO EXÉRCITO",
        "(Pel Pol QGR/4ª RM/1950)"
    ];

    private static string CleanOrigin(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return text.TrimEnd('.', ':');
    }

    private static List<MeasuresSelectedItem> SectionItems(MeasuresDocumentData data, string section)
        => data.Items.Where(x => x.Section == section).OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string DefaultMeasureFor(MeasuresDocumentData data, string section)
    {
        var value = section == MeasuresSections.PaystubExam ? data.PaystubDefaultMeasure : data.PaymentDefaultMeasure;
        if (string.IsNullOrWhiteSpace(value)) value = data.DefaultMeasure;
        return string.IsNullOrWhiteSpace(value) ? "Em análise." : value.Trim();
    }

    private static void AppendPreviewSection(StringBuilder sb, int number, string title, string organization, IReadOnlyList<MeasuresSelectedItem> items, string defaultMeasure)
    {
        sb.AppendLine($"{number}) {title} - da {organization.Trim()}:");
        sb.AppendLine();
        sb.AppendLine("Nº | Prec-CP | P/Grad | Nome | Medida Tomada");
        sb.AppendLine(new string('-', 110));
        if (items.Count == 0)
        {
            sb.AppendLine("—");
        }
        else
        {
            var index = 1;
            foreach (var item in items)
            {
                var measure = string.IsNullOrWhiteSpace(item.IndividualMeasure) ? defaultMeasure : item.IndividualMeasure;
                sb.AppendLine($"{index++} | {item.PrecCp} | {item.Rank} | {item.Name.ToUpper(CultureInfo.GetCultureInfo("pt-BR"))} | {measure}");
            }
        }
        sb.AppendLine();
        sb.AppendLine();
    }

    public async Task ExportPaymentRelationXlsxAsync(string path, MeasuresDocumentData data, CancellationToken cancellationToken = default)
    {
        var rows = SectionItems(data, MeasuresSections.PaymentExam).Select(x => x.Person.Military).ToList();
        if (rows.Count == 0) throw new InvalidOperationException("Não há militares no Exame de Pagamento para gerar a relação nominal.");
        await MilitaryExportService.ExportPersonnelRelationAsync(path, rows, cancellationToken);
    }

    public async Task ExportDocxAsync(string path, MeasuresDocumentData data, CancellationToken cancellationToken = default)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _paths.MeasuresTakenOutputDirectory);
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "[Content_Types].xml", WordContentTypes, cancellationToken);
                await WriteEntryAsync(archive, "_rels/.rels", WordRootRels, cancellationToken);
                await WriteEntryAsync(archive, "word/document.xml", BuildWordXml(data), cancellationToken);
            }
            using (var archive = ZipFile.OpenRead(temp))
                if (archive.GetEntry("word/document.xml") is null) throw new InvalidDataException("Documento Word incompleto.");
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task ExportOdtAsync(string path, MeasuresDocumentData data, CancellationToken cancellationToken = default)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _paths.MeasuresTakenOutputDirectory);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
                var mime = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                await using (var writer = new StreamWriter(mime.Open(), new UTF8Encoding(false))) await writer.WriteAsync("application/vnd.oasis.opendocument.text");
                await WriteEntryAsync(archive, "META-INF/manifest.xml", OdtManifest, cancellationToken);
                await WriteEntryAsync(archive, "styles.xml", OdtStyles, cancellationToken);
                await WriteEntryAsync(archive, "content.xml", BuildOdtContent(data), cancellationToken);
            }
            // O stream precisa estar totalmente fechado antes da troca atômica; evita "arquivo em uso".
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task<string> ExportPdfAsync(string path, MeasuresDocumentData data, CancellationToken cancellationToken = default)
    {
        var odt = Path.Combine(Path.GetTempPath(), $"medidas_{Guid.NewGuid():N}.odt");
        try
        {
            await ExportOdtAsync(odt, data, cancellationToken);
            var output = Path.GetDirectoryName(path) ?? _paths.MeasuresTakenOutputDirectory;
            Directory.CreateDirectory(output);
            var soffice = FindLibreOffice();
            if (soffice is null) throw new InvalidOperationException("LibreOffice não encontrado. Gere DOCX/ODT ou instale o LibreOffice para exportar PDF.");
            var psi = new ProcessStartInfo { FileName = soffice, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--headless", "--convert-to", "pdf", "--outdir", output, odt }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o LibreOffice.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException("O LibreOffice não conseguiu gerar o PDF.");
            var generated = Path.Combine(output, Path.GetFileNameWithoutExtension(odt) + ".pdf");
            if (!File.Exists(generated)) throw new FileNotFoundException("PDF não foi criado.", generated);
            File.Move(generated, path, true);
            return path;
        }
        finally { try { if (File.Exists(odt)) File.Delete(odt); } catch { } }
    }

    public async Task<List<MeasuresSavedWorkSummary>> ListWorksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<MeasuresSavedWorkSummary>();
        await using var connection = OpenWorks();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,nome,criado_em,atualizado_em FROM trabalhos_salvos ORDER BY atualizado_em DESC,nome COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new MeasuresSavedWorkSummary { Id = reader.GetInt32(0), Name = reader.GetString(1), CreatedAt = ParseDate(reader.GetString(2)), UpdatedAt = ParseDate(reader.GetString(3)) });
        return result;
    }

    public async Task<(string Name, MeasuresSavedWorkPayload Payload)?> LoadWorkAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenWorks(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT nome,dados_json FROM trabalhos_salvos WHERE id=$id;"; command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var payload = JsonSerializer.Deserialize<MeasuresSavedWorkPayload>(reader.GetString(1), JsonOptions()) ?? new MeasuresSavedWorkPayload();
        return (reader.GetString(0), payload);
    }

    public async Task<int> SaveWorkAsync(string name, MeasuresSavedWorkPayload payload, int? id = null, CancellationToken cancellationToken = default)
    {
        name = Regex.Replace(name ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Informe o nome do trabalho.");
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenWorks(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
        if (id is null)
        {
            await using var check = connection.CreateCommand(); check.Transaction = (SqliteTransaction)transaction; check.CommandText = "SELECT id FROM trabalhos_salvos WHERE nome=$nome COLLATE NOCASE;"; check.Parameters.AddWithValue("$nome", name);
            if (await check.ExecuteScalarAsync(cancellationToken) is not null) throw new InvalidOperationException("Já existe um trabalho com esse nome.");
            await using var insert = connection.CreateCommand(); insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO trabalhos_salvos(nome,dados_json,criado_em,atualizado_em) VALUES($nome,$json,$agora,$agora); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$nome", name); insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(payload, JsonOptions())); insert.Parameters.AddWithValue("$agora", now);
            var value = await insert.ExecuteScalarAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction; update.CommandText = "UPDATE trabalhos_salvos SET nome=$nome,dados_json=$json,atualizado_em=$agora WHERE id=$id;";
            update.Parameters.AddWithValue("$nome", name); update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(payload, JsonOptions())); update.Parameters.AddWithValue("$agora", now); update.Parameters.AddWithValue("$id", id.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken); return id.Value;
    }

    public async Task DeleteWorkAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken); await using var connection = OpenWorks(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM trabalhos_salvos WHERE id=$id;"; command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.MeasuresTakenWorksDatabaseFile)!);
            await using var connection = OpenWorks(); await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS trabalhos_salvos(id INTEGER PRIMARY KEY AUTOINCREMENT,nome TEXT NOT NULL UNIQUE,dados_json TEXT NOT NULL,criado_em TEXT NOT NULL,atualizado_em TEXT NOT NULL);"; await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }

    private SqliteConnection OpenWorks() => new(new SqliteConnectionStringBuilder { DataSource = _paths.MeasuresTakenWorksDatabaseFile, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString());
    private static DateTime ParseDate(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : DateTime.MinValue;
    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string SuggestOrigin(string text, string path)
    {
        var compact = Regex.Replace(text ?? string.Empty, @"\s+", " ");
        var month = Regex.Match(compact, @"M[eê]s\s+de\s+refer[eê]ncia\s*:\s*([A-ZÁÉÍÓÚÂÊÔÃÕÇ]+)", RegexOptions.IgnoreCase);
        var year = Regex.Match(compact, @"Exerc[ií]cio\s+Financeiro\s*:\s*(\d{4})", RegexOptions.IgnoreCase);
        if (month.Success && year.Success)
            return $"Relatório do Exame de Pagamento de Pessoal de {month.Groups[1].Value.ToUpper(CultureInfo.GetCultureInfo("pt-BR"))}/{year.Groups[1].Value}";
        return "Relatório do Exame de Pagamento de Pessoal";
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return Regex.Replace(new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var m = a.Length;
        var n = b.Length;
        var d = new int[m + 1, n + 1];
        for (var i = 0; i <= m; i++) d[i, 0] = i; for (var j = 0; j <= n; j++) d[0, j] = j;
        for (var i = 1; i <= m; i++) for (var j = 1; j <= n; j++) d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1d - (double)d[m, n] / Math.Max(m, n);
    }

    private static string BuildWordXml(MeasuresDocumentData data)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        foreach (var line in HeaderLines) AppendParagraph(sb, line, true, "center", 0);
        AppendParagraph(sb, string.Empty, false, "left", 0);
        AppendParagraph(sb, $"Medidas tomadas conforme {CleanOrigin(data.OriginText)}:", false, "left", 0);
        AppendParagraph(sb, string.Empty, false, "left", 0);
        AppendParagraph(sb, "a. RELATÓRIO DO EXAME DE PAGAMENTO DE PESSOAL", true, "left", 40);
        AppendParagraph(sb, string.Empty, false, "left", 0);

        AppendWordSection(sb, 1, "Exame de Pagamento", data.Organization, SectionItems(data, MeasuresSections.PaymentExam), DefaultMeasureFor(data, MeasuresSections.PaymentExam));
        AppendWordSection(sb, 2, "Exame de Contracheque", data.Organization, SectionItems(data, MeasuresSections.PaystubExam), DefaultMeasureFor(data, MeasuresSections.PaystubExam));

        AppendParagraph(sb, string.Empty, false, "left", 0);
        AppendParagraph(sb, string.Empty, false, "left", 160);
        AppendParagraph(sb, data.CommanderName?.Trim() ?? string.Empty, true, "center", 0);
        AppendParagraph(sb, MilitaryRankService.ShortName(data.CommanderRank), false, "center", 0);
        AppendParagraph(sb, data.SignatureRole?.Trim() ?? string.Empty, false, "center", 0);
        sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1417\"/></w:sectPr></w:body></w:document>");
        return sb.ToString();
    }

    private static void AppendWordSection(StringBuilder sb, int number, string title, string organization, IReadOnlyList<MeasuresSelectedItem> items, string defaultMeasure)
    {
        AppendParagraph(sb, $"{number}) {title} - da {organization.Trim()}:", false, "left", 40);
        AppendTable(sb, items, defaultMeasure);
        AppendParagraph(sb, string.Empty, false, "left", 40);
    }

    private static void AppendParagraph(StringBuilder sb, string text, bool bold, string align, int after)
    {
        sb.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"").Append(after).Append("\" w:line=\"240\" w:lineRule=\"auto\"/><w:jc w:val=\"").Append(align).Append("\"/></w:pPr>");
        AppendRun(sb, text, bold, 24);
        sb.Append("</w:p>");
    }

    private static void AppendTable(StringBuilder sb, IReadOnlyList<MeasuresSelectedItem> items, string defaultMeasure)
    {
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>");
        AppendRow(sb, ["Nº", "Prec-CP", "P/Grad", "Nome", "Medida Tomada"], null, true);
        if (items.Count == 0)
        {
            sb.Append("<w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"5\"/></w:tcPr><w:p><w:pPr><w:jc w:val=\"center\"/><w:spacing w:after=\"0\"/></w:pPr>");
            AppendRun(sb, "—", false, 22);
            sb.Append("</w:p></w:tc></w:tr>");
        }
        else
        {
            var number = 1;
            foreach (var item in items)
                AppendRow(sb, [number++.ToString(), item.PrecCp, item.Rank, item.Name.ToUpper(CultureInfo.GetCultureInfo("pt-BR")), string.IsNullOrWhiteSpace(item.IndividualMeasure) ? defaultMeasure : item.IndividualMeasure], item, false);
        }
        sb.Append("</w:tbl>");
    }

    private static void AppendRow(StringBuilder sb, string[] values, MeasuresSelectedItem? item, bool header)
    {
        sb.Append("<w:tr>");
        for (var i = 0; i < values.Length; i++)
        {
            var align = i < 3 ? "center" : "left";
            sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(i switch { 0 => "600", 1 => "1450", 2 => "1150", 3 => "2900", _ => "3750" }).Append("\" w:type=\"dxa\"/><w:tcMar><w:top w:w=\"70\" w:type=\"dxa\"/><w:left w:w=\"85\" w:type=\"dxa\"/><w:bottom w:w=\"70\" w:type=\"dxa\"/><w:right w:w=\"85\" w:type=\"dxa\"/></w:tcMar></w:tcPr><w:p><w:pPr><w:jc w:val=\"").Append(align).Append("\"/><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>");
            if (i == 3 && item is not null) AppendHighlightedName(sb, values[i], item.WarName); else AppendRun(sb, values[i], header, 22);
            sb.Append("</w:p></w:tc>");
        }
        sb.Append("</w:tr>");
    }

    private static void AppendHighlightedName(StringBuilder sb, string name, string warName)
    {
        foreach (var segment in NameHighlightHelper.BuildSegments(name, warName)) AppendRun(sb, segment.Text, segment.IsBold, 22);
    }

    private static void AppendRun(StringBuilder sb, string text, bool bold, int size)
    {
        sb.Append("<w:r><w:rPr><w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\" w:eastAsia=\"Times New Roman\"/><w:sz w:val=\"").Append(size).Append("\"/>");
        if (bold) sb.Append("<w:b/>");
        sb.Append("</w:rPr><w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(text) ?? string.Empty).Append("</w:t></w:r>");
    }

    private static string BuildOdtContent(MeasuresDocumentData data)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" office:version=\"1.2\"><office:automatic-styles><style:style style:name=\"CenterBold\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\"/><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"12pt\" fo:font-weight=\"bold\"/></style:style><style:style style:name=\"Left\" style:family=\"paragraph\"><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"12pt\"/></style:style><style:style style:name=\"LeftBold\" style:family=\"paragraph\"><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"12pt\" fo:font-weight=\"bold\"/></style:style><style:style style:name=\"Signature\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\"/><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"12pt\"/></style:style><style:style style:name=\"BoldText\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style><style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.08cm\" fo:border=\"0.03cm solid #000000\"/></style:style><style:style style:name=\"CellHeader\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.08cm\" fo:border=\"0.03cm solid #000000\" fo:background-color=\"#efefef\"/></style:style></office:automatic-styles><office:body><office:text>");
        foreach (var line in HeaderLines) OdtParagraph(sb, line, "CenterBold");
        OdtParagraph(sb, string.Empty, "Left");
        OdtParagraph(sb, $"Medidas tomadas conforme {CleanOrigin(data.OriginText)}:", "Left");
        OdtParagraph(sb, string.Empty, "Left");
        OdtParagraph(sb, "a. RELATÓRIO DO EXAME DE PAGAMENTO DE PESSOAL", "LeftBold");
        OdtParagraph(sb, string.Empty, "Left");
        AppendOdtSection(sb, 1, "Exame de Pagamento", data.Organization, SectionItems(data, MeasuresSections.PaymentExam), DefaultMeasureFor(data, MeasuresSections.PaymentExam));
        AppendOdtSection(sb, 2, "Exame de Contracheque", data.Organization, SectionItems(data, MeasuresSections.PaystubExam), DefaultMeasureFor(data, MeasuresSections.PaystubExam));
        OdtParagraph(sb, string.Empty, "Left");
        OdtParagraph(sb, data.CommanderName?.Trim() ?? string.Empty, "CenterBold");
        OdtParagraph(sb, MilitaryRankService.ShortName(data.CommanderRank), "Signature");
        OdtParagraph(sb, data.SignatureRole?.Trim() ?? string.Empty, "Signature");
        sb.Append("</office:text></office:body></office:document-content>");
        return sb.ToString();
    }

    private static void AppendOdtSection(StringBuilder sb, int number, string title, string organization, IReadOnlyList<MeasuresSelectedItem> items, string defaultMeasure)
    {
        OdtParagraph(sb, $"{number}) {title} - da {organization.Trim()}:", "Left");
        OdtParagraph(sb, string.Empty, "Left");
        sb.Append("<table:table table:name=\"Medidas").Append(number).Append("\">");
        OdtRow(sb, ["Nº", "Prec-CP", "P/Grad", "Nome", "Medida Tomada"], true, null);
        if (items.Count == 0)
        {
            sb.Append("<table:table-row><table:table-cell table:number-columns-spanned=\"5\" table:style-name=\"Cell\"><text:p text:style-name=\"Left\">—</text:p></table:table-cell></table:table-row>");
        }
        else
        {
            var index = 1;
            foreach (var item in items)
                OdtRow(sb, [index++.ToString(), item.PrecCp, item.Rank, item.Name.ToUpper(CultureInfo.GetCultureInfo("pt-BR")), string.IsNullOrWhiteSpace(item.IndividualMeasure) ? defaultMeasure : item.IndividualMeasure], false, item);
        }
        sb.Append("</table:table>");
        OdtParagraph(sb, string.Empty, "Left");
    }

    private static void OdtParagraph(StringBuilder sb, string value, string style)
        => sb.Append("<text:p text:style-name=\"").Append(style).Append("\">").Append(SecurityElement.Escape(value) ?? string.Empty).Append("</text:p>");

    private static void OdtRow(StringBuilder sb, IReadOnlyList<string> values, bool header, MeasuresSelectedItem? item)
    {
        sb.Append("<table:table-row>");
        for (var i = 0; i < values.Count; i++)
        {
            sb.Append("<table:table-cell office:value-type=\"string\" table:style-name=\"").Append(header ? "CellHeader" : "Cell").Append("\"><text:p text:style-name=\"").Append(header ? "LeftBold" : "Left").Append("\">");
            if (i == 3 && item is not null)
            {
                foreach (var segment in NameHighlightHelper.BuildSegments(values[i], item.WarName))
                {
                    if (segment.IsBold) sb.Append("<text:span text:style-name=\"BoldText\">");
                    sb.Append(SecurityElement.Escape(segment.Text) ?? string.Empty);
                    if (segment.IsBold) sb.Append("</text:span>");
                }
            }
            else sb.Append(SecurityElement.Escape(values[i]) ?? string.Empty);
            sb.Append("</text:p></table:table-cell>");
        }
        sb.Append("</table:table-row>");
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken) { var entry = archive.CreateEntry(name, CompressionLevel.Optimal); await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); await writer.WriteAsync(text.AsMemory(), cancellationToken); }
    private static string? FindLibreOffice() => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"), "soffice.exe" }.FirstOrDefault(x => x.Equals("soffice.exe", StringComparison.OrdinalIgnoreCase) || File.Exists(x));

    private const string WordContentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";
    private const string WordRootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
    private const string OdtManifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>";
    private const string OdtStyles = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" office:version=\"1.2\"><office:styles/></office:document-styles>";
}
