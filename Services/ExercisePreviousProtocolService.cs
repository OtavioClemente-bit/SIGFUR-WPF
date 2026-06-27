using System.IO.Compression;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

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
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
        try
        {
            var text = SimplePdfTextExtractor.ExtractText(path);
            return ExtractProtocolFromText(text);
        }
        catch { return string.Empty; }
    }

    public static string ExtractProtocolFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var patterns = new[]
        {
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

    public async Task<string> BuildBulletinTextAsync(ExercisePreviousProcess process, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(process.CpexProtocol))
            throw new InvalidOperationException("Salve primeiro o número do protocolo CPEx.");

        var totals = new Dictionary<int, (decimal Original, decimal Corrected)>();
        foreach (var entry in process.Entries)
        {
            entry.Factor = await _repository.GetIpcaFactorAsync(entry.Competence, ct);
            var old = totals.GetValueOrDefault(entry.CodeOrder);
            totals[entry.CodeOrder] = (old.Original + entry.Net, old.Corrected + entry.CorrectedNet);
        }

        var lines = new List<string>();
        decimal totalOriginal = 0m, totalCorrected = 0m;
        foreach (var pair in totals.OrderBy(x => x.Key))
        {
            if (Math.Abs(pair.Value.Original) < 0.005m && Math.Abs(pair.Value.Corrected) < 0.005m) continue;
            var raw = process.Codes.FirstOrDefault(x => x.Order == pair.Key)?.Description ?? $"Código {pair.Key}";
            var (code, description) = ExtractCode(raw);
            var label = !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(description) && !code.Equals(description, StringComparison.OrdinalIgnoreCase)
                ? $"{code} - {description}" : First(code, description, raw);
            lines.Add($"{label}: valor original {MoneyBr(pair.Value.Original)}; valor corrigido {MoneyBr(pair.Value.Corrected)}.");
            totalOriginal += pair.Value.Original;
            totalCorrected += pair.Value.Corrected;
        }
        if (lines.Count == 0)
            lines.Add($"Valor original total: {MoneyBr(totalOriginal)}; valor corrigido total: {MoneyBr(totalCorrected)}.");

        var bulletin = (process.BulletinThatRecorded ?? string.Empty)
            .Replace("{{OM_NOME}}", process.OrganizationName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{BI_NUMERO}}", ExercisePreviousRepository.ExtractBulletinNumber(process.BulletinNumber), StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (string.IsNullOrWhiteSpace(bulletin))
        {
            var date = FormatDateBr(process.BulletinDate);
            bulletin = !string.IsNullOrWhiteSpace(process.BulletinNumber) || !string.IsNullOrWhiteSpace(date)
                ? $"BI/ADT Nr {ExercisePreviousRepository.ExtractBulletinNumber(process.BulletinNumber)}, de {date}".Trim(' ', ',', '.')
                : "documento que autorizou o direito";
        }
        var reason = First(process.NonPaymentExplanation, process.RightMaterializationDocument);
        var pdfLine = string.IsNullOrWhiteSpace(process.CpexPrintPage) ? string.Empty : $"\nPDF/página de impressão CPEx: {process.CpexPrintPage}.";
        var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"\nMotivo/justificativa: {reason}";
        return
            "d. EXERCÍCIOS ANTERIORES - Ordem de saque\n\n" +
            "Seja realizado o saque da Despesa de Exercício Anterior em favor do militar abaixo nominado, " +
            $"autorizado de acordo com o {bulletin}, os valores foram atualizados de acordo com o índice IPCA " +
            $"da calculadora do cidadão, até o mês de {FormatCompetence(process.UpdatedThrough)}, conforme dados abaixo:\n\n" +
            $"Protocolo CPEx: {process.CpexProtocol}\n" +
            $"PERÍODO DA DÍVIDA: {FormatShortPeriod(process.PeriodStart, process.PeriodEnd)}\n" +
            string.Join(Environment.NewLine, lines) + pdfLine + reasonLine + "\n\n" +
            $"{AbbreviateRank(process.Rank)} {(process.FullName ?? string.Empty).ToUpperInvariant()}\n" +
            $"Prec-CP {process.PrecCp} CPF {FormatCpf(process.Cpf)}\n\n" +
            "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o direito remuneratório acima especificado.";
    }

    public string SaveBulletinText(ExercisePreviousProcess process, string text)
    {
        var folder = Path.Combine(_paths.ExercisePreviousOutputDirectory, $"EA_{process.Id:0000}_{SafeFilePart(First(process.WarName, process.FullName, "militar"), 50)}");
        Directory.CreateDirectory(folder);
        var path = UniquePath(Path.Combine(folder, $"BOLETIM_EA_CPEX_{SafeFilePart(process.CpexProtocol, 35)}.txt"));
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
