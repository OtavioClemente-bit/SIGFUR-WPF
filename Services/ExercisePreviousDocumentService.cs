using System.Xml.Linq;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed record ExercisePreviousBulletinRequestDraft(string Text, IReadOnlyList<string> MissingFields);

public sealed class ExercisePreviousDocumentService
{
    private readonly ExercisePreviousAssetsService _assets;
    public ExercisePreviousDocumentService(ExercisePreviousAssetsService assets) => _assets = assets;

    public string GenerateCover(ExercisePreviousProcess p)
    {
        _assets.EnsureInstalled();
        var output = Path.Combine(_assets.GetProcessFolder(p), $"CAPA_EA_{p.Id:0000}_{ExercisePreviousAssetsService.SafeName(p.WarName)}.docx");
        var split = SplitName(p.FullName, p.WarName);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SECAO"] = p.Section, ["PROTOCOLO_GERAL"] = p.GeneralProtocol,
            ["ASSUNTO_NUM"] = p.SubjectNumber, ["ASSUNTO_TEXTO"] = p.SubjectText,
            ["ANEXOS_FOLHAS"] = p.AttachmentSheets, ["NUM_PROCESSO"] = p.ProcessNumber,
            ["ANO_DOC"] = (p.ProcessYear ?? DateTime.Today.Year).ToString(CultureInfo.InvariantCulture),
            ["POSTO_GRAD"] = p.Rank, ["CPF"] = p.Cpf, ["PREC_CP"] = p.PrecCp,
            ["PERIODO_CAPA"] = FormatCoverPeriod(p.PeriodStart, p.PeriodEnd),
            ["NOME_ANTES_GUERRA"] = split.Before, ["NOME_GUERRA"] = split.War, ["NOME_DEPOIS_GUERRA"] = split.After,
            ["NOME_COMPLETO"] = p.FullName
        };
        ReplaceDocx(_assets.CoverTemplate, output, map);
        return output;
    }

    public string GenerateRequest(ExercisePreviousProcess p)
    {
        _assets.EnsureInstalled();
        var output = Path.Combine(_assets.GetProcessFolder(p), $"REQUERIMENTO_EA_{p.Id:0000}_{ExercisePreviousAssetsService.SafeName(p.WarName)}.docx");
        var split = SplitName(p.FullName, p.WarName);
        var bulletinDateInWords = DateInWords(p.BulletinDate);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["POSTO_GRAD"] = p.Rank, ["NOME_COMPLETO"] = p.FullName,
            ["NOME_ANTES_GUERRA"] = split.Before, ["NOME_GUERRA"] = split.War, ["NOME_DEPOIS_GUERRA"] = split.After,
            ["PREC_CP"] = p.PrecCp, ["CPF"] = p.Cpf, ["IDENTIDADE_EB"] = p.Identity, ["IDENTIDADE_EB_FMT"] = FormatIdentity(p.Identity),
            ["OM_NOME"] = p.OrganizationName, ["DESTINATARIO"] = p.Recipient, ["OBJETO"] = p.Object, ["TELEFONE"] = p.Phone,
            ["PERIODO_REQUERIMENTO"] = FormatRequestPeriod(p.PeriodStart, p.PeriodEnd),
            ["PERIODO_CAPA"] = FormatCoverPeriod(p.PeriodStart, p.PeriodEnd), ["CIDADE_ESTADO"] = p.CityState,
            ["DATA_REQUERIMENTO_EXTENSO"] = p.RequestDateInWords, ["DOC_MATERIALIZOU"] = p.RightMaterializationDocument,
            ["BOLETIM_AVERBOU"] = ExpandBulletin(p.BulletinThatRecorded, p), ["MOTIVO_PAGAMENTO"] = p.PaymentReason,
            ["BANCO"] = p.Bank, ["AGENCIA"] = p.Agency, ["CONTA"] = p.Account,
            ["EB_REQUERIMENTO"] = p.EbRequest, ["EB_INFO"] = p.EbInformation, ["REFERENTE_A"] = p.RefersTo,
            ["VALOR_REQUERIDO"] = p.RequestedValue, ["OD_EPOCA_NOME"] = p.FormerOdName,
            ["OD_EPOCA_IDT"] = p.FormerOdIdentity, ["OD_EPOCA_IDT_FMT"] = FormatIdentity(p.FormerOdIdentity),
            ["OD_EPOCA_CPF"] = p.FormerOdCpf, ["CMT_COMPANHIA"] = p.CompanyCommander,
            ["NUM_PROCESSO"] = p.ProcessNumber, ["SECAO"] = p.Section, ["OD_NOME_POSTO"] = p.OdNameRank, ["OD_FUNCAO"] = p.OdFunction,
            ["DATA_NASCIMENTO"] = p.BirthDate, ["BI_NUM"] = ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber),
            ["BI_NUMERO"] = ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber), ["BI_DATA"] = bulletinDateInWords,
            ["BI_DATA_RAW"] = p.BulletinDate, ["BI_DATA_EXTENSO"] = bulletinDateInWords, ["SITUACAO"] = p.Situation,
            ["IDT_EB_RAW"] = p.Identity, ["IDT_EB_FMT"] = FormatIdentity(p.Identity),
            ["OD_A_EPOCA_NOME"] = p.FormerOdName, ["OD_A_EPOCA_IDT"] = p.FormerOdIdentity,
            ["OD_A_EPOCA_IDT_FMT"] = FormatIdentity(p.FormerOdIdentity), ["OD_A_EPOCA_CPF"] = p.FormerOdCpf,
            ["NOME_OD_EPOCA"] = p.FormerOdName, ["IDT_OD_EPOCA"] = p.FormerOdIdentity,
            ["IDT_OD_EPOCA_FMT"] = FormatIdentity(p.FormerOdIdentity), ["CPF_OD_EPOCA"] = p.FormerOdCpf,
            ["NUMERO_PROCESSO"] = p.ProcessNumber
        };
        ReplaceDocx(_assets.RequestTemplate, output, map);
        return output;
    }

    /// <summary>
    /// Monta a publicação que registra a entrada do requerimento. O texto não concede
    /// o direito nem autoriza pagamento: apenas documenta o recebimento e o fluxo de
    /// análise com os dados que já constam do processo EA.
    /// </summary>
    public static ExercisePreviousBulletinRequestDraft BuildBulletinRequestDraft(
        ExercisePreviousProcess p,
        ExercisePreviousSummary summary)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(summary);

        var missing = new List<string>();
        var entryDate = TryDate(p.RequestDate, out var requestDate)
            ? DateInWords(requestDate)
            : CleanParagraph(p.RequestDateInWords);
        if (string.IsNullOrWhiteSpace(entryDate))
        {
            entryDate = "[CONFIRMAR DATA DE ENTRADA]";
            missing.Add("data de entrada do requerimento");
        }

        var fullName = CleanParagraph(p.FullName).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        if (string.IsNullOrWhiteSpace(fullName)) { fullName = "[CONFIRMAR NOME COMPLETO]"; missing.Add("nome completo"); }

        var subject = FirstText(p.SubjectText, p.Object, p.RefersTo, p.DebtType, p.PreviousExerciseType);
        if (string.IsNullOrWhiteSpace(subject))
        {
            subject = "[CONFIRMAR OBJETO DO EXERCÍCIO ANTERIOR]";
            missing.Add("objeto/natureza da dívida");
        }

        var requestedValue = CleanParagraph(p.RequestedValue);
        if (string.IsNullOrWhiteSpace(requestedValue) && Math.Abs(summary.CorrectedNet) > 0.005m)
            requestedValue = MoneyWithWords(summary.CorrectedNet);
        if (string.IsNullOrWhiteSpace(requestedValue))
        {
            requestedValue = "[CONFIRMAR VALOR]";
            missing.Add("valor requerido/apurado");
        }

        var period = FormatRequestPeriod(p.PeriodStart, p.PeriodEnd);

        var text = new StringBuilder();
        text.AppendLine("EXERCÍCIO ANTERIOR - Entrada de requerimento:").AppendLine();
        text.AppendLine($"Em {entryDate}, deu entrada nesta Organização Militar o requerimento apresentado por {fullName}, por meio do qual solicita o reconhecimento e o pagamento de despesa de exercício anterior referente a {subject}.");
        if (!string.IsNullOrWhiteSpace(period))
            text.AppendLine($"O pedido abrange o período de {period} e apresenta o valor total de {requestedValue.TrimEnd('.')}.");
        else
            text.AppendLine($"O pedido apresenta o valor total de {requestedValue.TrimEnd('.')}.");
        text.AppendLine();
        text.AppendLine("A presente publicação registra a entrada do requerimento e o início de sua análise administrativa, ficando o reconhecimento do direito e o pagamento sujeitos à decisão da autoridade competente.");
        text.AppendLine();
        text.AppendLine("Em consequência:");
        text.AppendLine("a. adote o setor responsável as medidas necessárias ao registro, à análise e à instrução do requerimento; e");
        text.Append("b. concluída a análise, sejam os autos submetidos à autoridade competente para decisão e demais providências cabíveis.");

        return new ExercisePreviousBulletinRequestDraft(
            text.ToString().TrimEnd(),
            missing.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    public string SaveBulletinRequestText(ExercisePreviousProcess p, string text)
    {
        var folder = _assets.GetProcessFolder(p);
        var file = $"REQUERIMENTO_BI_EA_{Math.Max(0, p.Id):0000}_{ExercisePreviousAssetsService.SafeName(FirstText(p.WarName, p.FullName, "MILITAR"))}.txt";
        var output = UniquePath(Path.Combine(folder, file));
        File.WriteAllText(output, text ?? string.Empty, new UTF8Encoding(false));
        return output;
    }

    public string ArchiveBankOrder(ExercisePreviousProcess p, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Id <= 0) throw new InvalidOperationException("Salve o processo antes de anexar a Ordem Bancária.");
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Arquivo da Ordem Bancária não encontrado.", sourcePath);

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var allowed = new HashSet<string>([".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".doc", ".docx"], StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(extension))
            throw new InvalidOperationException("A Ordem Bancária deve estar em PDF, imagem ou documento Word.");

        var source = Path.GetFullPath(sourcePath);
        if (!string.IsNullOrWhiteSpace(p.BankOrderPath) && File.Exists(p.BankOrderPath) &&
            source.Equals(Path.GetFullPath(p.BankOrderPath), StringComparison.OrdinalIgnoreCase)) return source;

        var folder = Path.Combine(_assets.GetProcessFolder(p), "Documentos");
        Directory.CreateDirectory(folder);
        var name = ExercisePreviousAssetsService.SafeName(FirstText(p.WarName, p.FullName, "MILITAR"));
        var destination = UniquePath(Path.Combine(folder, $"ORDEM_BANCARIA_EA_{p.Id:0000}_{name}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}"));
        File.Copy(source, destination, false);
        return destination;
    }

    private static void ReplaceDocx(string template, string output, IReadOnlyDictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var sourceStream = File.OpenRead(template);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read, false);
        using var targetStream = File.Create(output);
        using var target = new ZipArchive(targetStream, ZipArchiveMode.Create, false);
        foreach (var item in source.Entries)
        {
            var dest = target.CreateEntry(item.FullName, CompressionLevel.Optimal);
            dest.LastWriteTime = item.LastWriteTime;
            using var input = item.Open();
            using var outputStream = dest.Open();
            if (!item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) { input.CopyTo(outputStream); continue; }
            using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, true);
            var xml = reader.ReadToEnd();
            foreach (var pair in map) xml = xml.Replace("{{" + pair.Key + "}}", EscapeXml(pair.Value), StringComparison.OrdinalIgnoreCase);
            // Corrige placeholders partidos em vários runs do Word sem alterar parágrafos comuns.
            try
            {
                var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                foreach (var paragraph in document.Descendants(w + "p"))
                {
                    var nodes = paragraph.Descendants(w + "t").ToList();
                    if (nodes.Count == 0) continue;
                    var combined = string.Concat(nodes.Select(x => x.Value));
                    var changed = combined;
                    foreach (var pair in map) changed = changed.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                    if (changed == combined) continue;
                    nodes[0].Value = changed;
                    for (var i = 1; i < nodes.Count; i++) nodes[i].Value = string.Empty;
                }
                xml = document.ToString(SaveOptions.DisableFormatting);
            }
            catch { /* a substituição direta acima continua válida */ }
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false));
            writer.Write(xml);
        }
    }

    private static string EscapeXml(string? value)
        => System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private static (string Before, string War, string After) SplitName(string fullName, string warName)
    {
        var full = (fullName ?? string.Empty).Trim(); var war = (warName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(full)) return (string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(war)) return (string.Empty, full, string.Empty);
        var normalizedFull = RemoveAccents(full).ToUpperInvariant(); var normalizedWar = RemoveAccents(war).ToUpperInvariant();
        var index = normalizedFull.IndexOf(normalizedWar, StringComparison.Ordinal);
        return index < 0 ? (string.Empty, full, string.Empty)
            : (full[..index].TrimEnd(), full.Substring(index, Math.Min(war.Length, full.Length - index)).Trim(), full[(index + Math.Min(war.Length, full.Length - index))..].TrimStart());
    }

    private static string RemoveAccents(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }
    private static string FormatIdentity(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Contains('-')) return normalized;
        var digits = ExercisePreviousRepository.Digits(normalized);
        return digits.Length == 10 ? digits[..9] + "-" + digits[9..] : digits;
    }
    private static string ExpandBulletin(string value, ExercisePreviousProcess p)
        => (value ?? string.Empty).Replace("{{OM_NOME}}", p.OrganizationName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{BI_NUMERO}}", ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber), StringComparison.OrdinalIgnoreCase);

    private static readonly string[] MonthsLong = ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];
    private static readonly string[] MonthsShort = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];
    private static string DateInWords(string value) => TryDate(value, out var d) ? $"{d.Day:00} de {MonthsLong[d.Month - 1]} de {d.Year}" : value ?? string.Empty;
    private static string DateInWords(DateTime value) => $"{value.Day:00} de {MonthsLong[value.Month - 1]} de {value.Year}";
    private static string FormatCoverPeriod(string start, string end)
    {
        if (!TryDate(start, out var a) || !TryDate(end, out var b)) return string.Empty; if (b < a) (a, b) = (b, a);
        return $"{a.Day:00} {MonthsShort[a.Month - 1]} {a.Year % 100:00} a {b.Day:00} {MonthsShort[b.Month - 1]} {b.Year % 100:00}";
    }
    private static string FormatRequestPeriod(string start, string end)
    {
        if (!TryDate(start, out var a) || !TryDate(end, out var b)) return string.Empty; if (b < a) (a, b) = (b, a);
        if (a.Year == b.Year && a.Month == b.Month) return $"{a.Day} a {b.Day} de {MonthsLong[b.Month - 1]} de {b.Year}";
        if (a.Year == b.Year) return $"{a.Day} de {MonthsLong[a.Month - 1]} a {b.Day} de {MonthsLong[b.Month - 1]} de {b.Year}";
        return $"{a.Day} de {MonthsLong[a.Month - 1]} de {a.Year} a {b.Day} de {MonthsLong[b.Month - 1]} de {b.Year}";
    }
    private static bool TryDate(string value, out DateTime result)
    {
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        return DateTime.TryParseExact(value?.Trim(), formats, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out result);
    }

    private static string BuildProcessReference(ExercisePreviousProcess p)
    {
        var number = CleanParagraph(p.ProcessNumber);
        if (string.IsNullOrWhiteSpace(number)) return string.Empty;
        if (p.ProcessYear is > 0 && !Regex.IsMatch(number, $@"(?:/|\b){p.ProcessYear.Value}\b"))
            number += "/" + p.ProcessYear.Value.ToString(CultureInfo.InvariantCulture);
        return "Processo nº " + number;
    }

    private static string FormatCompetence(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"^(\d{4})-(\d{1,2})$");
        if (!match.Success) return CleanParagraph(value);
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 ? $"{MonthsLong[month - 1]} de {year}" : CleanParagraph(value);
    }

    private static string CleanParagraph(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string FirstText(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
    private static bool SameText(string left, string right) => CleanParagraph(left).Equals(CleanParagraph(right), StringComparison.CurrentCultureIgnoreCase);
    private static string JoinPortuguese(IReadOnlyList<string> values) => values.Count switch
    {
        0 => string.Empty,
        1 => values[0],
        2 => values[0] + " e " + values[1],
        _ => string.Join(", ", values.Take(values.Count - 1)) + " e " + values[^1]
    };
    private static string MoneyWithWords(decimal value) => $"{value.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))} ({NumberToWordsService.Convert(value, true)})";
    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var directory = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        for (var number = 2; ; number++)
        {
            var candidate = Path.Combine(directory, $"{name}_{number}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
