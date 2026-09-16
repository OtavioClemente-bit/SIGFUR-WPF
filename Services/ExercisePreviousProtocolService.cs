using System.IO.Compression;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;
using UglyToad.PdfPig;

namespace SIGFUR.Wpf.Services;

public readonly record struct CpexProtocolPdfData(string Protocol, string ProtocolledAt)
{
    public static CpexProtocolPdfData Empty => new(string.Empty, string.Empty);
}

/// <summary>
/// Registra e arquiva o protocolo gerado manualmente no site do CPEx.
/// A automação nunca envia o processo: esta classe apenas acompanha/copia o PDF,
/// tenta ler o número do protocolo e monta o texto da ordem de saque.
/// </summary>
public sealed class ExercisePreviousProtocolService
{
    private readonly AppPaths _paths;
    private readonly ExercisePreviousRepository _repository;

    public ExercisePreviousProtocolService(AppPaths paths, ExercisePreviousRepository repository)
    {
        _paths = paths;
        _repository = repository;
    }

    public string ProtocolDirectory => _paths.ExercisePreviousProtocolsDirectory;
    public string DownloadDirectory => _paths.ExercisePreviousCpexDownloadsDirectory;

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ProtocolDirectory);
        Directory.CreateDirectory(DownloadDirectory);
    }

    public Dictionary<string, (DateTime LastWrite, long Size)> SnapshotPdfs()
    {
        EnsureDirectories();
        var result = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateDownloadDirectories())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    result[info.FullName] = (info.LastWriteTimeUtc, info.Length);
                }
            }
            catch { }
        }
        return result;
    }

    public async Task<string?> WaitForNextPdfAsync(
        IReadOnlyDictionary<string, (DateTime LastWrite, long Size)> before,
        TimeSpan timeout,
        DateTime? minimumWriteUtc = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = SnapshotPdfs()
                .Where(x => !before.TryGetValue(x.Key, out var old) || old.Size != x.Value.Size || x.Value.LastWrite > old.LastWrite.AddSeconds(1))
                .Where(x => !minimumWriteUtc.HasValue || x.Value.LastWrite >= minimumWriteUtc.Value)
                .OrderByDescending(x => x.Value.LastWrite)
                .Select(x => x.Key)
                .ToList();
            foreach (var path in candidates)
                if (await IsPdfReadyAsync(path, ct)) return path;
            await Task.Delay(800, ct);
        }
        return null;
    }

    public async Task<string?> FindLatestReadyPdfAsync(DateTime? minimumWriteUtc = null, CancellationToken ct = default)
    {
        var files = SnapshotPdfs()
            .Where(x => !minimumWriteUtc.HasValue || x.Value.LastWrite >= minimumWriteUtc.Value)
            .OrderByDescending(x => x.Value.LastWrite)
            .Select(x => x.Key);
        foreach (var path in files)
            if (await IsPdfReadyAsync(path, ct)) return path;
        return null;
    }

    public async Task<bool> IsPdfReadyAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var first = new FileInfo(path).Length;
            if (first <= 4) return false;
            await Task.Delay(600, ct);
            var second = new FileInfo(path).Length;
            if (first != second) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[5];
            return await stream.ReadAsync(header.AsMemory(0, 5), ct) == 5 && Encoding.ASCII.GetString(header) == "%PDF-";
        }
        catch { return false; }
    }

    public string ArchivePdf(string sourcePdf, ExercisePreviousProcess process, string? protocol = null)
    {
        EnsureDirectories();
        if (!File.Exists(sourcePdf)) throw new FileNotFoundException("PDF do CPEx não encontrado.", sourcePdf);
        protocol = First(protocol, ExtractProtocolFromPdf(sourcePdf), "sem_protocolo");
        var name = First(process.FullName, process.WarName, "militar");
        var fileName = $"CPEX_{SafeFilePart(protocol, 30)}_{SafeFilePart(name, 60)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
        var destination = UniquePath(Path.Combine(ProtocolDirectory, fileName));
        if (Path.GetFullPath(sourcePdf).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return destination;
        File.Copy(sourcePdf, destination, false);
        return destination;
    }

    public string ExtractProtocolFromPdf(string path)
        => ExtractProtocolDataFromPdf(path).Protocol;

    /// <summary>
    /// Lê o comprovante baixado do CPEx. O número e a data pertencem ao mesmo
    /// comprovante e devem ser gravados juntos para não ficar a data do dia da
    /// operação no lugar da data real do protocolo.
    /// </summary>
    public CpexProtocolPdfData ExtractProtocolDataFromPdf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return CpexProtocolPdfData.Empty;
        try
        {
            var text = ExtractTextWithPdfPig(path);
            var protocol = ExtractProtocolFromText(text);
            var date = ExtractProtocolDateFromText(text);

            // Alguns PDFs antigos usam fontes que o PdfPig não consegue mapear.
            // Mantém a leitura simples como plano B, sem perder o comportamento anterior.
            if (string.IsNullOrWhiteSpace(protocol) || string.IsNullOrWhiteSpace(date))
            {
                var fallbackText = SimplePdfTextExtractor.ExtractText(path);
                protocol = First(protocol, ExtractProtocolFromText(fallbackText));
                date = First(date, ExtractProtocolDateFromText(fallbackText));
            }

            return new CpexProtocolPdfData(protocol, date);
        }
        catch { return CpexProtocolPdfData.Empty; }
    }

    public static string ExtractProtocolFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var patterns = new[]
        {
            // O PdfPig pode unir o final do número ao título seguinte (ex.: 521809DADOS).
            // No comprovante do CPEx o protocolo de envio é numérico, então lê o bloco de
            // dígitos antes de aplicar os formatos mais genéricos abaixo.
            @"PROTOCOLO\s+DE\s+ENVIO\s*[:\-]?\s*(\d{4,})",
            @"PROTOCOLO\s+DE\s+ENVIO\s*[:\-]?\s*([A-Z0-9./\-]{4,})",
            @"N[ºO°.]?\s*DO\s*PROTOCOLO\s*[:\-]?\s*([A-Z0-9./\-]{4,})",
            @"PROTOCOLO\s*CPEX\s*[:\-]?\s*([A-Z0-9./\-]{4,})",
            @"\bPROTOCOLO\b\D{0,35}([A-Z0-9./\-]{4,})"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            return Regex.Replace(match.Groups[1].Value.Trim().ToUpperInvariant(), @"[^A-Z0-9./\-]+", string.Empty);
        }
        return string.Empty;
    }

    public static string ExtractProtocolDateFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Ex.: "Processo Recebido em quarta-feira, 15 de julho de 2026".
        // Esta é a data efetiva do protocolo no comprovante do CPEx.
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["janeiro"] = 1, ["fevereiro"] = 2, ["março"] = 3, ["marco"] = 3,
            ["abril"] = 4, ["maio"] = 5, ["junho"] = 6, ["julho"] = 7,
            ["agosto"] = 8, ["setembro"] = 9, ["outubro"] = 10,
            ["novembro"] = 11, ["dezembro"] = 12
        };
        var match = Regex.Match(
            text,
            @"(?:PROCESSO\s+)?RECEBIDO\s+EM\s+(?:(?:SEGUNDA|TER[CÇ]A|QUARTA|QUINTA|SEXTA)[\s-]*FEIRA\s*,?\s*)?(?<day>\d{1,2})\s+DE\s+(?<month>[A-ZÇÃÕÁÉÍÓÚÂÊÔ]+)\s+DE\s+(?<year>\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !months.TryGetValue(match.Groups["month"].Value, out var month)) return string.Empty;
        if (!int.TryParse(match.Groups["day"].Value, out var day) || !int.TryParse(match.Groups["year"].Value, out var year)) return string.Empty;
        try { return new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return string.Empty; }
    }

    private static string ExtractTextWithPdfPig(string path)
    {
        using var document = PdfDocument.Open(path);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    public async Task<bool> HasPayableRubricsAsync(ExercisePreviousProcess process, CancellationToken ct = default)
        => (await BuildPayableRubricsAsync(process, ct)).Count > 0;

    public async Task<string> BuildBulletinTextAsync(ExercisePreviousProcess process, CancellationToken ct = default)
        => await BuildBulletinBlockAsync(process, includeTitle: true, ordinal: null, ct);

    public async Task<string> BuildBulletinTextAsync(IEnumerable<ExercisePreviousProcess> processes, CancellationToken ct = default)
    {
        var list = processes.Where(x => x is not null && !x.Paid).ToList();
        if (list.Count == 0)
            throw new InvalidOperationException("Nenhum processo em andamento foi selecionado para o boletim.");

        var blocks = new List<string>();
        foreach (var process in list)
            blocks.Add(await BuildBulletinBlockAsync(process, includeTitle: false, ordinal: list.Count > 1 ? blocks.Count + 1 : null, ct));

        if (blocks.Count > 0)
            return BuildProfessionalBulletinHeader() + "\n\n" + string.Join("\n\n", blocks);

        return "EXERCÍCIO ANTERIOR - Ordem de Saque:\n\n" + string.Join("\n\n", blocks);
    }

    private async Task<string> BuildBulletinBlockAsync(ExercisePreviousProcess process, bool includeTitle, int? ordinal, CancellationToken ct)
    {
        if (process.Paid)
            throw new InvalidOperationException($"O processo {process.Id:0000} esta marcado como pago e nao entra no boletim.");
        if (string.IsNullOrWhiteSpace(process.CpexProtocol))
            throw new InvalidOperationException("Salve primeiro o numero do protocolo CPEx.");

        var rubrics = await BuildPayableRubricsAsync(process, ct);
        if (rubrics.Count == 0)
            throw new InvalidOperationException($"O processo {process.Id:0000} nao possui rubrica com valor liquido a receber.");

        var professionalText = BuildProfessionalBulletinBlock(process, rubrics, includeTitle, ordinal);
        if (!string.IsNullOrWhiteSpace(professionalText)) return professionalText;

        var text = new StringBuilder();
        if (includeTitle) text.AppendLine("EXERCÍCIO ANTERIOR - Ordem de Saque:").AppendLine();
        if (ordinal.HasValue) text.Append(ordinal.Value.ToString(CultureInfo.InvariantCulture)).Append(". ");
        text.Append("Seja realizado o saque de Exercício Anterior em favor do militar abaixo nominado, ");
        text.Append($"autorizado conforme {BuildBulletinReference(process)}, com valores atualizados pelo IPCA-E ");
        text.Append($"até {FormatCompetence(process.UpdatedThrough)}, conforme dados abaixo:");
        text.AppendLine().AppendLine();

        text.AppendLine($"Militar: {BuildMilitaryName(process)}");
        text.AppendLine($"CPF: {FormatCpf(process.Cpf)}");
        text.AppendLine($"Prec-CP: {process.PrecCp}");
        text.AppendLine($"Protocolo CPEx: {process.CpexProtocol.Trim()}");
        text.AppendLine($"Período da dívida: {FormatShortPeriod(process.PeriodStart, process.PeriodEnd)}");

        var materialization = NormalizeParagraph(process.RightMaterializationDocument);
        if (!string.IsNullOrWhiteSpace(materialization))
            text.AppendLine($"Documento que materializou o direito: {materialization}");

        var reason = NormalizeParagraph(process.NonPaymentExplanation);
        if (!string.IsNullOrWhiteSpace(reason))
            text.AppendLine($"Justificativa do não pagamento à época: {reason}");

        text.AppendLine();
        text.AppendLine("Rubricas a implantar:");
        foreach (var rubric in rubrics)
        {
            text.AppendLine($"- {rubric.Label}");
            text.AppendLine($"  Valor original: {MoneyBr(rubric.Original)} | Valor corrigido: {MoneyBr(rubric.Corrected)}");
        }

        text.AppendLine();
        text.Append("Em consequência, solicito ao Ch SSPP/Cmdo 4ª RM o processamento do direito remuneratório acima especificado.");
        return text.ToString().TrimEnd();
    }

    private static string BuildProfessionalBulletinBlock(
        ExercisePreviousProcess process,
        IReadOnlyList<BulletinRubricLine> rubrics,
        bool includeTitle,
        int? ordinal)
    {
        var totalOriginal = rubrics.Sum(x => x.IsExpense ? -x.Original : x.Original);
        var totalCorrected = rubrics.Sum(x => x.IsExpense ? -x.Corrected : x.Corrected);
        var type = RequiredText(First(process.PreviousExerciseType, process.DebtType, process.RefersTo), "[CONFIRMAR tipo/código do exercício anterior]");
        var materialization = RequiredText(process.RightMaterializationDocument, "[CONFIRMAR documento que materializou o direito]");
        var nonPayment = RequiredText(process.NonPaymentExplanation, "[CONFIRMAR justificativa do não pagamento na competência própria]");
        var protocol = RequiredText(process.CpexProtocol, "[CONFIRMAR protocolo CPEx/SIPPES]");
        var protocolDate = RequiredText(FormatDateBr(process.CpexProtocolledAt), "[CONFIRMAR data do protocolo]");
        var rank = RequiredText(process.Rank, "[CONFIRMAR posto/graduação]");
        var warName = RequiredText(process.WarName, "[CONFIRMAR nome de guerra]").ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        var fullName = RequiredText(process.FullName, "[CONFIRMAR nome completo]").ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        var precCp = RequiredText(process.PrecCp, "[CONFIRMAR Prec-CP]");
        var cpf = RequiredText(FormatCpf(process.Cpf), "[CONFIRMAR CPF]");
        var period = RequiredText(FormatShortPeriod(process.PeriodStart, process.PeriodEnd), "[CONFIRMAR período da dívida]");
        var rubricLabel = rubrics.Count == 1 ? "Rubrica e valor a receber" : "Rubricas e valores a receber";
        var rubricsText = string.Join("; ", rubrics.Select(x => $"{x.Label}: {MoneyBr(x.Corrected)}"));

        var text = new StringBuilder();
        if (includeTitle) text.AppendLine(BuildProfessionalBulletinHeader()).AppendLine();
        if (ordinal.HasValue) text.AppendLine($"{ordinal.Value.ToString(CultureInfo.InvariantCulture)}. {rank} {fullName}");
        else text.AppendLine($"{rank} {fullName}");
        text.AppendLine();

        text.AppendLine($"Posto/Graduação: {rank}");
        text.AppendLine($"Nome de guerra: {warName}");
        text.AppendLine($"Nome completo: {fullName}");
        text.AppendLine($"Prec-CP: {precCp}");
        text.AppendLine($"CPF: {cpf}");
        text.AppendLine();
        text.AppendLine($"Documento que materializou o direito: {materialization}.");
        text.AppendLine($"Justificativa do não pagamento na época própria: {nonPayment}.");
        text.AppendLine();
        text.AppendLine($"Protocolo CPEx/SIPPES: {protocol}");
        text.AppendLine($"Data do protocolo: {protocolDate}");
        text.AppendLine($"Tipo/código do exercício anterior: {type}");
        text.AppendLine($"Período da dívida: {period}");
        text.AppendLine($"{rubricLabel}: {rubricsText}");
        text.AppendLine();
        text.AppendLine($"Valor bruto devido: {MoneyWithWords(totalOriginal)}");
        text.Append($"Valor corrigido/total solicitado: {MoneyWithWords(totalCorrected)}");
        return text.ToString().TrimEnd();
    }

    private static string BuildProfessionalBulletinHeader()
        => "EXERCÍCIO ANTERIOR - Ordem de Saque:\n\nSeja realizado o saque da Despesa de Exercício Anterior em favor do(s) militar(es) abaixo nominado(s), conforme dados extraídos do Formulário de Exercícios Anteriores e necessários ao lançamento no SIPPES.";

    private async Task<List<BulletinRubricLine>> BuildPayableRubricsAsync(ExercisePreviousProcess process, CancellationToken ct)
    {
        var totals = new Dictionary<int, (decimal Original, decimal Corrected)>();
        foreach (var entry in process.Entries)
        {
            entry.Factor = await _repository.GetIpcaFactorAsync(entry.Competence, ct);
            var old = totals.GetValueOrDefault(entry.CodeOrder);
            totals[entry.CodeOrder] = (old.Original + entry.Net, old.Corrected + entry.CorrectedNet);
        }

        var lines = new List<BulletinRubricLine>();
        foreach (var pair in totals.OrderBy(x => x.Key))
        {
            if (Math.Abs(pair.Value.Original) <= 0.005m && Math.Abs(pair.Value.Corrected) <= 0.005m) continue;
            var codeDefinition = process.Codes.FirstOrDefault(x => x.Order == pair.Key);
            var raw = codeDefinition?.Description ?? $"Código {pair.Key}";
            var (code, description) = ExtractCode(raw);
            var label = !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(description) && !code.Equals(description, StringComparison.OrdinalIgnoreCase)
                ? $"{code} - {description}"
                : First(code, description, raw);
            var isExpense = string.Equals(codeDefinition?.Type, "Despesa", StringComparison.OrdinalIgnoreCase);
            lines.Add(new BulletinRubricLine(label, pair.Value.Original, pair.Value.Corrected, isExpense));
        }
        return lines;
    }

    private static string BuildBulletinReference(ExercisePreviousProcess process)
    {
        var bulletin = (process.BulletinThatRecorded ?? string.Empty)
            .Replace("{{OM_NOME}}", process.OrganizationName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{BI_NUMERO}}", ExercisePreviousRepository.ExtractBulletinNumber(process.BulletinNumber), StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (!string.IsNullOrWhiteSpace(bulletin)) return bulletin;

        var date = FormatDateBr(process.BulletinDate);
        return !string.IsNullOrWhiteSpace(process.BulletinNumber) || !string.IsNullOrWhiteSpace(date)
            ? $"BI/ADT Nr {ExercisePreviousRepository.ExtractBulletinNumber(process.BulletinNumber)}, de {date}".Trim(' ', ',', '.')
            : "documento que autorizou o direito";
    }

    private static string BuildMilitaryName(ExercisePreviousProcess process)
    {
        var name = string.Join(' ', new[] { AbbreviateRank(process.Rank), process.FullName?.ToUpper(CultureInfo.GetCultureInfo("pt-BR")) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(name) ? "militar nao informado" : name;
    }

    private static string RequiredText(string? value, string fallback)
    {
        var normalized = NormalizeParagraph(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized.TrimEnd('.');
    }

    private static string NormalizeParagraph(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private readonly record struct BulletinRubricLine(string Label, decimal Original, decimal Corrected, bool IsExpense);

    public string SaveBulletinText(ExercisePreviousProcess process, string text)
    {
        var folder = Path.Combine(_paths.ExercisePreviousOutputDirectory, $"EA_{process.Id:0000}_{SafeFilePart(First(process.WarName, process.FullName, "militar"), 50)}");
        Directory.CreateDirectory(folder);
        var path = UniquePath(Path.Combine(folder, $"BOLETIM_EA_CPEX_{SafeFilePart(process.CpexProtocol, 35)}.txt"));
        File.WriteAllText(path, text ?? string.Empty, new UTF8Encoding(false));
        return path;
    }

    public string SaveBulletinText(IEnumerable<ExercisePreviousProcess> processes, string text)
    {
        var list = processes.Where(x => x is not null).ToList();
        if (list.Count == 1) return SaveBulletinText(list[0], text);

        var folder = Path.Combine(_paths.ExercisePreviousOutputDirectory, "EA_BOLETINS_CPEX");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = UniquePath(Path.Combine(folder, $"BOLETIM_EA_CPEX_LOTE_{stamp}.txt"));
        File.WriteAllText(path, text ?? string.Empty, new UTF8Encoding(false));
        return path;
    }

    private IEnumerable<string> CandidateDownloadDirectories()
    {
        EnsureDirectories();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var candidates = new[]
        {
            DownloadDirectory,
            Path.Combine(home, "Downloads"), Path.Combine(home, "Download"),
            Path.Combine(home, "OneDrive", "Downloads"), desktop
        };
        return candidates.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var directory = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SafeFilePart(string? value, int max)
    {
        var normalized = RemoveAccents(value ?? string.Empty);
        normalized = Regex.Replace(normalized, @"[^A-Za-z0-9._ \-]+", "_");
        normalized = Regex.Replace(normalized, @"\s+", "_").Trim('.', '_', '-', ' ');
        if (normalized.Length > max) normalized = normalized[..max];
        return string.IsNullOrWhiteSpace(normalized) ? "arquivo" : normalized;
    }

    private static (string Code, string Description) ExtractCode(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, string.Empty);
        var match = Regex.Match(text, @"^([A-Za-z]{1,4}\s*\d{0,5})\s*[-–—:]?\s*(.*)$");
        if (!match.Success) return (string.Empty, text);
        var code = Regex.Replace(match.Groups[1].Value.ToUpperInvariant(), @"\s+", string.Empty);
        var description = match.Groups[2].Value.Trim();
        if (string.IsNullOrWhiteSpace(description) && !code.Any(char.IsDigit)) return (string.Empty, text);
        return (code, string.IsNullOrWhiteSpace(description) ? text : description);
    }

    private static string First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
    private static string RemoveAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }
    private static string MoneyBr(decimal value) => "R$ " + value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
    private static string MoneyWithWords(decimal value) => $"{MoneyBr(value)} ({NumberToWordsService.Convert(value, true)})";
    private static string FormatCpf(string value)
    {
        var d = ExercisePreviousRepository.Digits(value);
        return d.Length == 11 ? $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}" : value ?? string.Empty;
    }
    private static string FormatDateBr(string value) => TryDate(value, out var date) ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : value ?? string.Empty;
    private static string FormatShortPeriod(string start, string end)
    {
        if (!TryDate(start, out var a) || !TryDate(end, out var b)) return string.Join(" a ", new[] { start?.Trim(), end?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (b < a) (a, b) = (b, a);
        return $"{a:dd} {ExercisePreviousDefaults.Months[a.Month - 1]} {a.Year % 100:00} a {b:dd} {ExercisePreviousDefaults.Months[b.Month - 1]} {b.Year % 100:00}";
    }

    private static string FormatLongPeriod(string start, string end)
    {
        if (!TryDate(start, out var a) || !TryDate(end, out var b))
            return string.Join(" a ", new[] { start?.Trim(), end?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (b < a) (a, b) = (b, a);
        return $"{a:dd} {ExercisePreviousDefaults.Months[a.Month - 1]} {a.Year % 100:00} a {b:dd} {ExercisePreviousDefaults.Months[b.Month - 1]} {b.Year % 100:00}";
    }

    private static string FormatCompetence(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"^(\d{4})-(\d{1,2})$");
        if (!match.Success) return (value ?? string.Empty).ToUpperInvariant();
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 ? $"{ExercisePreviousDefaults.Months[month - 1]} {year % 100:00}" : (value ?? string.Empty).ToUpperInvariant();
    }
    private static string AbbreviateRank(string rank)
        => MilitaryRankService.ShortName(rank);

    private static bool TryDate(string value, out DateTime result)
        => DateTime.TryParseExact(value?.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy" }, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out result);

    private static class SimplePdfTextExtractor
    {
        public static string ExtractText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var parts = new List<string> { Encoding.Latin1.GetString(bytes) };
            var raw = Encoding.Latin1.GetString(bytes);
            foreach (Match match in Regex.Matches(raw, @"stream\r?\n", RegexOptions.CultureInvariant))
            {
                var end = raw.IndexOf("endstream", match.Index + match.Length, StringComparison.Ordinal);
                if (end < 0) continue;
                var start = match.Index + match.Length;
                var length = end - start;
                if (length <= 0 || length > 20_000_000) continue;
                var prefixStart = Math.Max(0, match.Index - 500);
                var prefix = raw.Substring(prefixStart, match.Index - prefixStart);
                var chunk = bytes.AsSpan(start, length).ToArray();
                if (prefix.Contains("/FlateDecode", StringComparison.Ordinal))
                {
                    try
                    {
                        using var input = new MemoryStream(chunk);
                        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        zlib.CopyTo(output);
                        chunk = output.ToArray();
                    }
                    catch { continue; }
                }
                parts.Add(ExtractOperators(Encoding.Latin1.GetString(chunk)));
            }
            return string.Join("\n", parts);
        }

        private static string ExtractOperators(string content)
        {
            var result = new StringBuilder();
            foreach (Match match in Regex.Matches(content, @"\((?<v>(?:\\.|[^\\)])*)\)\s*Tj", RegexOptions.Singleline))
                result.Append(' ').Append(Unescape(match.Groups["v"].Value));
            foreach (Match array in Regex.Matches(content, @"\[(?<v>.*?)\]\s*TJ", RegexOptions.Singleline))
                foreach (Match item in Regex.Matches(array.Groups["v"].Value, @"\((?<v>(?:\\.|[^\\)])*)\)", RegexOptions.Singleline))
                    result.Append(' ').Append(Unescape(item.Groups["v"].Value));
            return result.ToString();
        }

        private static string Unescape(string value)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
                var next = value[++i];
                if (next is 'n' or 'r' or 't' or 'b' or 'f') sb.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', _ => '\f' });
                else if (next is '(' or ')' or '\\') sb.Append(next);
                else if (next is >= '0' and <= '7')
                {
                    var octal = new StringBuilder().Append(next);
                    for (var j = 0; j < 2 && i + 1 < value.Length && value[i + 1] is >= '0' and <= '7'; j++) octal.Append(value[++i]);
                    sb.Append((char)Convert.ToInt32(octal.ToString(), 8));
                }
                else sb.Append(next);
            }
            return sb.ToString();
        }
    }
}
